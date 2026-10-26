// Mouse wiggler for testing mouse-tracking software: on a random interval it sends a burst of random
// mouse input through SendInput: curved moves to random points across the screen, left and right
// clicks, and scrolling. Moves are relative (like a real mouse), so raw-input games see them too.
// Bursts run on schedule regardless of other input. Timing and click mix come from WigglerSettings,
// which the command center can change (RemoteControl.cs); Ctrl+Alt+W turns it on or off locally.
using System.Diagnostics;
using System.Runtime.InteropServices;

// Every value is already range-checked (WigglerSettings.Normalize) before it gets here.
sealed record WigglerSettings(
    bool Enabled, double MinInterval, double MaxInterval, double MinBurst, double MaxBurst,
    double LeftClick, double RightClick, double Scroll)
{
    public static readonly WigglerSettings Default = new(true, 30, 90, 0.5, 3, 0.25, 0.15, 0.25);

    // This PC's own limits; whatever a command asks for is fitted into them.
    public WigglerSettings Normalize()
    {
        static double Fit(double v, double min, double max, double fallback) =>
            double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        double minInterval = Fit(MinInterval, 1, 86400, 30), maxInterval = Fit(MaxInterval, 1, 86400, 90);
        double minBurst = Fit(MinBurst, 0.1, 60, 0.5), maxBurst = Fit(MaxBurst, 0.1, 60, 3);
        double left = Fit(LeftClick, 0, 1, 0.25), right = Fit(RightClick, 0, 1, 0.15), scroll = Fit(Scroll, 0, 1, 0.25);
        var total = left + right + scroll;
        if (total > 1) { left /= total; right /= total; scroll /= total; }  // they share one roll
        return new WigglerSettings(Enabled,
            Math.Min(minInterval, maxInterval), Math.Max(minInterval, maxInterval),
            Math.Min(minBurst, maxBurst), Math.Max(minBurst, maxBurst), left, right, scroll);
    }

    public override string ToString() =>
        $"{(Enabled ? "on" : "off")}, every {MinInterval:0.#}-{MaxInterval:0.#}s, bursts {MinBurst:0.#}-{MaxBurst:0.#}s, " +
        $"left {LeftClick:0%} / right {RightClick:0%} / scroll {Scroll:0%}";
}

sealed class Wiggler
{
    readonly Action<string> log;
    readonly bool dryRun;  // log bursts instead of sending input (testing)
    readonly Random random = new();
    readonly AutoResetEvent wake = new(false);
    volatile WigglerSettings settings;
    volatile bool burstRequested;

    public Wiggler(WigglerSettings initial, Action<string> log, bool dryRun = false)
    {
        settings = initial;
        this.log = log;
        this.dryRun = dryRun;
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

    // Sends a zero-length move: checks that input injection works without moving anything.
    // Returns 0 on success, otherwise the Windows error code.
    public static int CheckInput() =>
        Send(new MouseInput { Flags = MoveFlag }) ? 0 : Marshal.GetLastWin32Error();

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

    TimeSpan Seconds(double min, double max) => TimeSpan.FromSeconds(min + (max - min) * random.NextDouble());

    TimeSpan Between(TimeSpan min, TimeSpan max) => min + (max - min) * random.NextDouble();

    void Burst(WigglerSettings s, TimeSpan duration, bool requested)
    {
        var label = $"Wiggle {duration.TotalSeconds:0.0}s{(requested ? " (requested)" : "")}";
        if (dryRun)
        {
            log($"{label} [dry run, no input sent]");
            return;
        }
        timeBeginPeriod(1);  // 1ms sleeps for smooth movement
        try
        {
            var end = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
            int left = GetSystemMetrics(SmXVirtualScreen), top = GetSystemMetrics(SmYVirtualScreen);
            int width = GetSystemMetrics(SmCxVirtualScreen), height = GetSystemMetrics(SmCyVirtualScreen);
            int moves = 0, leftClicks = 0, rightClicks = 0, scrolls = 0;
            while (Stopwatch.GetTimestamp() < end)
            {
                MoveTo(left + random.Next(width), top + random.Next(height), Between(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(700)), end);
                moves++;
                var roll = random.NextDouble();
                if (roll < s.LeftClick) { Click(LeftDownFlag, LeftUpFlag); leftClicks++; }
                else if (roll < s.LeftClick + s.RightClick) { Click(RightDownFlag, RightUpFlag); rightClicks++; }
                else if (roll < s.LeftClick + s.RightClick + s.Scroll) { Scroll(); scrolls++; }
            }
            log($"{label}: {moves} moves, {leftClicks} left clicks, {rightClicks} right clicks, {scrolls} scrolls");
        }
        finally { timeEndPeriod(1); }
    }

    // Moves along a curved, eased path. The path is planned from the current cursor position and sent as
    // relative steps, so it reaches roughly the target on the desktop and turns smoothly in games.
    void MoveTo(int x, int y, TimeSpan time, long burstEnd)
    {
        GetCursorPos(out var start);
        double dx = x - start.X, dy = y - start.Y;
        var bend = (random.NextDouble() - 0.5) * 0.4;  // sideways arc, up to 20% of the distance
        double sentX = 0, sentY = 0;
        var t0 = Stopwatch.GetTimestamp();
        var total = time.TotalSeconds * Stopwatch.Frequency;
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var p = Math.Min(1, (now - t0) / total);
            var eased = p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2;
            var arc = Math.Sin(p * Math.PI) * bend;
            var wantX = dx * eased - dy * arc;
            var wantY = dy * eased + dx * arc;
            var stepX = (int)Math.Round(wantX - sentX);
            var stepY = (int)Math.Round(wantY - sentY);
            if (stepX != 0 || stepY != 0)
            {
                Send(new MouseInput { Dx = stepX, Dy = stepY, Flags = MoveFlag });
                sentX += stepX;
                sentY += stepY;
            }
            if (p >= 1 || now >= burstEnd) return;
            Thread.Sleep(1);
        }
    }

    void Click(int down, int up)
    {
        Send(new MouseInput { Flags = down });
        Thread.Sleep(random.Next(40, 121));
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

    static bool Send(MouseInput mouse)
    {
        var inputs = new[] { new Input { Type = InputMouse, Mouse = mouse } };
        return SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1;
    }

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
