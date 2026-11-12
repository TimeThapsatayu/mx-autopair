// Remote control: every 3s, asks GitHub which commit the repo's "control" branch points to (a git
// ref listing, which isn't cached), and when it changes, fetches control.json from that exact commit.
// The file must be signed with the command center's key. It can only choose among things this program
// already does - wiggler on/off, its timing, scenario and their settings, one burst now, pausing auto-pairing
// of the mouse or keyboard, playing the built-in signal sound on a loop and its volume - and this PC fits every
// value into its own limits (WigglerSettings.Normalize, SignalSettings.FromJson).
// Unknown fields are ignored; nothing in it is run.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

sealed record ControlInstructions(long Seq, DateTimeOffset Issued, bool BurstNow, WigglerSettings Settings, AutoPairSettings AutoPair,
                                  SignalSettings Signal);

// The signal sound (SignalPlayer.cs). It plays only when explicitly turned on; volume is 0-1 (of the system
// volume), 0.5 if missing or not a number. Mode "loop" (the default) or "keypress" (plays while typing, for
// KeyHold seconds after the last key, 0.1-10, default 1.2).
sealed record SignalSettings(bool Playing = false, double Volume = 0.5, string Mode = "loop", double KeyHold = 1.2)
{
    public static readonly SignalSettings Default = new();

    public static SignalSettings FromJson(JsonElement root)
    {
        if (!root.TryGetProperty("signal", out var s) || s.ValueKind != JsonValueKind.Object) return Default;
        var playing = s.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True;
        var mode = s.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == "keypress"
            ? "keypress" : "loop";
        return new(playing, Number(s, "volume", 0, 1, Default.Volume), mode, Number(s, "keyHold", 0.1, 10, Default.KeyHold));
    }

    static double Number(JsonElement s, string name, double min, double max, double fallback) =>
        s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
            ? Math.Clamp(d, min, max) : fallback;

    public void WriteJson(Utf8JsonWriter json)
    {
        json.WriteStartObject("signal");
        json.WriteBoolean("playing", Playing);
        json.WriteNumber("volume", Volume);
        json.WriteString("mode", Mode);
        json.WriteNumber("keyHold", KeyHold);
        json.WriteEndObject();
    }

    public override string ToString() => !Playing ? "signal sound off"
        : Mode == "keypress" ? $"signal sound on while typing ({KeyHold:0.0#}s after the last key) at {Volume:0%}"
        : $"signal sound on, looping at {Volume:0%}";
}

// Which kinds of device are auto-paired (and re-paired by the repairs). Only an explicit false pauses one,
// so a missing or odd value keeps auto-pairing on, as it was before this setting existed.
sealed record AutoPairSettings(bool Mouse = true, bool Keyboard = true)
{
    public static readonly AutoPairSettings Default = new();

    public bool For(string? kind) => kind switch { "mouse" => Mouse, "keyboard" => Keyboard, _ => true };

    public static AutoPairSettings FromJson(JsonElement root) =>
        root.TryGetProperty("autoPair", out var a) && a.ValueKind == JsonValueKind.Object
            ? new(!(a.TryGetProperty("mouse", out var m) && m.ValueKind == JsonValueKind.False),
                  !(a.TryGetProperty("keyboard", out var k) && k.ValueKind == JsonValueKind.False))
            : Default;

    public void WriteJson(Utf8JsonWriter json)
    {
        json.WriteStartObject("autoPair");
        json.WriteBoolean("mouse", Mouse);
        json.WriteBoolean("keyboard", Keyboard);
        json.WriteEndObject();
    }

    public override string ToString() => $"auto-pair mouse {(Mouse ? "on" : "paused")}, keyboard {(Keyboard ? "on" : "paused")}";
}

