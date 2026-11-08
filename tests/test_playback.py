"""Opt-in companion behavior across session changes, failures and actual media states."""

from datetime import UTC, datetime, timedelta

import aiohttp
import pytest
from homeassistant.components.media_player import MediaPlayerEntityFeature
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import entity_registry as er

from custom_components.quietthings.diagnostics import async_get_config_entry_diagnostics
from custom_components.quietthings.playback import parse_sample

from .conftest import API_TOKEN, HOST, AiohttpClientMockResponse

MP = "media_player.living_room_tv"
BASE = f"http://{HOST}:18766"


def body(state="paused", **changes):
    now = datetime.now(UTC).isoformat()
    data = {
        "app_id": "MoonfinApp.moonfin",
        "state": state,
        "adapter": "jellyfin-session",
        "title": "Private sample title",
        "media_type": "Movie",
        "position_seconds": 12,
        "duration_seconds": 120,
        "observed_at": now,
        "position_updated_at": now,
        "commands": ["play", "pause", "stop", "seek"],
    }
    data.update(changes)
    return {"ok": True, "extension_version": 1, "playback": data}


async def setup_playback(hass, config_entry, aioclient_mock, payload=None):
    response = {"body": payload or body(), "offline": False}

    async def handler(method, url, data):
        if response["offline"]:
            return AiohttpClientMockResponse(
                method, url, exc=aiohttp.ClientConnectionError("offline")
            )
        return AiohttpClientMockResponse(method, url, status=200, json=response["body"])

    aioclient_mock.get(f"{BASE}/v1/playback", side_effect=handler)
    config_entry.add_to_hass(hass)
    hass.config_entries.async_update_entry(
        config_entry, options={"playback_port": 18766}
    )
    assert await hass.config_entries.async_setup(config_entry.entry_id)
    await hass.async_block_till_done()
    return config_entry.runtime_data, response


async def test_playback_is_opt_in(hass, mock_tv, setup_integration, aioclient_mock):
    entry = await setup_integration()
    assert entry.runtime_data.playback_coordinator is None
    assert not any(call[1].port == 18766 for call in aioclient_mock.mock_calls)


@pytest.mark.parametrize("state", ["playing", "paused", "buffering", "idle"])
async def test_actual_state(hass, mock_tv, config_entry, aioclient_mock, state):
    await setup_playback(hass, config_entry, aioclient_mock, body(state))
    media = hass.states.get(MP)
    assert media.state == state
    assert media.attributes["app_id"] == "MoonfinApp.moonfin"
    if state == "idle":
        assert "media_title" not in media.attributes
    else:
        assert media.attributes["media_title"] == "Private sample title"
        assert media.attributes["media_position"] == 12
        assert media.attributes["media_duration"] == 120
        assert media.attributes["media_content_type"] == "movie"


async def test_hardware_off_clears_stale_playback(
    hass, mock_tv, config_entry, aioclient_mock
):
    mock_tv.power = "standby"
    await setup_playback(hass, config_entry, aioclient_mock)
    media = hass.states.get(MP)
    assert media.state == "off"
    assert "media_title" not in media.attributes
    assert not (media.attributes["supported_features"] & MediaPlayerEntityFeature.SEEK)


async def test_extension_failure_keeps_power_and_volume(
    hass, mock_tv, config_entry, aioclient_mock
):
    runtime, response = await setup_playback(hass, config_entry, aioclient_mock)
    response["offline"] = True
    await runtime.playback_coordinator.async_refresh()
    await hass.async_block_till_done()
    media = hass.states.get(MP)
    assert media.state == "on"
    assert media.attributes["volume_level"] == 0.2
    assert "media_title" not in media.attributes
    with pytest.raises(HomeAssistantError):
        await hass.services.async_call(
            "media_player", "media_play", {"entity_id": MP}, blocking=True
        )


async def test_unsupported_app_never_inherits_old_media(
    hass, mock_tv, config_entry, aioclient_mock
):
    runtime, response = await setup_playback(hass, config_entry, aioclient_mock)
    response["body"] = body("unknown", app_id="YouTube", adapter=None)
    await runtime.playback_coordinator.async_refresh()
    await hass.async_block_till_done()
    media = hass.states.get(MP)
    assert media.state == "on"
    assert media.attributes["app_id"] == "YouTube"
    assert "media_title" not in media.attributes
    assert not media.attributes["supported_features"] & MediaPlayerEntityFeature.PAUSE


async def test_session_replacement_preserves_entity_identity(
    hass, mock_tv, config_entry, aioclient_mock
):
    runtime, response = await setup_playback(hass, config_entry, aioclient_mock)
    registry = er.async_get(hass)
    identity = registry.async_get(MP).unique_id
    response["body"] = body("playing", title="After client reinstall")
    await runtime.playback_coordinator.async_refresh()
    await hass.async_block_till_done()
    assert registry.async_get(MP).unique_id == identity
    assert hass.states.get(MP).attributes["media_title"] == "After client reinstall"


