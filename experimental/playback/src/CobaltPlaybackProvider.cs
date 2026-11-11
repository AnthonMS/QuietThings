// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuietThings.Playback;

// YouTube and TizenTube share one native foreground ID. The attested document
// determines ownership; foreground identity alone never implies playback.
public sealed class CobaltPlaybackProvider : IPlaybackProvider
{
    public const string AppId = "com.samsung.tv.cobalt-yt";
    readonly WebPlaybackBridge bridge;
    public string Name => "cobalt-player";
    public CobaltPlaybackProvider(WebPlaybackBridge client) { bridge = client; }
    public bool CanObserve(string appId) => appId == AppId;
    static double? Number(JsonElement p, string name) => p.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var v) && double.IsFinite(v) && v >= 0 ? v : null;
    public async Task<PlaybackSnapshot> Observe(string appId, DateTimeOffset now, CancellationToken ct) {
        if (!CanObserve(appId)) return PlaybackSnapshot.Unknown(appId, now);
        using var doc = await bridge.Request("v1/cobalt/playback", null, ct).ConfigureAwait(false);
        var r = doc.RootElement;
        if (!r.GetProperty("ok").GetBoolean() || r.GetProperty("app_id").GetString() != appId) return PlaybackSnapshot.Unknown(appId, now);
        var at = r.GetProperty("observed_at").GetDateTimeOffset();
        if (now - at > TimeSpan.FromSeconds(3) || at - now > TimeSpan.FromSeconds(3)) return PlaybackSnapshot.Unknown(appId, now);
        var p = r.GetProperty("probe"); string state = p.GetProperty("state").GetString();
        if (state is not ("playing" or "paused" or "idle")) return PlaybackSnapshot.Unknown(appId, now);
        string mode = r.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        string appName = mode == "youtube" ? "YouTube" : mode == "tizentube" ? "TizenTube" : null;
        if (state == "idle" && (!r.TryGetProperty("page_ready", out var ready) || ready.ValueKind != JsonValueKind.True)) return PlaybackSnapshot.Unknown(appId, now);
        if (state == "idle") return new PlaybackSnapshot { AppId = appId, AppName = appName, Adapter = Name, State = "idle", ObservedAt = at };
        string lease = r.GetProperty("lease").GetString();
        if (lease == null || lease.Length != 64) return PlaybackSnapshot.Unknown(appId, now);
        double? duration = Number(p, "duration_seconds"), position = Number(p, "position_seconds");
        bool live = p.TryGetProperty("live", out var l) && l.ValueKind == JsonValueKind.True;
        if (live || duration == 0) duration = null;
        var commands = p.GetProperty("commands").EnumerateArray().Select(x => x.GetString()).Where(x => x is "play" or "pause" || x == "seek" && duration != null && !live).Distinct().ToArray();
        string title = p.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        return new PlaybackSnapshot { AppId = appId, AppName = appName, Adapter = Name, State = state, Title = title?.Substring(0, Math.Min(512, title.Length)), MediaType = "Video",
            PositionSeconds = position, DurationSeconds = duration, ObservedAt = at, PositionUpdatedAt = at, SessionId = lease, Commands = commands };
    }
    public async Task Command(PlaybackSnapshot current, string action, double? position, CancellationToken ct) {
        if (!CanObserve(current.AppId) || current.Adapter != Name || !current.Commands.Contains(action) || action is not ("play" or "pause" or "seek") ||
            action == "seek" && (position == null || !double.IsFinite(position.Value) || position < 0 || current.DurationSeconds == null || position > current.DurationSeconds)) throw new InvalidOperationException("Cobalt command unavailable");
        using var result = await bridge.Request("v1/cobalt/command", new { action, position, lease = current.SessionId }, ct).ConfigureAwait(false);
        if (!result.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Cobalt command rejected");
    }
}
