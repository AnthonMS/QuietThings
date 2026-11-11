// SPDX-License-Identifier: MIT
using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuietThings.Playback;

public static class CobaltLaunchArguments
{
    public static string UserData(string mode) {
        if (mode is not ("youtube" or "tizentube")) throw new ArgumentException("Unsupported Cobalt mode");
        // A restarted browser must fetch a new injected document capability.
        // Reusing cached HTML can replay an old proxy nonce with sequence one.
        return "--base_url=https://www.youtube.com/tv?quietthings_cobalt_v1&quietthings_launch=" + Guid.NewGuid().ToString("N") + " --proxy=http://127.0.0.2:" +
            (mode == "youtube" ? "8102" : "8101") + " --content=/home/owner/share/tizentube-content --dial_name=" +
            (mode == "youtube" ? "YouTube" : "TizenTube") + " --use_eden";
    }
}

// Deliver one video request to the actual native app after its document is ready.
// No player commands or repeated video requests are used as readiness probes.
public sealed class CobaltVideoLauncher
{
    readonly WebPlaybackBridge bridge;
    public CobaltVideoLauncher(WebPlaybackBridge client) { bridge = client; }
    public async Task Play(string mode, Func<Task> openAlias, Func<Task> sendNativePayload,
        Func<CancellationToken, Task<string>> foreground, CancellationToken cancellation)
    {
        if (mode is not ("youtube" or "tizentube")) throw new ArgumentException("Unsupported Cobalt mode");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(TimeSpan.FromSeconds(25)); var ct = limit.Token;
        using (var prepared = await bridge.Request("v1/cobalt/prepare", new { mode }, ct).ConfigureAwait(false))
            if (!prepared.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException("Cobalt launch unavailable");
        ct.ThrowIfCancellationRequested(); await openAlias().ConfigureAwait(false);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            bool ready = false;
            try {
                using var doc = await bridge.Request("v1/cobalt/playback", null, ct).ConfigureAwait(false);
                var root = doc.RootElement;
                ready = root.GetProperty("ok").GetBoolean() && root.GetProperty("app_id").GetString() == CobaltPlaybackProvider.AppId &&
                    root.GetProperty("mode").GetString() == mode && root.GetProperty("page_ready").GetBoolean();
            } catch (System.Net.Http.HttpRequestException) { /* No attested document yet. */ }
            if (ready && await foreground(ct).ConfigureAwait(false) == CobaltPlaybackProvider.AppId) {
                ct.ThrowIfCancellationRequested(); await sendNativePayload().ConfigureAwait(false); return;
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }
}
