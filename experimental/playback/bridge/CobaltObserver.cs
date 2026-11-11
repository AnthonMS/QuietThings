// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuietThings.WebBridge;

// Reports and command acknowledgements use their own gate, never the app-command gate.
public sealed class CobaltObserver
{
    public const string AppId = "com.samsung.tv.cobalt-yt";
    readonly object gate = new();
    readonly Func<DateTimeOffset> clock;
    readonly Func<string, bool> alive;
    string identity, document, instance, lease;
    long sequence;
    JsonElement probe;
    DateTimeOffset observed;
    Pending pending;
    bool pageReady;
    readonly HashSet<string> retired = new();
    readonly Queue<string> retiredOrder = new();
    sealed class Pending {
        public string Id, Action, Document, Instance;
        public double? Position;
        public DateTimeOffset Expires;
        public TaskCompletionSource<bool> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public CobaltObserver(Func<string, bool> isAlive, Func<DateTimeOffset> now = null) { alive = isAlive; clock = now ?? (() => DateTimeOffset.UtcNow); }
    static string Required(JsonElement value, string name, int max) {
        var text = value.GetProperty(name).GetString();
        if (string.IsNullOrEmpty(text) || text.Length > max) throw new InvalidDataException();
        return text;
    }
    static double? Number(JsonElement p, string key) => p.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var v) && double.IsFinite(v) && v >= 0 ? v : null;
    static JsonElement Sanitize(JsonElement p) {
        string state = Required(p, "state", 16);
        if (state is not ("playing" or "paused" or "unknown" or "idle")) throw new InvalidDataException();
        string instance = p.GetProperty("instance").ValueKind == JsonValueKind.Null ? null : Required(p, "instance", 64);
        string backend = p.GetProperty("backend").ValueKind == JsonValueKind.Null ? null : Required(p, "backend", 16);
        if (backend is not (null or "html5")) throw new InvalidDataException();
        if (state is "playing" or "paused" && (backend != "html5" || instance == null)) throw new InvalidDataException();
        string title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (title?.Length > 512) throw new InvalidDataException();
        double? duration = Number(p, "duration_seconds"), position = Number(p, "position_seconds");
        bool? live = p.TryGetProperty("live", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False ? l.GetBoolean() : null;
        if (live == true || duration == 0) duration = null;
        var commands = p.GetProperty("commands").EnumerateArray().Select(x => x.GetString()).Where(x => x is "play" or "pause" || x == "seek" && duration != null && live != true).Distinct().ToArray();
        if (state is not ("playing" or "paused")) { instance = null; commands = Array.Empty<string>(); position = duration = null; title = null; }
        return JsonSerializer.SerializeToElement(new { probe_version = 1, backend, instance, state, title, position_seconds = position, duration_seconds = duration, live, commands });
    }
    public object Report(JsonElement report, string attestedIdentity) {
        string doc = Required(report, "document", 64);
        if (doc.Length != 32 || doc.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException();
        long seq = report.GetProperty("sequence").GetInt64();
        if (seq < 1 || seq > 9007199254740991L) throw new InvalidDataException();
        var snapshot = Sanitize(report.GetProperty("probe"));
        string owner = snapshot.GetProperty("instance").GetString();
        lock (gate) {
            var now = clock();
            if (identity == attestedIdentity && document == doc && seq <= sequence) throw new InvalidOperationException();
            // A delayed older document must not take ownership back from its replacement.
            if (identity != attestedIdentity) { retired.Clear(); retiredOrder.Clear(); }
            if (retired.Contains(doc)) throw new InvalidOperationException();
            if (identity == attestedIdentity && document != null && document != doc) {
                retired.Add(document);
                retiredOrder.Enqueue(document);
                // The proxy retains at most sixteen document capabilities. A
                // longer bounded tombstone window prevents a permanent limit
                // after many legitimate app launches in the same TV boot.
                while (retiredOrder.Count > 64) retired.Remove(retiredOrder.Dequeue());
            }
            if (identity != attestedIdentity || document != doc || instance != owner) {
                pending?.Done.TrySetResult(false); pending = null;
                lease = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            }
            identity = attestedIdentity; document = doc; sequence = seq; instance = owner; probe = snapshot; observed = now;
            pageReady = report.TryGetProperty("page_ready", out var ready) && ready.ValueKind == JsonValueKind.True;
            if (pending != null && report.TryGetProperty("acknowledgement", out var ack) && ack.ValueKind == JsonValueKind.Object && ack.GetProperty("id").GetString() == pending.Id) {
                bool actual = pending.Action == "play" ? snapshot.GetProperty("state").GetString() == "playing" :
                    pending.Action == "pause" ? snapshot.GetProperty("state").GetString() == "paused" :
                    Number(snapshot, "position_seconds") is double v && Math.Abs(v - pending.Position.Value) < 2.5;
                pending.Done.TrySetResult(ack.GetProperty("ok").GetBoolean() && actual && now <= pending.Expires); pending = null;
            }
            if (pending != null && now > pending.Expires) { pending.Done.TrySetResult(false); pending = null; }
            return new { ok = true, command = pending == null ? null : new { id = pending.Id, action = pending.Action, position = pending.Position, document = pending.Document, instance = pending.Instance, expires = pending.Expires.ToUnixTimeMilliseconds() } };
        }
    }
    void Current() {
        if (identity == null || clock() - observed > TimeSpan.FromSeconds(3) || !alive(identity)) throw new InvalidOperationException();
    }
    public bool MatchesIdentity(string expected) { lock (gate) {
        try { Current(); return identity == expected; } catch { return false; }
    } }
    public object Read() { lock (gate) { Current(); return new { ok = true, app_id = AppId, mode = identity.Split('|').Last(), page_ready = pageReady, observed_at = observed, lease, probe }; } }
    public async Task<object> Command(JsonElement body, CancellationToken ct) {
        Pending work;
        lock (gate) {
            Current();
            var names = new HashSet<string>();
            foreach (var f in body.EnumerateObject()) if (!names.Add(f.Name) || f.Name is not ("action" or "lease" or "position")) throw new InvalidDataException();
            string action = Required(body, "action", 8);
            if (body.GetProperty("lease").GetString() != lease || pending != null || action is not ("play" or "pause" or "seek") || !probe.GetProperty("commands").EnumerateArray().Any(x => x.GetString() == action)) throw new InvalidOperationException();
            double? position = Number(body, "position");
            if (action == "seek" && (position == null || Number(probe, "duration_seconds") is not double duration || position > duration)) throw new InvalidOperationException();
            work = new Pending { Id = Guid.NewGuid().ToString("N"), Action = action, Position = position, Document = document, Instance = instance, Expires = clock().AddSeconds(4) }; pending = work;
        }
        try { if (!await work.Done.Task.WaitAsync(TimeSpan.FromSeconds(4.5), ct)) throw new InvalidOperationException(); return new { ok = true }; }
        finally { lock (gate) { if (pending == work) pending = null; } }
    }
}
