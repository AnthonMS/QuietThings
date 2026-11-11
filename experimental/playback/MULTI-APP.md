# Six-app development status

The goal is local playback observations and controls for Moonfin, Jellyfin, YouTube, TizenTube, Twitch and KickTV. The installed experimental build has passed actual playback checks for these six apps on the development TV. Controlled boot, standby/wake and app-exit clearing also passed. The upstream PR remains closed; development source is published on the owner's fork for review and daily-use testing. This is evidence for one inspected TV and the tested app versions. Foreground identity does not establish playback.

| App | Evidence on the development TV | Installed HA playback support |
| --- | --- | --- |
| Moonfin | Playing, paused, advancing position, episode changes, HA play/pause, finite seek and stop accepted | Yes, through the Jellyfin-session provider |
| Jellyfin | Normal client login persisted; actual HA pause/resume, advancing position, finite seek and stop accepted | Yes, through the Jellyfin-session provider |
| YouTube | Actual Cobalt player title, duration, progressing position and HA pause/resume/seek accepted through the separate app-local proxy | Yes, through cobalt-player; actual launcher and cold-boot checks passed |
| TizenTube | Actual Cobalt player title, duration, progressing position and HA pause/resume/seek accepted through its existing app-local proxy | Yes, through cobalt-player; mode switching and stale-lease rejection passed |
| Twitch | Actual HTML5 live state/progress, channel-matched stream title, HA pause/resume and confirmed menu idle accepted | Yes, through twitch-web-player; latest title build tested on two channels; earlier managed launch after reboot passed |
| KickTV | AVPlay state/progress, cached actual broadcast title, HA pause/resume and confirmed menu idle accepted; stale reopen commands rejected | Yes, through kicktv-web-player; latest title build tested on two channels; earlier live regression after reboot passed |

## Latest owner-review qualifications, 10 November 2026

Confirmed readable empty YouTube/TizenTube menus report idle with the correct app name. The later title/menu update also passes confirmed Twitch/KickTV menus through as idle. Twitch now reads a channel-matched hidden player heading; KickTV retrieves actual session_title from the app's normal API with a bounded cache. Two channels in each app passed title/play/pause/menu checks through HA. Metadata sources may fail after app/API changes, and the initial Kick title can take until a later HA poll. Launcher remote remaps use the same managed path as app tiles, without visiting Home first. No further full reboot was performed for this title build.

The final Home startup repair required enabling Samsung's separate integer Home-autostart policy as well as restoring startup_appid. A full boot and input-to-Home wake passed programmatic foreground/power/HA checks; physical owner observation was unavailable. Direct Samsung HA wake was intermittent and one QuietThings call was skipped while unavailable; repeated PC WoL recovered the TV for final acceptance. Do not infer guaranteed dashboard wake from the earlier successful power test below. No HA wake-routing fix was installed. Daily-use testing and the owner's closed-PR hold remain in effect.

## Cobalt implementation

The [authenticated Cobalt observer](web/COBALT.md) now provides actual playback through an app-local page observer. Both YouTube modes passed HA pause, resume and finite-video seeking on the development TV. Ordinary YouTube uses a separate loopback proxy that preserves its normal player behavior. TizenTube retains its existing proxy and modifications. Production Cobalt debugging is not required.

A full restart exposed an app-context access failure when selecting the optional signed startup pair. Correcting only the public startup assets to the bootstrap package label allowed cryptographic verification and automatic playback-service startup on the next boot. Existing privacy masks, sign-ins, remote grabs and Home/startup selectors remained intact. Actual Quiet Home cold YouTube recommendations and warm YouTube-to-TizenTube recommendations passed. The launcher supplies fixed mode-specific USERDATA, waits for the attested ready native document, then sends one video PAYLOAD to the native app. A saved old-mode lease was rejected, and app exit cleared metadata. Final boot and separate standby/wake passed. A further cold-boot test exposed active Cobalt behind Samsung's generic loader. The verifier now requires the exact loader, kernel package label, native argv, executable mapping of the Cobalt binary and actual TCP ownership for that case. The final cold boot then passed real launcher recommendation playback and HA pause/resume/seek with diagnostics disabled and no manual proxy restart.

## Twitch diagnostic

The tested official app is hosted rather than a local HTML bundle. A blank inspector target elsewhere on the device belonged to HbbTV and was unrelated. A targeted web-debug launch provided an inspector for the actual Twitch app, verified through its Tizen application identity and page origin.

During a live stream, its HTML5 video reported playing and advancing position. Its AVPlay singleton remained NONE and MediaSession did not provide playback metadata. The experimental web probe correctly reported HTML5, playing, live, no finite duration and no seek.

A direct probe pause held the same position for one second, and probe play resumed actual progression. The owner's physical remote Pause attempt did not pause the stream. These are separate observations: the remote path was not accepted, while the app-owned player command did work. No claim is made about long pauses, broadcast DVR, recovery after buffer exhaustion, or every Twitch stream.

