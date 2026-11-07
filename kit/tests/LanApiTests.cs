// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

sealed class FakeBackend : ILanBackend
{
    public bool Paused { get; set; }
    public long UptimeSeconds { get { return 42; } }
    public int Port = 2, SourceId = 14;          // current selection
    public int ChangeToPortAfterPolls = -1;      // selection follows SelectPort after N polls (-1 = never)
    public int Polls, SelectResult = 0, Volume = 30, Mute = 0;
    public bool UiFails, NativeFails, SetFails;
    public int Selected = -1, Delayed;
    public List<LanSource> Inputs = new List<LanSource> {
        new LanSource { Id = 13, Name = "HDMI 1", Connected = false },
        new LanSource { Id = 14, Name = "Cable Box", Connected = true },
        new LanSource { Id = 15, Name = "HW-Q800B", Connected = true },
        new LanSource { Id = 16, Name = "PC", Connected = false } };
    public int SetVolumeCalls = -1, SetMuteCalls = -1;
    public LanSelection ReadSelection()
    {
        Polls++;
        if (Selected > 0 && ChangeToPortAfterPolls >= 0 && Polls > ChangeToPortAfterPolls) { Port = Selected; SourceId = -1; }
        return new LanSelection { Port = Port, SourceId = SourceId };
    }
    public int NativeReads;
    public LanNative ReadNative(int selectedPort)
    {
        NativeReads++;
        if (NativeFails) throw new TimeoutException();
        return new LanNative { Inputs = Inputs, Volume = Volume, Mute = Mute };
    }
    public int SelectPort(int port) { if (UiFails) throw new TimeoutException(); Selected = port; return SelectResult; }
    public bool SetVolume(int level) { if (UiFails) throw new TimeoutException(); SetVolumeCalls = level; if (SetFails) return false; Volume = level; return true; }
    public bool SetMute(bool mute) { if (UiFails) throw new TimeoutException(); SetMuteCalls = mute ? 1 : 0; if (SetFails) return false; Mute = mute ? 1 : 0; return true; }
    public void Delay(int ms) { Delayed += ms; }
}

static class LanApiTests
{
    const string Token = "0123456789abcdef0123";
    static int checks;
    static void Is(bool cond, string what) { checks++; if (!cond) throw new Exception("FAILED: " + what); }
    static void Eq<T>(T expected, T actual, string what) { checks++; if (!Equals(expected, actual)) throw new Exception("FAILED: " + what + ": expected " + expected + ", got " + actual); }

    static LanResponse Call(FakeBackend b, string method, string path, string body = null, string token = Token, string auth = "default")
    {
        var head = method + " " + path + " HTTP/1.1\r\nHost: x\r\n" + (body != null ? "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n" : "");
        if (auth == "default") auth = "Bearer " + Token;
        if (auth != null) head += "Authorization: " + auth + "\r\n";
        LanRequest rq; LanResponse bad;
        Is(LanApiCore.TryParseHead(head, out rq, out bad), "head parses: " + head);
        return LanApiCore.Handle(rq, token, body == null ? null : Encoding.UTF8.GetBytes(body), b);
    }
    static JsonElement J(LanResponse r) { return JsonDocument.Parse(r.Json).RootElement; }

    public static int Main()
    {
        Auth(); Health(); State(); Hdmi(); Volume(); Mute(); Routing(); Parsing(); FreshReads(); Names(); Limiter();
        Console.WriteLine("LanApiTests passed: " + checks + " checks");
        return 0;
    }