sealed class RemoteControl
{
    const string Repo = "TimeThapsatayu/mx-autopair";
    const string CommandPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOpHAYvZWr+ZU1+bafKrNjeFpjaXsoVBnpGXF3Jz8Nu62L4hon+J9pqu2iYng+wrHZr/W8vboihB5APMdvZdvgg==";
    const int MaxFileBytes = 8192;
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3), BackoffInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan BurstFreshness = TimeSpan.FromMinutes(2);  // older "burst now" requests are ignored
    // MXAUTOPAIR_CONTROL_BRANCH points a test run at another branch; installed copies use "control".
    static readonly string Branch = Environment.GetEnvironmentVariable("MXAUTOPAIR_CONTROL_BRANCH") is { Length: > 0 } b ? b : "control";
    static readonly Regex ControlRef = new($@"([0-9a-f]{{40}}) refs/heads/{Regex.Escape(Branch)}(?:\x00|\n|$)", RegexOptions.CultureInvariant);

    readonly HttpClient http;
    readonly Action<string> log;
    readonly string statePath;
    readonly object gate = new();
    long appliedSeq;
    WigglerSettings settings;
    AutoPairSettings autoPair;
    SignalSettings signal;
    string? lastSha;

    public RemoteControl(HttpClient http, string dataDir, Action<string> log)
    {
        this.http = http;
        this.log = log;
        statePath = Path.Combine(dataDir, "control-state.json");
        (appliedSeq, settings, autoPair, signal) = LoadState(statePath, dataDir);
    }

    public WigglerSettings Settings { get { lock (gate) return settings; } }
    public AutoPairSettings AutoPair { get { lock (gate) return autoPair; } }
    public SignalSettings Signal { get { lock (gate) return signal; } }
    // Raised when the command center changes the signal sound (on/off or volume).
    public event Action<SignalSettings>? SignalChanged;
    // Raised when the command center turns auto-pairing of a kind of device back on.
    public event Action? AutoPairResumed;
    public long AppliedSeq { get { lock (gate) return appliedSeq; } }

    public async Task RunAsync(Wiggler wiggler)
    {
        wiggler.LocalChange += local =>
        {
            lock (gate) settings = local;
            SaveState();
        };
        var failing = false;
        while (true)
        {
            var delay = PollInterval;
            try
            {
                await PollOnceAsync(wiggler);
                if (failing) log("Remote control: checks work again");
                failing = false;
            }
            catch (Exception ex)
            {
                if (!failing) log($"Remote control: check failed ({ex.Message}); retrying every {BackoffInterval.TotalSeconds:0}s");
                failing = true;
                delay = BackoffInterval;  // e.g. offline, or GitHub asking us to slow down
            }
            await Task.Delay(delay);
        }
    }

    async Task PollOnceAsync(Wiggler wiggler)
    {
        var sha = await GetControlShaAsync();
        if (sha == null || sha == lastSha) return;  // no control branch yet, or nothing new
        var (instructions, reason) = Verify(await GetControlFileAsync(sha));
        lastSha = sha;
        AutoPairSettings previous;
        SignalSettings previousSignal;
        if (instructions == null)
        {
            log($"Remote control: ignored commit {sha[..7]}: {reason}");
            return;
        }
        lock (gate)
        {
            if (instructions.Seq <= appliedSeq)
            {
                if (instructions.Seq < appliedSeq) log($"Remote control: ignored #{instructions.Seq}: older than #{appliedSeq}");
                return;
            }
            appliedSeq = instructions.Seq;
            settings = instructions.Settings;
            previous = autoPair;
            autoPair = instructions.AutoPair;
            previousSignal = signal;
            signal = instructions.Signal;
        }
        SaveState();
        wiggler.Apply(instructions.Settings);
        var burst = instructions.BurstNow && DateTimeOffset.UtcNow - instructions.Issued < BurstFreshness;
        if (burst) wiggler.RequestBurst();
        log($"Remote control: applied #{instructions.Seq}: wiggler {instructions.Settings}{(burst ? "; burst now" : "")}; {instructions.AutoPair}; {instructions.Signal}");
        if ((instructions.AutoPair.Mouse && !previous.Mouse) || (instructions.AutoPair.Keyboard && !previous.Keyboard))
            AutoPairResumed?.Invoke();
        if (instructions.Signal != previousSignal) SignalChanged?.Invoke(instructions.Signal);
    }

    // The commit the control branch points to, or null if there is no such branch.
    async Task<string?> GetControlShaAsync()
    {
        var refs = await http.GetStringAsync($"https://github.com/{Repo}.git/info/refs?service=git-upload-pack");
        var match = ControlRef.Match(refs);
        return match.Success ? match.Groups[1].Value : null;
    }

    async Task<byte[]> GetControlFileAsync(string sha) =>
        await http.GetByteArrayAsync($"https://raw.githubusercontent.com/{Repo}/{sha}/control.json");

    // Checks the signature over the exact payload bytes, then reads only the known fields.
    public static (ControlInstructions? Instructions, string Reason) Verify(byte[] file)
    {
        if (file.Length > MaxFileBytes) return (null, $"file too large ({file.Length} bytes)");
        try
        {
            byte[] payload, signature;
            using (var outer = JsonDocument.Parse(file))
            {
                payload = Convert.FromBase64String(outer.RootElement.GetProperty("payload").GetString() ?? "");
                signature = Convert.FromBase64String(outer.RootElement.GetProperty("signature").GetString() ?? "");
            }
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(CommandPublicKey), out _);
            if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256)) return (null, "signature doesn't match");

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var seq = root.GetProperty("seq").GetInt64();
            var issued = root.TryGetProperty("issued", out var i) && i.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.MinValue;
            var burstNow = root.TryGetProperty("burstNow", out var b) && b.ValueKind == JsonValueKind.True;
            var settings = WigglerSettings.FromJson(root.GetProperty("wiggler"));
            return (new ControlInstructions(seq, issued, burstNow, settings, AutoPairSettings.FromJson(root),
                SignalSettings.FromJson(root)), "ok");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, $"not a valid control file ({ex.GetType().Name})");
        }
    }

    // For --diagnose: what's on the branch and what this PC would do with it, without applying it.
    public async Task DescribeAsync()
    {
        log($"Remote control: last applied #{AppliedSeq}; wiggler {Settings}; {AutoPair}; {Signal}");
        try
        {
            var sha = await GetControlShaAsync();
            if (sha == null)
            {
                log("Remote control: no control branch yet (nothing sent from the command center)");
                return;
            }
            var (instructions, reason) = Verify(await GetControlFileAsync(sha));
            if (instructions == null) log($"Remote control: branch at {sha[..7]}, would ignore it: {reason}");
            else log($"Remote control: branch at {sha[..7]}, #{instructions.Seq} signed OK, " +
                     (instructions.Seq > AppliedSeq ? "would apply" : "already applied or older") +
                     $": wiggler {instructions.Settings}{(instructions.BurstNow ? "; burst now requested" : "")}; {instructions.AutoPair}; {instructions.Signal}");
        }
        catch (Exception ex) { log($"Remote control: couldn't check ({ex.Message})"); }
    }

    static (long Seq, WigglerSettings Settings, AutoPairSettings AutoPair, SignalSettings Signal) LoadState(string path, string dataDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var settings = WigglerSettings.FromJson(root.GetProperty("wiggler"));
            return (root.GetProperty("appliedSeq").GetInt64(), settings, AutoPairSettings.FromJson(root),
                    SignalSettings.FromJson(root));
        }
        catch
        {
            // First run (or unreadable state): defaults, honouring the old Ctrl+Alt+W "off" marker.
            var off = File.Exists(Path.Combine(dataDir, "wiggler-off"));
            return (0, WigglerSettings.Default with { Enabled = !off }, AutoPairSettings.Default, SignalSettings.Default);
        }
    }

    void SaveState()
    {
        long seq;
        WigglerSettings s;
        AutoPairSettings a;
        SignalSettings sig;
        lock (gate) (seq, s, a, sig) = (appliedSeq, settings, autoPair, signal);
        try
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteNumber("appliedSeq", seq);
                json.WritePropertyName("wiggler");
                s.WriteJson(json);
                a.WriteJson(json);
                sig.WriteJson(json);
                json.WriteEndObject();
            }
            var temp = statePath + ".tmp";
            File.WriteAllBytes(temp, stream.ToArray());
            File.Move(temp, statePath, overwrite: true);
        }
        catch (Exception ex) { log($"Remote control: couldn't save state ({ex.Message})"); }
    }
}
