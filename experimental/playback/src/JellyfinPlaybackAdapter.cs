// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QuietThings.Playback;

public sealed class JellyfinPlaybackAdapter
{
    readonly HttpClient http;
    public JellyfinPlaybackAdapter(HttpClient client) { http = client; }
    public static Uri Origin(PlaybackAccount account)
    {
        if (account == null || string.IsNullOrWhiteSpace(account.Token) || string.IsNullOrWhiteSpace(account.User) || !Uri.TryCreate(account.Server?.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "") throw new InvalidOperationException("Playback account unavailable");
        return uri;
    }
    public async Task<PlaybackSnapshot> Observe(PlaybackAccount account, string foreground, IPAddress tv, DateTimeOffset now, CancellationToken ct)
    {
        if (!PlaybackSelection.SupportedApp(foreground)) return PlaybackSnapshot.Unknown(foreground, now);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Origin(account), "Sessions?controllableByUserId=" + Uri.EscapeDataString(account.User)));
        request.Headers.Add("X-Emby-Token", account.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Reject unbounded responses before parsing. No URLs or account values enter status output.
        using var buffer = new System.IO.MemoryStream(); var bytes = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false)) != 0) { if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidOperationException("Session response too large"); buffer.Write(bytes, 0, count); }
        using var doc = JsonDocument.Parse(buffer.ToArray());
        var result = PlaybackSelection.Select(Parse(doc.RootElement), foreground, account.User, tv, now);
        result.AccountIdentity = Origin(account).AbsoluteUri + "|" + account.User;
        return result;
    }
    public async Task Command(PlaybackAccount account, PlaybackSnapshot current, string action, double? position, CancellationToken ct)
    {
        if (current.AccountIdentity != Origin(account).AbsoluteUri + "|" + account.User) throw new InvalidOperationException("Playback account changed");
        if (current.SessionId == null || Array.IndexOf(current.Commands, action) < 0) throw new InvalidOperationException("Playback command unavailable");
        string name = action == "play" ? "Unpause" : action == "pause" ? "Pause" : action == "stop" ? "Stop" : action == "seek" ? "Seek" : throw new InvalidOperationException("Unknown command");
        string path = "Sessions/" + Uri.EscapeDataString(current.SessionId) + "/Playing/" + name;
        if (action == "seek") { if (!position.HasValue || !double.IsFinite(position.Value) || position < 0 || !current.DurationSeconds.HasValue || position > current.DurationSeconds) throw new InvalidOperationException("Invalid seek position"); path += "?seekPositionTicks=" + checked((long)(position.Value * 10000000)).ToString(System.Globalization.CultureInfo.InvariantCulture); }
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin(account), path));
        request.Headers.Add("X-Emby-Token", account.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
    public static IEnumerable<PlaybackSession> Parse(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Invalid sessions");
        foreach (var s in array.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            bool hasMedia = s.TryGetProperty("NowPlayingItem", out var media) && media.ValueKind == JsonValueKind.Object;
            s.TryGetProperty("PlayState", out var play);
            if (!DateTimeOffset.TryParse(Text(s, "LastPlaybackCheckIn"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) continue;
            yield return new PlaybackSession { Id = Text(s, "Id"), UserId = Text(s, "UserId"), Client = Text(s, "Client"), RemoteEndpoint = Text(s, "RemoteEndPoint"), LastCheckIn = time, HasMedia = hasMedia, IsPaused = Flag(play, "IsPaused"), Title = hasMedia ? Text(media, "Name") : null, MediaType = hasMedia ? Text(media, "Type") : null, PositionTicks = Number(play, "PositionTicks"), DurationTicks = hasMedia ? Number(media, "RunTimeTicks") : null, CanControl = Flag(s, "SupportsRemoteControl") == true, CanSeek = Flag(play, "CanSeek") == true };
        }
    }
    static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool? Flag(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
    static long? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}
