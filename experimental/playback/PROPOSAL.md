# Proposed upstream contribution

QuietThings currently provides power, volume and HDMI control but does not expose the foreground player's actual playback state. A local optional companion can add that data while retaining the existing v1 contract and launcher customizations.

This development branch contains three separable contributions:

1. Compatibility with HA 2026.5.4 and its samsungtvws 2.7.2 requirement, alongside the current HA 2026.10.4/samsungtvws 3.0.7 test target. Test fixtures accommodate both core APIs, and a CI matrix exercises both.
2. Honest power uncertainty: inability to read Samsung REST is unknown, not an inferred off state. Explicit standby/off and the existing availability threshold remain authoritative.
3. An opt-in .NET 6 playback companion with provider boundaries, a foreground-gated Jellyfin/Moonfin provider, authenticated status/control endpoints, and HA playback properties/capabilities. The default remains disabled.

The separate companion port is a development choice that avoids modifying the kit's frozen v1 server source. Before proposing a merge, discuss whether upstream prefers this shape or a negotiated additive extension on the original listener. Do not silently change api_version 1 or the meaning of paused.

Current device evidence is limited to temporary read-only diagnostics and loopback HTTP. Persistent launcher installation, actual control delivery and standby/background behavior remain pending. No public PR should claim those tests passed. Other app providers remain a subsequent contribution; the provider interface has automated coverage, not proven all-app coverage.
