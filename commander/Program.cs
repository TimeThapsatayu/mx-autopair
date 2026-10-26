// MxAutoPair command center (runs on the publishing PC only): a control page on http://localhost:8790
// that signs wiggler settings with the command key and force-pushes them as control.json to the repo's
// "control" branch, which installed copies check every 3 seconds (RemoteControl.cs).
// The page only answers this PC: requests must use Host localhost:8790, and changes need a custom
// header that other websites can't send, so a web page open in the browser can't trigger a publish.
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string Repo = "TimeThapsatayu/mx-autopair";
const int Port = 8790;
const string Origin = "http://localhost:8790";
var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mxautopair");
var keyPath = Path.Combine(home, "command-signing-key.pem");
var repoDir = Path.Combine(home, "control-repo");
var lastPath = Path.Combine(home, "command-last.json");  // last payload sent, to show on the page
var publishLock = new SemaphoreSlim(1, 1);

if (!File.Exists(keyPath))
{
    Console.Error.WriteLine($"Command key not found at {keyPath}");
    return 1;
}

string page;
using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!)
using (var reader = new StreamReader(stream))
    page = reader.ReadToEnd();

var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{Port}/");
listener.Start();
Console.WriteLine($"Command center running at {Origin} - close this window to stop it.");
if (!args.Contains("--no-browser"))
    try { Process.Start(new ProcessStartInfo(Origin) { UseShellExecute = true }); } catch { }

while (true)
{
    var context = await listener.GetContextAsync();
    _ = Task.Run(() => HandleAsync(context));
}

async Task HandleAsync(HttpListenerContext context)
{
    var request = context.Request;
    var response = context.Response;
    try
    {
        // Only this PC, by its own name: blocks DNS-rebinding tricks from websites.
        if (!string.Equals(request.Headers["Host"], $"localhost:{Port}", StringComparison.OrdinalIgnoreCase))
        {
            await Respond(response, 403, "text/plain", "Forbidden");
            return;
        }
        var path = request.Url!.AbsolutePath;
        if (request.HttpMethod == "GET" && path == "/")
            await Respond(response, 200, "text/html; charset=utf-8", page);
        else if (request.HttpMethod == "GET" && path == "/api/state")
            await Respond(response, 200, "application/json", File.Exists(lastPath) ? await File.ReadAllTextAsync(lastPath) : "{}");
        else if (request.HttpMethod == "POST" && path == "/api/publish")
        {
            // Other websites can't add this header without a preflight this server never approves.
            var origin = request.Headers["Origin"];
            if (request.Headers["X-MxCommand"] != "1" || (origin != null && origin != Origin))
            {
                await Respond(response, 403, "text/plain", "Forbidden");
                return;
            }
            if (request.ContentLength64 > 16384)
            {
                await Respond(response, 413, "text/plain", "Too large");
                return;
            }
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var result = await PublishAsync(await reader.ReadToEndAsync());
            await Respond(response, result.Ok ? 200 : 500, "application/json", result.Json);
        }
        else await Respond(response, 404, "text/plain", "Not found");
    }
    catch (Exception ex)
    {
        try { await Respond(response, 500, "application/json", JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = ex.Message })); }
        catch { }
    }
}

static async Task Respond(HttpListenerResponse response, int status, string type, string body)
{
    var bytes = Encoding.UTF8.GetBytes(body);
    response.StatusCode = status;
    response.ContentType = type;
    response.Headers["Cache-Control"] = "no-store";
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes);
    response.Close();
}

// Builds the payload from the page's request, signs it, and pushes it to the control branch.
async Task<(bool Ok, string Json)> PublishAsync(string body)
{
    using var input = JsonDocument.Parse(body);
    var w = input.RootElement.GetProperty("wiggler");
    var burstNow = input.RootElement.TryGetProperty("burstNow", out var b) && b.ValueKind == JsonValueKind.True;
    var seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();  // always increases, nothing to remember
    var issued = DateTimeOffset.UtcNow;

    using var payloadStream = new MemoryStream();
    using (var json = new Utf8JsonWriter(payloadStream))
    {
        json.WriteStartObject();
        json.WriteNumber("seq", seq);
        json.WriteString("issued", issued);
        json.WriteBoolean("burstNow", burstNow);
        json.WriteStartObject("wiggler");
        json.WriteBoolean("enabled", w.GetProperty("enabled").GetBoolean());
        foreach (var name in new[] { "minInterval", "maxInterval", "minBurst", "maxBurst", "leftClick", "rightClick", "scroll" })
            json.WriteNumber(name, w.GetProperty(name).GetDouble());
        json.WriteEndObject();
        json.WriteEndObject();
    }
    var payload = payloadStream.ToArray();

    using var key = ECDsa.Create();
    key.ImportFromPem(await File.ReadAllTextAsync(keyPath));
    var signature = key.SignData(payload, HashAlgorithmName.SHA256);
    var file = $"{{\"payload\":\"{Convert.ToBase64String(payload)}\",\"signature\":\"{Convert.ToBase64String(signature)}\"}}\n";

    await publishLock.WaitAsync();
    try
    {
        await EnsureRepoAsync();
        await File.WriteAllTextAsync(Path.Combine(repoDir, "control.json"), file, new UTF8Encoding(false));
        await Git("add", "control.json");
        // One commit, replaced each time, so the branch never grows.
        var hasCommit = (await Git("rev-parse", "--verify", "-q", "HEAD")).Ok;
        var commit = hasCommit
            ? await Git("commit", "-q", "--amend", "-m", $"control #{seq}")
            : await Git("commit", "-q", "-m", $"control #{seq}");
        if (!commit.Ok) return (false, Error($"git commit failed: {commit.Output}"));
        var push = await Git("push", "-q", "-f", "origin", "control");
        if (!push.Ok) return (false, Error($"git push failed: {push.Output}"));
    }
    finally { publishLock.Release(); }

    var last = Encoding.UTF8.GetString(payload);
    await File.WriteAllTextAsync(lastPath, last);
    return (true, last);
}

static string Error(string message) => JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = message.Trim() });

async Task EnsureRepoAsync()
{
    if (Directory.Exists(Path.Combine(repoDir, ".git"))) return;
    Directory.CreateDirectory(repoDir);
    await Git("init", "-q", "-b", "control");
    await Git("config", "user.email", "262097405+TimeThapsatayu@users.noreply.github.com");
    await Git("config", "user.name", "TimeThapsatayu");
    await Git("config", "core.autocrlf", "false");
    await Git("remote", "add", "origin", $"https://github.com/{Repo}.git");
}

async Task<(bool Ok, string Output)> Git(params string[] args)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = repoDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    start.Environment["GIT_TERMINAL_PROMPT"] = "0";  // fail instead of waiting for a password prompt
    using var git = Process.Start(start)!;
    var output = git.StandardOutput.ReadToEndAsync();
    var error = git.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try { await git.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { try { git.Kill(); } catch { } return (false, "timed out"); }
    return (git.ExitCode == 0, (await output) + (await error));
}