    static void Auth()
    {
        var b = new FakeBackend();
        var r = Call(b, "GET", "/health", auth: null);
        Eq(401, r.Code, "missing auth"); Eq("{\"ok\":false,\"error\":\"unauthorized\"}", r.Json, "401 body");
        Eq(401, Call(b, "GET", "/health", auth: "Bearer wrongwrongwrongwrong").Code, "wrong token");
        Eq(401, Call(b, "GET", "/health", auth: "Basic " + Token).Code, "wrong scheme");
        Eq(401, Call(b, "GET", "/health", token: null).Code, "no token configured");
        Eq(401, Call(b, "POST", "/volume", "{\"level\":5}", auth: null).Code, "unauthorized before body handling");
        Eq(200, Call(b, "GET", "/health").Code, "right token");
        Eq(200, Call(b, "GET", "/health", auth: "bearer " + Token).Code, "scheme case-insensitive");
        var big = new LanRequest { Method = "POST", Path = "/volume", ContentLength = 1025, Authorization = "Bearer " + Token };
        Eq(413, LanApiCore.Handle(big, Token, null, b).Code, "oversize body");
        big.Authorization = null;
        Eq(401, LanApiCore.Handle(big, Token, null, b).Code, "oversize unauthenticated gets 401");
        var ok = new LanRequest { Method = "POST", Path = "/volume", ContentLength = 1024, Authorization = "Bearer " + Token };
        Is(LanApiCore.Handle(ok, Token, Encoding.UTF8.GetBytes("{\"level\":1}"), b).Code != 413, "1024 allowed");
        Is(LanApiCore.SameBytes("abc", "abc") && !LanApiCore.SameBytes("abc", "abd") && !LanApiCore.SameBytes("abc", "abcd"), "SameBytes");
    }

    static void Health()
    {
        var b = new FakeBackend { Paused = true };
        var e = J(Call(b, "GET", "/health"));
        Is(e.GetProperty("ok").GetBoolean(), "health ok");
        Eq("quiet-home", e.GetProperty("app").GetString(), "app");
        Eq(1, e.GetProperty("api_version").GetInt32(), "api_version");
        Eq(42, e.GetProperty("uptime_s").GetInt32(), "uptime");
        Is(e.GetProperty("paused").GetBoolean(), "paused");
    }

    static void State()
    {
        var b = new FakeBackend();
        var r = Call(b, "GET", "/state"); Eq(200, r.Code, "state 200");
        var e = J(r);
        Is(e.GetProperty("ok").GetBoolean(), "ok"); Eq(1, e.GetProperty("api_version").GetInt32(), "ver");
        Eq(2, e.GetProperty("selected").GetProperty("port").GetInt32(), "selected port");
        Eq(14, e.GetProperty("selected").GetProperty("id").GetInt32(), "selected id");
        Eq("Cable Box", e.GetProperty("selected").GetProperty("name").GetString(), "selected name");
        Is(e.GetProperty("selected").GetProperty("connected").GetBoolean(), "selected connected");
        Eq(14, e.GetProperty("signal").GetProperty("source_id").GetInt32(), "signal id");
        Is(e.GetProperty("signal").GetProperty("active").GetBoolean(), "signal active");
        Eq(4, e.GetProperty("inputs").GetArrayLength(), "four inputs");
        Eq("PC", e.GetProperty("inputs")[3].GetProperty("name").GetString(), "input 4 name");
        Eq(16, e.GetProperty("inputs")[3].GetProperty("id").GetInt32(), "input 4 id");
        Eq(30, e.GetProperty("volume").GetProperty("level").GetInt32(), "level");
        Is(!e.GetProperty("volume").GetProperty("mute").GetBoolean(), "mute false");

        // HDMI 4 selected, no signal: source_type is -1
        b = new FakeBackend { Port = 4, SourceId = -1 };
        e = J(Call(b, "GET", "/state"));
        Eq(4, e.GetProperty("selected").GetProperty("port").GetInt32(), "selected 4 without signal");
        Is(!e.GetProperty("signal").GetProperty("active").GetBoolean(), "inactive");
        Eq(-1, e.GetProperty("signal").GetProperty("source_id").GetInt32(), "-1");

        // Nothing selected, native batch down: defaults and nulls
        b = new FakeBackend { Port = 0, SourceId = -1, NativeFails = true };
        e = J(Call(b, "GET", "/state"));
        Eq(JsonValueKind.Null, e.GetProperty("selected").ValueKind, "selected null");
        Eq(JsonValueKind.Null, e.GetProperty("volume").ValueKind, "volume null");
        Eq("HDMI 3", e.GetProperty("inputs")[2].GetProperty("name").GetString(), "default name");
        Is(!e.GetProperty("signal").GetProperty("active").GetBoolean(), "inactive when none");
        Eq(405, Call(b, "POST", "/state").Code, "state POST 405");
    }

