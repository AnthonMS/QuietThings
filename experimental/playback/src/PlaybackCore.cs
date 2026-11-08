// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuietThings.Playback;

public sealed class PlaybackSnapshot
{
    public string AppId { get; set; }
    public string State { get; set; } = "unknown";
    public string Adapter { get; set; }
    public string Title { get; set; }
    public string MediaType { get; set; }
    public double? PositionSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset? PositionUpdatedAt { get; set; }
    public string[] Commands { get; set; } = Array.Empty<string>();
    [JsonIgnore] public string AccountIdentity { get; set; }
    [JsonIgnore] public string SessionId { get; set; }
    [JsonIgnore] public long ForegroundGeneration { get; set; }
    public static PlaybackSnapshot Unknown(string app, DateTimeOffset now) => new PlaybackSnapshot { AppId = app, ObservedAt = now };
}

public sealed class PlaybackAccount
{
    public string Server { get; set; }
    public string Token { get; set; }
    public string User { get; set; }
}

public sealed class PlaybackSession
{
    public string Id { get; set; }
    public string AppId { get; set; }
    public string UserId { get; set; }
    public string RemoteEndpoint { get; set; }
    public string Client { get; set; }
    public DateTimeOffset LastCheckIn { get; set; }
    public bool HasMedia { get; set; }
    public bool? IsPaused { get; set; }
    public string Title { get; set; }
    public string MediaType { get; set; }
    public long? PositionTicks { get; set; }
    public long? DurationTicks { get; set; }
    public bool CanControl { get; set; }
    public bool CanSeek { get; set; }
}

public static class PlaybackSelection
{
    public const int MaxSessionAgeSeconds = 45;
    public static IPAddress EndpointAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (IPAddress.TryParse(value.Trim(), out var plain)) return plain.IsIPv4MappedToIPv6 ? plain.MapToIPv4() : plain;
        if (!Uri.TryCreate("http://" + value.Trim(), UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var host)) return null;
        return host.IsIPv4MappedToIPv6 ? host.MapToIPv4() : host;
    }
    public static string ClientApp(string client) => (string.Equals(client, "Moonfin", StringComparison.OrdinalIgnoreCase) || string.Equals(client, "Moonfin for Tizen", StringComparison.OrdinalIgnoreCase)) ? "MoonfinApp.moonfin" : string.Equals(client, "Jellyfin", StringComparison.OrdinalIgnoreCase) || string.Equals(client, "Jellyfin for Tizen", StringComparison.OrdinalIgnoreCase) ? "AprZAARz4r.Jellyfin" : null;
    public static bool SupportedApp(string app) => app == "MoonfinApp.moonfin" || app == "AprZAARz4r.Jellyfin";
    public static PlaybackSnapshot Select(IEnumerable<PlaybackSession> sessions, string foreground, string user, IPAddress tv, DateTimeOffset now)
    {
        var unknown = PlaybackSnapshot.Unknown(foreground, now);
        if (!SupportedApp(foreground) || tv == null || string.IsNullOrEmpty(user)) return unknown;
        var candidates = sessions.Where(s => !string.IsNullOrEmpty(s.Id) && s.UserId == user && ClientApp(s.Client) == foreground && tv.Equals(EndpointAddress(s.RemoteEndpoint)) && now - s.LastCheckIn <= TimeSpan.FromSeconds(MaxSessionAgeSeconds) && s.LastCheckIn - now <= TimeSpan.FromSeconds(5)).OrderByDescending(s => s.LastCheckIn).ToArray();
        if (candidates.Length == 0 || candidates.Length > 1 && candidates[0].LastCheckIn == candidates[1].LastCheckIn) return unknown;
        var item = candidates[0];
        if (item.HasMedia && !item.IsPaused.HasValue) return unknown;
        double? duration = item.DurationTicks > 0 ? item.DurationTicks / 10000000d : null;
        double? position = item.PositionTicks >= 0 ? item.PositionTicks / 10000000d : null;
        if (position > duration) position = duration;
        var commands = new List<string>();
        if (item.CanControl && item.HasMedia) { commands.AddRange(new[] { "play", "pause", "stop" }); if (item.CanSeek && duration.HasValue) commands.Add("seek"); }
        return new PlaybackSnapshot { AppId = foreground, State = !item.HasMedia ? "idle" : item.IsPaused.Value ? "paused" : "playing", Adapter = "jellyfin-session", SessionId = item.Id, Title = item.HasMedia ? Clip(item.Title, 512) : null, MediaType = item.HasMedia ? item.MediaType : null, PositionSeconds = item.HasMedia ? position : null, DurationSeconds = item.HasMedia ? duration : null, ObservedAt = now, PositionUpdatedAt = item.HasMedia ? item.LastCheckIn : null, Commands = commands.ToArray() };
    }
    static string Clip(string text, int max) => text == null ? null : text.Substring(0, Math.Min(text.Length, max));
}

// Cached observations must never leak media from an app that lost foreground ownership.
public sealed class PlaybackStore
{
    readonly object gate = new object();
    string app;
    long generation;
    PlaybackSnapshot snapshot;
    public long SetForeground(string value) { lock (gate) { if (app != value) { app = value; generation++; snapshot = null; } return generation; } }
    public bool Publish(PlaybackSnapshot value, long expectedGeneration) { lock (gate) { if (expectedGeneration != generation || value.AppId != app) return false; value.ForegroundGeneration = generation; snapshot = value; return true; } }
    public PlaybackSnapshot Read(DateTimeOffset now) { lock (gate) { if (snapshot == null || now - snapshot.ObservedAt > TimeSpan.FromSeconds(15) || snapshot.ObservedAt - now > TimeSpan.FromSeconds(5)) return PlaybackSnapshot.Unknown(app, now); return snapshot; } }
}
