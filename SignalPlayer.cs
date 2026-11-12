// The signal sound: the built-in signal.mp3 (embedded in the exe, so it arrives with signed updates), turned on
// and off by the command center, in one of two modes:
//   loop      plays on a loop until turned off
//   keypress  plays while someone types: each key press (other than Shift/Ctrl/Alt/Win on their own) starts
//             it or keeps it going; it fades out over KeyHold seconds after the last key and jumps back to
//             full volume on the next one; when that time runs out it stops and rewinds, so the next key
//             starts the clip from the beginning. Only the time of a key press is used, never which key,
//             and nothing about typing is logged. A key press also undoes a mute or lowered slider for this
//             app in the Volume Mixer (AudioSessions.cs), so the set volume applies again.
// Turning the sound on does that too. Volume is relative to the Windows volume.
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Media.Core;
using Windows.Media.Playback;

sealed class SignalPlayer(Action<string> log, bool muted = false, bool hookKeys = true)
{
    readonly object gate = new();
    readonly AutoResetEvent wake = new(false);
    MediaPlayer? player;
    SignalSettings current = SignalSettings.Default;
    bool audible;  // keypress mode: playing because of a recent key
    long lastKey = long.MinValue / 2;  // Environment.TickCount64 of the last key press
    CancellationTokenSource? listening;
    uint hookThreadId;

    public void Apply(SignalSettings settings)
    {
        lock (gate)
        {
            var old = current;
            current = settings;
            try
            {
                if (!settings.Playing)
                {
                    StopListening();
                    if (player != null)
                    {
                        player.Pause();
                        player.Dispose();
                        player = null;
                    }
                    audible = false;
                    if (old.Playing) log("Signal sound: off");
                    return;
                }
                player ??= CreatePlayer();
                if (settings.Mode == "loop")
                {
                    StopListening();
                    audible = false;
                    player.Volume = settings.Volume;
                    if (!old.Playing || old.Mode != "loop")
                    {
                        player.Play();
                        _ = RestoreMixerSoonAsync();  // the mixer entry exists once playback has started
                    }
                }
                else
                {
                    if (old.Playing && old.Mode == "loop") Rewind();
                    StartListening();
                    wake.Set();  // picks up a changed volume or hold time straight away
                }
                if (settings != old) log($"Command center: {settings}");
            }
            catch (Exception ex) { log($"Signal sound error: {ex.Message}"); }
        }
    }

    // A key was pressed (from the keyboard hook, or from --keypress-test).
    public void OnKey()
    {
        Interlocked.Exchange(ref lastKey, Environment.TickCount64);
        wake.Set();
    }

    MediaPlayer CreatePlayer()
    {
        var sound = new MemoryStream();
        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("signal.mp3")!)
            resource.CopyTo(sound);
        sound.Position = 0;
        var created = new MediaPlayer
        {
            IsLoopingEnabled = true,  // typing for longer than the clip keeps it going
            IsMuted = muted,
            Source = MediaSource.CreateFromStream(sound.AsRandomAccessStream(), "audio/mpeg"),
        };
        created.CommandManager.IsEnabled = false;  // no media overlay or media keys for it
        created.MediaFailed += (_, e) => log($"Signal sound: can't play ({e.Error}: {e.ErrorMessage})");
        return created;
    }

    void Rewind()
    {
        player?.Pause();
        if (player != null) player.PlaybackSession.Position = TimeSpan.Zero;
        audible = false;
    }

    // Keypress mode: follows the time since the last key, about 60 times a second while playing.
    void Follow(CancellationToken stop)
    {
        var handledKey = Interlocked.Read(ref lastKey);
        var lastRestore = 0L;
        while (!stop.IsCancellationRequested)
        {
            WaitHandle.WaitAny([wake, stop.WaitHandle], audible ? 15 : Timeout.Infinite);
            var key = Interlocked.Read(ref lastKey);
            if (key != handledKey && Environment.TickCount64 - lastRestore >= 250)
            {
                handledKey = key;
                lastRestore = Environment.TickCount64;
                RestoreMixer();
            }
            lock (gate)
            {
                if (stop.IsCancellationRequested || player == null) return;
                var since = (Environment.TickCount64 - Interlocked.Read(ref lastKey)) / 1000.0;
                if (since < current.KeyHold)
                {
                    player.Volume = current.Volume * (1 - since / current.KeyHold);
                    if (!audible)
                    {
                        player.Play();
                        audible = true;
                    }
                }
                else if (audible) Rewind();
            }
        }
    }

    void RestoreMixer()
    {
        try
        {
            if (AudioSessions.Restore() > 0) log("Signal sound: it was muted or turned down in the Volume Mixer; restored");
        }
        catch (Exception ex) { log($"Signal sound: couldn't check the Volume Mixer ({ex.Message})"); }
    }

    async Task RestoreMixerSoonAsync()
    {
        await Task.Delay(1000);
        RestoreMixer();
    }

    void StartListening()
    {
        if (listening != null) return;
        var stop = listening = new CancellationTokenSource();
        new Thread(() => Follow(stop.Token)) { IsBackground = true, Name = "signal keypress" }.Start();
        if (!hookKeys) return;
        listener = this;
        var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            hookThreadId = GetCurrentThreadId();
            var hook = SetWindowsHookExW(KeyboardLowLevel, Callback, GetModuleHandleW(null), 0);
            if (hook == IntPtr.Zero) log($"Signal sound: can't listen for key presses (error {Marshal.GetLastWin32Error()})");
            ready.Set();
            if (hook == IntPtr.Zero) return;
            while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }  // a keyboard hook needs its thread to wait for messages
            UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "signal keyboard hook" }.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    void StopListening()
    {
        if (listening == null) return;
        listening.Cancel();
        listening = null;
        if (hookKeys)
        {
            listener = null;
            PostThreadMessageW(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // For the test modes.
    public string Describe()
    {
        lock (gate)
            return player == null
                ? "no player"
                : $"{player.PlaybackSession.PlaybackState}, position {player.PlaybackSession.Position.TotalSeconds:0.0}s of {player.PlaybackSession.NaturalDuration.TotalSeconds:0.0}s, volume {player.Volume:0%}, muted {player.IsMuted}";
    }

    // ---- Keyboard hook: only notes that a key went down ----

    const int KeyboardLowLevel = 13, KeyDown = 0x100, SysKeyDown = 0x104;
    const uint WmQuit = 0x12;
    static SignalPlayer? listener;
    static readonly HookProc Callback = OnHook;  // kept in a field so it isn't collected while hooked

    static IntPtr OnHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message == KeyDown || message == SysKeyDown) && listener is { } target
            && !IsModifier(Marshal.ReadInt32(data)))
            target.OnKey();
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    // Shift, Ctrl, Alt (either side) and the Windows keys.
    static bool IsModifier(int key) => key is >= 0x10 and <= 0x12 or >= 0xA0 and <= 0xA5 or 0x5B or 0x5C;

    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct WindowMessage { public IntPtr Window; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; public uint Private; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int type, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out WindowMessage msg, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);
}
