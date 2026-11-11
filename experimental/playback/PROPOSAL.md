# Potential upstream contributions

Current assessment: 10 November 2026. This working tree contains installed six-app development support on one customized Q60C. Upstream PR1 is closed, and the owner wants several days of daily-use testing before any further publication. This document does not authorize commits, pushes, reopening or contacting maintainers.

## Three separate scopes

1. General HA/dependency compatibility and reliability fixes. Tests cover HA 2026.5.4 with its stock samsungtvws 2.7.2 and HA 2026.10.4 with 3.0.7. Stock-service timeouts remain unknown rather than being treated as proof of power off.
2. Optional HA companion-protocol support. A compatible TV can provide current state, metadata and capabilities through an explicitly enabled companion. The original API v1 remains unchanged and companion port zero disables extra polling. Provider failure clears media without fabricating state.
3. TV runtime and app-specific instrumentation. Jellyfin/Moonfin session selection runs in the launcher. Twitch/KickTV need a protected root inspector bridge and managed launches. YouTube/TizenTube need app-local Cobalt observation/proxy support, launcher handoff and optional boot-service provisioning. This is a materially larger setup and maintenance burden than an HA-only patch.

## Recommended contribution boundary

Consider independent general fixes and an opt-in HA client/protocol proposal separately from the specialized TV runtime. The full six-app deployment should not be presented as a routine integration update or require ordinary users to adopt this owner's entire launcher. Remote remaps, dock layout and privacy masks are not intrinsic protocol requirements.

Discuss architectural preference only after owner authorization: the separate listener is a development choice that avoids changing the original kit's API v1 source. Upstream may prefer a different capability-negotiation arrangement. Do not silently change api_version 1, claim unmodified launchers supply playback or promise maintainer acceptance.

## Actual implementation status

All six named apps passed the controls listed in [MULTI-APP.md](MULTI-APP.md), including Moonfin/Jellyfin seek and stop and YouTube/TizenTube finite-video seek. Twitch/KickTV provide live state, broadcast titles, play/pause and confirmed menu idle. Twitch uses channel-matched page headings; KickTV uses a bounded lookup of its normal channel metadata API. Both dock tiles and launcher remote remaps use managed launch; other routes are not automatically reattached. Title-source changes, lookup delays and app/API failures remain compatibility limits.

Confirmed readable Cobalt menus report idle and retain the correct YouTube/TizenTube name. Actual app updates/reinstalls, long DVR pauses, extended memory pressure and broader TV/firmware compatibility remain outside completed hardware acceptance. The later Home startup-policy repair and intermittent HA deep-standby wake must be described separately from earlier successful lifecycle checks.

The owner currently has a custom TV stack, a modified QuietThings HA integration and a personal dashboard. None should be described as merged upstream or a generally released six-app installer. See [README.md](README.md) for protocol, exact constraints and validation boundaries.
