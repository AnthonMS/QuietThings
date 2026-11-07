# QuietThings API contract (api_version 1)

The QuietThings API is served from inside the Quiet Home launcher on the TV. The
Home Assistant integration `quietthings` is the reference client
(`custom_components/quietthings/api.py`). The server source is `src/LanApiCore.cs`
(routing and JSON) and `src/LanApi.cs` (sockets and token file).

## Fixed, do not change

Anything that talks to this API depends on these, so a patched launcher must keep them:

- Port 18765.
- Paths: `/health`, `/state`, `/hdmi/{1-4}`, `/volume`, `/mute`.
- Every JSON field name below.
- `Authorization: Bearer <token>` on every request, `/health` included.
- `api_version` 1. A change to the wire format means a new version number, never an edit inside version 1.

## Transport

- Plain HTTP/1.1 over TCP on all interfaces. No TLS. Treat it as a LAN-only service.
- Every reply carries `Content-Type: application/json` and `Connection: close`. A 401 also carries `WWW-Authenticate: Bearer`.
- Requests are handled one at a time. `Transfer-Encoding` requests are rejected. The query string is ignored.
- The request line and headers must fit in 8192 bytes. Bodies are capped at 1024 bytes.
- The server allows 3 s for reading a request.
- The reference client (the integration) uses a 10 s timeout for `POST /hdmi/N` and 5 s for everything else.

## Token

- The token is read from `lan-api.token` in the launcher's app data directory. If the app data directory cannot be resolved, the fallback is `/tmp/quietthings-api.token`.
- The file holds the token on one line. Surrounding whitespace is trimmed. The trimmed length must be 16 to 256 characters; anything else counts as no token.
- With no valid token, every request is answered 401.
- The file is checked for changes at most every 10 s, so a new token takes effect within about 10 s. A missing token is retried on each request.
- Deleting the file disables the API (all requests 401).
- The token is a secret. The integration never logs it and redacts it in diagnostics.

## Order of checks

1. Rate limit: more than 4 requests inside one second get 429, before anything is read.
2. Request parsing: 400, 408 or 431.
3. Authorization: 401.
4. Body size: 413.
5. Routing, then the handler.

## Errors

Every error is `{"ok": false, "error": "<text>"}` with an HTTP status:

| Status | Text | When |
|---|---|---|
| 400 | `bad request` | malformed request line |
| 400 | `bad content-length` | unparseable `Content-Length` |
| 400 | `transfer-encoding not supported` | `Transfer-Encoding` header present |
| 400 | `hdmi must be 1-4` | `/hdmi/` followed by anything but 1, 2, 3 or 4 |
| 400 | `body required` | `/volume` or `/mute` with an empty body |
| 400 | `malformed json` | body is not valid JSON |
| 400 | `body must be an object` | JSON body is not an object |
| 400 | `level must be a number` | `/volume` without a numeric `level` |
| 400 | `level must be an integer` | `/volume` with a fractional `level` |
| 400 | `mute must be true or false` | `/mute` without a boolean `mute` |
| 401 | `unauthorized` | missing, wrong or unconfigured token |
| 404 | `not found` | unknown path |
| 405 | `method not allowed` | known path, wrong method |
| 408 | `timeout` | a request that keeps arriving, but too slowly, is not complete after 3 s; a client that stops sending entirely gets its connection closed with no reply |
| 413 | `body too large` | `Content-Length` above 1024 |
| 429 | `rate limited` | more than 4 requests per second |
| 431 | `request too large` | request line and headers above 8192 bytes |
| 502 | `connect_source failed` | the TV refused the switch; the body also carries `connect_result` (the negative integer returned) |
| 502 | `volume unavailable` | volume or mute could not be read back after a set |
| 502 | `volume set failed` | the volume change was refused |
| 502 | `mute set failed` | the mute change was refused |
| 503 | `ui call failed` | the launcher's UI thread did not answer in time or threw |
| 503 | `internal error` | an unexpected failure inside the handler |
| 504 | `not confirmed` | `/hdmi/N`: the TV never reported the new input; see below |

