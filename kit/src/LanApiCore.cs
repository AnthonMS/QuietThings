// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Pure logic of the QuietThings API: request parsing, auth, routing and JSON shaping.
// No native calls and no sockets here, so it runs under the desktop test project.

public sealed class LanSource { public int Id; public string Name; public bool Connected; }

public sealed class LanSelection { public int Port; public int SourceId = -1; }   // Port 0 = unknown

public sealed class LanNative { public List<LanSource> Inputs; public int Volume = -1; public int Mute = -1; }

public interface ILanBackend
{
 bool Paused { get; }
 long UptimeSeconds { get; }
 LanSelection ReadSelection();          // vconf only; must not throw
 LanNative ReadNative(int selectedPort); // one UI-thread batch: source list (read fresh each time), volume, mute; may throw. -1 = port unknown to caller
 int SelectPort(int port);              // UI thread; connect_source result (negative = failure); may throw
 bool SetVolume(int level);             // UI thread; true on success; may throw
 bool SetMute(bool mute);               // UI thread; true on success; may throw
 void Delay(int ms);
}

public sealed class LanRequest { public string Method = ""; public string Path = ""; public int ContentLength; public string Authorization; }

public sealed class LanResponse
{
 public int Code; public string Json;
 public LanResponse(int code, string json) { Code = code; Json = json; }
}

public sealed class LanRateLimiter
{
 readonly int max; long windowStart = long.MinValue; int count;
 public LanRateLimiter(int maxPerSecond) { max = maxPerSecond; }
 public bool Allow(long nowMs)
 {
  if (windowStart == long.MinValue || nowMs - windowStart >= 1000 || nowMs < windowStart) { windowStart = nowMs; count = 0; }
  if (count >= max) return false;
  count++; return true;
 }
}

public static class LanApiCore
{
 public const int ApiVersion = 1, MaxBody = 1024, ConfirmMs = 6000, PollMs = 300;
 static readonly Regex Uuid = new Regex("\"uuid\"\\s*:\\s*\"SOURCE_TYPE_HDMI([1-4])\"", RegexOptions.CultureInvariant);
 static readonly Regex Bare = new Regex("^SOURCE_TYPE_HDMI([1-4])$", RegexOptions.CultureInvariant);

 // ---------- request parsing ----------

