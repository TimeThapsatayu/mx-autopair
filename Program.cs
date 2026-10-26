// Watches for unpaired Bluetooth LE devices whose name starts with a prefix (default "MX")
// and pairs them automatically, accepting the "Pair device?" confirmation.
// Only Just Works (ConfirmOnly) pairing is accepted; PIN-based pairing is ignored.
// Also turns the Bluetooth radio back on whenever it is switched off, and repairs two
// states that block re-pairing after a device is removed in Device Manager:
//   - a leftover pairing whose device was removed: the pairing is removed so the mouse can pair again
//   - a connected mouse whose driver was removed: Windows is asked to rescan and reinstall it
// Run with --diagnose to only log what it finds (no pairing, no changes) and exit.
// Updates itself from signed GitHub releases (checked every minute); see publish-update.ps1.
// Also runs the mouse wiggler in Wiggler.cs (Ctrl+Alt+W toggles it).
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

var prefix = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "MX";
var diagnose = args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase);
var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MxAutoPair");
var logPath = Path.Combine(logDir, "MxAutoPair.log");
Directory.CreateDirectory(logDir);

const string AddressProperty = "System.Devices.Aep.DeviceAddress";
const string ConnectedProperty = "System.Devices.Aep.IsConnected";

// Releases are published by publish-update.ps1 and signed with a private key kept on the publishing PC;
// only a manifest signed by that key is accepted. MXAUTOPAIR_UPDATE_URL overrides the source for testing.
const string UpdateBaseUrl = "https://github.com/TimeThapsatayu/mx-autopair/releases/latest/download/";
const string UpdatePublicKey =
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEs/H1209+J9nx5Ebqlxv23yV7WCmjHKksXogkoroVr7Gw/pj8gLj1E71CwguWbNmg0+leXTreGYcpGmG5YErLXA==";
var currentVersion = NormalizeVersion(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));
var updateCheckFailing = false;  // log a failing check once, not on every retry
// One client for all checks, so the connection to GitHub is reused instead of set up every minute.
var updateHttp = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })
{
    Timeout = TimeSpan.FromMinutes(5),
};
updateHttp.DefaultRequestHeaders.UserAgent.ParseAdd($"MxAutoPair/{currentVersion}");

var gate = new object();
var devices = new Dictionary<string, DeviceInformation>();
var pairingBusy = false;  // one pairing at a time: one mouse can show up as several entries
var lastAttempt = new Dictionary<string, DateTime>();
var cooldown = TimeSpan.FromSeconds(20);
var recentPairs = new List<(string Name, string Address, DateTime Time)>();
var togglingRadio = false;
var reportedSkips = new HashSet<string>();  // so each skipped entry is logged once

void Log(string msg)
{
    lock (gate)
    {
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 1_000_000) File.Delete(logPath);
            File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}

bool IsMatch(string? name) => !string.IsNullOrEmpty(name) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

// "d6:03:d9:84:42:fb" -> "D603D98442FB", the form used in device IDs
string AddressOf(DeviceInformation info) =>
    info.Properties.TryGetValue(AddressProperty, out var a) && a is string s
        ? s.Replace(":", "").Replace("-", "").ToUpperInvariant()
        : "";

bool PairedRecently(Func<(string Name, string Address, DateTime Time), bool> match, TimeSpan within)
{
    lock (gate) return recentPairs.Any(p => DateTime.Now - p.Time < within && match(p));
}