    static void Hdmi()
    {
        var b = new FakeBackend { Port = 2, SourceId = 14, ChangeToPortAfterPolls = 0 };
        var r = Call(b, "POST", "/hdmi/4"); Eq(200, r.Code, "hdmi 4 with no signal");
        var e = J(r);
        Is(e.GetProperty("ok").GetBoolean(), "ok");
        Eq(4, e.GetProperty("selected").GetProperty("port").GetInt32(), "selected 4");
        Eq("PC", e.GetProperty("selected").GetProperty("name").GetString(), "name PC");
        Is(!e.GetProperty("signal").GetProperty("active").GetBoolean(), "signal.active false");
        Eq(-1, e.GetProperty("signal").GetProperty("source_id").GetInt32(), "signal -1");
        Eq(4, b.Selected, "selected via backend");

        b = new FakeBackend { Port = 3, SourceId = 15 };
        r = Call(b, "POST", "/hdmi/3"); Eq(200, r.Code, "already selected is immediate"); Eq(0, b.Delayed, "no waiting");

        b = new FakeBackend { Port = 2, SourceId = 14, ChangeToPortAfterPolls = 3 };
        Eq(200, Call(b, "POST", "/hdmi/1").Code, "confirms after a few polls"); Is(b.Delayed > 0, "delayed");

        b = new FakeBackend { Port = 2, SourceId = 14 };
        r = Call(b, "POST", "/hdmi/1"); Eq(504, r.Code, "never changes");
        e = J(r);
        Is(!e.GetProperty("ok").GetBoolean(), "not ok"); Eq("not confirmed", e.GetProperty("error").GetString(), "error text");
        Eq(2, e.GetProperty("selected").GetProperty("port").GetInt32(), "reports current selection");
        Eq(14, e.GetProperty("signal").GetProperty("source_id").GetInt32(), "reports signal");
        Is(b.Delayed >= 6000, "waited about 6 s");

        Eq(502, Call(new FakeBackend { SelectResult = -1 }, "POST", "/hdmi/2").Code, "connect_source fails");
        Eq(503, Call(new FakeBackend { UiFails = true }, "POST", "/hdmi/2").Code, "ui fails");
        foreach (var bad in new[] { "/hdmi/0", "/hdmi/5", "/hdmi/", "/hdmi/10", "/hdmi/x" })
            Eq(400, Call(new FakeBackend(), "POST", bad).Code, "range " + bad);
        Eq(405, Call(new FakeBackend(), "GET", "/hdmi/1").Code, "GET hdmi 405");
    }

