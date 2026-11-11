// SPDX-License-Identifier: MIT
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace QuietThings.WebBridge;

public sealed class InspectorEndpoint
{
    public int Port { get; set; }
    public int Pid { get; set; }
    public string Start { get; set; }
    public string Boot { get; set; }
    public string Nonce { get; set; }
}

// No arbitrary evaluation endpoint: only this embedded probe and fixed controls.
public sealed class TwitchInspector
{
    public const string AppId = "Jz7aUrV2Q4.Twitch";
    readonly string probe;
    readonly string appId, kickContext;
    bool IsKick => appId == "CrKckTV001.KickTV";
    readonly string sourceVersion;
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    public TwitchInspector(string source, string app = AppId, string context = null) {
        if (app != AppId && app != "CrKckTV001.KickTV") throw new InvalidOperationException();
        appId = app;
        if (app == AppId && context == null) {
            using var resource = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("twitch-context.js"));
            context = resource.ReadToEnd();
        }
        kickContext = context;
        if (IsKick && string.IsNullOrEmpty(context)) throw new InvalidOperationException();
        probe = source; sourceVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + app + context)));
    }
    bool Origin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && (IsKick ? u.IsFile && u.AbsolutePath == "/index.html" : u.Scheme == "https" && u.Port == 443 && u.Host.EndsWith(".tv.twitch.tv", StringComparison.OrdinalIgnoreCase));
    public async Task<object> Read(InspectorEndpoint endpoint, string action, string expectedLease, CancellationToken ct)
    {
        if (endpoint.Port < 1024 || endpoint.Port > 65535 || string.IsNullOrEmpty(endpoint.Nonce)) throw new InvalidOperationException();
        using var reply = await http.GetAsync($"http://127.0.0.1:{endpoint.Port}/json/list", HttpCompletionOption.ResponseHeadersRead, ct);
        reply.EnsureSuccessStatusCode();
        using var list = JsonDocument.Parse(await ReadLimited(await reply.Content.ReadAsStreamAsync(ct), 32768, ct));
        if (list.RootElement.ValueKind != JsonValueKind.Array || list.RootElement.GetArrayLength() > 16) throw new InvalidDataException();
        var pages = list.RootElement.EnumerateArray().Where(t => t.TryGetProperty("type", out var type) && type.GetString() == "page").ToArray();
        JsonElement accepted = default; int matches = 0;
        foreach (var page in pages)
        {
            if (!page.TryGetProperty("url", out var url) || !string.IsNullOrEmpty(url.GetString()) && !Origin(url.GetString())) continue;
            accepted = page; matches++;
        }
        if (matches != 1) throw new InvalidOperationException("Ambiguous player page");
        string target = accepted.GetProperty("id").GetString();
        if (target == null || target.Length > 128 || target.Any(c => !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9') && c != '-')) throw new InvalidDataException();
        using var ws = new ClientWebSocket(); ws.Options.Proxy = null;
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{endpoint.Port}/devtools/page/{target}"), ct);
        int id = 0;
        async Task<JsonElement> Evaluate(string expression, bool awaitPromise = false)
        {
            int sequence = ++id;
            var data = JsonSerializer.SerializeToUtf8Bytes(new { id = sequence, method = "Runtime.evaluate", @params = new { expression, returnByValue = true, awaitPromise } });
            await ws.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, ct);
            for (int events = 0; events < 32; events++)
            {
                using var buffer = new MemoryStream(); var part = new byte[2048]; ValueWebSocketReceiveResult frame;
                do { frame = await ws.ReceiveAsync(part.AsMemory(), ct); if (frame.MessageType != WebSocketMessageType.Text || buffer.Length + frame.Count > 16384) throw new InvalidDataException(); buffer.Write(part, 0, frame.Count); } while (!frame.EndOfMessage);
                using var doc = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 }); var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != sequence) continue;
                if (root.TryGetProperty("error", out _) || !root.TryGetProperty("result", out var result) || result.TryGetProperty("exceptionDetails", out _)) throw new InvalidOperationException();
                return result.GetProperty("result").GetProperty("value").Clone();
            }
            throw new InvalidOperationException();
        }
        string identity = "tizen.application.getCurrentApplication().appInfo.id===" + JsonSerializer.Serialize(appId) + "&&location.protocol==='https:'&&location.hostname.slice(-13)==='.tv.twitch.tv'&&(!location.port||location.port==='443')";
        if (IsKick) identity = "tizen.application.getCurrentApplication().appInfo.id===" + JsonSerializer.Serialize(appId) + "&&location.protocol==='file:'&&location.pathname==='/index.html'";
        string contextSetup = "var module={exports:{}};" + kickContext + ";var metadata=module.exports({document:document,location:location,webapis:window.webapis,XMLHttpRequest:window.XMLHttpRequest});";
        // A page-lifetime probe preserves instance changes between requests.
        string initialize = "(function(){if(!(" + identity + "))throw Error('owner');if(!window.__quietThingsTwitchProbeV1||window.__quietThingsTwitchProbeV1.source!==" + JsonSerializer.Serialize(sourceVersion) + "){" + contextSetup + "var module={exports:{}};" + probe + ";window.__quietThingsTwitchProbeV1={source:" + JsonSerializer.Serialize(sourceVersion) + ",documentInstance:" + JsonSerializer.Serialize(Guid.NewGuid().ToString("N")) + ",player:module.exports({document:document,navigator:navigator,webapis:window.webapis,metadata:metadata,isMenu:metadata.isMenu})};}return {app_id:tizen.application.getCurrentApplication().appInfo.id,document_instance:window.__quietThingsTwitchProbeV1.documentInstance,probe:window.__quietThingsTwitchProbeV1.player.read()};})()";
        var observed = await Evaluate(initialize);
        if (observed.GetProperty("app_id").GetString() != appId) throw new InvalidOperationException("Wrong page owner");
        var p = observed.GetProperty("probe");
        string documentInstance = observed.GetProperty("document_instance").GetString();
        if (string.IsNullOrEmpty(documentInstance) || documentInstance.Length > 64) throw new InvalidDataException();
        string Lease(JsonElement sample) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.Nonce + "|" + target + "|" + sourceVersion + "|" + documentInstance + "|" + (sample.GetProperty("instance").GetString() ?? "none")))).ToLowerInvariant();
        string lease = Lease(p);
        if (action != null)
        {
            if ((action != "play" && action != "pause") || expectedLease != lease || p.GetProperty("state").GetString() is not ("playing" or "paused")) throw new InvalidOperationException("Player changed");
            string invoke = "(function(){if(!(" + identity + ")||!window.__quietThingsTwitchProbeV1||window.__quietThingsTwitchProbeV1.documentInstance!==" + JsonSerializer.Serialize(documentInstance) + ")throw Error('owner');return Promise.resolve(window.__quietThingsTwitchProbeV1.player.command(" + JsonSerializer.Serialize(action) + ",null," + JsonSerializer.Serialize(p.GetProperty("instance").GetString()) + ")).then(function(){return true;});})()";
            await Evaluate(invoke, true);
            observed = await Evaluate("(function(){if(!(" + identity + "))throw Error('owner');return {app_id:tizen.application.getCurrentApplication().appInfo.id,document_instance:window.__quietThingsTwitchProbeV1.documentInstance,probe:window.__quietThingsTwitchProbeV1.player.read()};})()");
            if (observed.GetProperty("app_id").GetString() != appId) throw new InvalidOperationException("Wrong page owner");
            p = observed.GetProperty("probe");
            if (observed.GetProperty("document_instance").GetString() != documentInstance || Lease(p) != lease) throw new InvalidOperationException("Player changed");
        }
        // Only known probe fields are returned, never DevTools URLs or exceptions.
        return new { ok = true, app_id = appId, observed_at = DateTimeOffset.UtcNow, lease,
            probe = new { state = p.GetProperty("state").GetString(), title = Text(p, "title"), position_seconds = Number(p, "position_seconds"), duration_seconds = (double?)null, live = p.GetProperty("live").Clone(),
                commands = p.GetProperty("commands").EnumerateArray().Select(x => x.GetString()).Where(x => x == "play" || x == "pause").Distinct().ToArray() } };
    }
    static string Text(JsonElement p, string key) => p.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()[..Math.Min(512, x.GetString().Length)] : null;
    static double? Number(JsonElement p, string key) => p.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 ? n : null;
    internal static async Task<byte[]> ReadLimited(Stream input, int max, CancellationToken ct)
    { using var data = new MemoryStream(); var buffer = new byte[2048]; int n; while ((n = await input.ReadAsync(buffer.AsMemory(), ct)) > 0) { if (data.Length + n > max) throw new InvalidDataException(); data.Write(buffer, 0, n); } return data.ToArray(); }
}
