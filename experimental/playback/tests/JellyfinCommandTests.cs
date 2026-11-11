// SPDX-License-Identifier: MIT
using System.Net;
using System.Text.Json;
using QuietThings.Playback;

class SessionServer : HttpMessageHandler {
 public bool Paused, Media = true;
 public int Posts;
 public string Session = "tv-session";
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
  if (request.Method == HttpMethod.Post) { Posts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
  string text = JsonSerializer.Serialize(new[] { new { Id = Session, Client = "Jellyfin for Tizen", UserId = "owner", RemoteEndPoint = "192.0.2.10:1234",
   LastPlaybackCheckIn = Media ? DateTimeOffset.UtcNow.ToString("O") : "0001-01-01T00:00:00Z", LastActivityDate = DateTimeOffset.UtcNow.ToString("O"),
   NowPlayingItem = Media ? new { Name = "Example", Type = "Episode", RunTimeTicks = 1000000000L } : null,
   PlayState = new { IsPaused = Paused, PositionTicks = 100000000L, CanSeek = true }, SupportsRemoteControl = true } });
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
 }
}
class Program {
 static int checks;
 static void Check(bool ok) { checks++; if (!ok) throw new Exception("Check " + checks); }
 static async Task Main() {
  var handler = new SessionServer(); using var http = new HttpClient(handler);
  var account = new PlaybackAccount { Server = "http://192.0.2.20", User = "owner", Token = "test-only-key" };
  var provider = new JellyfinPlaybackProvider(new JellyfinPlaybackAdapter(http), () => account, (_, _) => Task.FromResult(IPAddress.Parse("192.0.2.10")));
  var before = await provider.Observe("AprZAARz4r.Jellyfin", DateTimeOffset.UtcNow, default);
  var work = provider.Command(before, "pause", null, default); await Task.Delay(50);
  Check(handler.Posts == 1 && !work.IsCompleted); // Server delivered; player has not acknowledged.
  handler.Paused = true; await work;
  Check((await provider.Observe(before.AppId, DateTimeOffset.UtcNow, default)).State == "paused");
  work = provider.Command(before, "play", null, default); await Task.Delay(50);
  Check(handler.Posts == 2 && !work.IsCompleted);
  handler.Session = "replacement";
  try { await work; throw new Exception("Replacement accepted"); } catch (InvalidOperationException) { Check(true); }
  handler.Session = "tv-session"; handler.Media = true;
  work = provider.Command(before, "stop", null, default); await Task.Delay(50); Check(!work.IsCompleted);
  handler.Media = false; await work;
  var stopped = await provider.Observe(before.AppId, DateTimeOffset.UtcNow, default);
  Check(stopped.State == "idle" && stopped.Title == null && stopped.Commands.Length == 0);
  handler.Media = true; handler.Paused = false;
  try { await provider.Command(before, "pause", null, default); throw new Exception("Unacknowledged command accepted"); }
  catch (InvalidOperationException e) { Check(e.Message == "Player did not acknowledge command"); }
  using (var cancel = new CancellationTokenSource(50)) {
   try { await provider.Command(before, "pause", null, cancel.Token); throw new Exception("Caller cancellation ignored"); }
   catch (OperationCanceledException) { Check(cancel.IsCancellationRequested); }
  }
  Console.WriteLine($"{checks} Jellyfin actual-acknowledgement checks passed");
 }
}