    static void Volume()
    {
        var b = new FakeBackend();
        var r = Call(b, "POST", "/volume", "{\"level\":55}"); Eq(200, r.Code, "volume");
        var e = J(r);
        Eq(55, e.GetProperty("volume").GetProperty("level").GetInt32(), "re-read level");
        Is(!e.GetProperty("volume").GetProperty("mute").GetBoolean(), "mute in reply");
        Eq(55, b.SetVolumeCalls, "set called");
        Call(b, "POST", "/volume", "{\"level\":150}"); Eq(100, b.SetVolumeCalls, "clamp high");
        Call(b, "POST", "/volume", "{\"level\":-5}"); Eq(0, b.SetVolumeCalls, "clamp low");
        Call(b, "POST", "/volume", "{\"level\":1e9}"); Eq(100, b.SetVolumeCalls, "clamp huge");
        Call(b, "POST", "/volume", "{\"level\":40.0}"); Eq(40, b.SetVolumeCalls, "integral float ok");
        foreach (var bad in new[] { "{}", "{\"level\":\"5\"}", "{\"level\":null}", "{\"level\":50.5}", "[1]", "5", "{\"mute\":true}" })
            Eq(400, Call(b, "POST", "/volume", bad).Code, "bad volume " + bad);
        Eq(400, Call(b, "POST", "/volume").Code, "no body");
        Eq(405, Call(b, "GET", "/volume").Code, "GET 405");
        Eq(502, Call(new FakeBackend { SetFails = true }, "POST", "/volume", "{\"level\":5}").Code, "set fails");
        Eq(503, Call(new FakeBackend { UiFails = true }, "POST", "/volume", "{\"level\":5}").Code, "ui fails");
        Eq(502, Call(new FakeBackend { NativeFails = true }, "POST", "/volume", "{\"level\":5}").Code, "re-read fails");
    }

    static void Mute()
    {
        var b = new FakeBackend();
        var r = Call(b, "POST", "/mute", "{\"mute\":true}"); Eq(200, r.Code, "mute");
        Is(J(r).GetProperty("volume").GetProperty("mute").GetBoolean(), "muted"); Eq(1, b.SetMuteCalls, "mute set");
        r = Call(b, "POST", "/mute", "{\"mute\":false}");
        Is(!J(r).GetProperty("volume").GetProperty("mute").GetBoolean(), "unmuted"); Eq(0, b.SetMuteCalls, "unmute set");
        Eq(30, J(r).GetProperty("volume").GetProperty("level").GetInt32(), "level present");
        foreach (var bad in new[] { "{}", "{\"mute\":1}", "{\"mute\":\"true\"}", "{\"mute\":null}", "true" })
            Eq(400, Call(b, "POST", "/mute", bad).Code, "bad mute " + bad);
        Eq(405, Call(b, "GET", "/mute").Code, "GET 405");
        Eq(502, Call(new FakeBackend { SetFails = true }, "POST", "/mute", "{\"mute\":true}").Code, "set fails");
    }

    static void Routing()
    {
        var b = new FakeBackend();
        Eq(404, Call(b, "GET", "/nope").Code, "404");
        Eq(404, Call(b, "GET", "/").Code, "404 root");
        Eq(405, Call(b, "POST", "/health").Code, "health POST");
        Eq(200, Call(b, "GET", "/health?x=1").Code, "query ignored");
        var r = Call(b, "POST", "/volume", "{\"level\":"); Eq(400, r.Code, "malformed json");
        Eq("malformed json", J(r).GetProperty("error").GetString(), "malformed text");
        Eq(400, Call(b, "POST", "/mute", "{nope}").Code, "malformed mute");
        LanRequest rq; LanResponse bad;
        Is(!LanApiCore.TryParseHead("GARBAGE", out rq, out bad) && bad.Code == 400, "bad request line");
        Is(!LanApiCore.TryParseHead("POST /x HTTP/1.1\r\nContent-Length: abc", out rq, out bad) && bad.Code == 400, "bad content-length");
        Is(!LanApiCore.TryParseHead("POST /x HTTP/1.1\r\nContent-Length: -1", out rq, out bad) && bad.Code == 400, "negative content-length");
        Is(!LanApiCore.TryParseHead("POST /x HTTP/1.1\r\nTransfer-Encoding: chunked", out rq, out bad) && bad.Code == 400, "chunked rejected");
        Is(LanApiCore.TryParseHead("GET /state HTTP/1.1\r\nauthorization: Bearer abc\r\nCONTENT-LENGTH: 7", out rq, out bad), "parses");
        Eq("Bearer abc", rq.Authorization, "auth header case-insensitive"); Eq(7, rq.ContentLength, "content-length"); Eq("/state", rq.Path, "path");
        var wire = Encoding.ASCII.GetString(LanApiCore.FormatHttp(401, "{}"));
        Is(wire.StartsWith("HTTP/1.1 401 Unauthorized\r\n") && wire.Contains("Connection: close\r\n") && wire.Contains("Content-Length: 2\r\n") && wire.EndsWith("\r\n\r\n{}"), "wire 401");
        Is(Encoding.ASCII.GetString(LanApiCore.FormatHttp(504, "{}")).StartsWith("HTTP/1.1 504 Gateway Timeout"), "wire 504");
    }

