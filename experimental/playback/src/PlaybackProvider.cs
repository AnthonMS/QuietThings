// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace QuietThings.Playback;

// Providers report real observations. Foreground ownership remains the agent's responsibility.
public interface IPlaybackProvider
{
    string Name { get; }
    bool CanObserve(string appId);
    Task<PlaybackSnapshot> Observe(string appId, DateTimeOffset now, CancellationToken ct);
    Task Command(PlaybackSnapshot current, string action, double? position, CancellationToken ct);
}

public sealed class JellyfinPlaybackProvider : IPlaybackProvider
{
    readonly JellyfinPlaybackAdapter adapter;
    readonly Func<PlaybackAccount> account;
    readonly Func<PlaybackAccount, CancellationToken, Task<IPAddress>> address;
    public string Name => "jellyfin-session";
    public JellyfinPlaybackProvider(JellyfinPlaybackAdapter client, Func<PlaybackAccount> credentials, Func<PlaybackAccount, CancellationToken, Task<IPAddress>> localAddress)
    { adapter = client; account = credentials; address = localAddress; }
    public bool CanObserve(string appId) => PlaybackSelection.SupportedApp(appId);
    public async Task<PlaybackSnapshot> Observe(string appId, DateTimeOffset now, CancellationToken ct)
    {
        var credentials = account();
        var ip = await address(credentials, ct).ConfigureAwait(false);
        return await adapter.Observe(credentials, appId, ip, now, ct).ConfigureAwait(false);
    }
    public Task Command(PlaybackSnapshot current, string action, double? position, CancellationToken ct) => adapter.Command(account(), current, action, position, ct);
}
