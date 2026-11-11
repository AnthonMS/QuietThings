// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuietThings.WebBridge;

sealed class FakeInspector : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    public string Url = "https://example.tv.twitch.tv/app-shell", Target = "ABC123", State = "playing", Instance = "1", Document = "document-one", App = TwitchInspector.AppId;
    public bool Duplicate, Oversize, RejectCommand;
    public int Connections, Commands;
    public List<string> Expressions = new();
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public FakeInspector() { listener.Start(); _ = Accept(); }
    async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var c = await listener.AcceptTcpClientAsync(stop.Token); _ = Serve(c); } }
        catch (OperationCanceledException) { }
    }
    async Task Serve(TcpClient client)
    {
        using (client) try
        {
            var stream = client.GetStream(); var bytes = new List<byte>(); var one = new byte[1];
            while (bytes.Count < 8192)
            {
                if (await stream.ReadAsync(one.AsMemory(), stop.Token) == 0) return;
                bytes.Add(one[0]); int n = bytes.Count;
                if (n >= 4 && Encoding.ASCII.GetString(bytes.Skip(n - 4).ToArray()) == "\r\n\r\n") break;
            }
            string header = Encoding.ASCII.GetString(bytes.ToArray());
            if (header.StartsWith("GET /json/list "))
            {
                var page = new { type = "page", id = Target, url = Url, webSocketDebuggerUrl = "ws://untrusted.invalid/unused" };
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(Duplicate ? new[] { page, page } : new[] { page });
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(body); return;
            }
            var key = header.Split("\r\n").First(x => x.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"));
            Connections++;
            using var ws = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
            while (!stop.IsCancellationRequested)
            {
                using var data = new MemoryStream(); var buffer = new byte[2048]; ValueWebSocketReceiveResult frame;
                do { frame = await ws.ReceiveAsync(buffer.AsMemory(), stop.Token); if (frame.MessageType != WebSocketMessageType.Text) return; data.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
                using var request = JsonDocument.Parse(data.ToArray()); var root = request.RootElement;
                string expression = root.GetProperty("params").GetProperty("expression").GetString(); Expressions.Add(expression);
                bool command = expression.Contains(".command(");
                object response;
                if (command && RejectCommand) response = new { id = root.GetProperty("id").GetInt32(), result = new { exceptionDetails = new { text = "Private exception must not escape" } } };
                else
                {
                    object value;
                    if (command) { Commands++; State = expression.Contains("\"pause\"") ? "paused" : "playing"; value = true; }
                    else value = new { app_id = App, document_instance = Document, probe = new { state = State, instance = Instance, title = "Example stream", position_seconds = 12.5, live = true, commands = new[] { "play", "pause", "seek" }, private_source = "https://private.invalid/signed-token" } };
                    response = new { id = root.GetProperty("id").GetInt32(), result = new { result = new { type = "object", value } } };
                }
                byte[] reply = Oversize ? Encoding.UTF8.GetBytes(new string('x', 20000)) : JsonSerializer.SerializeToUtf8Bytes(response);
                await ws.SendAsync(reply.AsMemory(), WebSocketMessageType.Text, true, stop.Token);
            }
        }
        catch (Exception) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

class WebBridgeTests
{
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    static async Task Denied(Func<Task> action, string name)
    { bool denied = false; try { await action(); } catch (Exception) { denied = true; } Check(denied, name); }
    static async Task Main()
    {
        using var fixture = new FakeInspector();
        using var source = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("media-probe.js"));
        string probe = await source.ReadToEndAsync();
        var inspector = new TwitchInspector(probe);
        var endpoint = new InspectorEndpoint { Port = fixture.Port, Nonce = "local-test-identity" };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = Json(await inspector.Read(endpoint, null, null, limit.Token));
        Check(first.GetProperty("probe").GetProperty("state").GetString() == "playing", "real HTTP and WebSocket transport");
        Check(!first.ToString().Contains("private.invalid") && !first.ToString().Contains("untrusted.invalid"), "only allowlisted fields exported");
        Check(first.GetProperty("probe").GetProperty("commands").GetArrayLength() == 2, "only supported live controls exported");
        string lease = first.GetProperty("lease").GetString();
        var paused = Json(await inspector.Read(endpoint, "pause", lease, limit.Token));
        Check(paused.GetProperty("probe").GetProperty("state").GetString() == "paused" && fixture.Commands == 1, "command followed by actual read");
        fixture.Instance = "2";
        await Denied(async () => await inspector.Read(endpoint, "play", lease, limit.Token), "stale player lease rejected");
        Check(fixture.Commands == 1, "stale command never issued");
        fixture.Instance = "1"; fixture.Document = "document-two";
        await Denied(async () => await inspector.Read(endpoint, "play", lease, limit.Token), "page reload under same target invalidates lease");
        Check(fixture.Commands == 1, "old document cannot command replacement page");
        int connections = fixture.Connections;
        fixture.Url = "https://tv.twitch.tv.attacker.invalid/";
        await Denied(async () => await inspector.Read(endpoint, null, null, limit.Token), "lookalike Twitch origin rejected");
        Check(fixture.Connections == connections, "untrusted origin rejected before socket");
        fixture.Url = "https://example.tv.twitch.tv/app-shell"; fixture.Duplicate = true;
        await Denied(async () => await inspector.Read(endpoint, null, null, limit.Token), "ambiguous page rejected");
        fixture.Duplicate = false; fixture.Target = "../escape";
        await Denied(async () => await inspector.Read(endpoint, null, null, limit.Token), "invalid target cannot alter socket URL");
        fixture.Target = "ABC123"; fixture.App = "other.app";
        await Denied(async () => await inspector.Read(endpoint, null, null, limit.Token), "returned app ownership rechecked");
        fixture.App = TwitchInspector.AppId; fixture.Oversize = true;
        await Denied(async () => await inspector.Read(endpoint, null, null, limit.Token), "oversized socket frame bounded");
        fixture.Oversize = false;
        var current = Json(await inspector.Read(endpoint, null, null, limit.Token));
        fixture.RejectCommand = true;
        await Denied(async () => await inspector.Read(endpoint, "play", current.GetProperty("lease").GetString(), limit.Token), "player promise failure not reported as accepted");
        using var kickSource = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("kick-context.js"));
        var kick = new TwitchInspector(probe, "CrKckTV001.KickTV", await kickSource.ReadToEndAsync());
        fixture.RejectCommand = false; fixture.Url = "file:///index.html"; fixture.App = "CrKckTV001.KickTV";
        var kickSample = Json(await kick.Read(endpoint, null, null, limit.Token));
        Check(kickSample.GetProperty("app_id").GetString() == fixture.App, "KickTV has its own exact page owner");
        await Denied(async () => await kick.Read(endpoint, "pause", lease, limit.Token), "Twitch lease cannot command KickTV");
        var kickPaused = Json(await kick.Read(endpoint, "pause", kickSample.GetProperty("lease").GetString(), limit.Token));
        Check(kickPaused.GetProperty("probe").GetProperty("state").GetString() == "paused", "KickTV command reads actual follow-up state");
        fixture.Url = "file:///other.html";
        await Denied(async () => await kick.Read(endpoint, null, null, limit.Token), "KickTV unexpected local page rejected");
        fixture.Url = "file:///index.html"; fixture.App = TwitchInspector.AppId;
        await Denied(async () => await kick.Read(endpoint, null, null, limit.Token), "KickTV cannot accept another application owner");
        // Parse the exact generated controller expressions with a real JS engine.
        var start = new ProcessStartInfo("node") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-e"); start.ArgumentList.Add("let s='';process.stdin.on('data',c=>s+=c);process.stdin.on('end',()=>{for(const e of JSON.parse(s))new Function('return '+e);});");
        using var node = Process.Start(start); await node.StandardInput.WriteAsync(JsonSerializer.Serialize(fixture.Expressions)); node.StandardInput.Close();
        string error = await node.StandardError.ReadToEndAsync(); await node.WaitForExitAsync();
        Check(node.ExitCode == 0, "generated app controller is valid JavaScript: " + error);
        Console.WriteLine("Web bridge tests passed: " + checks + " checks");
    }
}
