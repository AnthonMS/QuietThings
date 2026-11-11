# Optional native Cobalt observation

The tested YouTube and TizenTube wrappers share the native foreground ID `com.samsung.tv.cobalt-yt`. This provider reads their actual HTML5 player inside the page. The development TV has passed HA title, duration, progressing position, pause, resume and finite-video seek checks in both modes. Managed launches, mode switching and the stated lifecycle checks passed as recorded in [MULTI-APP.md](../MULTI-APP.md). Broader daily-use and compatibility limits remain.

This is an additional, explicitly installed component for an inspected rooted TV with TizenTube's existing app-local HTTPS proxy and trust assets. It is not an installer for arbitrary Samsung TVs. It does not depend on a production Cobalt debugger.

## Data and commands

`cobalt-client.js` uses the shared media probe. It sends bounded snapshots through a document-specific same-origin route, watches real media events and detects a new source generation on `loadstart`. Source URLs remain inside the page. Multiple active players are ambiguous and report unknown.

The app-local addon signs reports with a dedicated observer credential. The page never receives this credential or the launcher's LAN token. The root bridge checks both request and response HMAC proofs. It independently verifies live kernel TCP ownership: the signed request must belong to the proxy worker, and the browser callback connection must belong to the native Cobalt process. The callback remains open until this check finishes.

HA continues using the existing companion on its existing port and token. Its new `cobalt-player` provider reads `/v1/cobalt/playback` and sends controls to `/v1/cobalt/command`. Reports enter `/v1/cobalt/report` using the separate observer credential. That credential cannot read or control other bridge routes. Report processing bypasses the app-command gate so a command can await its real acknowledgement.

Commands require the current document and player lease. The client rechecks its player instance before acting, then acknowledges with a new actual observation. The bridge confirms paused/playing state or the requested seek position before returning success. It rejects expired snapshots, retired documents and changed players. Unknown and ended media clear their old metadata. Seeking requires a finite duration and actual seekable ranges. Live media has no fabricated VOD duration or seek support.

## Two local proxy modes

TizenTube retains its original proxy on port 8101 and its existing modifications. The addon intercepts only its bounded observer routes and `/tv` HTML injection.

Ordinary YouTube uses the separate proxy on `127.0.0.2:8102`. It uses the existing app-local CA and content trust without loading TizenTube's ad or UI script. Other HTTPS traffic remains an end-to-end tunnel. The CA private key stays on the TV. No global trust or security policy is changed.

The second loopback address matters on the tested device: its SMACK networking policy rejected the Cobalt-to-proxy flow through `127.0.0.1`. The existing TizenTube route uses `127.0.0.2`. Binding the new listener to that loopback address keeps it inaccessible from the LAN.

## Build and installation contract

```sh
cd experimental/playback/web
npm ci
npm run bundle
```

The bundle targets the older Node runtime. Build tooling runs on the PC. Keep the emitted `quiet-cobalt-forge-LICENSE` with the bundle. The output contains no device credentials or proprietary TV assets.

An operator must inspect package paths, metadata schemas, runtime ownership and labels on their own TV, then back up every affected file and prepare guarded inverses before installing:

1. Stage the addon, media probe, client and license beside the existing TizenTube service bundle. Add only the require prefix to the exact backed-up original service. Preserve the original service and configuration for recovery.
2. Generate a separate observer key. The root bridge reads `cobalt-key` beside its helper. The addon reads `quiet-cobalt-key` beside its bundle. Use restrictive ownership, mode and package labels; never place either key inside HTML, an API response or a public archive.
3. Keep wrapper native arguments consistent with their package metadata and the actual web-app registry launch cache. On the tested device, `widgetinfo.reserved_string_6` held these arguments. Update only the inspected wrapper rows and metadata attributes, with count and exact-value guards. Never restore whole live databases. Preserve the original values and unrelated settings.
4. The ordinary wrapper needs its existing arguments plus the dedicated proxy and app-local content trust. TizenTube keeps its existing proxy and modifications. Both still use the same native engine.
5. Recycle only the verified worker owning the existing proxy listeners. Stopping the service's launch stub alone does not unload a module from its shared worker. Refuse an unexpected executable or listener set.
6. Register `CobaltPlaybackProvider` with the launcher's optional playback providers. Keep power, Samsung pairing, remote mappings and the original API intact.
7. Start the root service after the existing signed root bootstrap. Public optional signed startup assets must be readable under the bootstrap package's actual SMACK label. Keep the original signed recovery pair and validate the complete optional pair before selection. A local filesystem test does not verify app-context access on the TV.
8. Validate both normal app launches, actual VOD controls, source replacement, app exit, bridge restart, a full boot and a separate standby/wake cycle. Inspect cleanup after each privileged maintenance session.

The launcher must also supply the fixed mode-specific `USERDATA` on managed app opens: wrappers can restore their cached original arguments. For a recommendation, `/v1/cobalt/prepare` checks the fixed mode and verified proxy; it stops the native engine only when its actual arguments identify the other mode. `CobaltVideoLauncher` opens the alias, waits for an attested matching mode, complete document and native foreground, then sends one `PAYLOAD` to the native Cobalt ID. It does not repeatedly send a video request while the observer starts.


A boot-preloaded Cobalt may use `/usr/bin/app-defined-loader` instead of the final native executable. The prepare step recognizes the exact platform loader, Cobalt kernel package label and native argv path. An active instance can retain this loader. Playback attestation additionally requires an executable file mapping of the exact Cobalt binary and live socket ownership. A fresh attested process/mode identity prevents restarting an already correct loader instance merely because it lacks launch arguments in its command line. Unverified preloads and different modes are retired before a managed launch.

Optional local diagnostics count page, client and report events and bridge HTTP status codes. They require the operator-owned `quiet-cobalt-diagnostic.enabled` marker and write only bounded counters to the app data directory. They contain no keys, URLs or media metadata. Remove the marker after diagnosis.

The automated suites exercise the actual bridge HTTP parser, HMAC separation, concurrency, observer acknowledgements, stale sessions, client promise handling and proxy HTTP framing. They supplement the device checks.

## Empty menus and document freshness

Complete readable documents report idle when there are no loaded media players. Ended elements and paused empty placeholders clear media fields and controls; failed, ambiguous and actively initializing players retain unknown status. The companion preserves the attested YouTube or TizenTube app name for idle as well as playing/paused media.

Managed native launch arguments include a fresh per-launch query marker. A restarted browser must fetch a newly injected document capability instead of replaying cached HTML whose observer sequence starts again at one. The proxy replay and retired-document checks remain intact. Warm native video delivery still uses one PAYLOAD after the existing ready-document check.