    static void Parsing()
    {
        Eq(4, LanApiCore.ParseCurrentSourcePort("{\"uuid\":\"SOURCE_TYPE_HDMI4\",\"x\":1}"), "uuid hdmi4");
        Eq(1, LanApiCore.ParseCurrentSourcePort("{\"a\":2, \"uuid\" : \"SOURCE_TYPE_HDMI1\"}"), "uuid spacing");
        Eq(3, LanApiCore.ParseCurrentSourcePort("SOURCE_TYPE_HDMI3"), "bare");
        Eq(0, LanApiCore.ParseCurrentSourcePort("{\"uuid\":\"SOURCE_TYPE_TV\"}"), "non hdmi");
        Eq(0, LanApiCore.ParseCurrentSourcePort("{\"uuid\":\"SOURCE_TYPE_HDMI5\"}"), "hdmi5 rejected");
        Eq(0, LanApiCore.ParseCurrentSourcePort(null), "null");
        Eq(0, LanApiCore.ParseCurrentSourcePort(""), "empty");
        var s = LanApiCore.MakeSelection("{\"uuid\":\"SOURCE_TYPE_HDMI4\"}", -1);
        Eq(4, s.Port, "selected despite -1"); Eq(-1, s.SourceId, "source id kept");
        s = LanApiCore.MakeSelection(null, 15); Eq(3, s.Port, "fallback to source_type");
        s = LanApiCore.MakeSelection(null, -1); Eq(0, s.Port, "no fallback available");
        s = LanApiCore.MakeSelection("garbage", 20); Eq(0, s.Port, "out of range fallback");

        var list = LanApiCore.ParseSourceList("[{\"sourceType\":14,\"editName\":\"Cable%20Box\",\"detectedName\":\"x\",\"connectionState\":true},"
            + "{\"sourceType\":16,\"editName\":\"\",\"detectedName\":\"My%2BPC%21\",\"connectionState\":false},{\"sourceType\":13}]");
        Eq(3, list.Count, "three sources");
        Eq("Cable Box", list[0].Name, "editName decoded"); Is(list[0].Connected, "connected");
        Eq("My+PC!", list[1].Name, "detectedName decoded"); Is(!list[1].Connected, "not connected");
        Eq("", list[2].Name, "missing names"); Eq(13, list[2].Id, "id");
        bool threw = false; try { LanApiCore.ParseSourceList("{not json"); } catch (JsonException) { threw = true; }
        Is(threw, "bad source json throws");
    }

    // get_source_list() returns a per-call heap copy that LanApi frees, so nothing is rationed:
    // every /state reads the list fresh, with no timer, cache or refresh request in between.
    static void FreshReads()
    {
        var b = new FakeBackend();
        for (int i = 1; i <= 50; i++) { Call(b, "GET", "/state"); Eq(i, b.NativeReads, "state " + i + " reads the source list"); }
        b.Inputs = new List<LanSource> { new LanSource { Id = 14, Name = "Renamed", Connected = true } };
        var e = J(Call(b, "GET", "/state"));
        Eq("Renamed", e.GetProperty("inputs")[1].GetProperty("name").GetString(), "rename visible on the very next state");
        b = new FakeBackend { Port = 2, SourceId = 14, ChangeToPortAfterPolls = 0 };
        Call(b, "POST", "/hdmi/4");
        Is(b.NativeReads >= 1, "hdmi reply reads the list");
    }

