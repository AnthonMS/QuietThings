// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuietThings.Playback;
using QuietThings.WebBridge;

// The installation supplies private paths as arguments. Nothing is discoverable over LAN.
class Program
{
    static string tokenPath, helper, statePath;
    sealed class AppSession {
        public string Key, App, StatePath;
        public TwitchInspector Inspector;
    }
    static AppSession[] sessions;
    static readonly SemaphoreSlim stateGate = new(1, 1);
    static readonly Dictionary<string,long> nonces = new();
    static readonly SemaphoreSlim connections = new(8, 8);
    static readonly CobaltProcessAttestation cobaltIdentity = new();
    static readonly CobaltObserver cobalt = new(cobaltIdentity.Alive);
    static readonly SemaphoreSlim cobaltLaunchGate = new(1, 1);
    static readonly ManagedUpdateRecovery updateRecovery = new(
        "/opt/usr/share/quietthings-update-recovery",
        "/opt/usr/home/owner/apps_rw/com.samsung.tv.bixby-promotion/data",
        "/opt/usr/apps", "/opt/usr/home/owner/apps_rw",
        new Dictionary<string,string[]> {
            ["MoonfinApp"]=new[]{"MoonfinApp.moonfin"},
            ["xvvl3S1TT1"]=new[]{"com.samsung.tv.cobalt-yt","xvvl3S1TT1.StandaloneService"}
        });
    static async Task<int> Main(string[] args)
    {
        if (args.Length is not (3 or 4) || args.Take(3).Any(x => !Path.IsPathFullyQualified(x))) return 2;
        int port = 18767;
        if (args.Length == 4 && (!int.TryParse(args[3], out port) || port < 0 || port > 65535)) return 2;
        tokenPath = args[0]; helper = args[1]; statePath = args[2];
        using var source = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("media-probe.js"));
        string probe = await source.ReadToEndAsync();
        using var context = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("kick-context.js"));
        sessions = new[] {
            new AppSession { Key = "twitch", App = TwitchInspector.AppId, StatePath = statePath, Inspector = new TwitchInspector(probe) },
            new AppSession { Key = "kicktv", App = "CrKckTV001.KickTV", StatePath = statePath + ".kicktv", Inspector = new TwitchInspector(probe, "CrKckTV001.KickTV", await context.ReadToEndAsync()) }
        };
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(4);
        if (args.Length == 4) Console.WriteLine(((IPEndPoint)listener.LocalEndpoint).Port);
        _ = RetireExitedSessions();
        while (true)
        {
            await connections.WaitAsync();
            var client = await listener.AcceptTcpClientAsync();
            _ = Serve(client);
        }
    }
    static async Task Serve(TcpClient client) {
        using (client) using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(35))) {
            try { await Handle(client.GetStream(), (IPEndPoint)client.Client.RemoteEndPoint, (IPEndPoint)client.Client.LocalEndPoint, limit.Token); } catch { }
            finally { connections.Release(); }
        }
    }
    static async Task Handle(NetworkStream stream, IPEndPoint peer, IPEndPoint local, CancellationToken ct)
    {
        var head = new List<byte>(); var one = new byte[1];
        using var inputLimit = CancellationTokenSource.CreateLinkedTokenSource(ct); inputLimit.CancelAfter(1500);
        while (head.Count < 8192)
        {
            if (await stream.ReadAsync(one.AsMemory(), inputLimit.Token) == 0) return;
            head.Add(one[0]); int n = head.Count;
            if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 && head[n - 2] == 13 && head[n - 1] == 10) break;
        }
        if (head.Count >= 8192) { await Reply(stream, 431, new { ok = false }, ct); return; }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n"); var first = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(x => x != ""))
        { int colon = line.IndexOf(':'); if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) { await Reply(stream, 400, new { ok = false }, ct); return; } }
        if (first.Length != 3 || first[2] != "HTTP/1.1" || headers.ContainsKey("Transfer-Encoding")) { await Reply(stream, 400, new { ok = false }, ct); return; }
        bool isReport = first[0] == "POST" && first[1] == "/v1/cobalt/report";
        string secret; try { secret = File.ReadAllText(isReport ? Path.Combine(Path.GetDirectoryName(helper), "cobalt-key") : tokenPath).Trim(); } catch { secret = null; }
        if (!headers.TryGetValue("Authorization", out var auth) || !auth.StartsWith("QuietThings-HMAC ", StringComparison.Ordinal) || secret == null || secret.Length < 16 || secret.Length > 256) { await Reply(stream, 401, new { ok = false }, ct); return; }
        int length = 0;
        if (headers.TryGetValue("Content-Length", out var raw) && (!int.TryParse(raw, out length) || length < 0 || length > (isReport ? 4096 : 1024))) { await Reply(stream, 413, new { ok = false }, ct); return; }
        var body = new byte[length]; int offset = 0;
        while (offset < body.Length) { int n = await stream.ReadAsync(body.AsMemory(offset), inputLimit.Token); if (n == 0) return; offset += n; }
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (nonces) {
            foreach (string expired in nonces.Where(x => now - x.Value > 30000).Select(x => x.Key).ToArray()) nonces.Remove(expired);
            if (!WebBridgeAuthentication.VerifyRequest(auth, secret, first[0], first[1], body, DateTimeOffset.UtcNow, out var nonce) || nonces.ContainsKey(nonce)) {
                secret = null;
            } else if (nonces.Count >= 512) secret = null;
            else nonces.Add(nonce, now);
        }
        if (secret == null) { await Reply(stream, 401, new { ok = false }, ct); return; }
        Task Signed(int status, object value) => Reply(stream, status, value, ct, secret, auth);
        if(first[1]=="/v1/updates/recovery") {
            if(first[0]!="POST"){await Signed(405,new{ok=false});return;}
            try {using var doc=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=3});await Signed(200,await updateRecovery.Handle(doc.RootElement,ct));}
            catch {await Signed(409,new{ok=false,error="Recovery check failed. Any existing backup was kept."});}
            return;
        }
        if (isReport || first[1].StartsWith("/v1/cobalt/", StringComparison.Ordinal)) {
            try {
                if (isReport) {
                    using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 8 });
                    string identity = cobaltIdentity.Verify(doc.RootElement, peer, local);
                    await Signed(200, cobalt.Report(doc.RootElement, identity)); return;
                }
                if (first[0] == "GET" && first[1] == "/v1/cobalt/playback") { await Signed(200, cobalt.Read()); return; }
                if (first[0] == "POST" && first[1] == "/v1/cobalt/prepare") {
                    using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || doc.RootElement.EnumerateObject().Count() != 1) throw new InvalidDataException();
                    string mode = doc.RootElement.GetProperty("mode").GetString();
                    await cobaltLaunchGate.WaitAsync(ct);
                    try {
                        if (cobaltIdentity.NeedsRestart(mode, cobalt.MatchesIdentity)) await Run("/usr/bin/app_launcher", new[] { "-u", "5001", "-t", CobaltObserver.AppId }, ct);
                    } finally { cobaltLaunchGate.Release(); }
                    await Signed(200, new { ok = true }); return;
                }
                if (first[0] == "POST" && first[1] == "/v1/cobalt/command") {
                    using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
                    await Signed(200, await cobalt.Command(doc.RootElement, ct)); return;
                }
                await Signed(404, new { ok = false });
            } catch { await Signed(409, new { ok = false, error = "player unavailable or changed" }); }
            return;
        }
        await stateGate.WaitAsync(ct);
        try { await HandleApp(); } finally { stateGate.Release(); }
        async Task HandleApp() {
        var session = sessions[0]; string route = first[1];
        if (route.StartsWith("/v1/kicktv/", StringComparison.Ordinal)) { session = sessions[1]; route = "/v1/" + route.Substring(11); }
        try
        {
            if (first[0] == "POST" && route == "/v1/launch")
            {
                bool existing = await ValidEndpoint(session, ct);
                if (!existing)
                {
                    string output;
                    try { output = await Helper("launch", session, ct); }
                    catch
                    {
                        // A killed shell cannot run its trap. Close debug and remove
                        // temporary guards even when the request deadline expired.
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        try { await Helper("stop", session, cleanup.Token); } catch { }
                        throw;
                    }
                    var match = Regex.Match(output, @"(?m)^QT_ENDPOINT ([0-9]+) ([0-9]+)\s*$");
                    if (!match.Success) throw new InvalidOperationException();
                    var state = new InspectorEndpoint { Pid = int.Parse(match.Groups[1].Value), Port = int.Parse(match.Groups[2].Value),
                        Start = ProcessStart(int.Parse(match.Groups[1].Value)), Boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(), Nonce = Guid.NewGuid().ToString("N") };
                    File.WriteAllText(session.StatePath + ".new", JsonSerializer.Serialize(state));
                    await Run("/bin/chmod", new[] { "600", session.StatePath + ".new" }, ct);
                    File.Move(session.StatePath + ".new", session.StatePath, true);
                }
                // Existing protected instance still needs to be brought to foreground.
                if (existing) await Helper("foreground", session, ct);
                if (!await ValidEndpoint(session, ct)) throw new InvalidOperationException();
                await Signed(200, new { ok = true }); return;
            }
            if ((first[0] == "GET" && route == "/v1/playback") || (first[0] == "POST" && route == "/v1/command"))
            {
                if (!await ValidEndpoint(session, ct)) throw new InvalidOperationException();
                var endpoint = JsonSerializer.Deserialize<InspectorEndpoint>(File.ReadAllText(session.StatePath));
                string action = null, lease = null;
                if (first[0] == "POST")
                {
                    using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
                    var keys = new HashSet<string>();
                    foreach (var field in doc.RootElement.EnumerateObject()) if (!keys.Add(field.Name) || field.Name is not ("action" or "lease")) throw new InvalidOperationException();
                    action = doc.RootElement.GetProperty("action").GetString(); lease = doc.RootElement.GetProperty("lease").GetString();
                    if (action is not ("play" or "pause") || lease == null || lease.Length != 64) throw new InvalidOperationException();
                }
                var result = await session.Inspector.Read(endpoint, action, lease, ct);
                if (!await ValidEndpoint(session, ct)) throw new InvalidOperationException();
                await Signed(200, result); return;
            }
            await Signed(404, new { ok = false });
        }
        catch { await Signed(409, new { ok = false, error = "player unavailable or changed" }); }
        }
    }
    static string ProcessStart(int pid)
    { string stat = File.ReadAllText($"/proc/{pid}/stat"); return stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[19]; }
    static async Task<bool> ValidEndpoint(AppSession session, CancellationToken ct)
    {
        try
        {
            var s = JsonSerializer.Deserialize<InspectorEndpoint>(File.ReadAllText(session.StatePath));
            if (!EndpointAlive(s, session)) return false;
            await Helper("verify", session, ct, s.Port.ToString()); return true;
        }
        catch { return false; }
    }
    static bool EndpointAlive(InspectorEndpoint s, AppSession session)
    {
        try
        {
            if (s.Port < 1024 || s.Port > 65535 || s.Pid < 1 || s.Nonce?.Length != 32 || s.Boot != File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() || s.Start != ProcessStart(s.Pid)) return false;
            return Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{s.Pid}/cmdline")).Split('\0')[0] == "/opt/usr/apps/" + session.App.Split('.')[0] + "/bin/" + session.App;
        }
        catch { return false; }
    }
    static async Task RetireExitedSessions()
    {
        while (true)
        {
            await Task.Delay(2000); await stateGate.WaitAsync();
            try {
                foreach (var session in sessions) {
                    if (!File.Exists(session.StatePath)) continue;
                    var endpoint = JsonSerializer.Deserialize<InspectorEndpoint>(File.ReadAllText(session.StatePath));
                    if (EndpointAlive(endpoint, session)) continue;
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Helper("retire", session, limit.Token, endpoint.Port.ToString());
                    File.Delete(session.StatePath);
                }
            } catch { }
            finally { stateGate.Release(); }
        }
    }
    static Task<string> Helper(string operation, AppSession session, CancellationToken ct, string value = null) => Run("/bin/sh", value == null ? new[] { "-s", "--", operation } : new[] { "-s", "--", operation, value }, ct, File.ReadAllText(helper), session.Key);
    static async Task<string> Run(string file, string[] arguments, CancellationToken ct, string input = null, string profile = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (profile != null) start.Environment["QT_WEB_APP"] = profile;
        using var process = Process.Start(start);
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try { if (input != null) { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); } await process.WaitForExitAsync(ct); } catch { try { process.Kill(true); } catch { } throw; }
        string result = await output; await errors;
        if (process.ExitCode != 0 || result.Length > 8192) throw new InvalidOperationException();
        return result;
    }
    static async Task Reply(Stream stream, int code, object value, CancellationToken ct, string key = null, string proof = null)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value);
        string signature = key == null ? "" : "X-QuietThings-Response: " + WebBridgeAuthentication.Response(key, proof, code, body) + "\r\n";
        byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Result\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n{signature}\r\n");
        await stream.WriteAsync(header.AsMemory(), ct); await stream.WriteAsync(body.AsMemory(), ct);
    }
}
