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
        Console.WriteLine("Playback tests passed: " + checks + " checks");
    }
}
