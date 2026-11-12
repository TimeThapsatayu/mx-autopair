// This process's own entries in the Windows Volume Mixer (its audio sessions, one per output device it has played
// on). Used by the signal sound to undo a mute or a lowered app slider, so the volume set in the command center
// applies again. Only this process's sessions are touched; the Windows master volume and other apps are left alone.
// Plain COM calls through the interfaces' function tables (no runtime COM support, which the trimmed build lacks).
using System.Runtime.InteropServices;

static unsafe class AudioSessions
{
    static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid EnumeratorInterface = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid SessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    static readonly Guid SessionControl2 = new("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D");
    static readonly Guid SimpleVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    const int Render = 0, Active = 1, ClsCtxAll = 0x17, MultiThreaded = 0;

    // Unmutes this process's sessions and puts their slider at 100%. Returns how many needed a change.
    public static int Restore() => ForEachOwnSession((volume) =>
    {
        float level;
        int muted;
        Call(volume, 4, &level);  // GetMasterVolume
        Call(volume, 6, &muted);  // GetMute
        if (muted == 0 && level >= 0.999f) return false;
        var context = Guid.Empty;
        Call(volume, 5, 0, &context);  // SetMute(false)
        Call(volume, 3, 1f, &context);  // SetMasterVolume(1.0)
        return true;
    });

    // For --unmute-test: mutes this process's sessions and lowers their slider, as someone in the Volume Mixer might.
    public static int MuteForTest() => ForEachOwnSession((volume) =>
    {
        var context = Guid.Empty;
        Call(volume, 5, 1, &context);
        Call(volume, 3, 0.2f, &context);
        return true;
    });

    // For --unmute-test: "muted, 20%" for each of this process's sessions.
    public static string Describe()
    {
        var states = new List<string>();
        ForEachOwnSession((volume) =>
        {
            float level;
            int muted;
            Call(volume, 4, &level);
            Call(volume, 6, &muted);
            states.Add($"{(muted != 0 ? "muted" : "not muted")}, slider {level:0%}");
            return false;
        });
        return states.Count == 0 ? "no session" : string.Join("; ", states);
    }

    static int ForEachOwnSession(Func<IntPtr, bool> action)
    {
        var self = (uint)Environment.ProcessId;
        return ForEachSession((pid, volume) => pid == self && action(volume));
    }

    static int ForEachSession(Func<uint, IntPtr, bool> action)
    {
        CoInitializeEx(IntPtr.Zero, MultiThreaded);  // already set up on this thread is fine
        var changed = 0;
        IntPtr enumerator = 0, devices = 0;
        try
        {
            var clsid = EnumeratorClass;
            var iid = EnumeratorInterface;
            Check(CoCreateInstance(&clsid, IntPtr.Zero, ClsCtxAll, &iid, &enumerator));
            Check(Call(enumerator, 3, Render, Active, &devices));  // EnumAudioEndpoints
            uint deviceCount;
            Check(Call(devices, 3, &deviceCount));
            for (uint d = 0; d < deviceCount; d++)
            {
                IntPtr device = 0, manager = 0, sessions = 0;
                try
                {
                    if (Call(devices, 4, d, &device) < 0) continue;  // Item
                    var managerIid = SessionManager2;
                    if (Call(device, 3, &managerIid, ClsCtxAll, IntPtr.Zero, &manager) < 0) continue;  // Activate
                    if (Call(manager, 5, &sessions) < 0) continue;  // GetSessionEnumerator
                    int sessionCount;
                    if (Call(sessions, 3, &sessionCount) < 0) continue;
                    for (var s = 0; s < sessionCount; s++)
                    {
                        IntPtr control = 0, control2 = 0, volume = 0;
                        try
                        {
                            if (Call(sessions, 4, s, &control) < 0) continue;  // GetSession
                            var control2Iid = SessionControl2;
                            if (Call(control, 0, &control2Iid, &control2) < 0) continue;  // QueryInterface
                            uint pid;
                            if (Call(control2, 14, &pid) < 0) continue;  // GetProcessId
                            var volumeIid = SimpleVolume;
                            if (Call(control, 0, &volumeIid, &volume) < 0) continue;
                            if (action(pid, volume)) changed++;
                        }
                        finally { Release(volume); Release(control2); Release(control); }
                    }
                }
                finally { Release(sessions); Release(manager); Release(device); }
            }
        }
        finally { Release(devices); Release(enumerator); }
        return changed;
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    static void Release(IntPtr p) { if (p != 0) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(*(void***)p + 2)))(p); }

    static void* Slot(IntPtr p, int slot) => *(*(void***)p + slot);

    static int Call(IntPtr p, int slot, void* a) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)Slot(p, slot))(p, a);
    static int Call(IntPtr p, int slot, void* a, void* b) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, void*, int>)Slot(p, slot))(p, a, b);
    static int Call(IntPtr p, int slot, int a, int b, void* c) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int, void*, int>)Slot(p, slot))(p, a, b, c);
    static int Call(IntPtr p, int slot, uint a, void* b) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, int>)Slot(p, slot))(p, a, b);
    static int Call(IntPtr p, int slot, int a, void* b) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, void*, int>)Slot(p, slot))(p, a, b);
    static int Call(IntPtr p, int slot, float a, void* b) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, float, void*, int>)Slot(p, slot))(p, a, b);
    static int Call(IntPtr p, int slot, void* a, int b, IntPtr c, void* d) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, int, IntPtr, void*, int>)Slot(p, slot))(p, a, b, c, d);

    [DllImport("ole32.dll")]
    static extern int CoInitializeEx(IntPtr reserved, int model);

    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(Guid* clsid, IntPtr outer, int context, Guid* iid, IntPtr* result);
}
