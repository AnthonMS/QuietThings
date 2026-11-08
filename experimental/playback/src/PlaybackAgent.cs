// SPDX-License-Identifier: MIT
// Copyright (c) 2026 QuietThings contributors
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace QuietThings.Playback;

public sealed class PlaybackAgent : IDisposable
{
    readonly Func<CancellationToken, Task<string>> foreground;
    readonly IPlaybackProvider[] providers;
    readonly SemaphoreSlim observation = new SemaphoreSlim(1, 1);
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly PlaybackStore store = new PlaybackStore();
    public string[] AdapterNames => providers.Select(p => p.Name).ToArray();

    public PlaybackAgent(Func<CancellationToken, Task<string>> readForeground,
        Func<PlaybackAccount> readAccount,
        Func<PlaybackAccount, CancellationToken, Task<IPAddress>> readAddress,
        HttpClient client = null, IEnumerable<IPlaybackProvider> additionalProviders = null)
    {
        foreground = readForeground;
        ownsHttp = client == null;
        http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(4) };
        var list = additionalProviders?.ToList() ?? new List<IPlaybackProvider>();
        list.Add(new JellyfinPlaybackProvider(new JellyfinPlaybackAdapter(http), readAccount, readAddress));
        if (list.Any(p => p == null || string.IsNullOrWhiteSpace(p.Name)) || list.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new ArgumentException("Playback provider names must be unique");
        providers = list.ToArray();
    }

    public PlaybackSnapshot Read() => store.Read(DateTimeOffset.UtcNow);

    public async Task<PlaybackSnapshot> Refresh(CancellationToken ct)
    {
        await observation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string active = await foreground(ct).ConfigureAwait(false);
            long generation = store.SetForeground(active);
            var provider = providers.FirstOrDefault(p => p.CanObserve(active));
            if (provider == null)
            {
                var unknown = PlaybackSnapshot.Unknown(active, DateTimeOffset.UtcNow);
                store.Publish(unknown, generation);
                return unknown;
            }
            PlaybackSnapshot next;
            try { next = await provider.Observe(active, DateTimeOffset.UtcNow, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { next = PlaybackSnapshot.Unknown(active, DateTimeOffset.UtcNow); }
            string confirmed = await foreground(ct).ConfigureAwait(false);
            if (confirmed != active) { store.SetForeground(confirmed); return Read(); }
            // A provider cannot claim media belonging to a different foreground app.
            if (next == null || next.AppId != active)
                next = PlaybackSnapshot.Unknown(active, DateTimeOffset.UtcNow);
            store.Publish(next, generation);
            return Read();
        }
        finally { observation.Release(); }
    }

    public async Task Command(string action, double? position, CancellationToken ct)
    {
        var current = await Refresh(ct).ConfigureAwait(false);
        var provider = providers.FirstOrDefault(p => p.Name == current.Adapter && p.CanObserve(current.AppId));
        if (provider == null || Array.IndexOf(current.Commands, action) < 0 ||
            await foreground(ct).ConfigureAwait(false) != current.AppId)
            throw new InvalidOperationException("Playback command unavailable");
        await provider.Command(current, action, position, ct).ConfigureAwait(false);
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
