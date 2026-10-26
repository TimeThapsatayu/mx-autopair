// Mouse wiggler for testing mouse-tracking software: on a random interval it sends a burst of mouse input
// through SendInput, following one scenario (WigglerSettings): random moves and clicks, flicks onto targets
// with micro-adjustments, jiggle-peeks, tracking a strafing target, or recoil-style sprays, with an optional
// pause between actions. A simulated hand (HandNoise) adds tremor and twitches scaled by the stress setting. Moves are
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
    int sentEvents, steps, reversals, lastDirX;
    HandNoise hand = new(new Random(), 0);
    double handX, handY, handCarryX, handCarryY;
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
        sentEvents = steps = reversals = lastDirX = 0;
        hand = new HandNoise(random, s.Stress);
        (handX, handY) = hand.Offset(Now());
        handCarryX = handCarryY = 0;
        timeBeginPeriod(1);
        try
        {
            var summary = scenario switch
            {
                "flick" => Flick(s, end),
                "track" => Track(s, end),
                "spray" => Spray(s, end),
                "peek" => Peek(s, end),
                _ => RandomMoves(s, end),
            };
            var seconds = timer.Elapsed.TotalSeconds;
            log($"Wiggle {duration.TotalSeconds:0.0}s {scenario}{(requested ? " (requested)" : "")}{(dryRun ? " [dry run]" : "")}: " +
                $"{summary}; {sentEvents} input events in {seconds:0.0}s, movement updates ~{steps / seconds:0}/s, " +
                $"direction changes ~{reversals / seconds:0}/s");
        }
        finally { timeEndPeriod(1); }
    }

    static bool Before(long end) => Stopwatch.GetTimestamp() < end;

    static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Waits about 1ms between movement steps.
    void Step()
    {
        steps++;
        long due = -10_000;  // 1ms, relative, in 100ns units
        if (stepTimer != IntPtr.Zero && SetWaitableTimerEx(stepTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            WaitForSingleObject(stepTimer, 1000);
        else Thread.Sleep(1);
    }

    // How far the hand's tremor and twitches moved since the last step, in whole pixels.
    (int X, int Y) HandStep()
    {
        var (x, y) = hand.Offset(Now());
        handCarryX += x - handX;
        handCarryY += y - handY;
        (handX, handY) = (x, y);
        int stepX = (int)Math.Truncate(handCarryX), stepY = (int)Math.Truncate(handCarryY);
        handCarryX -= stepX;
        handCarryY -= stepY;
        return (stepX, stepY);
    }

    // "Holding still": no intended movement, but the hand keeps trembling.
    void Hold(double ms, long end = long.MaxValue)
    {
        var until = Math.Min(end, Stopwatch.GetTimestamp() + (long)(ms / 1000 * Stopwatch.Frequency));
        while (Stopwatch.GetTimestamp() < until)
        {
            Step();
            var (x, y) = HandStep();
            Move(x, y);
        }
    }

    // The pause between mouse actions, cut short at the end of the burst.
    void Pause(WigglerSettings s, long end)
    {
        if (s.MaxActionDelay > 0) Hold(Between(s.MinActionDelay, s.MaxActionDelay), end);
    }

    void Move(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (dx != 0)
        {
            var direction = Math.Sign(dx);
            if (lastDirX != 0 && direction != lastDirX) reversals++;
            lastDirX = direction;
        }
        Send(new MouseInput { Dx = dx, Dy = dy, Flags = MoveFlag });
    }

    // Moves to random points on the screen, landing a little off and correcting, each followed by a
    // possible click, right click or scroll.
    string RandomMoves(WigglerSettings s, long end)
    {
        var (left, top, width, height) = Screen();
        int moves = 0, leftClicks = 0, rightClicks = 0, scrolls = 0;
        while (Before(end))
        {
            GetCursorPos(out var at);
            Aim(left + random.Next(width) - at.X, top + random.Next(height) - at.Y, Between(150, 700), end, 0.4, s.Stress, 0.1);
            moves++;
            var roll = random.NextDouble();
            if (roll < s.LeftClick) { Click(LeftDownFlag, LeftUpFlag, 40, 120); leftClicks++; }
            else if (roll < s.LeftClick + s.RightClick) { Click(RightDownFlag, RightUpFlag, 40, 120); rightClicks++; }
            else if (roll < s.LeftClick + s.RightClick + s.Scroll) { Scroll(); scrolls++; }
            Pause(s, end);
        }
        return $"{moves} moves, {leftClicks} left clicks, {rightClicks} right clicks, {scrolls} scrolls";
    }

    // Pause, fast flick to a target (sometimes overshooting), a correction, a few tiny micro-adjustments
    // while settling, then a shot.
    string Flick(WigglerSettings s, long end)
    {
        int flicks = 0, overshoots = 0, adjustments = 0, shots = 0;
        while (Before(end))
        {
            Pause(s, end);
            if (!Before(end)) break;
            var (dx, dy) = OnScreenVector(Between(s.MinFlickDistance, s.MaxFlickDistance));
            if (Aim(dx, dy, Between(s.MinFlickTime, s.MaxFlickTime), end, 0.08, s.Stress, s.FlickOvershoot)) overshoots++;
            var count = random.Next(2, 3 + (int)Math.Round(4 * s.Stress));
            MicroAdjust(count, s.Stress);
            adjustments += count;
            shots += FireBurst(s);
            flicks++;
        }
        return $"{flicks} flicks ({overshoots} overshot), {adjustments} micro-adjustments, {shots} shots";
    }

    // Jiggle-peeking: quick, uneven left-right swings like strafing in and out of cover, then a few
    // micro-adjustments and, more often than not, a shot.
    string Peek(WigglerSettings s, long end)
    {
        int peeks = 0, swings = 0, shots = 0;
        while (Before(end))
        {
            Pause(s, end);
            if (!Before(end)) break;
            var direction = random.Next(2) == 0 ? -1 : 1;
            for (var i = random.Next(2, 6); i > 0 && Before(end); i--)
            {
                var size = Between(s.MinPeekAmplitude, s.MaxPeekAmplitude) * (random.NextDouble() < 0.25 ? 0.4 : 1);
                MoveBy(direction * size, Between(-3, 3), Between(s.MinPeekSwing, s.MaxPeekSwing), end, 0.05, s.Stress);
                if (random.NextDouble() < 0.7) direction = -direction;  // usually swings back, sometimes repeats
                Hold(Between(0, 40), end);
                swings++;
            }
            if (!Before(end)) break;
            MicroAdjust(random.Next(1, 3 + (int)Math.Round(2 * s.Stress)), s.Stress);
            if (random.NextDouble() < 0.6) shots += FireBurst(s);
            peeks++;
        }
        return $"{peeks} peeks, {swings} swings, {shots} shots";
    }

    // Follows a strafing target: steady movement that reverses direction with a short reaction lag, with
    // speed that surges and sags under stress, plus the hand's tremor.
    string Track(WigglerSettings s, long end)
    {
        var (left, top, width, height) = Screen();
        double carryX = 0, carryY = 0, prevX = 0, prevY = 0;
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
                (dirX, dirY) = (dirX / norm, dirY / norm);
                double surgeHz = Between(0.5, 2), surgePhase = Between(0, Math.Tau), lag = Between(0.06, 0.18);
                var segmentStart = Now();
                var segmentEnd = Math.Min(end, Stopwatch.GetTimestamp() + (long)(Between(0.15, 0.7) * Stopwatch.Frequency));
                var last = segmentStart;
                while (Stopwatch.GetTimestamp() < segmentEnd)
                {
                    Step();
                    var t = Now();
                    var dt = t - last;
                    last = t;
                    // Reaction lag: the aim keeps its old heading briefly before turning to the new one.
                    var k = Math.Min(1, (t - segmentStart) / lag);
                    var headingX = prevX * (1 - k) + dirX * k;
                    var headingY = prevY * (1 - k) + dirY * k;
                    var v = speed * (1 + 0.35 * s.Stress * Math.Sin(Math.Tau * surgeHz * t + surgePhase));
                    carryX += headingX * v * dt;
                    carryY += headingY * v * dt;
                    int stepX = (int)Math.Truncate(carryX), stepY = (int)Math.Truncate(carryY);
                    carryX -= stepX;
                    carryY -= stepY;
                    var (hx, hy) = HandStep();
                    Move(stepX + hx, stepY + hy);
                }
                (prevX, prevY) = (dirX, dirY);
                segments++;
                Pause(s, end);
            }
        }
        finally { if (s.TrackHoldFire) Send(new MouseInput { Flags = LeftUpFlag }); }
        return $"{segments} tracking segments{(s.TrackHoldFire ? ", fire held" : "")}";
    }

    // Holds fire while pulling down against recoil with a sideways wobble; under stress the pull is uneven.
    string Spray(WigglerSettings s, long end)
    {
        var sprays = 0;
        while (Before(end))
        {
            Pause(s, end);
            if (!Before(end)) break;
            var sprayEnd = Math.Min(end, Stopwatch.GetTimestamp() + (long)(Between(s.MinSprayTime, s.MaxSprayTime) / 1000 * Stopwatch.Frequency));
            double wobbleHz = Between(2, 5), phase = Between(0, Math.Tau), pullHz = Between(1, 3), carryX = 0, carryY = 0;
            Send(new MouseInput { Flags = LeftDownFlag });
            try
            {
                var last = Now();
                while (Stopwatch.GetTimestamp() < sprayEnd)
                {
                    Step();
                    var t = Now();
                    var dt = t - last;
                    last = t;
                    carryX += s.SprayWobble * Math.Tau * wobbleHz * Math.Cos(Math.Tau * wobbleHz * t + phase) * dt;
                    carryY += s.SprayPull * (1 + 0.4 * s.Stress * Math.Sin(Math.Tau * pullHz * t)) * dt;
                    int stepX = (int)Math.Truncate(carryX), stepY = (int)Math.Truncate(carryY);
                    carryX -= stepX;
                    carryY -= stepY;
                    var (hx, hy) = HandStep();
                    Move(stepX + hx, stepY + hy);
                }
            }
            finally { Send(new MouseInput { Flags = LeftUpFlag }); }
            sprays++;
        }
        return $"{sprays} sprays";
    }

    // Burst fire: several quick taps, with a small downward pull against recoil and the hand trembling
    // between them. Returns the number of shots.
    int FireBurst(WigglerSettings s)
    {
        var shots = random.Next((int)s.MinShots, (int)s.MaxShots + 1);
        for (var i = 0; i < shots; i++)
        {
            var gap = Between(s.MinShotGap, s.MaxShotGap);
            var hold = Math.Min(Between(25, 60), gap * 0.6);
            Send(new MouseInput { Flags = LeftDownFlag });
            Hold(hold);
            Send(new MouseInput { Flags = LeftUpFlag });
            if (i == shots - 1) break;
            var pull = Math.Min(gap - hold, Between(12, 25));
            MoveBy(Between(-1, 1), Between(1, 3), pull, long.MaxValue, 0, s.Stress);
            Hold(gap - hold - pull);
        }
        Hold(Between(30, 100));
        return shots;
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

    // An aimed move: the main movement lands a little off (overshooting with the given chance, more
    // scattered under stress), then one or two small corrections bring it onto the target.
    // Returns whether it overshot.
    bool Aim(double dx, double dy, double ms, long end, double bend, double stress, double overshootChance)
    {
        var overshoot = random.NextDouble() < overshootChance;
        var reach = overshoot ? Between(1.05, 1.17 + 0.08 * stress) : Between(0.9 - 0.05 * stress, 0.99);
        var miss = Between(-0.03, 0.03) * (1 + stress);  // angle error, radians
        double mainX = (dx * Math.Cos(miss) - dy * Math.Sin(miss)) * reach;
        double mainY = (dx * Math.Sin(miss) + dy * Math.Cos(miss)) * reach;
        MoveBy(mainX, mainY, ms, end, bend, stress);
        double restX = dx - mainX, restY = dy - mainY;
        var corrections = random.NextDouble() < stress ? 2 : 1;
        for (var i = corrections; i > 0; i--)
        {
            var part = i == 1 ? 1 : Between(0.5, 0.8);
            MoveBy(restX * part, restY * part, overshoot ? Between(60, 150) : Between(30, 90), long.MaxValue, 0, stress);
            restX -= restX * part;
            restY -= restY * part;
            Hold(Between(10, 40));
        }
        return overshoot;
    }

    // Tiny fine-aim nudges (1-6px) with short hesitations, like settling onto a head.
    void MicroAdjust(int count, double stress)
    {
        for (var i = 0; i < count; i++)
        {
            var size = Between(1, 3 + 3 * stress);
            var angle = Between(0, Math.Tau);
            MoveBy(Math.Cos(angle) * size, Math.Sin(angle) * size, Between(15, 45), long.MaxValue, 0, stress);
            Hold(Between(20, 80));
        }
    }

    // Moves by (dx, dy) along an eased path, sent as relative steps so it lands roughly on target on the
    // desktop and turns naturally in games. Stress adds speed surges and sideways wander mid-move (both
    // fade out at the ends, so the landing point is unchanged), and the hand's tremor rides on top.
    void MoveBy(double dx, double dy, double ms, long burstEnd, double bend, double stress)
    {
        var length = Math.Sqrt(dx * dx + dy * dy);
        var arcSize = (random.NextDouble() - 0.5) * bend;
        double surgeHz = Between(5, 12), surgePhase = Between(0, Math.Tau), wanderHz = Between(3, 9), wanderPhase = Between(0, Math.Tau);
        var wander = length > 0 ? Math.Min(15, length * 0.04) * stress / length : 0;  // fraction of the move
        double sentX = 0, sentY = 0;
        var t0 = Stopwatch.GetTimestamp();
        var total = Math.Max(1, ms / 1000 * Stopwatch.Frequency);
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var p = Math.Min(1, (now - t0) / total);
            var t = now / (double)Stopwatch.Frequency;
            var envelope = Math.Sin(p * Math.PI);
            var eased = (p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2)
                      + 0.06 * stress * envelope * Math.Sin(Math.Tau * surgeHz * t + surgePhase);
            var side = arcSize * envelope + wander * envelope * Math.Sin(Math.Tau * wanderHz * t + wanderPhase);
            var pathX = (int)Math.Round(dx * eased - dy * side - sentX);
            var pathY = (int)Math.Round(dy * eased + dx * side - sentY);
            sentX += pathX;
            sentY += pathY;
            var (hx, hy) = HandStep();
            Move(pathX + hx, pathY + hy);
            if (p >= 1 || now >= burstEnd) return;
            Step();
        }
    }

    // A click; the hand keeps trembling while the button is down and just after.
    void Click(int down, int up, int minHold, int maxHold)
    {
        Send(new MouseInput { Flags = down });
        Hold(random.Next(minHold, maxHold + 1));
        Send(new MouseInput { Flags = up });
        Hold(random.Next(30, 101));
    }

    void Scroll()
    {
        var direction = random.Next(2) == 0 ? 1 : -1;
        for (int i = random.Next(1, 6); i > 0; i--)
        {
            Send(new MouseInput { MouseData = 120 * direction, Flags = WheelFlag });
            Hold(random.Next(20, 61));
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
