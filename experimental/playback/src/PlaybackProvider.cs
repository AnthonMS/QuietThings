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
    public async Task Command(PlaybackSnapshot current, string action, double? position, CancellationToken ct)
    {
        var credentials = account();
        var ip = await address(credentials, ct).ConfigureAwait(false);
        await adapter.Command(credentials, current, action, position, ct).ConfigureAwait(false);
        // Server delivery is not a player acknowledgement. Wait for the same
        // client to report the actual result before HA performs its next read.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                limit.Token.ThrowIfCancellationRequested();
                var next = await adapter.Observe(credentials, current.AppId, ip, DateTimeOffset.UtcNow, limit.Token).ConfigureAwait(false);
                if (next.SessionId != null && next.SessionId != current.SessionId) throw new InvalidOperationException("Playback session changed");
                bool confirmed = next.SessionId == current.SessionId && (action == "play" ? next.State == "playing" :
                    action == "pause" ? next.State == "paused" : action == "stop" ? next.State == "idle" :
                    action == "seek" && next.PositionSeconds.HasValue && Math.Abs(next.PositionSeconds.Value - position.Value) < 2.5);
                if (confirmed) return;
                await Task.Delay(125, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Player did not acknowledge command");
        }
    }
}
