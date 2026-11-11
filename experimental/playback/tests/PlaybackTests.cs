// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using System.Text.Json;
using QuietThings.Playback;

class Handler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Respond(r);
}
class FakeProvider : IPlaybackProvider
{
    public string Name => "example-web-events";
    public bool WrongOwner, Stale;
    public int Commands;
    public bool CanObserve(string app) => app == "example.player";
    public Task<PlaybackSnapshot> Observe(string app, DateTimeOffset now, CancellationToken ct) => Task.FromResult(new PlaybackSnapshot { AppId = WrongOwner ? "other.player" : app, State = "paused", Adapter = Name, Title = "Web event title", ObservedAt = Stale ? now.AddSeconds(-16) : now, Commands = new[] { "play" } });
    public Task Command(PlaybackSnapshot current, string action, double? position, CancellationToken ct) { Commands++; return Task.CompletedTask; }
}
class Program
{
    static int checks;
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    static void Check(bool ok, string name) { checks++; if (!ok) throw new Exception(name); }
    static PlaybackSession Session(string id = "session-new") => new PlaybackSession { Id = id, Client = "Moonfin for Tizen", UserId = "user-a", RemoteEndpoint = "192.0.2.10:4444", LastCheckIn = Now, HasMedia = true, IsPaused = false, Title = "Sample", MediaType = "Movie", PositionTicks = 100000000, DurationTicks = 1000000000, CanControl = true, CanSeek = true };
    static PlaybackSnapshot Select(params PlaybackSession[] rows) => PlaybackSelection.Select(rows, "MoonfinApp.moonfin", "user-a", IPAddress.Parse("192.0.2.10"), Now);
    static async Task Main()
    {
        var s = Session(); var selected = Select(s); Check(selected.State == "playing" && selected.PositionSeconds == 10 && selected.DurationSeconds == 100, "real playback"); Check(selected.Commands.Contains("seek"), "seek capability");
        s.IsPaused = true; Check(Select(s).State == "paused", "pause is actual state");
        s.Client = "Jellyfin for Tizen"; Check(Select(s).State == "unknown", "wrong app excluded");
        s = Session(); s.RemoteEndpoint = "192.0.2.100:4444"; Check(Select(s).State == "unknown", "IP prefix excluded");
        s = Session(); s.RemoteEndpoint = "[::ffff:192.0.2.10]:1234"; Check(Select(s).State == "playing", "mapped IPv4 endpoint");
        s = Session(); s.UserId = "other-user"; Check(Select(s).State == "unknown", "other account excluded");
        s = Session(); s.LastCheckIn = Now.AddSeconds(-46); Check(Select(s).Title == null, "stale session clears title");
        s = Session(); s.LastCheckIn = Now.AddSeconds(6); Check(Select(s).State == "unknown", "future session rejected");
        Check(Select(Session("a"), Session("b")).State == "unknown", "ambiguous same-time sessions rejected");
        s = Session("old-install"); s.LastCheckIn = Now.AddSeconds(-10); Check(Select(s, Session("reinstalled")).SessionId == "reinstalled", "client reinstall follows new session");
        s = Session(); s.IsPaused = null; Check(Select(s).State == "unknown", "missing pause bit not assumed playing");
        s = Session(); s.CanControl = false; Check(Select(s).Commands.Length == 0, "read-only client advertises no commands");
        s = Session(); s.DurationTicks = null; Check(!Select(s).Commands.Contains("seek"), "live content seek excluded");
        s = Session(); s.HasMedia = false; Check(Select(s).State == "idle" && Select(s).Title == null && Select(s).Commands.Length == 0, "idle clears media");
        var store = new PlaybackStore(); long generation = store.SetForeground("MoonfinApp.moonfin"); Check(store.Publish(selected, generation), "publish current generation"); store.SetForeground("YouTube"); Check(store.Read(Now).Title == null, "foreground switch clears metadata"); Check(!store.Publish(selected, generation), "late response rejected");
        generation = store.SetForeground("MoonfinApp.moonfin"); store.Publish(selected, generation); Check(store.Read(Now.AddSeconds(16)).State == "unknown", "cached observations expire");
        const string token = "test-only-token-123456789"; Check(!PlaybackServer.Authorized("Bearer ", token), "empty bearer safe"); Check(!PlaybackServer.Authorized("Bearer " + token + "x", token), "token mismatch"); Check(PlaybackServer.Authorized("Bearer " + token, token), "valid bearer");
        string foreground = "MoonfinApp.moonfin"; int posts = 0; string sessionId = "install-one"; bool switchInFlight = false;
        var account = new PlaybackAccount { Server = "http://192.0.2.20:8096/", Token = "secret-not-on-wire-body", User = "user-a" };
        var handler = new Handler(); handler.Respond = request =>
        {
            if (request.Method == HttpMethod.Post) { posts++; Check(request.RequestUri.AbsolutePath.Contains(sessionId), "command targets newly selected session"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            string json = JsonSerializer.Serialize(new[] { new { Id = sessionId, Client = "Moonfin for Tizen", UserId = "user-a", RemoteEndPoint = "192.0.2.10:7777", LastPlaybackCheckIn = DateTimeOffset.UtcNow.ToString("O"), NowPlayingItem = new { Name = "Example title", Type = "Movie", RunTimeTicks = 1000000000 }, PlayState = new { IsPaused = true, PositionTicks = 100000000, CanSeek = true }, SupportsRemoteControl = true } });
            if (switchInFlight) foreground = "YouTube";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        };
        using var http = new HttpClient(handler); using var agent = new PlaybackAgent(_ => Task.FromResult(foreground), () => account, (_, _) => Task.FromResult(IPAddress.Parse("192.0.2.10")), http);
        var sample = await agent.Refresh(default); Check(sample.State == "paused" && sample.Title == "Example title", "adapter parses actual JSON"); sessionId = "install-two"; await agent.Command("pause", null, default); Check(posts == 1, "remote command sent once");
        switchInFlight = true; Check((await agent.Refresh(default)).Title == null, "app switch during HTTP clears metadata");
        bool rejected = false; try { await agent.Command("play", null, default); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && posts == 1, "unsupported foreground cannot command old session");
        foreground = "MoonfinApp.moonfin"; switchInFlight = false;
        using var server = new PlaybackServer(agent, () => token, 0, IPAddress.Loopback); server.Start(); using var api = new HttpClient(); var url = "http://127.0.0.1:" + server.Port;
        using (var response = await api.GetAsync(url + "/v1/playback")) { Check(response.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated read rejected"); Check(!(await response.Content.ReadAsStringAsync()).Contains("Example title"), "unauthorized response hides media"); }
        api.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
        using (var response = await api.GetAsync(url + "/v1/capabilities")) { Check(response.IsSuccessStatusCode, "companion handshake"); }
        using (var response = await api.GetAsync(url + "/v1/playback")) { var body = await response.Content.ReadAsStringAsync(); Check(response.IsSuccessStatusCode && body.Contains("Example title"), "loopback end-to-end read"); Check(!body.Contains("install-two") && !body.Contains(account.Token), "wire hides server session and account token"); }
        using (var response = await api.PostAsync(url + "/v1/command", new StringContent("{\"action\":\"shell\"}"))) { Check(response.StatusCode == HttpStatusCode.BadRequest, "arbitrary command rejected"); }
        var futureProvider = new FakeProvider();
        using var generic = new PlaybackAgent(_ => Task.FromResult("example.player"), () => account, (_, _) => Task.FromResult(IPAddress.Loopback), http, new[] { futureProvider });
        Check((await generic.Refresh(default)).State == "paused", "additional provider supports another app without changing API");
        await generic.Command("play", null, default); Check(futureProvider.Commands == 1, "commands route to reporting provider");
        futureProvider.WrongOwner = true; Check((await generic.Refresh(default)).Title == null, "provider cannot claim another app");
        futureProvider.WrongOwner = false; futureProvider.Stale = true; Check((await generic.Refresh(default)).Title == null, "provider cannot return expired media");
        int credentialReads = 0;
        using var changingAccount = new PlaybackAgent(_ => Task.FromResult("MoonfinApp.moonfin"), () => ++credentialReads == 1 ? account : new PlaybackAccount { Server = "http://192.0.2.99/", Token = "different-token", User = "different-user" }, (_, _) => Task.FromResult(IPAddress.Parse("192.0.2.10")), http);
        int postsBefore = posts; bool changedRejected = false;
        try { await changingAccount.Command("play", null, default); } catch (InvalidOperationException) { changedRejected = true; }
        Check(changedRejected && posts == postsBefore, "account change cannot command old server session");
        var webAt = DateTimeOffset.UtcNow;
        string webApp = WebPlaybackProvider.TwitchApp, webState = "playing";
        double? webPosition = 25;
        double? webDuration = 1000;
        bool? webLive = null;
        string webLease = "private-page-lease";
        int webCalls = 0, webCommands = 0;
        bool webReject = false, webOversize = false, webImposter = false;
        var webHandler = new Handler { Respond = async request => {
            webCalls++;
            Check(request.RequestUri.Host == "127.0.0.1" && request.RequestUri.Port == 18767, "web bridge stays loopback");
            string proof = request.Headers.GetValues("Authorization").Single();
            byte[] requestBytes = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync();
            Check(WebBridgeAuthentication.VerifyRequest(proof, token, request.Method.Method, request.RequestUri.AbsolutePath, requestBytes, DateTimeOffset.UtcNow, out _), "web bridge request authenticated");
            Check(!proof.Contains(token), "reusable token never sent to local listener");
            HttpResponseMessage Reply(HttpStatusCode status, string text) {
                var reply = new HttpResponseMessage(status) { Content = new StringContent(text) };
                if (!webImposter) reply.Headers.Add("X-QuietThings-Response", WebBridgeAuthentication.Response(token, proof, (int)status, Encoding.UTF8.GetBytes(text)));
                return reply;
            }
            if (request.RequestUri.AbsolutePath is "/v1/command" or "/v1/cobalt/command") {
                webCommands++;
                var body = await request.Content.ReadAsStringAsync();
                Check(body.Contains(webLease), "command retains exact observed lease");
                return Reply(webReject ? HttpStatusCode.Conflict : HttpStatusCode.OK, "{\"ok\":true}");
            }
            return Reply(HttpStatusCode.OK, webOversize ? new string('x', 8193) : JsonSerializer.Serialize(new { ok = true, app_id = webApp, mode = "youtube", page_ready = true, observed_at = webAt, lease = webLease, probe = new { state = webState, title = (string)null, position_seconds = webPosition, duration_seconds = webDuration, live = webLive, commands = new[] { "play", "pause", "seek", "stop", "shell" } } }));
        }};
        using var webBridge = new WebPlaybackBridge(() => token, webHandler);
        var webProvider = new WebPlaybackProvider(webBridge);
        Check(webProvider.CanObserve(WebPlaybackProvider.TwitchApp) && !webProvider.CanObserve("com.samsung.tv.cobalt-yt"), "Twitch provider keeps exact app scope");
        var webSample = await webProvider.Observe(WebPlaybackProvider.TwitchApp, webAt, default);
        Check(webSample.State == "playing" && webSample.PositionSeconds == 25 && webSample.DurationSeconds == null, "Twitch actual state and live timing");
        Check(webSample.Commands.SequenceEqual(new[] { "play", "pause" }), "Twitch does not promise live seek stop or shell");
        Check(!JsonSerializer.Serialize(webSample).Contains("private-page-lease"), "web lease stays off HA wire");
        await webProvider.Command(webSample, "pause", null, default);
        Check(webCommands == 1, "Twitch command routed once");
        bool webDenied = false;
        try { await webProvider.Command(webSample, "seek", 20, default); } catch (InvalidOperationException) { webDenied = true; }
        Check(webDenied && webCommands == 1, "Twitch unsupported command never sent");
        webPosition = null;
        Check((await webProvider.Observe(WebPlaybackProvider.TwitchApp, webAt, default)).State == "playing", "null web position does not discard actual state");
        webAt = webAt.AddSeconds(-6);
        Check((await webProvider.Observe(WebPlaybackProvider.TwitchApp, DateTimeOffset.UtcNow, default)).State == "unknown", "expired bridge observation rejected");
        webAt = DateTimeOffset.UtcNow; webApp = "other.app";
        Check((await webProvider.Observe(WebPlaybackProvider.TwitchApp, webAt, default)).State == "unknown", "wrong bridge app rejected");
        webApp = WebPlaybackProvider.TwitchApp; webState = "idle";
        var menuWeb = await webProvider.Observe(WebPlaybackProvider.TwitchApp, webAt, default);
        Check(menuWeb.State == "idle" && menuWeb.Adapter == "twitch-web-player", "confirmed web menu reaches HA with app identity");
        Check(menuWeb.Title == null && menuWeb.PositionSeconds == null && menuWeb.SessionId == null && menuWeb.Commands.Length == 0, "idle web metadata and controls cleared");
        webState = "unknown";
        Check((await webProvider.Observe(WebPlaybackProvider.TwitchApp, webAt, default)).State == "unknown", "loading or failed web player is not inferred idle");
        webReject = true; webDenied = false;
        try { await webProvider.Command(webSample, "play", null, default); } catch (HttpRequestException) { webDenied = true; }
        Check(webDenied, "changed player command failure propagated");
        webOversize = true; webDenied = false;
        try { await webProvider.Observe(WebPlaybackProvider.TwitchApp, webAt, default); } catch (InvalidDataException) { webDenied = true; }
        Check(webDenied, "oversized bridge response bounded");
        webOversize = false; webImposter = true; webState = "playing"; webDenied = false;
        try { await webProvider.Observe(WebPlaybackProvider.TwitchApp, webAt, default); } catch (InvalidDataException) { webDenied = true; }
        Check(webDenied, "local impostor cannot forge a playback response");
        webImposter = false; webApp = WebPlaybackProvider.KickApp; webPosition = 12;
        var kickProvider = new WebPlaybackProvider(webBridge, WebPlaybackProvider.KickApp);
        Check(kickProvider.CanObserve(webApp) && !kickProvider.CanObserve(WebPlaybackProvider.TwitchApp), "KickTV provider has separate app scope");
        var kickSample = await kickProvider.Observe(webApp, webAt, default);
        Check(kickSample.Adapter == "kicktv-web-player" && kickSample.State == "playing" && kickSample.DurationSeconds == null, "KickTV live state uses its own adapter");
        Check(kickSample.Commands.SequenceEqual(new[] { "play", "pause" }), "KickTV does not promise live seek or unsafe stop");
        webApp = WebPlaybackProvider.TwitchApp;
        Check((await kickProvider.Observe(WebPlaybackProvider.KickApp, webAt, default)).State == "unknown", "KickTV rejects cross-app bridge response");
        var cobaltProvider = new CobaltPlaybackProvider(webBridge);
        webApp = CobaltPlaybackProvider.AppId; webLease = new string('b', 64); webState = "paused"; webLive = false; webReject = false;
        var cobaltSample = await cobaltProvider.Observe(webApp, webAt, default);
        Check(cobaltSample.Adapter == "cobalt-player" && cobaltSample.State == "paused" && cobaltSample.DurationSeconds == 1000, "Cobalt retains finite actual VOD duration");
        Check(cobaltSample.Commands.SequenceEqual(new[] { "play", "pause", "seek" }), "Cobalt advertises only tested capabilities");
        await cobaltProvider.Command(cobaltSample, "seek", 120, default);
        int cobaltCommands = webCommands; webDenied = false;
        try { await cobaltProvider.Command(cobaltSample, "seek", 1001, default); } catch (InvalidOperationException) { webDenied = true; }
        Check(webDenied && cobaltCommands == webCommands, "Cobalt rejects out-of-duration seeks before transport");
        webLive = true;
        var liveCobalt = await cobaltProvider.Observe(webApp, webAt, default);
        Check(liveCobalt.DurationSeconds == null && !liveCobalt.Commands.Contains("seek"), "Cobalt live media cannot advertise VOD seeks");
        Check((await cobaltProvider.Observe(webApp, webAt.AddSeconds(4), default)).State == "unknown", "Cobalt expires unrefreshed snapshots");
        webLive = false; webState = "idle";
        var emptyCobalt = await cobaltProvider.Observe(webApp, webAt, default);
        Check(emptyCobalt.State == "idle" && emptyCobalt.AppName == "YouTube", "attested complete Cobalt menu retains app name and actual idle");
        Check(emptyCobalt.Title == null && emptyCobalt.PositionSeconds == null && emptyCobalt.Commands.Length == 0, "idle Cobalt clears media and controls");
        webState = "unknown";
        Check((await cobaltProvider.Observe(webApp, webAt, default)).State == "unknown", "unknown Cobalt is never inferred idle");
        webApp = WebPlaybackProvider.TwitchApp;
        Check((await cobaltProvider.Observe(CobaltPlaybackProvider.AppId, webAt, default)).State == "unknown", "Cobalt rejects another app's observation");
        byte[] empty = Array.Empty<byte>();
        string signed = WebBridgeAuthentication.Request(token, "GET", "/v1/playback", empty, webAt);
        Check(!WebBridgeAuthentication.VerifyRequest(signed, token, "POST", "/v1/playback", empty, webAt, out _), "proof cannot authorize another method");
        Check(!WebBridgeAuthentication.VerifyRequest(signed, token, "GET", "/v1/launch", empty, webAt, out _), "proof cannot authorize another route");
        Check(!WebBridgeAuthentication.VerifyRequest(signed, token, "GET", "/v1/playback", Encoding.UTF8.GetBytes("tampered"), webAt, out _), "proof binds request body");
        Check(!WebBridgeAuthentication.VerifyRequest(signed, token, "GET", "/v1/playback", empty, webAt.AddSeconds(16), out _), "expired proof rejected");
        int previousWebCalls = webCalls;
        using var missingWebToken = new WebPlaybackBridge(() => null, new Handler { Respond = request => throw new Exception("must not send") });
        webDenied = false;
        try { using var unused = await missingWebToken.Request("v1/playback", null, default); } catch (InvalidOperationException) { webDenied = true; }
        Check(webDenied && webCalls == previousWebCalls, "missing bridge token fails before network");
        Console.WriteLine("Playback tests passed: " + checks + " checks");
    }
}