## GET /health

```json
{"ok": true, "app": "quiet-home", "api_version": 1, "uptime_s": 1234, "paused": false}
```

- Needs the token like every other path. The integration relies on this: a 401 here means a wrong token, a connection error means the TV or the API is not there.
- `app` is `"quiet-home"` and names the host launcher, which is still Quiet Home. It is not the product name and clients should not depend on it.
- `uptime_s` is seconds since the API started.
- `paused` is true while the launcher is paused (another app is in front).

## GET /state

```json
{
  "ok": true,
  "api_version": 1,
  "paused": false,
  "selected": {"port": 2, "id": 14, "name": "Cable Box", "connected": true},
  "signal": {"source_id": 14, "active": true},
  "inputs": [
    {"port": 1, "id": 13, "name": "HDMI 1", "connected": false},
    {"port": 2, "id": 14, "name": "Cable Box", "connected": true},
    {"port": 3, "id": 15, "name": "Soundbar", "connected": true},
    {"port": 4, "id": 16, "name": "PC", "connected": true}
  ],
  "volume": {"level": 20, "mute": false}
}
```

- `inputs` always lists HDMI 1 to 4 in order. `id` is `12 + port` (13 to 16). A TV or launcher with fewer physical ports still reports four; the missing ones read as not connected.
- `name` is the name set on the TV, else the name the TV detected, else the launcher's own fallback name, else `HDMI n`.
- `connected` is whether the TV sees a device on that port.
- `selected` is `null` when the selected HDMI port is unknown; otherwise it is the matching entry of `inputs`.
- `signal.source_id` is the TV's raw source id, or -1 when the TV's source type cannot be read. `signal.active` is true only when `source_id` equals `12 + port` of the selected port. The selected input and its signal are separate facts.
- `volume` is `null` when either volume or mute cannot be read; otherwise `level` is 0 to 100.

## POST /hdmi/{1-4}

No body. The server asks the TV to switch, then checks every 300 ms for up to 6 s whether the TV now reports that port.

- 200 `{"ok": true, "selected": {...}, "signal": {...}}` once the port is confirmed. `signal.active` can still be false: the input is selected but no device is sending.
- 504 `{"ok": false, "error": "not confirmed", "selected": ..., "signal": ...}` when the TV did not report the new port in time. The switch may still have happened.
- 502 `connect_source failed` when the TV refused the switch immediately.
- 503 when the UI thread failed to answer. 400 for a bad port, 405 for a method other than POST.

A switch can take over 6 s in total, so clients need a timeout longer than that.

## POST /volume

Body `{"level": 0-100}`. The level must be an integer value; numbers outside 0 to 100 are clamped. Reply:
`200 {"ok": true, "volume": {"level": int, "mute": bool}}`, read back from the TV after the change.

## POST /mute

Body `{"mute": true|false}`. Same 200 reply as `/volume`.

## Client mapping

How the reference client turns replies into exceptions:

| Condition | Exception |
|---|---|
| HTTP 401 | `QuietThingsAuthError` (starts reauth) |
| connect error or timeout | `QuietThingsUnavailable` |
| any other HTTP error, `ok: false`, or unusable body | `QuietThingsRejected` (message from `error`) |

## Known limits

- Whether the launcher process may read `memory/eden/source/current_source` is unverified on the TV. If that read fails, the selected port falls back to `memory/system/source_type`. If `POST /hdmi/N` returns 504 on a port with no signal although the TV switched, this is the likely cause.
- The API has no power, settings, app or token-management endpoints. Power stays on the stock Samsung transports below.

## Stock Samsung transports (no QuietThings API needed)

- REST `http://host:8001/api/v2/`: `device.PowerState` (`on` or `standby`), `device.id`,
  `name`, `modelName`, `wifiMac`.
- WebSocket 8002 through `samsungtvws` with a pairing token: `KEY_POWER`.
- Wake-on-LAN: UDP port 9 to the configured broadcast address, payload 6 x `0xff`
  then the MAC 16 times.