    static void Names()
    {
        Func<int, string> json = port => port == 2 ? "Streaming" : port == 4 ? "Gaming" : null;
        // Samsung names win: editName, else detectedName (URL-decoded by the parser)
        var list = LanApiCore.ParseSourceList("[{\"sourceType\":14,\"editName\":\"Cable%20Box\",\"detectedName\":\"PC\"},"
            + "{\"sourceType\":15,\"editName\":\"\",\"detectedName\":\"HW-Q800B\"},{\"sourceType\":16,\"editName\":\"PC\",\"detectedName\":\"PC\"},"
            + "{\"sourceType\":13,\"editName\":\"\",\"detectedName\":\"\"},{\"sourceType\":5,\"editName\":\"Antenna\"}]");
        var named = LanApiCore.WithFallbackNames(list, json);
        Eq(4, named.Count, "four ports only");
        Eq("HDMI 1", LanApiCore.NameOf(named, 1), "blank Samsung name, no inputs.json entry: HDMI n");
        Eq("Cable Box", LanApiCore.NameOf(named, 2), "editName beats inputs.json");
        Eq("HW-Q800B", LanApiCore.NameOf(named, 3), "detectedName when editName empty");
        Eq("PC", LanApiCore.NameOf(named, 4), "editName beats inputs.json for port 4");
        Eq(13, named[0].Id, "ids in port order"); Eq(16, named[3].Id, "id 16");
        // After the rename, every input shows the agreed names
        var renamed = LanApiCore.WithFallbackNames(LanApiCore.ParseSourceList("[{\"sourceType\":13,\"editName\":\"HDMI 1\"},{\"sourceType\":14,\"editName\":\"Streaming\"},"
            + "{\"sourceType\":15,\"editName\":\"HW-Q800B\"},{\"sourceType\":16,\"editName\":\"Gaming\"}]"), json);
        Eq("HDMI 1|Streaming|HW-Q800B|Gaming", string.Join("|", renamed.ConvertAll(s => s.Name)), "agreed names");
        // Fallbacks when Samsung's list is missing
        var none = LanApiCore.WithFallbackNames(null, json);
        Eq("HDMI 1|Streaming|HDMI 3|Gaming", string.Join("|", none.ConvertAll(s => s.Name)), "inputs.json then HDMI n");
        Eq("HDMI 1|HDMI 2|HDMI 3|HDMI 4", string.Join("|", LanApiCore.WithFallbackNames(null, null).ConvertAll(s => s.Name)), "no fallback at all");
        Eq("HDMI 2", string.Join("|", LanApiCore.WithFallbackNames(null, p => { throw new Exception(); }).ConvertAll(s => s.Name)).Split('|')[1], "throwing fallback contained");
        Eq("HDMI 3", LanApiCore.NameOf(null, 3), "NameOf with no list");
        Is(named[1].Connected == false && LanApiCore.WithFallbackNames(LanApiCore.ParseSourceList("[{\"sourceType\":14,\"connectionState\":true}]"), null)[1].Connected, "connected flag kept");
        Eq("Spaced", LanApiCore.NameOf(LanApiCore.WithFallbackNames(LanApiCore.ParseSourceList("[{\"sourceType\":13,\"editName\":\"%20Spaced \"}]"), null), 1), "trimmed");
    }

    static void Limiter()
    {
        var l = new LanRateLimiter(4);
        for (int i = 0; i < 4; i++) Is(l.Allow(1000 + i), "allowed " + i);
        Is(!l.Allow(1500), "5th in window refused");
        Is(!l.Allow(1999), "still refused");
        Is(l.Allow(2000), "new window");
    }
}
