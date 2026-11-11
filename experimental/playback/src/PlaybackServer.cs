// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuietThings.Playback;

// Optional companion protocol. The original port 18765 and API v1 are untouched.
public sealed class PlaybackServer : IDisposable
{
    readonly PlaybackAgent agent;
    readonly Func<string> readToken;
    readonly Func<JsonElement,CancellationToken,Task<object>> notifications;
    readonly Func<bool> accessEnabled;
    readonly Func<long,CancellationToken,Task<object>> notificationEvents;
    readonly Func<JsonElement,CancellationToken,Task<object>> dismissNotification;
    readonly Func<JsonElement,CancellationToken,Task<object>> notificationImage;
    readonly Func<CancellationToken,Task<object>> notificationStatus;
    readonly TcpListener listener;
    readonly CancellationTokenSource lifetime = new CancellationTokenSource();
    Task run;
    long rateWindow;
    int rateCount;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public PlaybackServer(PlaybackAgent source, Func<string> token, int port = 18766, IPAddress bind = null, Func<JsonElement,CancellationToken,Task<object>> notifications = null,Func<bool> accessEnabled=null,Func<long,CancellationToken,Task<object>> notificationEvents=null,Func<JsonElement,CancellationToken,Task<object>> dismissNotification=null,Func<JsonElement,CancellationToken,Task<object>> notificationImage=null,Func<CancellationToken,Task<object>> notificationStatus=null) { agent = source; readToken = token;this.notifications=notifications;this.accessEnabled=accessEnabled??(()=>true);this.notificationEvents=notificationEvents;this.dismissNotification=dismissNotification;this.notificationImage=notificationImage;this.notificationStatus=notificationStatus; listener = new TcpListener(bind ?? IPAddress.Any, port); }
    public void Start() { if (run != null) throw new InvalidOperationException("Already started"); listener.Start(4); run = Run(); }
    async Task Run()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client = null;
            try { client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false); using (client) { using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); limit.CancelAfter(18000); await Handle(client.GetStream(), limit.Token).ConfigureAwait(false); } }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception) { client?.Dispose(); }
        }
    }
    async Task Handle(NetworkStream stream, CancellationToken ct)
    {
        using var inputTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); inputTimeout.CancelAfter(3000);
        var head = new List<byte>(); var one = new byte[1]; int ending = 0;
        while (ending < 4) { if (head.Count >= 8192) { await Reply(stream, 431, new { ok = false, error = "headers too large" }, ct); return; } if (await stream.ReadAsync(one.AsMemory(), inputTimeout.Token) == 0) return; byte b = one[0]; head.Add(b); ending = (ending == 0 && b == 13) ? 1 : (ending == 1 && b == 10) ? 2 : (ending == 2 && b == 13) ? 3 : (ending == 3 && b == 10) ? 4 : b == 13 ? 1 : 0; }
        var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None); var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1") { await Reply(stream, 400, new { ok = false, error = "bad request" }, ct); return; }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines) { if (line == lines[0] || line == "") continue; int colon = line.IndexOf(':'); if (colon <= 0 || !headers.TryAdd(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim())) { await Reply(stream, 400, new { ok = false, error = "ambiguous header" }, ct); return; } }
        if (headers.ContainsKey("Transfer-Encoding")) { await Reply(stream, 400, new { ok = false, error = "transfer encoding unsupported" }, ct); return; }
        int length=0;if(headers.TryGetValue("Content-Length",out var raw)&&(!int.TryParse(raw,NumberStyles.None,CultureInfo.InvariantCulture,out length)||length<0)){await Reply(stream,413,new{ok=false,error="invalid body length"},ct);return;}
        async Task Reject(int code,object error){
            await Reply(stream,code,error,ct);int left=Math.Min(length,262144);var discard=new byte[4096];
            try{while(left>0){int n=await stream.ReadAsync(discard.AsMemory(0,Math.Min(left,discard.Length)),inputTimeout.Token);if(n==0)break;left-=n;}}catch{}
        }
        if (!headers.TryGetValue("Authorization", out var auth) || !Authorized(auth, readToken())) { await Reject(401,new { ok = false, error = "unauthorized" }); return; }
        if(!accessEnabled()){await Reject(503,new{ok=false,error="Local Home Assistant access disabled"});return;}
        long now = Environment.TickCount64; if (now - rateWindow >= 1000) { rateWindow = now; rateCount = 0; }
        if (++rateCount > 8) { await Reject(429,new { ok = false, error = "rate limited" }); return; }
        int maxBody=first[1]=="/v1/notifications"?1500000:first[1]=="/v1/notifications/image"?262144:1024;
        if(length>maxBody){await Reject(413,new{ok=false,error="body too large"});return;}
        var body = new byte[length]; int got = 0; while (got < length) { int n = await stream.ReadAsync(body.AsMemory(got), inputTimeout.Token); if (n == 0) return; got += n; }
        string method = first[0], path = first[1];
        if(path=="/v1/notifications/status"&&method=="GET"&&notificationStatus!=null){await Reply(stream,200,await notificationStatus(ct).ConfigureAwait(false),ct);return;}
        if(path=="/v1/notifications/image"&&method=="POST"&&notificationImage!=null){
            try{using var document=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=3});await Reply(stream,200,await notificationImage(document.RootElement,ct).ConfigureAwait(false),ct);}
            catch(JsonException){await Reply(stream,400,new{ok=false,error="invalid live notification frame"},ct);}
            catch{await Reply(stream,503,new{ok=false,error="notification image unavailable"},ct);}return;
        }
        if(method=="GET"&&notificationEvents!=null&&(path=="/v1/notifications/events"||path.StartsWith("/v1/notifications/events?after=",StringComparison.Ordinal))) {
            long after=0;if(path.Contains('?')&&(!long.TryParse(path.Substring("/v1/notifications/events?after=".Length),NumberStyles.None,CultureInfo.InvariantCulture,out after)||after<0)){await Reply(stream,400,new{ok=false,error="invalid event cursor"},ct);return;}
            await Reply(stream,200,await notificationEvents(after,ct).ConfigureAwait(false),ct);return;
        }
        if(path=="/v1/notifications/dismiss"&&method=="POST"&&dismissNotification!=null) {
            try{using var document=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=3});await Reply(stream,200,await dismissNotification(document.RootElement,ct).ConfigureAwait(false),ct);}
            catch(JsonException){await Reply(stream,400,new{ok=false,error="invalid notification identity"},ct);}return;
        }
        if(path=="/v1/notifications"&&method=="POST"&&notifications!=null) {
            try{using var document=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=5});await Reply(stream,200,await notifications(document.RootElement,ct).ConfigureAwait(false),ct);}
            catch(JsonException){await Reply(stream,400,new{ok=false,error="invalid notification"},ct);}
            catch{await Reply(stream,503,new{ok=false,error="notification display unavailable"},ct);}
            return;
        }
        if (path == "/v1/capabilities" && method == "GET") { await Reply(stream, 200, new { ok = true, extension = "quietthings-playback", extension_version = 1, adapters = agent.AdapterNames, apps = false, transport = true, notifications=notifications!=null,notification_actions=notificationEvents!=null,notification_images=notifications!=null,notification_media_version=2,notification_live_images=notificationImage!=null,notification_gif=notifications!=null }, ct); return; }
        if (path == "/v1/playback" && method == "GET")
        {
            try { var sample = await agent.Refresh(ct).ConfigureAwait(false); await Reply(stream, 200, new { ok = true, extension_version = 1, playback = Wire(sample) }, ct); }
            catch (OperationCanceledException) { throw; }
            catch { await Reply(stream, 503, new { ok = false, error = "observation unavailable" }, ct); }
            return;
        }
        if (path == "/v1/command" && method == "POST")
        {
            string action; double? position = null;
            try { using var doc = JsonDocument.Parse(body); var root = doc.RootElement; if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("action", out var value) || value.ValueKind != JsonValueKind.String) throw new JsonException(); action = value.GetString(); if (action != "play" && action != "pause" && action != "stop" && action != "seek") throw new JsonException(); if (root.TryGetProperty("position_seconds", out value)) { if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var n) || !double.IsFinite(n) || n < 0) throw new JsonException(); position = n; } }
            catch (JsonException) { await Reply(stream, 400, new { ok = false, error = "invalid command" }, ct); return; }
            try { await agent.Command(action, position, ct).ConfigureAwait(false); await Reply(stream, 200, new { ok = true, accepted = true }, ct); }
            catch (OperationCanceledException) { throw; }
            catch { await Reply(stream, 409, new { ok = false, error = "command unavailable or rejected" }, ct); }
            return;
        }
        await Reply(stream, path == "/v1/capabilities" || path == "/v1/playback" || path == "/v1/command" ? 405 : 404, new { ok = false, error = "not found or wrong method" }, ct);
    }
    public static bool Authorized(string auth, string token)
    {
        token = token?.Trim(); if (token == null || token.Length < 16 || token.Length > 256 || auth == null || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth.Substring(7)), Encoding.UTF8.GetBytes(token));
    }
    public static object Wire(PlaybackSnapshot s) => new { app_id = s.AppId, app_name = s.AppName, state = s.State, adapter = s.Adapter, title = s.Title, media_type = s.MediaType, position_seconds = s.PositionSeconds, duration_seconds = s.DurationSeconds, observed_at = s.ObservedAt.ToUniversalTime().ToString("O"), position_updated_at = s.PositionUpdatedAt?.ToUniversalTime().ToString("O"), commands = s.Commands };
    static async Task Reply(NetworkStream stream, int status, object body, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body); string reason = status == 200 ? "OK" : status == 401 ? "Unauthorized" : "Request Failed";
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + reason + "\r\nContent-Type: application/json\r\nContent-Length: " + json.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\n" + (status == 401 ? "WWW-Authenticate: Bearer\r\n" : "") + "\r\n");
        await stream.WriteAsync(header.AsMemory(), ct); await stream.WriteAsync(json.AsMemory(), ct);
    }
    public void Dispose() { lifetime.Cancel(); listener.Stop(); _ = (run ?? Task.CompletedTask).ContinueWith(_ => lifetime.Dispose()); }
}
