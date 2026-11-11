// SPDX-License-Identifier: MIT
using System.Net;
using System.Text;
using System.Text.Json;
using QuietThings.Playback;

class Transport : HttpMessageHandler {
 public string Key = "test-observer-launch-key-0000", Mode = "youtube";
 public bool Ready = true;
 public int Prepared, Read;
 public List<string> Calls = new();
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
  ct.ThrowIfCancellationRequested();
  string proof = request.Headers.GetValues("Authorization").Single();
  byte[] input = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
  if (!WebBridgeAuthentication.VerifyRequest(proof, Key, request.Method.Method, request.RequestUri.AbsolutePath, input, DateTimeOffset.UtcNow, out _)) throw new Exception();
  Calls.Add(request.RequestUri.AbsolutePath);
  if (request.RequestUri.AbsolutePath == "/v1/cobalt/prepare") {
   Prepared++; if (JsonDocument.Parse(input).RootElement.GetProperty("mode").GetString() != "youtube") throw new Exception();
  } else Read++;
  string text = JsonSerializer.Serialize(new { ok = true, app_id = CobaltPlaybackProvider.AppId, mode = Mode, page_ready = Ready, probe = new { state = "unknown" } });
  var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
  result.Headers.Add("X-QuietThings-Response", WebBridgeAuthentication.Response(Key, proof, 200, Encoding.UTF8.GetBytes(text))); return result;
 }
}
class Program {
 static int checks;
 static void Check(bool ok) { checks++; if (!ok) throw new Exception("Check " + checks); }
 static async Task Main() {
  string yt = CobaltLaunchArguments.UserData("youtube"), tt = CobaltLaunchArguments.UserData("tizentube");
  string prefix = "--base_url=https://www.youtube.com/tv?quietthings_cobalt_v1&quietthings_launch=";
  Check(yt.StartsWith(prefix) && yt.EndsWith(" --proxy=http://127.0.0.2:8102 --content=/home/owner/share/tizentube-content --dial_name=YouTube --use_eden"));
  Check(tt.StartsWith(prefix) && tt.EndsWith(" --proxy=http://127.0.0.2:8101 --content=/home/owner/share/tizentube-content --dial_name=TizenTube --use_eden"));
  Check(yt.Substring(prefix.Length, 32).All(Uri.IsHexDigit) && yt[prefix.Length + 32] == ' ');
  Check(CobaltLaunchArguments.UserData("youtube") != yt);
  try { CobaltLaunchArguments.UserData("other"); throw new Exception("Unknown launch mode accepted"); }
  catch (ArgumentException) { Check(true); }
  var handler = new Transport(); using var bridge = new WebPlaybackBridge(() => handler.Key, handler);
  var launcher = new CobaltVideoLauncher(bridge); int opened = 0, sent = 0;
  await launcher.Play("youtube", () => { opened++; return Task.CompletedTask; }, () => { sent++; return Task.CompletedTask; }, _ => Task.FromResult(CobaltPlaybackProvider.AppId), default);
  Check(opened == 1 && sent == 1 && handler.Prepared == 1);
  Check(handler.Calls.All(x => x is "/v1/cobalt/prepare" or "/v1/cobalt/playback"));
  // A ready document with unknown/idle media is enough; playback is never
  // invented or used to trigger a repeated request.
  Check(handler.Read == 1);
  handler.Mode = "tizentube"; opened = sent = 0;
  using (var cancel = new CancellationTokenSource(500)) {
   try { await launcher.Play("youtube", () => { opened++; return Task.CompletedTask; }, () => { sent++; return Task.CompletedTask; }, _ => Task.FromResult(CobaltPlaybackProvider.AppId), cancel.Token); throw new Exception("Wrong mode accepted"); }
   catch (OperationCanceledException) { Check(opened == 1 && sent == 0); }
  }
  handler.Mode = "youtube"; handler.Ready = false; opened = sent = 0;
  using (var cancel = new CancellationTokenSource(500)) {
   try { await launcher.Play("youtube", () => { opened++; return Task.CompletedTask; }, () => { sent++; return Task.CompletedTask; }, _ => Task.FromResult(CobaltPlaybackProvider.AppId), cancel.Token); throw new Exception("Loading document accepted"); }
   catch (OperationCanceledException) { Check(opened == 1 && sent == 0); }
  }
  handler.Ready = true; opened = sent = 0;
  using (var cancel = new CancellationTokenSource(500)) {
   try { await launcher.Play("youtube", () => { opened++; return Task.CompletedTask; }, () => { sent++; return Task.CompletedTask; }, _ => Task.FromResult("other.app"), cancel.Token); throw new Exception("Other foreground accepted"); }
   catch (OperationCanceledException) { Check(opened == 1 && sent == 0); }
  }
  Console.WriteLine($"{checks} Cobalt launch handoff checks passed");
 }
}
