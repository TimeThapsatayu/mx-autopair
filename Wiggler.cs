// Mouse wiggler for testing mouse-tracking software: on a random interval it sends a burst of mouse input
// through SendInput, following one scenario (WigglerSettings): random moves and clicks, flicks onto targets,
// tracking a strafing target, or recoil-style sprays, with an optional pause between actions. Moves are
// relative (like a real mouse), so raw-input games see them too. Bursts run on schedule regardless of
// other input. The command center changes the settings (RemoteControl.cs); Ctrl+Alt+W toggles locally.
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Wiggler
{
    readonly Action<string> log;
    readonly bool dryRun;  // everything runs, but no input reaches Windows (testing)
    readonly Random random = new();
    readonly AutoResetEvent wake = new(false);
    volatile WigglerSettings settings;
    volatile bool burstRequested;
    int sentEvents, steps;
    // A 1ms step needs a high-resolution timer: Windows 11 stretches Thread.Sleep(1) to ~15ms for
    // programs without a visible window, which would cut movement to ~60 updates per second.
    readonly IntPtr stepTimer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolutionTimer, TimerAllAccess);

    public Wiggler(WigglerSettings initial, Action<string> log, bool dryRun = false)
    {
        settings = initial;
        this.log = log;
        this.dryRun = dryRun;
        // Opt out of power throttling of timers and CPU speed for this background process.
        var state = new PowerThrottlingState { Version = 1, ControlMask = ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution, StateMask = 0 };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    public WigglerSettings Settings => settings;

    // Raised when Ctrl+Alt+W changes the settings, so they can be saved.
    public event Action<WigglerSettings>? LocalChange;

    public void Start(bool hotkey = true)
    {
        log($"Wiggler {settings}{(hotkey ? " (Ctrl+Alt+W toggles)" : "")}{(dryRun ? " [dry run]" : "")}");
        if (hotkey) new Thread(HotkeyLoop) { IsBackground = true, Name = "Wiggler hotkey" }.Start();
        new Thread(RunLoop) { IsBackground = true, Name = "Wiggler" }.Start();
    }

    // New settings take effect at once: the wait for the next burst restarts with the new interval.
    public void Apply(WigglerSettings newSettings)
    {
        settings = newSettings;
        wake.Set();
    }

    // One burst as soon as possible, even while the wiggler is off.
    public void RequestBurst()
    {
        burstRequested = true;
        wake.Set();
    }

    // Runs one burst right away on the calling thread (testing).
    public void RunBurst(WigglerSettings s, double seconds) => Burst(s, TimeSpan.FromSeconds(seconds), requested: true);

    // Sends a zero-length move: checks that input injection works without moving anything.
    // Returns 0 on success, otherwise the Windows error code.
    public static int CheckInput() =>
        SendRaw(new MouseInput { Flags = MoveFlag }) ? 0 : Marshal.GetLastWin32Error();

    void Toggle()
    {
        var toggled = settings with { Enabled = !settings.Enabled };
        Apply(toggled);
        log($"Wiggler turned {(toggled.Enabled ? "on" : "off")} (Ctrl+Alt+W)");
        LocalChange?.Invoke(toggled);
    }

    void HotkeyLoop()
    {
        if (!RegisterHotKey(IntPtr.Zero, 1, ModControl | ModAlt | ModNoRepeat, 'W'))
        {
            log($"Wiggler: couldn't register Ctrl+Alt+W (error {Marshal.GetLastWin32Error()}); another app may be using it");
            return;
        }
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            if (msg.Message == WmHotkey) Toggle();
    }

    void RunLoop()
    {
        var next = DateTime.UtcNow + Interval(settings);
        while (true)
        {
            var current = settings;
            if (burstRequested || (current.Enabled && DateTime.UtcNow >= next))
            {
                var requested = burstRequested;
                burstRequested = false;
                try { Burst(current, Seconds(current.MinBurst, current.MaxBurst), requested); }
                catch (Exception ex) { log($"Wiggler error: {ex.Message}"); }
                next = DateTime.UtcNow + Interval(settings);
                continue;
            }

            // Sleep until the next burst is due (or indefinitely while off), waking early for changes.
            var wait = current.Enabled ? next - DateTime.UtcNow : TimeSpan.FromHours(1);
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
            if (wake.WaitOne(wait) && !burstRequested)
                next = DateTime.UtcNow + Interval(settings);  // settings changed: restart the wait
        }
    }

    TimeSpan Interval(WigglerSettings s) => Seconds(s.MinInterval, s.MaxInterval);

    TimeSpan Seconds(double min, double max) => TimeSpan.FromSeconds(Between(min, max));

    TimeSpan Millis(double min, double max) => TimeSpan.FromMilliseconds(Between(min, max));

    double Between(double min, double max) => min + (max - min) * random.NextDouble();

    void Burst(WigglerSettings s, TimeSpan duration, bool requested)
    {
        var scenario = s.Scenario == "mixed" ? s.Mixed[random.Next(s.Mixed.Count)] : s.Scenario;
        var end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var timer = Stopwatch.StartNew();
        sentEvents = steps = 0;
        timeBeginPeriod(1);  // 1ms sleeps for smooth movement
        try
        {
            var summary = scenario switch
            {
                "flick" => Flick(s, end),
                "track" => Track(s, end),
                "spray" => Spray(s, end),
                _ => RandomMoves(s, end),
            };
            log($"Wiggle {duration.TotalSeconds:0.0}s {scenario}{(requested ? " (requested)" : "")}{(dryRun ? " [dry run]" : "")}: " +
                $"{summary}; {sentEvents} input events in {timer.Elapsed.TotalSeconds:0.0}s, movement updates ~{steps / timer.Elapsed.TotalSeconds:0}/s");
        }
        finally { timeEndPeriod(1); }
    }

    static bool Before(long end) => Stopwatch.GetTimestamp() < end;

    // Waits about 1ms between movement steps.
    void Step()
    {
        steps++;
        long due = -10_000;  // 1ms, relative, in 100ns units
        if (stepTimer != IntPtr.Zero && SetWaitableTimerEx(stepTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            WaitForSingleObject(stepTimer, 1000);
        else Thread.Sleep(1);
    }

    // The pause between mouse actions, cut short at the end of the burst.
    void Pause(WigglerSettings s, long end)
    {
        if (s.MaxActionDelay <= 0) return;
        var remaining = (end - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
        var ms = Math.Min(Between(s.MinActionDelay, s.MaxActionDelay), remaining);
        if (ms > 0) Thread.Sleep(TimeSpan.FromMilliseconds(ms));
    }

    // Curved moves to random points on the screen, each followed by a possible click, right click or scroll.
    string RandomMoves(WigglerSettings s, long end)
    {
        var (left, top, width, height) = Screen();
        int moves = 0, leftClicks = 0, rightClicks = 0, scrolls = 0;
        while (Before(end))
        {
            MoveTo(left + random.Next(width), top + random.Next(height), Millis(150, 700), end, bend: 0.4);
            moves++;
            var roll = random.NextDouble();
            if (roll < s.LeftClick) { Click(LeftDownFlag, LeftUpFlag, 40, 120); leftClicks++; }
            else if (roll < s.LeftClick + s.RightClick) { Click(RightDownFlag, RightUpFlag, 40, 120); rightClicks++; }
            else if (roll < s.LeftClick + s.RightClick + s.Scroll) { Scroll(); scrolls++; }
            Pause(s, end);
        }
        return $"{moves} moves, {leftClicks} left clicks, {rightClicks} right clicks, {scrolls} scrolls";
    }

    // Pause, fast flick to a target (sometimes overshooting), a small settling correction, then a shot.
    string Flick(WigglerSettings s, long end)
    {
        int flicks = 0, overshoots = 0;
        while (Before(end))
        {
            Pause(s, end);
            if (!Before(end)) break;
            var (dx, dy) = OnScreenVector(Between(s.MinFlickDistance, s.MaxFlickDistance));
            var overshoot = random.NextDouble() < s.FlickOvershoot;
            var reach = overshoot ? Between(1.05, 1.17) : Between(0.95, 0.99);  // past it, or just short
            MoveBy(dx * reach, dy * reach, Millis(s.MinFlickTime, s.MaxFlickTime), end, bend: 0.08);
            MoveBy(dx * (1 - reach), dy * (1 - reach), overshoot ? Millis(60, 150) : Millis(40, 90), long.MaxValue, bend: 0);
            Click(LeftDownFlag, LeftUpFlag, 30, 80);
            flicks++;
            if (overshoot) overshoots++;
        }
        return $"{flicks} flicks ({overshoots} overshot), {flicks} shots";
    }

    // Follows a strafing target: steady movement that reverses direction, with slight hand tremor.
    string Track(WigglerSettings s, long end)
    {
        var (left, top, width, height) = Screen();
        double tremorHz = Between(8, 12), tremorAmp = Between(0.5, 1.5), phase = Between(0, Math.Tau);
        double carryX = 0, carryY = 0;
        var segments = 0;
        if (s.TrackHoldFire) Send(new MouseInput { Flags = LeftDownFlag });
        try
        {
            while (Before(end))
            {
                var speed = Between(s.MinTrackSpeed, s.MaxTrackSpeed);
                GetCursorPos(out var at);
                // Mostly sideways like a strafing target, heading back toward the middle near an edge.
                double dirX = random.Next(2) == 0 ? -1 : 1, dirY = Between(-0.2, 0.2);
                if (at.X < left + width * 0.15) dirX = 1;
                if (at.X > left + width * 0.85) dirX = -1;
                if (at.Y < top + height * 0.15) dirY = Math.Abs(dirY);
                if (at.Y > top + height * 0.85) dirY = -Math.Abs(dirY);
                var norm = Math.Sqrt(dirX * dirX + dirY * dirY);
                var segmentEnd = Math.Min(end, Stopwatch.GetTimestamp() + (long)(Between(0.15, 0.7) * Stopwatch.Frequency));
                var last = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() < segmentEnd)
                {
                    Step();
                    var now = Stopwatch.GetTimestamp();
                    var dt = (now - last) / (double)Stopwatch.Frequency;
                    var t = now / (double)Stopwatch.Frequency;
                    last = now;
                    // Tremor is added as the change in a small sine offset, so it never drifts.
                    var shake = tremorAmp * Math.Tau * tremorHz * dt;
                    carryX += dirX / norm * speed * dt + shake * Math.Cos(Math.Tau * tremorHz * t + phase);
                    carryY += dirY / norm * speed * dt + shake * Math.Sin(Math.Tau * tremorHz * t + phase * 1.7);
                    SendCarry(ref carryX, ref carryY);
                }
                segments++;
                Pause(s, end);
            }
        }
        finally { if (s.TrackHoldFire) Send(new MouseInput { Flags = LeftUpFlag }); }
        return $"{segments} tracking segments{(s.TrackHoldFire ? ", fire held" : "")}";
    }

    // Holds fire while pulling down steadily (recoil control) with a slow sideways wobble.
    string Spray(WigglerSettings s, long end)
    {
        var sprays = 0;
        while (Before(end))
        {
            Pause(s, end);
            if (!Before(end)) break;
            var sprayEnd = Math.Min(end, Stopwatch.GetTimestamp() + (long)(Between(s.MinSprayTime, s.MaxSprayTime) / 1000 * Stopwatch.Frequency));
            double wobbleHz = Between(2, 5), phase = Between(0, Math.Tau), carryX = 0, carryY = 0;
            Send(new MouseInput { Flags = LeftDownFlag });
            try
            {
                var last = Stopwatch.GetTimestamp();
                while (Stopwatch.GetTimestamp() < sprayEnd)
                {
                    Step();
                    var now = Stopwatch.GetTimestamp();
                    var dt = (now - last) / (double)Stopwatch.Frequency;
                    var t = now / (double)Stopwatch.Frequency;
                    last = now;
                    carryX += s.SprayWobble * Math.Tau * wobbleHz * Math.Cos(Math.Tau * wobbleHz * t + phase) * dt;
                    carryY += s.SprayPull * dt;
                    SendCarry(ref carryX, ref carryY);
                }
            }
            finally { Send(new MouseInput { Flags = LeftUpFlag }); }
            sprays++;
        }
        return $"{sprays} sprays";
    }

    // A vector of the given length in a random direction, turned around if it would leave the screen.
    (double X, double Y) OnScreenVector(double length)
    {
        var (left, top, width, height) = Screen();
        GetCursorPos(out var at);
        for (var attempt = 0; ; attempt++)
        {
            var angle = Between(0, Math.Tau);
            double x = Math.Cos(angle) * length, y = Math.Sin(angle) * length;
            var inside = at.X + x >= left && at.X + x < left + width && at.Y + y >= top && at.Y + y < top + height;
            if (inside || attempt == 20) return (x, y);  // in a game the cursor is pinned, so any direction is fine
        }
    }

    void MoveTo(int x, int y, TimeSpan time, long burstEnd, double bend)
    {
        GetCursorPos(out var start);
        MoveBy(x - start.X, y - start.Y, time, burstEnd, bend);
    }

    // Moves by (dx, dy) along an eased path with an optional sideways arc, sent as relative steps, so it
    // lands roughly on target on the desktop and turns smoothly in games.
    void MoveBy(double dx, double dy, TimeSpan time, long burstEnd, double bend)
    {
        var arcSize = (random.NextDouble() - 0.5) * bend;
        double sentX = 0, sentY = 0;
        var t0 = Stopwatch.GetTimestamp();
        var total = Math.Max(1, time.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var p = Math.Min(1, (now - t0) / total);
            var eased = p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2;
            var arc = Math.Sin(p * Math.PI) * arcSize;
            var stepX = (int)Math.Round(dx * eased - dy * arc - sentX);
            var stepY = (int)Math.Round(dy * eased + dx * arc - sentY);
            if (stepX != 0 || stepY != 0)
            {
                Send(new MouseInput { Dx = stepX, Dy = stepY, Flags = MoveFlag });
                sentX += stepX;
                sentY += stepY;
            }
            if (p >= 1 || now >= burstEnd) return;
            Step();
        }
    }

    // Sends the whole-pixel part of an accumulated movement, keeping the fraction for later.
    void SendCarry(ref double carryX, ref double carryY)
    {
        int stepX = (int)Math.Truncate(carryX), stepY = (int)Math.Truncate(carryY);
        if (stepX == 0 && stepY == 0) return;
        Send(new MouseInput { Dx = stepX, Dy = stepY, Flags = MoveFlag });
        carryX -= stepX;
        carryY -= stepY;
    }

    void Click(int down, int up, int minHold, int maxHold)
    {
        Send(new MouseInput { Flags = down });
        Thread.Sleep(random.Next(minHold, maxHold + 1));
        Send(new MouseInput { Flags = up });
        Thread.Sleep(random.Next(30, 101));
    }

    void Scroll()
    {
        var direction = random.Next(2) == 0 ? 1 : -1;
        for (int i = random.Next(1, 6); i > 0; i--)
        {
            Send(new MouseInput { MouseData = 120 * direction, Flags = WheelFlag });
            Thread.Sleep(random.Next(20, 61));
        }
    }

    static (int Left, int Top, int Width, int Height) Screen() =>
        (GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
         GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));

    bool Send(MouseInput mouse)
    {
        sentEvents++;
        return dryRun || SendRaw(mouse);
    }

    static bool SendRaw(MouseInput mouse)
    {
        var inputs = new[] { new Input { Type = InputMouse, Mouse = mouse } };
        return SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1;
    }

    const uint HighResolutionTimer = 0x2, TimerAllAccess = 0x1F0003;
    const int ProcessPowerThrottling = 4;
    const uint ThrottleExecutionSpeed = 0x1, ThrottleIgnoreTimerResolution = 0x4;
    const int InputMouse = 0;
    const int MoveFlag = 0x1, LeftDownFlag = 0x2, LeftUpFlag = 0x4, RightDownFlag = 0x8, RightUpFlag = 0x10, WheelFlag = 0x800;
    const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, WmHotkey = 0x312;

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int Dx, Dy, MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct Input { public int Type; public MouseInput Mouse; }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowMessage { public IntPtr Window; public uint Message; public IntPtr WParam, LParam; public uint Time; public CursorPoint Point; public uint Private; }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr routine, IntPtr arg, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out WindowMessage msg, IntPtr window, uint min, uint max);

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint period);
}
