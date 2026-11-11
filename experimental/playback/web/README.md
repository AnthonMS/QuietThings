# App-owned web media probe

`media-probe.js` is an ES5-compatible reader/command adapter for actual HTML5 video and Samsung AVPlay. The experimental protected bridge and app contexts are installed on the development Q60C for Twitch/KickTV, with real state, titles, HA play/pause and confirmed menu idle accepted. This is one-TV evidence, not a universal release.

Create a probe with `createQuietThingsMediaProbe({document, navigator, webapis, metadata, isMenu})`. An optional metadata callback returns `title`, `key` and `live`; it must use current app/player identity. `read()` returns bounded metadata, actual player state, seconds, a transient `instance` and current capabilities. Signed source URLs stay in page memory. The core probe installs no timers or transport. An optional app-specific isMenu callback can confirm idle; otherwise an unused AVPlay must not turn a loading/failed player view into a menu.

`twitch-context.js` reads a unique channel/title heading pair matching the active route, including hidden headings omitted by innerText. `kick-context.js` observes native opens and uses the actual channel label to fetch session_title from the normal Kick channel API. Its bounded cache runs on reads for ready playback, validates channel/generation, rejects late replies and expires old titles. This adds normal app-side internet metadata requests without a new account/API key. The five-second request timeout, 30-second refresh and 90-second stale limit are not continuous background timers.

`command(action, position, expectedInstance)` rechecks ownership before acting. Changes of player, source URL or stream object invalidate old commands. HTML5 seek checks the current seekable ranges. AVPlay seek requires an explicit VOD identity and `live: false`. Live playback has no finite duration or advertised seek. Multiple active HTML5 players return unknown rather than selecting arbitrarily.

The installed host verifies foreground/page ownership, bounds requests, expires observations and supplies authenticated transport. A probe instance remains page-local rather than globally unique. Root process/document leases provide the wider boundary. Unit tests alone do not prove device compatibility; the accepted hardware scope is in ../MULTI-APP.md.

Run local checks with:

```sh
node experimental/playback/tests/web-media-probe.test.cjs
```