@pytest.mark.parametrize(
    "service,action,extra",
    [
        ("media_play", "play", {}),
        ("media_pause", "pause", {}),
        ("media_stop", "stop", {}),
        ("media_seek", "seek", {"seek_position": 44}),
    ],
)
async def test_transport_uses_companion_and_waits_for_observation(
    hass, mock_tv, config_entry, aioclient_mock, service, action, extra
):
    await setup_playback(hass, config_entry, aioclient_mock)
    aioclient_mock.post(f"{BASE}/v1/command", json={"ok": True, "accepted": True})
    await hass.services.async_call(
        "media_player", service, {"entity_id": MP, **extra}, blocking=True
    )
    posts = [
        c
        for c in aioclient_mock.mock_calls
        if c[0].lower() == "post" and c[1].path == "/v1/command"
    ]
    assert len(posts) == 1
    assert posts[0][2]["action"] == action
    if action == "seek":
        assert posts[0][2]["position_seconds"] == 44
    assert posts[0][3]["Authorization"] == f"Bearer {API_TOKEN}"
    assert hass.states.get(MP).state == "paused"  # No invented optimistic play state.


async def test_diagnostics_omit_viewing_metadata(
    hass, mock_tv, config_entry, aioclient_mock
):
    await setup_playback(hass, config_entry, aioclient_mock)
    result = await async_get_config_entry_diagnostics(hass, config_entry)
    assert result["playback"]["enabled"]
    assert "Private sample title" not in str(result)
    assert API_TOKEN not in str(result)


@pytest.mark.parametrize(
    "change",
    [
        {"state": "on"},
        {"observed_at": "yesterday"},
        {"app_id": None},
        {"commands": ["shell"]},
        {"title": 42},
        {"position_seconds": 999},
        {"observed_at": "2000-01-01T00:00:00+00:00"},
        {"position_updated_at": None},
    ],
)
def test_invalid_sample_rejected(change):
    with pytest.raises(ValueError):
        parse_sample(body(**change))


def test_extension_version_must_be_integer():
    payload = body()
    payload["extension_version"] = True
    with pytest.raises(ValueError):
        parse_sample(payload)


async def test_options_can_enable_and_disable_companion(
    hass, mock_tv, config_entry, aioclient_mock, setup_integration
):
    await setup_integration()
    aioclient_mock.get(f"{BASE}/v1/playback", json=body())
    for port in (18766, 0):
        result = await hass.config_entries.options.async_init(config_entry.entry_id)
        result = await hass.config_entries.options.async_configure(
            result["flow_id"],
            {
                "scan_interval": 10,
                "mac": config_entry.data["mac"],
                "broadcast": config_entry.data["broadcast"],
                "playback_port": port,
            },
        )
        await hass.async_block_till_done()
        assert (config_entry.runtime_data.playback_coordinator is not None) == bool(
            port
        )
    assert hass.states.get(MP).state == "on"
    assert "media_title" not in hass.states.get(MP).attributes


@pytest.mark.parametrize("status", [401, 503])
async def test_companion_error_does_not_disable_hardware(
    hass, mock_tv, config_entry, aioclient_mock, status
):
    aioclient_mock.get(f"{BASE}/v1/playback", status=status, json={"ok": False})
    config_entry.add_to_hass(hass)
    hass.config_entries.async_update_entry(
        config_entry, options={"playback_port": 18766}
    )
    assert await hass.config_entries.async_setup(config_entry.entry_id)
    await hass.async_block_till_done()
    assert hass.states.get(MP).state == "on"
    assert hass.states.get(MP).attributes["volume_level"] == 0.2


def test_future_position_timestamp_rejected():
    with pytest.raises(ValueError):
        parse_sample(
            body(
                position_updated_at=(
                    datetime.now(UTC) + timedelta(seconds=10)
                ).isoformat()
            )
        )


async def test_stock_timeout_does_not_invent_power_from_playback(
    hass, mock_tv, config_entry, aioclient_mock
):
    runtime, _response = await setup_playback(hass, config_entry, aioclient_mock)
    mock_tv.stock_down = True
    await runtime.stock_coordinator.async_refresh()
    await hass.async_block_till_done()
    media = hass.states.get(MP)
    assert media.state == "unknown"
    assert "media_title" not in media.attributes


async def test_media_expires_without_waiting_for_a_long_poll(
    hass, mock_tv, config_entry, aioclient_mock, freezer
):
    from pytest_homeassistant_custom_component.common import async_fire_time_changed

    runtime, _response = await setup_playback(hass, config_entry, aioclient_mock)
    # Simulate a long configured poll: only the entity's expiration timer should fire.
    for coordinator in (
        runtime.stock_coordinator,
        runtime.api_coordinator,
        runtime.playback_coordinator,
    ):
        coordinator.update_interval = timedelta(seconds=300)
        coordinator.async_set_updated_data(coordinator.data)
    assert hass.states.get(MP).state == "paused"
    freezer.tick(timedelta(seconds=16))
    async_fire_time_changed(hass, datetime.now(UTC))
    await hass.async_block_till_done()
    assert hass.states.get(MP).state == "on"
    assert "media_title" not in hass.states.get(MP).attributes
