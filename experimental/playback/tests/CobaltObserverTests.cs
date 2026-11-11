// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text;
using QuietThings.WebBridge;

int checks = 0;
void Check(bool ok) { if (!ok) throw new Exception("check " + checks); checks++; }
void Reject(Action action) { try { action(); } catch { checks++; return; } throw new Exception("expected rejection"); }
async Task RejectAsync(Func<Task> action) { try { await action(); } catch { checks++; return; } throw new Exception("expected rejection"); }
JsonElement E(object o) => JsonSerializer.SerializeToElement(o);
var now = DateTimeOffset.UtcNow; bool alive = true;
var observer = new CobaltObserver(_ => alive, () => now);
string doc = new('a', 32);
JsonElement Report(long seq, string state = "playing", object ack = null, string instance = "1", string document = null) => E(new {
    document = document ?? doc, sequence = seq, acknowledgement = ack,
    probe = new { backend = "html5", instance, state, title = "Test", position_seconds = 10, duration_seconds = 100, live = false, commands = new[] { "play", "pause", "seek", "stop" } } });
Reject(() => observer.Read());
observer.Report(Report(1), "process");
Check(observer.MatchesIdentity("process"));
Check(!observer.MatchesIdentity("other"));
var initial = E(observer.Read()); string lease = initial.GetProperty("lease").GetString();
Check(initial.GetProperty("probe").GetProperty("commands").GetArrayLength() == 3);
Reject(() => observer.Report(Report(1), "process"));
var pause = observer.Command(E(new { action = "pause", lease }), CancellationToken.None);
Check(!pause.IsCompleted);
var response = E(observer.Report(Report(2), "process"));
string id = response.GetProperty("command").GetProperty("id").GetString();
Check(!pause.IsCompleted);
observer.Report(Report(3, "paused", new { id, ok = true }), "process");
Check(E(await pause).GetProperty("ok").GetBoolean());
var play = observer.Command(E(new { action = "play", lease }), CancellationToken.None);
response = E(observer.Report(Report(4, "paused"), "process")); id = response.GetProperty("command").GetProperty("id").GetString();
observer.Report(Report(5, "paused", new { id, ok = true }), "process");
await RejectAsync(async () => await play); // A successful API call without actual state is not success.
await RejectAsync(async () => await observer.Command(E(new { action = "seek", position = 101, lease }), CancellationToken.None));
await RejectAsync(async () => await observer.Command(E(new { action = "stop", lease }), CancellationToken.None));
observer.Report(Report(6, instance: "2"), "process");
await RejectAsync(async () => await observer.Command(E(new { action = "pause", lease }), CancellationToken.None));
observer.Report(Report(1, document: new string('b', 32)), "process");
Reject(() => observer.Report(Report(7), "process")); // Retired page cannot reclaim current ownership.
alive = false; Reject(() => observer.Read()); Check(!observer.MatchesIdentity("process")); alive = true;
now = now.AddSeconds(4); Reject(() => observer.Read()); Check(!observer.MatchesIdentity("process"));
for (int i = 0; i < 300; i++) observer.Report(Report(1, document: i.ToString("x32")), "process");
Check(E(observer.Read()).GetProperty("ok").GetBoolean());
Reject(() => observer.Report(Report(2, document: 298.ToString("x32")), "process"));
byte[] Args(string text) => Encoding.UTF8.GetBytes("native\0USERDATA\0" + Convert.ToBase64String(Encoding.UTF8.GetBytes("header\0" + text + "\0")) + "\0");
Check(CobaltProcessAttestation.ModeFromCommandLine(Args("--proxy=http://127.0.0.2:8102 --use_eden")) == "youtube");
Check(CobaltProcessAttestation.ModeFromCommandLine(Args("--proxy=http://127.0.0.2:8101 --use_eden")) == "tizentube");
Check(CobaltProcessAttestation.ModeFromCommandLine(Args("--proxy=http://127.0.0.2:81020")) == null);
Check(CobaltProcessAttestation.ModeFromCommandLine(Args("--proxy=http://127.0.0.1:8102")) == null);
Check(CobaltProcessAttestation.ModeFromCommandLine(Encoding.UTF8.GetBytes("native\0USERDATA\0bad-base64\0")) == null);
Check(CobaltProcessAttestation.ModeFromCommandLine(Args("--proxy=http://127.0.0.2:8101 --proxy=http://127.0.0.2:8102")) == null);
byte[] preload = Encoding.UTF8.GetBytes("/usr/apps/com.samsung.tv.cobalt/bin/cobalt_launcher\0\0");
Check(CobaltProcessAttestation.IsPreloadedCobalt("/usr/bin/app-defined-loader", "User::Pkg::com.samsung.tv.cobalt\n", preload));
Check(!CobaltProcessAttestation.IsPreloadedCobalt("/usr/bin/other", "User::Pkg::com.samsung.tv.cobalt", preload));
Check(!CobaltProcessAttestation.IsPreloadedCobalt("/usr/bin/app-defined-loader", "User::Pkg::other", preload));
Check(!CobaltProcessAttestation.IsPreloadedCobalt("/usr/bin/app-defined-loader", "User::Pkg::com.samsung.tv.cobalt", Encoding.UTF8.GetBytes("/usr/bin/other\0")));
Check(!CobaltProcessAttestation.IsPreloadedCobalt("/usr/bin/app-defined-loader", "User::Pkg::com.samsung.tv.cobalt", new byte[65537]));
Check(CobaltProcessAttestation.HasNativeMapping(new[] { "b52a5000-b540d000 r-xp 00000000 b3:19 33836 /usr/apps/com.samsung.tv.cobalt/bin/cobalt_launcher" }));
Check(!CobaltProcessAttestation.HasNativeMapping(new[] { "b52a5000-b540d000 rw-p 00000000 b3:19 33836 /usr/apps/com.samsung.tv.cobalt/bin/cobalt_launcher" }));
Check(!CobaltProcessAttestation.HasNativeMapping(new[] { "b52a5000-b540d000 r-xp 00000000 b3:19 33836 /usr/apps/other/bin/cobalt_launcher" }));
Check(!CobaltProcessAttestation.HasNativeMapping(new[] { "b52a5000-b540d000 r-xp 00000000 b3:19 0 /usr/apps/com.samsung.tv.cobalt/bin/cobalt_launcher" }));
Console.WriteLine($"{checks} Cobalt observer checks passed");
