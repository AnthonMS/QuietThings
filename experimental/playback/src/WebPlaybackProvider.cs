// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuietThings.Playback;

// The privileged bridge is loopback-only. HA still uses the existing companion.
public sealed class WebPlaybackBridge : IDisposable
{
    readonly HttpClient http;
    readonly Func<string> token;
    public WebPlaybackBridge(Func<string> readToken, HttpMessageHandler handler = null)
    {
        token = readToken;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = new Uri("http://127.0.0.1:18767/"), Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task<JsonDocument> Request(string path, object body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(path == "v1/updates/recovery" ? 30000 : path == "v1/launch" ? 15000 : 7000); ct = deadline.Token;
        string secret = token()?.Trim();
        if (secret == null || secret.Length < 16 || secret.Length > 256) throw new InvalidOperationException("Bridge unavailable");
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, path);
        byte[] content = body == null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body);
        string auth = WebBridgeAuthentication.Request(secret, request.Method.Method, "/" + path, content, DateTimeOffset.UtcNow);
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        if (body != null) { request.Content = new ByteArrayContent(content); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); }
        using var reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        reply.EnsureSuccessStatusCode();
        if (!reply.Headers.TryGetValues("X-QuietThings-Response", out var signatures) || signatures.Count() != 1) throw new InvalidDataException("Bridge identity unavailable");
        if (reply.Content.Headers.ContentLength > 8192) throw new InvalidDataException("Bridge response too large");
        using var stream = await reply.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[1024]; int n;
        while ((n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        { if (output.Length + n > 8192) throw new InvalidDataException("Bridge response too large"); output.Write(buffer, 0, n); }
        byte[] bytes = output.ToArray();
        if (!WebBridgeAuthentication.VerifyResponse(signatures.Single(), secret, auth, (int)reply.StatusCode, bytes)) throw new InvalidDataException("Bridge identity unavailable");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
    }
    public async Task<bool> Launch(CancellationToken ct, string appId = WebPlaybackProvider.TwitchApp)
    {
        try { using var result = await Request(WebPlaybackProvider.Route(appId) + "launch", new { }, ct).ConfigureAwait(false); return result.RootElement.GetProperty("ok").GetBoolean(); }
        catch { return false; }
    }
    public void Dispose() => http.Dispose();
}

public sealed class WebPlaybackProvider : IPlaybackProvider
{
    public const string TwitchApp = "Jz7aUrV2Q4.Twitch";
    public const string KickApp = "CrKckTV001.KickTV";
    readonly WebPlaybackBridge bridge;
    readonly string app;
    public string Name => app == KickApp ? "kicktv-web-player" : "twitch-web-player";
    public static string Route(string appId) => appId == TwitchApp ? "v1/" : appId == KickApp ? "v1/kicktv/" : throw new InvalidOperationException("Unsupported web app");
    public WebPlaybackProvider(WebPlaybackBridge client, string appId = TwitchApp) { Route(appId); bridge = client; app = appId; }
    public bool CanObserve(string appId) => appId == app;
    public async Task<PlaybackSnapshot> Observe(string appId, DateTimeOffset now, CancellationToken ct)
    {
        if (!CanObserve(appId)) return PlaybackSnapshot.Unknown(appId, now);
        using var result = await bridge.Request(Route(app) + "playback", null, ct).ConfigureAwait(false);
        var root = result.RootElement;
        if (!root.GetProperty("ok").GetBoolean() || root.GetProperty("app_id").GetString() != appId) return PlaybackSnapshot.Unknown(appId, now);
        var observed = root.GetProperty("observed_at").GetDateTimeOffset();
        if (now - observed > TimeSpan.FromSeconds(5) || observed - now > TimeSpan.FromSeconds(5)) return PlaybackSnapshot.Unknown(appId, now);
        var p = root.GetProperty("probe"); var state = p.GetProperty("state").GetString();
        if (state != "playing" && state != "paused" && state != "idle") return PlaybackSnapshot.Unknown(appId, now);
        string lease = root.GetProperty("lease").GetString();
        if (string.IsNullOrEmpty(lease) || lease.Length > 128) return PlaybackSnapshot.Unknown(appId, now);
        if (state == "idle") return new PlaybackSnapshot { AppId = appId, Adapter = Name, State = "idle", ObservedAt = observed };
        // These live-app profiles deliberately have no VOD duration or seeking.
        var commands = p.GetProperty("commands").EnumerateArray().Select(x => x.GetString()).Where(x => x == "play" || x == "pause").Distinct().ToArray();
        return new PlaybackSnapshot { AppId = appId, Adapter = Name, State = state, Title = Text(p, "title"), MediaType = "Video",
            PositionSeconds = Number(p, "position_seconds"), DurationSeconds = null, ObservedAt = observed, PositionUpdatedAt = observed,
            SessionId = lease, Commands = commands };
    }
    static string Text(JsonElement p, string key) => p.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()) ? x.GetString().Substring(0, Math.Min(512, x.GetString().Length)) : null;
    static double? Number(JsonElement p, string key) => p.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var v) && double.IsFinite(v) && v >= 0 ? v : null;
    public async Task Command(PlaybackSnapshot current, string action, double? position, CancellationToken ct)
    {
        if (!CanObserve(current.AppId) || current.Adapter != Name || (action != "play" && action != "pause") || !current.Commands.Contains(action)) throw new InvalidOperationException("Web command unavailable");
        using var result = await bridge.Request(Route(app) + "command", new { action, lease = current.SessionId }, ct).ConfigureAwait(false);
        if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Web command rejected");
    }
}