 // head = request line and headers, without the terminating blank line.
 public static bool TryParseHead(string head, out LanRequest req, out LanResponse error)
 {
  req = null; error = null;
  var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
  var parts = lines[0].Split(' ');
  if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.")) { error = Fail(400, "bad request"); return false; }
  var r = new LanRequest { Method = parts[0], Path = parts[1] };
  for (int i = 1; i < lines.Length; i++)
  {
   int k = lines[i].IndexOf(':'); if (k <= 0) continue;
   string name = lines[i].Substring(0, k).Trim(), val = lines[i].Substring(k + 1).Trim();
   if (name.Equals("authorization", StringComparison.OrdinalIgnoreCase)) r.Authorization = val;
   else if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
   {
    int n; if (!int.TryParse(val, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n)) { error = Fail(400, "bad content-length"); return false; }
    r.ContentLength = n;
   }
   else if (name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase)) { error = Fail(400, "transfer-encoding not supported"); return false; }
  }
  req = r; return true;
 }

 public static bool SameBytes(string a, string b)
 {
  var x = Encoding.UTF8.GetBytes(a); var y = Encoding.UTF8.GetBytes(b);
  int d = x.Length ^ y.Length;
  for (int i = 0; i < x.Length; i++) d |= x[i] ^ y[i % Math.Max(1, y.Length)];
  return d == 0;
 }

 public static bool IsAuthorized(string auth, string token)
 {
  return token != null && token.Length > 0 && auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && SameBytes(auth.Substring(7).Trim(), token);
 }

 // ---------- response shaping ----------

 public static string Json(Action<Utf8JsonWriter> fill)
 {
  using (var ms = new MemoryStream())
  {
   using (var w = new Utf8JsonWriter(ms)) { w.WriteStartObject(); fill(w); w.WriteEndObject(); }
   return Encoding.UTF8.GetString(ms.ToArray());
  }
 }

 public static LanResponse Fail(int code, string text)
 {
  return new LanResponse(code, Json(w => { w.WriteBoolean("ok", false); w.WriteString("error", text); }));
 }

 public static byte[] FormatHttp(int code, string json)
 {
  string reason = code == 200 ? "OK" : code == 400 ? "Bad Request" : code == 401 ? "Unauthorized" : code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed"
   : code == 408 ? "Request Timeout" : code == 413 ? "Payload Too Large" : code == 429 ? "Too Many Requests" : code == 431 ? "Request Header Fields Too Large"
   : code == 502 ? "Bad Gateway" : code == 503 ? "Service Unavailable" : code == 504 ? "Gateway Timeout" : "Error";
  var body = Encoding.UTF8.GetBytes(json);
  var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + reason + "\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" + (code == 401 ? "WWW-Authenticate: Bearer\r\n" : "") + "\r\n");
  var all = new byte[head.Length + body.Length];
  Buffer.BlockCopy(head, 0, all, 0, head.Length); Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
  return all;
 }

 // ---------- current_source and source list parsing ----------

 // current_source is the JSON-ish value of memory/eden/source/current_source; same reading as tools/qn90b_control.py.
 public static int ParseCurrentSourcePort(string value)
 {
  if (string.IsNullOrEmpty(value)) return 0;
  var m = Uuid.Match(value); if (m.Success) return m.Groups[1].Value[0] - '0';
  m = Bare.Match(value.Trim()); return m.Success ? m.Groups[1].Value[0] - '0' : 0;
 }

 // Port from current_source when readable, else from source_type (13-16), else 0.
 public static LanSelection MakeSelection(string currentSource, int sourceType)
 {
  int port = ParseCurrentSourcePort(currentSource);
  if (port == 0 && sourceType >= 13 && sourceType <= 16) port = sourceType - 12;
  return new LanSelection { Port = port, SourceId = sourceType };
 }

 public static List<LanSource> ParseSourceList(string raw)
 {
  var list = new List<LanSource>();
  using (var doc = JsonDocument.Parse(raw))
  {
   foreach (var e in doc.RootElement.EnumerateArray())
   {
    var s = new LanSource { Id = e.GetProperty("sourceType").GetInt32() };
    s.Name = Decode(e, "editName"); if (s.Name == "") s.Name = Decode(e, "detectedName");
    s.Connected = e.TryGetProperty("connectionState", out var cs) && cs.ValueKind == JsonValueKind.True;
    list.Add(s);
   }
  }
  return list;
 }

 // One entry per HDMI port 1-4 (ids 13-16), in port order. Name is Samsung's name (editName, else
 // detectedName), else fallback(port) (inputs.json), else "HDMI n". Used by both the launcher's cards
 // and the LAN API so they always show the same names.
 public static List<LanSource> WithFallbackNames(List<LanSource> list, Func<int, string> fallback)
 {
  var result = new List<LanSource>();
  for (int port = 1; port <= 4; port++)
  {
   LanSource src = null;
   if (list != null) foreach (var s in list) if (s.Id == 12 + port) { src = s; break; }
   string name = src != null && !string.IsNullOrWhiteSpace(src.Name) ? src.Name.Trim() : null;
   if (name == null && fallback != null) { string f = null; try { f = fallback(port); } catch { } if (!string.IsNullOrWhiteSpace(f)) name = f.Trim(); }
   if (name == null) name = "HDMI " + port;
   result.Add(new LanSource { Id = 12 + port, Name = name, Connected = src != null && src.Connected });
  }
  return result;
 }

 public static string NameOf(List<LanSource> named, int port)
 {
  if (named != null) foreach (var s in named) if (s.Id == 12 + port) return s.Name;
  return "HDMI " + port;
 }

 static string Decode(JsonElement e, string key)
 {
  try { if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return Uri.UnescapeDataString(v.GetString()); } catch { }
  return "";
 }

 // ---------- routing ----------

 public static LanResponse Handle(LanRequest rq, string token, byte[] body, ILanBackend b)
 {
  try { return Route(rq, token, body, b); }
  catch (Exception) { return Fail(503, "internal error"); }
 }

 static LanResponse Route(LanRequest rq, string token, byte[] body, ILanBackend b)
 {
  if (!IsAuthorized(rq.Authorization, token)) return Fail(401, "unauthorized");
  if (rq.ContentLength > MaxBody) return Fail(413, "body too large");
  string path = rq.Path; int q = path.IndexOf('?'); if (q >= 0) path = path.Substring(0, q);
  string m = rq.Method;
  if (path == "/health") return m != "GET" ? Fail(405, "method not allowed") : Health(b);
  if (path == "/state") return m != "GET" ? Fail(405, "method not allowed") : State(b);
  if (path.StartsWith("/hdmi/"))
  {
   if (m != "POST") return Fail(405, "method not allowed");
   string num = path.Substring(6);
   if (num.Length != 1 || num[0] < '1' || num[0] > '4') return Fail(400, "hdmi must be 1-4");
   return Hdmi(num[0] - '0', b);
  }
  if (path == "/volume") return m != "POST" ? Fail(405, "method not allowed") : Volume(body, b);
  if (path == "/mute") return m != "POST" ? Fail(405, "method not allowed") : Mute(body, b);
  return Fail(404, "not found");
 }

 static LanResponse Health(ILanBackend b)
 {
  return new LanResponse(200, Json(w => { w.WriteBoolean("ok", true); w.WriteString("app", "quiet-home"); w.WriteNumber("api_version", ApiVersion);
   w.WriteNumber("uptime_s", b.UptimeSeconds); w.WriteBoolean("paused", b.Paused); }));
 }

 static LanNative TryNative(ILanBackend b, int port) { try { return b.ReadNative(port); } catch (Exception) { return null; } }

 static LanSource Find(LanNative n, int port)
 {
  if (n != null && n.Inputs != null) foreach (var s in n.Inputs) if (s.Id == 12 + port) return s;
  return null;
 }

 static void WriteInput(Utf8JsonWriter w, int port, LanNative n)
 {
  var s = Find(n, port);
  w.WriteNumber("port", port); w.WriteNumber("id", 12 + port);
  w.WriteString("name", s != null && s.Name != "" ? s.Name : "HDMI " + port);
  w.WriteBoolean("connected", s != null && s.Connected);
 }

 static void WriteSelected(Utf8JsonWriter w, LanSelection sel, LanNative n)
 {
  w.WritePropertyName("selected");
  if (sel.Port < 1 || sel.Port > 4) { w.WriteNullValue(); return; }
  w.WriteStartObject(); WriteInput(w, sel.Port, n); w.WriteEndObject();
 }

 static void WriteSignal(Utf8JsonWriter w, LanSelection sel)
 {
  w.WritePropertyName("signal"); w.WriteStartObject();
  w.WriteNumber("source_id", sel.SourceId);
  w.WriteBoolean("active", sel.Port >= 1 && sel.Port <= 4 && sel.SourceId == 12 + sel.Port);
  w.WriteEndObject();
 }

 static void WriteVolume(Utf8JsonWriter w, LanNative n)
 {
  w.WritePropertyName("volume");
  if (n == null || n.Volume < 0 || n.Mute < 0) { w.WriteNullValue(); return; }
  w.WriteStartObject(); w.WriteNumber("level", n.Volume); w.WriteBoolean("mute", n.Mute == 1); w.WriteEndObject();
 }

 static LanResponse State(ILanBackend b)
 {
  var sel = b.ReadSelection() ?? new LanSelection();
  var n = TryNative(b, sel.Port);
  return new LanResponse(200, Json(w => {
   w.WriteBoolean("ok", true); w.WriteNumber("api_version", ApiVersion); w.WriteBoolean("paused", b.Paused);
   WriteSelected(w, sel, n); WriteSignal(w, sel);
   w.WritePropertyName("inputs"); w.WriteStartArray();
   for (int p = 1; p <= 4; p++) { w.WriteStartObject(); WriteInput(w, p, n); w.WriteEndObject(); }
   w.WriteEndArray();
   WriteVolume(w, n);
  }));
 }

 static LanResponse Hdmi(int port, ILanBackend b)
 {
  int r;
  try { r = b.SelectPort(port); }
  catch (Exception) { return Fail(503, "ui call failed"); }
  if (r < 0) return new LanResponse(502, Json(w => { w.WriteBoolean("ok", false); w.WriteString("error", "connect_source failed"); w.WriteNumber("connect_result", r); }));
  var sel = b.ReadSelection() ?? new LanSelection();
  int waited = 0;
  while (sel.Port != port && waited < ConfirmMs) { b.Delay(PollMs); waited += PollMs; sel = b.ReadSelection() ?? new LanSelection(); }
  bool ok = sel.Port == port;
  var n = TryNative(b, sel.Port);
  return new LanResponse(ok ? 200 : 504, Json(w => {
   w.WriteBoolean("ok", ok); if (!ok) w.WriteString("error", "not confirmed");
   WriteSelected(w, sel, n); WriteSignal(w, sel);
  }));
 }

 static bool ReadObject(byte[] body, out JsonDocument doc, out LanResponse error)
 {
  doc = null; error = null;
  if (body == null || body.Length == 0) { error = Fail(400, "body required"); return false; }
  try { doc = JsonDocument.Parse(body); }
  catch (JsonException) { error = Fail(400, "malformed json"); return false; }
  if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); doc = null; error = Fail(400, "body must be an object"); return false; }
  return true;
 }

 static LanResponse VolumeReply(ILanBackend b)
 {
  var n = TryNative(b, -1);
  if (n == null || n.Volume < 0 || n.Mute < 0) return Fail(502, "volume unavailable");
  return new LanResponse(200, Json(w => { w.WriteBoolean("ok", true); WriteVolume(w, n); }));
 }

 static LanResponse Volume(byte[] body, ILanBackend b)
 {
  JsonDocument doc; LanResponse err;
  if (!ReadObject(body, out doc, out err)) return err;
  int level;
  using (doc)
  {
   JsonElement v; double d;
   if (!doc.RootElement.TryGetProperty("level", out v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out d)) return Fail(400, "level must be a number");
   if (d != Math.Floor(d)) return Fail(400, "level must be an integer");
   level = (int)Math.Max(0, Math.Min(100, d));
  }
  try { if (!b.SetVolume(level)) return Fail(502, "volume set failed"); }
  catch (Exception) { return Fail(503, "ui call failed"); }
  return VolumeReply(b);
 }

 static LanResponse Mute(byte[] body, ILanBackend b)
 {
  JsonDocument doc; LanResponse err;
  if (!ReadObject(body, out doc, out err)) return err;
  bool mute;
  using (doc)
  {
   JsonElement v;
   if (!doc.RootElement.TryGetProperty("mute", out v) || (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)) return Fail(400, "mute must be true or false");
   mute = v.ValueKind == JsonValueKind.True;
  }
  try { if (!b.SetMute(mute)) return Fail(502, "mute set failed"); }
  catch (Exception) { return Fail(503, "ui call failed"); }
  return VolumeReply(b);
 }
}
