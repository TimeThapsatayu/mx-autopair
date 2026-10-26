// Mouse wiggler for testing mouse-tracking software: every 30-90s it sends a 0.5-3s burst of random
// mouse input through SendInput: curved moves to random points across the screen, left and right
// clicks, and scrolling. Moves are relative (like a real mouse), so raw-input games see them too.
// Bursts run on schedule regardless of other input. Ctrl+Alt+W turns it on or off; "off" is
// remembered by a wiggler-off file next to the log.
using System.Diagnostics;
using System.Runtime.InteropServices;

sealed class Wiggler
{
    static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30), MaxInterval = TimeSpan.FromSeconds(90);
    static readonly TimeSpan MinBurst = TimeSpan.FromSeconds(0.5), MaxBurst = TimeSpan.FromSeconds(3);

    readonly Action<string> log;
    readonly string offFlagPath;
    readonly Random random = new();
    volatile bool enabled;

    public Wiggler(string dataDir, Action<string> log)
    {
        this.log = log;
        offFlagPath = Path.Combine(dataDir, "wiggler-off");
        enabled = !File.Exists(offFlagPath);
    }

    public static bool IsEnabled(string dataDir) => !File.Exists(Path.Combine(dataDir, "wiggler-off"));

    public void Start()
    {
        log($"Wiggler {(enabled ? "on" : "off")} (Ctrl+Alt+W turns it {(enabled ? "off" : "on")})");
        new Thread(HotkeyLoop) { IsBackground = true, Name = "Wiggler hotkey" }.Start();
        new Thread(RunLoop) { IsBackground = true, Name = "Wiggler" }.Start();
    }

    // Sends a zero-length move: checks that input injection works without moving anything.
    // Returns 0 on success, otherwise the Windows error code.
    public static int CheckInput() =>
        Send(new MouseInput { Flags = MoveFlag }) ? 0 : Marshal.GetLastWin32Error();

    void Toggle()
    {
        enabled = !enabled;
        try
        {
            if (enabled) File.Delete(offFlagPath);
            else File.WriteAllText(offFlagPath, "");
        }
        catch { }
        log($"Wiggler turned {(enabled ? "on" : "off")} (Ctrl+Alt+W)");
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
        while (true)
        {
            Thread.Sleep(Between(MinInterval, MaxInterval));
            if (!enabled) continue;
            try { Burst(Between(MinBurst, MaxBurst)); }
            catch (Exception ex) { log($"Wiggler error: {ex.Message}"); }
        }
    }

    TimeSpan Between(TimeSpan min, TimeSpan max) => min + (max - min) * random.NextDouble();

    void Burst(TimeSpan duration)
    {
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
                if (roll < 0.25) { Click(LeftDownFlag, LeftUpFlag); leftClicks++; }
                else if (roll < 0.40) { Click(RightDownFlag, RightUpFlag); rightClicks++; }
                else if (roll < 0.65) { Scroll(); scrolls++; }
            }
            log($"Wiggle {duration.TotalSeconds:0.0}s: {moves} moves, {leftClicks} left clicks, {rightClicks} right clicks, {scrolls} scrolls");
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