async Task TryPairAsync(DeviceInformation info)
{
    if (!IsMatch(info.Name)) return;
    if (info.Pairing.IsPaired || !info.Pairing.CanPair)
    {
        lock (gate) if (!reportedSkips.Add(info.Id)) return;
        Log($"Saw '{info.Name}' but Windows says it {(info.Pairing.IsPaired ? "is already paired" : "can't be paired right now")}; skipping");
        return;
    }
    // Other entries for a mouse that just paired would only fail and clutter the log.
    if (PairedRecently(p => p.Name == info.Name, TimeSpan.FromSeconds(30))) return;

    lock (gate)
    {
        if (lastAttempt.TryGetValue(info.Id, out var t) && DateTime.Now - t < cooldown) return;
        if (pairingBusy) return;
        pairingBusy = true;
        lastAttempt[info.Id] = DateTime.Now;
    }

    try
    {
        Log($"Found '{info.Name}', pairing...");
        var custom = info.Pairing.Custom;
        custom.PairingRequested += (_, e) =>
        {
            Log($"  ceremony: {e.PairingKind} -> accept");
            e.Accept();
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.Default)
            .AsTask(timeout.Token);  // a stuck attempt would otherwise block every later one
        Log($"  result: {result.Status}");
        if (result.Status == DevicePairingResultStatus.Paired)
            lock (gate) recentPairs.Add((info.Name, AddressOf(info), DateTime.Now));
    }
    catch (OperationCanceledException)
    {
        Log("  no answer after 20s; gave up, will try again");
    }
    catch (Exception ex)
    {
        Log($"  error: {ex.Message}");
    }
    finally
    {
        lock (gate) pairingBusy = false;
    }
}

DeviceWatcher CreateWatcher()
{
    var watcher = DeviceInformation.CreateWatcher(
        BluetoothLEDevice.GetDeviceSelectorFromPairingState(false),
        new[] { "System.Devices.Aep.IsPaired", "System.Devices.Aep.CanPair", AddressProperty },
        DeviceInformationKind.AssociationEndpoint);

    watcher.Added += (sender, info) =>
    {
        lock (gate) devices[info.Id] = info;
        _ = TryPairAsync(info);
    };
    watcher.Updated += (sender, update) =>
    {
        DeviceInformation? info;
        lock (gate)
        {
            if (!devices.TryGetValue(update.Id, out info)) return;
            info.Update(update);  // BLE names often arrive in a later update
        }
        _ = TryPairAsync(info);
    };
    watcher.Removed += (_, update) => { lock (gate) devices.Remove(update.Id); };
    return watcher;
}

Radio? bluetoothRadio = null;  // kept so the StateChanged subscription stays alive

async Task EnsureBluetoothOnAsync(Radio radio)
{
    if (togglingRadio) return;  // a driver repair is switching it off and on on purpose
    if (radio.State != RadioState.Off) return;  // Disabled = adapter disabled in Device Manager; leave it
    Log("Bluetooth is off, turning it on");
    try { Log($"  result: {await radio.SetStateAsync(RadioState.On)}"); }
    catch (Exception ex) { Log($"  error: {ex.Message}"); }
}

// StateChanged reacts immediately; the 30s check is a backup for missed events.
// If the radio object goes stale (sleep, adapter reset), it is fetched again.
async Task KeepBluetoothOnAsync()
{
    var access = await Radio.RequestAccessAsync();
    if (access != RadioAccessStatus.Allowed)
    {
        Log($"Can't control the Bluetooth radio: {access}");
        return;
    }
    while (true)
    {
        try
        {
            if (bluetoothRadio == null)
            {
                bluetoothRadio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bluetoothRadio != null)
                    bluetoothRadio.StateChanged += (sender, e) => _ = EnsureBluetoothOnAsync(sender);
            }
            if (bluetoothRadio != null) await EnsureBluetoothOnAsync(bluetoothRadio);
        }
        catch (Exception ex)
        {
            Log($"Bluetooth radio error: {ex.Message}");
            bluetoothRadio = null;
        }
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

// ---- Repairs for devices removed in Device Manager ----

async Task<List<(DeviceInformation Info, string Address)>> GetPairedMatchesAsync()
{
    var found = await DeviceInformation.FindAllAsync(
        BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
        new[] { AddressProperty, ConnectedProperty },
        DeviceInformationKind.AssociationEndpoint);
    return found.Where(d => IsMatch(d.Name))
        .Select(d => (d, AddressOf(d)))
        .Where(x => x.Item2.Length == 12)
        .ToList();
}

// What Windows has installed for one paired mouse.
(string? NodeId, bool NodePresent, bool DriverPresent) Inspect(DeviceSnapshot snap, string address)
{
    bool ForAddress(string id) => id.StartsWith($@"BTHLE\DEV_{address}\", StringComparison.OrdinalIgnoreCase);
    return (snap.BleAll.FirstOrDefault(ForAddress),
            snap.BlePresent.Any(ForAddress),
            snap.HidPresent.Any(id => id.Contains("_" + address, StringComparison.OrdinalIgnoreCase)));
}

async Task<bool> SetRadioAsync(RadioState state)
{
    var radio = bluetoothRadio ?? (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
    return radio != null && await radio.SetStateAsync(state) == RadioAccessStatus.Allowed;
}

async Task RemoveLeftoverPairingAsync(DeviceInformation info, string address)
{
    Log($"'{info.Name}' ({address}) is paired but its device was removed; removing the leftover pairing");
    var result = await info.Pairing.UnpairAsync();
    Log($"  result: {result.Status}. Hold Easy-Switch until it blinks fast to pair again.");
}

async Task ReinstallDriverAsync(string name, string address, string nodeId, bool reportedConnected)
{
    Log($"'{name}' ({address}) is connected but its mouse driver is missing; asking Windows to rescan");
    var cr = DeviceNodes.Rescan(nodeId);
    Log(cr == 0 ? "  rescan requested" : $"  rescan not allowed (CR 0x{cr:X})");
    await Task.Delay(TimeSpan.FromSeconds(10));
    if (DeviceNodes.List("HID", true) is { } hid && hid.Any(id => id.Contains("_" + address, StringComparison.OrdinalIgnoreCase)))
    {
        Log("  mouse driver is back");
        return;
    }
    if (!reportedConnected)
    {
        Log("  still missing, but Windows doesn't report the mouse as connected, so Bluetooth is left alone");
        return;
    }

    Log("  still missing; turning Bluetooth off and on (all Bluetooth devices disconnect for a few seconds)");
    togglingRadio = true;
    try
    {
        await SetRadioAsync(RadioState.Off);
        await Task.Delay(TimeSpan.FromSeconds(3));
        await SetRadioAsync(RadioState.On);
    }
    catch (Exception ex) { Log($"  error: {ex.Message}"); }
    finally { togglingRadio = false; }
    await Task.Delay(TimeSpan.FromSeconds(15));
    var back = DeviceNodes.List("HID", true)?.Any(id => id.Contains("_" + address, StringComparison.OrdinalIgnoreCase)) == true;
    Log(back ? "  mouse driver is back" : "  mouse driver still missing; restart the PC or remove the mouse in Settings and pair again");
}

// Every 10s. A problem must be seen on several checks in a row before anything is changed,
// a mouse paired in the last 2 minutes is left alone, and each address gets one fix per 5 minutes.
async Task RepairLoopAsync()
{
    var leftoverStrikes = new Dictionary<string, int>();
    var driverStrikes = new Dictionary<string, int>();
    var lastFix = new Dictionary<string, DateTime>();
    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        try
        {
            var snap = DeviceSnapshot.Take();
            if (snap == null) continue;  // couldn't read the device list; never act on a partial view
            foreach (var (info, address) in await GetPairedMatchesAsync())
            {
                var (nodeId, nodePresent, driverPresent) = Inspect(snap, address);
                leftoverStrikes[address] = nodeId == null ? leftoverStrikes.GetValueOrDefault(address) + 1 : 0;
                driverStrikes[address] = nodePresent && !driverPresent ? driverStrikes.GetValueOrDefault(address) + 1 : 0;

                if (PairedRecently(p => p.Address == address, TimeSpan.FromMinutes(2))) continue;
                if (lastFix.TryGetValue(address, out var t) && DateTime.Now - t < TimeSpan.FromMinutes(5)) continue;

                if (leftoverStrikes[address] >= 3)
                {
                    lastFix[address] = DateTime.Now;
                    leftoverStrikes[address] = 0;
                    await RemoveLeftoverPairingAsync(info, address);
                }
                else if (driverStrikes[address] >= 2 && nodeId != null)
                {
                    lastFix[address] = DateTime.Now;
                    driverStrikes[address] = 0;
                    var connected = info.Properties.TryGetValue(ConnectedProperty, out var c) && c is true;
                    await ReinstallDriverAsync(info.Name, address, nodeId, connected);
                }
            }
        }
        catch (Exception ex) { Log($"Repair check error: {ex.Message}"); }
    }
}

async Task DiagnoseAsync()
{
    Log($"--- diagnose v{currentVersion.ToString(3)} (no changes made) ---");
    try
    {
        var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        Log($"Bluetooth: {radio?.State.ToString() ?? "no adapter found"}");
    }
    catch (Exception ex) { Log($"Bluetooth: couldn't read ({ex.Message})"); }
    var services = BluetoothService.States();
    if (services == null) Log("Bluetooth services: couldn't read");
    else foreach (var (name, pid) in services)
        Log($"{BluetoothService.FriendlyName(name)} ({name}): {(pid == 0 ? "not running" : $"running (process {pid})")}");

    var snap = DeviceSnapshot.Take(out var error);
    if (snap == null)
    {
        Log($"Couldn't read the device list ({error})");
        return;
    }
    Log($"Bluetooth LE devices installed: {snap.BleAll.Count} ({snap.BlePresent.Count} present); HID devices present: {snap.HidPresent.Count}");

    var paired = await GetPairedMatchesAsync();
    var inputCheck = Wiggler.CheckInput();
    Log($"Wiggler: {(Wiggler.IsEnabled(logDir) ? "on" : "off")}; mouse input {(inputCheck == 0 ? "works" : $"blocked (error {inputCheck})")}");
    Log($"Paired '{prefix}*' entries: {paired.Count}");
    foreach (var (info, address) in paired)
    {
        var (nodeId, nodePresent, driverPresent) = Inspect(snap, address);
        var connected = info.Properties.TryGetValue(ConnectedProperty, out var c) ? c : "?";
        var device = nodeId == null ? "removed (leftover pairing)" : nodePresent ? "present" : "installed, not present";
        Log($"  '{info.Name}' {address}: connected={connected}, device={device}, mouse driver={(driverPresent ? "present" : "missing")}");
    }
}

// ---- Self-update ----

// System.Version treats 1.1.0 and 1.1.0.0 as different; compare all four parts.
static Version NormalizeVersion(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

void CleanUpOldVersions()
{
    var dir = Path.GetDirectoryName(Environment.ProcessPath);
    if (dir == null) return;
    foreach (var file in Directory.GetFiles(dir, "MxAutoPair.old-*.exe").Append(Path.Combine(dir, "MxAutoPair.new.exe")))
        try { File.Delete(file); } catch { }  // a copy that's still exiting is cleaned up next time
}

// Downloads, verifies and swaps in a newer release. Returns true if one was installed.
async Task<bool> CheckForUpdateAsync(bool verbose)
{
    var baseUrl = Environment.GetEnvironmentVariable("MXAUTOPAIR_UPDATE_URL") is { Length: > 0 } custom
        ? custom.TrimEnd('/') + "/"
        : UpdateBaseUrl;
    try
    {
        var http = updateHttp;
        var manifest = await http.GetByteArrayAsync(baseUrl + "update.json");
        var signature = Convert.FromBase64String((await http.GetStringAsync(baseUrl + "update.json.sig")).Trim());
        updateCheckFailing = false;

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKey), out _);
        if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256))
        {
            Log("Update rejected: its signature doesn't match");
            return false;
        }

        using var doc = JsonDocument.Parse(manifest);
        var version = NormalizeVersion(Version.Parse(doc.RootElement.GetProperty("version").GetString()!));
        var expectedHash = doc.RootElement.GetProperty("sha256").GetString()!;
        if (version <= currentVersion)
        {
            if (verbose) Log($"No update: running {currentVersion.ToString(3)}, latest is {version.ToString(3)}");
            return false;
        }

        Log($"Update {version.ToString(3)} available (running {currentVersion.ToString(3)}), downloading");
        var exe = await http.GetByteArrayAsync(baseUrl + "MxAutoPair.exe");
        if (!Convert.ToHexString(SHA256.HashData(exe)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            Log("  rejected: the download doesn't match the signed checksum");
            return false;
        }

        var path = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(path)!;
        var newPath = Path.Combine(dir, "MxAutoPair.new.exe");
        await File.WriteAllBytesAsync(newPath, exe);

        // Make sure the new version starts on this PC before swapping it in.
        using (var test = Process.Start(new ProcessStartInfo(newPath, "--selftest") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await test.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { test.Kill(); } catch { } }
            if (!test.HasExited || test.ExitCode != 0)
            {
                Log("  rejected: the new version didn't start on this PC");
                try { File.Delete(newPath); } catch { }
                return false;
            }
        }

        // A running exe can be renamed but not overwritten.
        var oldPath = Path.Combine(dir, $"MxAutoPair.old-{DateTime.Now.Ticks}.exe");
        File.Move(path, oldPath);
        try { File.Move(newPath, path); }
        catch
        {
            File.Move(oldPath, path);
            throw;
        }
        Log($"  installed {version.ToString(3)}");
        return true;
    }
    catch (Exception ex)
    {
        if (verbose || !updateCheckFailing) Log($"Update check failed: {ex.Message}");
        updateCheckFailing = true;
        return false;
    }
}

async Task UpdateLoopAsync(TaskCompletionSource firstCheckDone)
{
    while (true)
    {
        var installed = await CheckForUpdateAsync(verbose: false);
        firstCheckDone.TrySetResult();
        if (installed)
        {
            Log("Restarting into the new version (within a minute)");
            Environment.Exit(0);  // the startup task's every-minute trigger launches the new exe
        }
        await Task.Delay(TimeSpan.FromMinutes(1));
    }
}

// Used by the updater to check that a downloaded version starts.
if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
{
    _ = BluetoothLEDevice.GetDeviceSelectorFromPairingState(false);
    return;
}

// One-off update check, without starting Bluetooth or pairing.
if (args.Contains("--update", StringComparer.OrdinalIgnoreCase))
{
    CleanUpOldVersions();
    await CheckForUpdateAsync(verbose: true);
    return;
}

if (diagnose)
{
    await DiagnoseAsync();
    return;
}

using var mutex = new Mutex(true, @"Local\MxAutoPair", out var isFirstInstance);
if (!isFirstInstance) return;

Log($"Started v{currentVersion.ToString(3)}, watching for devices named '{prefix}*'");
CleanUpOldVersions();
// The first update check runs before Bluetooth starts, so a release with a Bluetooth bug
// can still be replaced remotely. It waits at most 15s (e.g. no network yet at sign-in).
var firstUpdateCheck = new TaskCompletionSource();
_ = UpdateLoopAsync(firstUpdateCheck);
await Task.WhenAny(firstUpdateCheck.Task, Task.Delay(TimeSpan.FromSeconds(15)));
_ = KeepBluetoothOnAsync();
_ = RepairLoopAsync();
new Wiggler(logDir, Log).Start();
// The watcher is rebuilt when Windows stops it, when a Bluetooth service stops or restarts
// (e.g. from services.msc), and quietly every 10 minutes, since a watcher can go quiet without notice.
while (true)
{
    DeviceWatcher watcher;
    try
    {
        watcher = CreateWatcher();
        watcher.Start();
    }
    catch (Exception ex)  // e.g. the Bluetooth service is still starting
    {
        Log($"Couldn't start the device watcher ({ex.Message}); retrying in 10s");
        await Task.Delay(TimeSpan.FromSeconds(10));
        continue;
    }
    var services = BluetoothService.States();
    var watcherStarted = DateTime.Now;
    string? reason = null;
    while (reason == null)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        var status = watcher.Status;
        if (status is DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
            reason = $"Device watcher {status.ToString().ToLowerInvariant()}";
        else if (BluetoothService.Changed(services, BluetoothService.States()) is { } change)
            reason = change;
        else if (DateTime.Now - watcherStarted > TimeSpan.FromMinutes(10))
            reason = "";  // routine refresh
    }
    try { if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); }
    catch { }
    lock (gate) devices.Clear();
    if (reason == "") continue;  // routine refresh: no log, no radio check
    Log($"{reason}; restarting the watcher in 3s");
    bluetoothRadio = null;  // the radio object from before a service restart may be stale
    await Task.Delay(TimeSpan.FromSeconds(3));
    try
    {
        if ((await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth) is { } radio)
            await EnsureBluetoothOnAsync(radio);
    }
    catch (Exception ex) { Log($"Bluetooth radio error: {ex.Message}"); }
}

