# Optional private web-player bridge

Status: installed on the development TV for Twitch and KickTV. Actual state/progress and HA pause/resume passed for both. Bridge restart preserved Twitch playback. Managed live controls passed again after reboot. Controlled full boot and standby/wake also passed for the installed setup; this is not a general release.

The launcher-hosted playback agent registers `WebPlaybackProvider` for the tested Twitch and KickTV apps. The provider talks to a separate bridge at `127.0.0.1:18767`. The existing private API token is used as a key for signed requests and signed responses, with fresh nonces and replay rejection; the reusable token is never sent to the local listener. A different local app occupying that port cannot forge accepted playback data or obtain that token. Home Assistant continues using the existing companion and its unchanged LAN authentication. No HA configuration or Python runtime change is required for this provider.

The bridge accepts only fixed Twitch/KickTV launch, observation, play and pause operations. It does not accept application IDs, shell commands, JavaScript, arbitrary URLs or file paths over HTTP. It verifies page origin and Tizen application identity, bounds HTTP/WebSocket messages and requires a page/player lease before a command. Signed media URLs stay inside the page. Missing title and duration remain absent; live seeking and stop are not advertised.

## Protected launch

The tested Samsung runtime assigns a random inspector port and binds it on network interfaces. This implementation does not assume that a localhost client makes that listener private.

Before starting a supported app, the root helper guards new inbound handshakes to newly created listeners for both IPv4 and IPv6. Existing listener ports, loopback and outbound client connections are exempt. The temporary gate matches TCP SYN/ACK responses without an owner match, which is unavailable in the tested TV kernel. TCP-reset rules explicitly select TCP. Once the launcher returns its port, the helper installs rules allowing only loopback access to that port, verifies protection, then removes the temporary guards. Failures close the debug-launched app before guard removal. Both firewall families are required on this profile.

Process start identity and boot identity prevent stale endpoint reuse. A small lifecycle task retires the component's port rules when its process exits. A bridge restart retains a completed protected session; an interrupted launch is cleaned up. No background activity is interpreted as playback.

Modeled failure tests cover both fixed profiles. Actual per-port IPv4/IPv6 rules, direct IPv4 LAN blocking, local authenticated observation, HA commands and one bridge restart were checked. The TV requires explicit TCP selectors on TCP-reset rules and lacks an owner match. IPv6 LAN probing and wider failure recovery remain pending. Controlled full boot and standby/wake passed on the development TV.

## Host installation contract

This requires the owner's existing root bootstrap and matching .NET runtime. The tested app ID, user ID and package paths are device observations, not a universal installer.

1. Take a fresh backup and prepare guarded inverses for every changed file.
2. Install the bridge DLL, shell helpers and configuration in a root-owned directory. The `token-path` file contains the existing launcher's token file path; `runtime-path` contains the inspected `corerun` path. Neither contains a token value. Add the root-owned `enabled` marker only after staging completes.
3. Start with `cat /opt/usr/share/quietthings-web-playback/start-service.sh | /bin/sh`, then confirm the listener is loopback-only and unauthenticated requests fail. The service does not open or restart Twitch on startup.
4. Compile the provider into the launcher and register it as an additional provider. Route supported app launches through `WebPlaybackBridge.Launch`. Preserve normal app launching as fallback when the optional component is unavailable. The example host uses a private `web-playback.enabled` marker to opt in.
5. Arrange optional startup after the existing root bootstrap. The customized host stages an immutable, versioned signed archive pair, validates it before use and retains the original recovery resources. A missing, invalid or incomplete optional pair falls back to the original pair. Root-owned addon code must not be writable by other users. It does not replace stock registries, accounts or global security settings.
6. Test a launcher-originated Twitch launch, actual playing/paused state, HA commands, app exit, bridge restart, standby and full boot. Inspect temporary guard cleanup and retire obsolete port rules.

The source supports launches through the customized launcher. Starting a normal supported instance through another route does not automatically attach an inspector or interrupt it to relaunch. That case reports unknown until a managed launch. Twitch titles now come from channel-matched hidden player headings; KickTV titles use the normal channel API with a bounded current-player cache.

The customized launcher's remote remaps call the same managed Launch path as dock tiles, including over another app. App-specific menu checks now expose confirmed idle, clearing title/timing/controls, while failed/loading views remain unknown. Twitch headings must match the channel route and be unambiguous. KickTV validates channel, native-open generation and ready-player UI; stale/oversized/wrong-channel replies are ignored. Title edits do not change command ownership. Its metadata lookup uses one in-flight request, five-second timeout, 30-second refresh and 90-second stale limit. This is normal app-side internet metadata access without extra credentials, not a purely passive local extraction.

## Local checks

```sh
dotnet run --project experimental/playback/tests/PlaybackTests.csproj
dotnet run --project experimental/playback/tests/WebBridgeTests.csproj
dotnet build experimental/playback/bridge/Bridge.csproj -o .local/twitch-bridge-build
python -m unittest discover -s experimental/playback/tests -p 'test_*.py' -v
node experimental/playback/tests/web-media-probe.test.cjs
```

The HTTP tests run the actual bridge bound to loopback, with temporary test credentials and no privileged helper. The transport tests use real HTTP/WebSocket exchanges and parse the generated controller expressions with Node. The shell launch tests exercise ordering and cleanup with fake OS commands. They do not replace device tests.

## Fixed profiles and script policy

The original /v1/launch, /v1/playback and /v1/command routes retain Twitch compatibility. KickTV uses /v1/kicktv/launch, /v1/kicktv/playback and /v1/kicktv/command. Each profile has an exact app/process/page identity, endpoint state and firewall chain. The root helper accepts only the fixed QT_WEB_APP enum selected internally by the bridge. Unknown profiles fail before any OS action.

The tested TV rejects unsigned shell filenames. The bridge feeds its root-owned helper source through sh stdin with fixed arguments, and boot/stop paths use the same method. Platform signing policy is unchanged.

KickTV's AVPlay context observes successful opens and reads the channel label to anchor actual broadcast-title lookup. Source keys stay in-page and invalidate old leases even on same-channel reopen. Live seek and stop are not advertised. Tests on two channels in each live app verified title, play/pause and menu clearing through HA; this title build has not undergone another full TV reboot.

## Native Cobalt extension

The optional [Cobalt observer](../web/COBALT.md) uses a separate authenticated report route and app credential. The server accepts bounded concurrent clients so real player acknowledgements can arrive while a command waits. The existing Twitch and KickTV routes remain serialized. The observer adds finite-video seeking only when its actual HTML5 player supplies a valid seek range.