The temporary diagnostic inspector was removed at that earlier stage. The [private bridge and provider](bridge/README.md) were subsequently installed. HA pause/resume and a bridge restart passed, with title still unresolved at that point. Later inspection found the title in a hidden full-screen player heading and the installed title context now exposes it, as summarized above. Earlier managed launch after a full boot passed; broader stream/DVR behavior and a new full reboot of the title build remain unverified.

## Historical YouTube investigation

The following paragraphs retain the earlier investigation in chronological order. Their missing-provider and pending-debugger statements describe that earlier stage, not the current implementation. The current Cobalt path is documented above and in web/COBALT.md.

A second passive sample during owner-confirmed regular YouTube playback again found the native Cobalt process, with ESPlusPlayer libraries and media pipeline threads. No TCP debugging listener belonged to Cobalt. Samsung's shared media-controller fields still had an empty server name and did not expose usable current playback. The installed HA entities identified the YouTube app but supplied no title, playback position or playing/paused state.

The native libraries contain player-instance state functions. Finding those symbols does not establish an interface for observing another process's existing player. MUSE's local service and player functions were also inspected, but no verified cross-app observation route was established. Process presence, active threads and an open app must not be used as playback state.

No app restart, debug enablement, player command, service change or TV installation was performed during this sample. The current Twitch inspector transport cannot be assumed compatible with native Cobalt. A controlled Cobalt debugging/attachment test remains necessary when playback may be interrupted. Google's [Cobalt debugging documentation](https://developers.google.com/youtube/cobalt/docs/gen/cobalt/doc/web_debugging) describes debugging for non-gold builds; it does not establish availability in Samsung's installed production build. TizenTube's existing app-local proxy/injection route is a separate candidate and is not evidence of regular YouTube support.

The owner subsequently authorized a controlled relaunch. The standard web-debug launch started native Cobalt but timed out without exposing a Cobalt inspector. The app was returned to normal mode, temporary diagnostic firewall rules were removed, and the existing launcher hash and localhost developer host were verified unchanged. Private inspection of the installed compressed Cobalt library found no standard remote-debug switches or web-debug server identifiers. These observations are consistent with a production build without the standard Cobalt debugger; they do not prove every possible native observation route unavailable. Do not repeat the same debug launch as if it were a verified transport. Regular YouTube still needs a different native observation path.

The device test also established that TCP-reset rules require an explicit TCP selector and that this kernel lacks the firewall owner match. The prepared Twitch helper was corrected to use temporary SYN/ACK guards without an owner match, retaining exemptions for existing listener ports, loopback and outbound client connections. The revised package remains uninstalled and still requires full Twitch hardware acceptance.

## Acceptance limits

Continue hardware lifecycle and regression acceptance for the installed Twitch/KickTV bridge. Per-port IPv4/IPv6 rules and direct IPv4 LAN blocking were checked on the device, and mutually authenticated local access succeeded. An actual IPv6 LAN connection test remains pending; both-family rules were verified. Controlled boot and standby/wake have passed. The actual Cobalt path has passed the controls documented above. KickTV uses a small in-page AVPlay open observer and channel-label reader; package files remain unchanged. A same-channel reopen changed the lease, and the previous lease was rejected without pausing the replacement player.

Each app's stated controls, app switching and stale metadata behavior subsequently passed the documented checks. Live streams still must not advertise VOD duration or seeking without actual capabilities. Existing Moonfin behavior, accounts and remote mappings were retained. Live-app regressions after reboot and controlled lifecycle checks passed within the limits above. Broader and later limitations are described in the main playback README.

## KickTV acceptance

The app uses AVPlay and has no HTML5 video element. Its singleton reported actual PLAYING/PAUSED and advancing or stable position. An offline/NONE channel was not treated as playing. Initial acceptance exposed only a channel identifier; the later installed context uses that identifier to fetch the actual broadcast title from the normal channel API. It validates channel/source generation and ready-player state before returning title data.

The in-page context preserves native open arguments, receiver, return values and exceptions. Each successful open changes generation, including reopening the same channel. Signed source URLs are used only inside page memory to detect replacement and are never returned. The provider exposes tested play/pause only, with no finite live duration, seek or unsafe singleton stop.

## Controlled lifecycle acceptance

HA power-off from active video put both the QuietThings and existing universal entities into off and cleared title and position. After at least 40 seconds from the start of that test, HA wake returned both entities to on. The boot ID remained unchanged; remote mappings, accounts, privacy masks and Home/startup selectors remained intact. Volume and mute changes were also verified through HA and restored afterward. A subsequent full restart started root and the playback service automatically and passed actual YouTube launcher recommendation playback and HA controls.

Live streams expose play/pause and elapsed position, with no fabricated finite duration, VOD seek or stop. Current title support and confirmed menu idle are described above. Twitch/KickTV still require a managed launcher open; other routes are not silently interrupted to attach an inspector. Actual app reinstall, IPv6 LAN probing, long DVR pauses, every stream type and prolonged memory pressure remain outside acceptance.