// Device instance IDs Windows has installed, read in one pass.
record DeviceSnapshot(List<string> BleAll, List<string> BlePresent, List<string> HidPresent)
{
    public static DeviceSnapshot? Take() => Take(out _);

    public static DeviceSnapshot? Take(out string error)
    {
        error = "";
        var bleAll = DeviceNodes.List("BTHLE", false, out var cr1);
        var blePresent = DeviceNodes.List("BTHLE", true, out var cr2);
        var hidPresent = DeviceNodes.List("HID", true, out var cr3);
        if (bleAll == null || blePresent == null || hidPresent == null)
        {
            error = $"CR 0x{cr1:X}/0x{cr2:X}/0x{cr3:X}";
            return null;
        }
        return new DeviceSnapshot(bleAll, blePresent, hidPresent);
    }
}

static class BluetoothService
{
    const uint ScManagerConnect = 0x1, ServiceQueryStatus = 0x4;
    const int StatusProcessInfo = 0, Running = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatusProcess
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
                   CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll")]
    static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr handle);

    public static string FriendlyName(string name) =>
        name == "bthserv" ? "Bluetooth Support Service" : "Bluetooth User Support Service";

    // Process IDs of the Bluetooth Support Service (bthserv) and the per-user Bluetooth User Support
    // Service (BluetoothUserService_<suffix>), or null if bthserv can't be read. Needs no admin rights.
    public static Dictionary<string, int>? States()
    {
        if (State("bthserv") is not { } bthserv) return null;
        var result = new Dictionary<string, int> { ["bthserv"] = bthserv };
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in key?.GetSubKeyNames() ?? [])
                if (name.StartsWith("BluetoothUserService_", StringComparison.OrdinalIgnoreCase) && State(name) is { } pid)
                    result[name] = pid;
        }
        catch { }  // the user service is optional; bthserv alone still works
        return result;
    }

    // Describes the first service that stopped or restarted between two readings, or null.
    public static string? Changed(Dictionary<string, int>? before, Dictionary<string, int>? now)
    {
        if (before == null || now == null) return null;
        foreach (var (name, pid) in before)
        {
            var current = now.GetValueOrDefault(name);
            if (current != pid) return $"{FriendlyName(name)} {(current == 0 ? "stopped" : "restarted")}";
        }
        return null;
    }

    // Process ID of a running service: changes when it restarts, 0 while it is stopped or starting,
    // null if it can't be read.
    public static int? State(string name)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenServiceW(manager, name, ServiceQueryStatus);
            if (service == IntPtr.Zero) return null;
            try
            {
                if (!QueryServiceStatusEx(service, StatusProcessInfo, out var s, Marshal.SizeOf<ServiceStatusProcess>(), out _)) return null;
                return s.CurrentState == Running ? s.ProcessId : 0;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
}

