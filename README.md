<p align="center">   <img src="assets/logomark.png" alt="QuietThings logo" width="120"> </p>

<h1 align="center">QuietThings</h1>

<p align="center">   Local control of Samsung TVs running QuietTizen, directly from Home Assistant. </p>

QuietThings is a Home Assistant integration for Samsung TVs running [QuietTizen](https://github.com/jss8649/quiet-tizen). It communicates with your TV over your local network, with no cloud connection, account, or additional hardware required.

The project has two parts:

- **The QuietThings API patch kit** (`kit/`), which adds a small local API to your existing
  QuietTizen installation. Give the kit to your AI assistant to patch your launcher.
- **The integration** (`custom_components/quietthings`), which you install in Home Assistant.

## What it does

Development-branch note, 10 November 2026: this working tree also contains a modified HA playback client and an optional TV companion. Six-app playback was tested on one customized Q60C. It requires the additional components documented in [experimental/playback](experimental/playback/README.md); Twitch/KickTV and Cobalt observation need more TV setup than the original API kit. The list below describes the original base integration. The playback stack is not merged upstream, and the owner has paused PR publication for daily-use testing.

- Turn the TV on and off
- Adjust volume and mute audio
- Switch HDMI inputs
- Report the state of all the above, refreshed every 10 seconds by default

## How it fits together

```
Home Assistant
  QuietThings integration
     |                         |
     | QuietThings API         | stock Samsung ports 8001 / 8002
     | (port 18765, token)     | (state, pairing, power)
     v                         v
  Samsung TV
    Quiet Home launcher + QuietThings API    stock TV services
```

The QuietThings API runs inside the Quiet Home launcher and handles volume, mute, and
HDMI input selection. Power state and on/off controls use the TV's own services
(Wake-on-LAN, followed by the stock remote channel), so power controls remain available
when the launcher is not running.

## Requirements

- A Samsung TV with Developer Mode and a working QuietTizen installation
- Home Assistant 2026.5.4 or newer
- The TV and Home Assistant connected to the same LAN

QuietThings was developed using a Samsung QN90B, while QuietTizen itself was
built for the U8000F. Other models may work, but compatibility is not guaranteed.

## Install the TV side

For the complete customized Quiet Home stack, including six-app playback, Community Apps and TV notifications, start with the launcher's [AI installation guide](https://github.com/AnthonMS/samsung-tv-launcher/blob/codex/quietthings-playback/docs/AI-INSTALLATION.md). Use the matching development branches it lists. The original patch kit below provides base control; it does not install all of those additional components.

<h4 align="center">NOTE: You must run the patch kit on the same PC you used to install QuietTizen</h4>

Follow [kit/README.md](kit/README.md). Download the `kit` folder and give it to your AI
assistant along with your QuietTizen launcher source, then have it follow
[kit/AGENTS.md](kit/AGENTS.md). The kit tests the patch on your PC before making any changes
to the TV and supports rollback using `kit/recipes/rollback-api.sh`. It also generates the
API token for you.

## Install the integration

You can do the two parts in whichever order you prefer, but you can't add a TV to the integration until that TV has been patched.

### With HACS

1. In Home Assistant, open **HACS**, select the three-dot menu at the top right, and choose **Custom repositories**.
2. Paste this repository's GitHub URL, select **Integration** as the category, and choose **Add**.
3. Find **QuietThings** in HACS and select **Download**.
4. Restart Home Assistant, then add the integration by following steps 3 through 5 under **Manually** below.

### Manually

1. Copy `custom_components/quietthings` into Home Assistant's `config/custom_components/` folder.
2. Restart Home Assistant.
3. Go to **Settings**, open **Devices and services**, select **Add integration**, and choose **QuietThings**.
4. Enter a name, the TV's IP address, the API port (default: 18765), and the API token.
   The TV's MAC address and Wake-on-LAN broadcast address are detected automatically; you are only asked for the MAC if it can't be found.
5. When prompted to pair, select **Allow** on the TV within 60 seconds.
   If nobody selects **Allow**, setup can get stuck, and a new attempt for the same TV says the setup is already in progress until Home Assistant is restarted. Restart Home Assistant and try again with someone at the TV.

Add the integration once per TV. You can rename the device and its entities later in
the Home Assistant UI, and the integration will preserve any names you set. You can adjust
the polling interval (default 10 seconds), the MAC address and the broadcast address in the
integration's options.

## Entities

The integration creates one device per TV.

| Entity                                | Notes                                                        |
| ------------------------------------- | ------------------------------------------------------------ |
| `media_player` (uses the device name) | Reports power state from fresh native display observations when supported, with the TV's stock service on port 8001 as fallback. Supports power on (Wake-on-LAN, with `KEY_POWER` as a fallback), power off, volume level, mute, and volume up/down in steps of 1. Sources range from `HDMI 1` to `HDMI 4`, with labels such as `HDMI 2 (Cable Box)`. `select_source` accepts `HDMI 4` and `HDMI 4 (PC)`; any other value is rejected. |
| `select` HDMI input                   | Options are exactly `HDMI 1` through `HDMI 4`. Attributes: `labels`, `connected`. |
| `sensor` Current input                | Reports the selected input name. Attributes: `port`, `id`.   |
| `sensor` Input signal                 | Reports `active`, `no_signal` (the selected port has no signal), or `unknown`. |

## Behavior and limitations

- The select entity and both sensors are unavailable if the most recent QuietThings API `/state` request failed.
- The media player remains available for power control. A loss of communication with the stock port is reported as unknown; it does not prove that the TV is off. The media player stays available as a Wake-on-LAN command target even when all TV endpoints sleep. Its state is unknown when power cannot be observed. Volume/source data and playback are not invented while offline. Toggle checks current power and attempts wake for off or unknown, rather than treating unknown as a power-off request.
- The integration checks the API version during setup. A TV reporting a different version is not accepted.
- Keep the API token private. Do not paste it into chats, logs, or issues. Diagnostic downloads redact it.
- The API allows approximately 4 requests per second. Requests exceeding this rate receive a 429 response.
- The API contract is documented in [kit/CONTRACT.md](kit/CONTRACT.md).

## Testing

Home Assistant Core requires Linux APIs, so the integration tests run on Linux
(including WSL). The CI matrix tests HA 2026.5.4 with its stock samsungtvws 2.7.2
and HA 2026.10.4 with samsungtvws 3.0.7. Test dependencies also follow each core's
package constraints, including async-timeout 4.0.3.
See `requirements_test.txt` and `requirements_test_2026_5.txt` for details.

```
python3.14 -m venv .venv   # Linux, Python >= 3.14
source .venv/bin/activate
pip install -r requirements_test.txt
python -m pytest tests -q
```

The kit includes its own checks:

```
python kit/tools/predeploy.py --launcher <your launcher source folder>
python kit/tools/test_tools.py
dotnet run --project kit/tests/LanApiTests.csproj
```

`predeploy.py` checks the patched launcher on your PC and lists anything it could not verify.
The C# tests require the .NET 6 SDK.

## Credits

QuietThings builds on the work of the following projects and contributors:

- [QuietTizen](https://github.com/jss8649/quiet-tizen) by jss8649 provides the launcher this
  project patches (MIT, Copyright (c) 2026 Quiet Tizen contributors). The kit reproduces a few
  lines from QuietTizen's `Home.cs` under that MIT notice. See [kit/NOTICE.md](kit/NOTICE.md).
- Projects credited by QuietTizen:
  - [Free My TV](https://github.com/simbasjungles/free-my-tv) (MIT): layout, palette, and focus treatment
  - [samsung-tv-root](https://github.com/chris-ritsen/samsung-tv-root) (Unlicense): the archive-launcher access route
  - Material Symbols (Apache-2.0), via Free My TV
  - [Inter](https://github.com/rsms/inter) (SIL OFL 1.1), by The Inter Project Authors
- [samsungtvws](https://github.com/xchwarze/samsung-tv-ws-api) by xchwarze (LGPL-3.0) provides
  communication with the TV's stock remote channel. Home Assistant installs it as a dependency;
  it is not included in this repository.

QuietThings does not include Samsung firmware, SDK assemblies, SDB, or any QuietTizen assets.

It is not affiliated with or endorsed by Samsung or the creators of QuietTizen.

## License

MIT. See [LICENSE](LICENSE).


## Experimental playback extension

The development branch adds an optional playback companion for actual Moonfin/Jellyfin state and progress, stable TV entity identity across client-session changes, and conditional play/pause/stop/seek controls. The original API v1 remains unchanged. The companion is disabled by default for new HA configurations. The custom development stack has passed the documented checks on the owner's TV; it is not a general installed-TV release. See [experimental/playback/README.md](experimental/playback/README.md) for architecture, protocol, validation and remaining device tests.
## Optional Quiet Home notifications

The custom Quiet Home development companion can advertise notification capabilities in addition to playback. This working copy adds `quietthings.notify`, `quietthings.dismiss_notification` and `quietthings_notification_action` events. They require the optional playback port and the compatible custom TV build. Stock QuietThings/QuietTizen do not gain an overlay from the HA change alone.

Home Assistant sends title/message, a live camera preview or bounded URL image/GIF and optional named buttons to the TV. Navigation is captured automatically; OK/dismiss is selected first and closing the notice returns input to the app. A new accepted notice replaces the visible one without queuing it. Images are resized in HA, so camera URLs and credentials never reach the TV. Buttons emit explicit, expiring events rather than executing HA services on the TV. A persisted cursor prevents replay after restart. The service requires an explicit QuietThings configuration entry ID. See [notification services](custom_components/quietthings/services.yaml) and the launcher's `docs/NOTIFICATIONS.md` for examples and limits. The optional package [doorbell-tv.yaml](examples/doorbell-tv.yaml) demonstrates doorbell and camera-zone person triggers with a shared TV-on script and explicit lock callback; the integration itself does not install automations.


### Optional native display power

A matching Quiet Home build offers authenticated GET `/power` on the existing control port. It reads the native Tizen display state, reports `power` (`on`, `off` or `unknown`), `power_source: tizen-display` and a timezone-qualified `observed_at`. The integration validates source, value and freshness before preferring it over Samsung REST, including power-command guards. Legacy hosts returning 404 and unknown/error observations retain stock fallback. The media player exposes `power_source` and `samsung_power_state` for diagnosis.

This addresses a reproduced Samsung standby report while the panel and YouTube were actually on. Two digital-remote off/on cycles passed on the development TV, including 45-second standby. Native off also clears background media. This is not certification of all sleep durations, firmware or network wake conditions. For a universal media player, configure its state and power command authority to this QuietThings entity; leaving Samsung as its explicit state authority bypasses the correction. The existing token and trusted-LAN security boundaries remain unchanged.


When wrapping this entity in a universal player, forward its `toggle` command explicitly to `media_player.toggle` on the QuietThings entity, as well as forwarding turn_on/turn_off. HA's default universal toggle otherwise treats an unknown state as turn_off. Use QuietThings for `volume_level`, `is_volume_muted` and the volume commands too; a Samsung child reporting stale off may still accept volume writes while omitting its readback attributes. Other universal controls can keep their existing routes.