static class DeviceNodes
{
    const int FilterEnumerator = 0x1, FilterPresent = 0x100;
    const int CrSuccess = 0, CrBufferSmall = 0x1A, CrNoSuchValue = 0x25;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_List_SizeW(out int length, string filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Reenumerate_DevNode(int devInst, int flags);

    public static List<string>? List(string enumerator, bool presentOnly) => List(enumerator, presentOnly, out _);

    // Device instance IDs under an enumerator such as "BTHLE", or null if the list couldn't be read.
    public static List<string>? List(string enumerator, bool presentOnly, out int cr)
    {
        int flags = FilterEnumerator | (presentOnly ? FilterPresent : 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cr = CM_Get_Device_ID_List_SizeW(out var length, enumerator, flags);
            if (cr == CrNoSuchValue) return new List<string>();  // no devices under this enumerator
            if (cr != CrSuccess) return null;
            var buffer = new char[length];
            cr = CM_Get_Device_ID_ListW(enumerator, buffer, length, flags);
            if (cr == CrBufferSmall) continue;  // a device appeared between the two calls
            if (cr == CrNoSuchValue) return new List<string>();
            if (cr != CrSuccess) return null;
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        cr = CrBufferSmall;
        return null;
    }

    // Same as Device Manager's "Scan for hardware changes", for one device and everything under it.
    public static int Rescan(string deviceId)
    {
        var cr = CM_Locate_DevNodeW(out var devInst, deviceId, 0);
        return cr != CrSuccess ? cr : CM_Reenumerate_DevNode(devInst, 0);
    }
}
