"""Regressions for stale Samsung standby after a successful network wake."""
from datetime import UTC, datetime, timedelta
from unittest.mock import AsyncMock, patch

import pytest
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from custom_components.quietthings.api import QuietThingsApi, QuietThingsUnavailable
from custom_components.quietthings.stock import StockTv
from .conftest import API_TOKEN, API_URL, HOST, MAC
from .test_playback import MP, body, setup_playback


async def test_native_on_overrides_stale_samsung_standby(hass, mock_tv, config_entry, aioclient_mock):
    mock_tv.power = "standby"
    mock_tv.display_power = "on"
    runtime, _ = await setup_playback(hass, config_entry, aioclient_mock, body("playing"))
    state = hass.states.get(MP)
    assert state.state == "playing"
    assert state.attributes["media_title"] == "Private sample title"
    assert runtime.stock_coordinator.data["power_source"] == "tizen-display"


async def test_native_off_clears_even_fresh_background_playback(hass, mock_tv, config_entry, aioclient_mock):
    mock_tv.power = "on"
    mock_tv.display_power = "off"
    await setup_playback(hass, config_entry, aioclient_mock, body("playing"))
    state = hass.states.get(MP)
    assert state.state == "off"
    assert state.attributes.get("media_title") is None
    assert state.attributes.get("media_position") is None


async def test_native_observation_survives_stock_transport_failure(hass, mock_tv, setup_integration):
    mock_tv.display_power = "on"
    entry = await setup_integration()
    mock_tv.stock_down = True
    for _ in range(4):
        await entry.runtime_data.stock_coordinator.async_refresh()
    await hass.async_block_till_done()
    assert hass.states.get(MP).state == "on"
    assert not entry.runtime_data.stock_coordinator.lost


@pytest.mark.parametrize("patch_body", [
    {"power":"unknown"}, {"power":True}, {"power":[]},
    {"power_source":"process-running"}, {"observed_at":"invalid"},
    {"observed_at":datetime.now(UTC).replace(tzinfo=None).isoformat()},
    {"observed_at":(datetime.now(UTC)-timedelta(minutes=2)).isoformat()},
    {"observed_at":(datetime.now(UTC)+timedelta(minutes=2)).isoformat()},
])
async def test_rejects_unknown_or_unfresh_native_power(hass, aioclient_mock, patch_body):
    payload = {"ok":True,"power":"on","power_source":"tizen-display","observed_at":datetime.now(UTC).isoformat(),**patch_body}
    aioclient_mock.get(f"{API_URL}/power",json=payload)
    api=QuietThingsApi(async_get_clientsession(hass), HOST, 18765, API_TOKEN)
    assert await api.display_power() is None


async def test_turn_on_does_not_toggle_an_already_lit_panel(hass):
    tv=StockTv(async_get_clientsession(hass),HOST,MAC,"192.0.2.255",display_power=AsyncMock(return_value="on"))
    with patch("custom_components.quietthings.stock.send_magic_packet",AsyncMock()) as wol, patch.object(tv,"_send_power_key",AsyncMock()) as key:
        await tv.power_on()
    wol.assert_not_called()
    key.assert_not_called()


async def test_power_off_uses_native_on_despite_stale_rest_standby(hass):
    tv=StockTv(async_get_clientsession(hass),HOST,MAC,"192.0.2.255",display_power=AsyncMock(return_value="on"))
    with patch.object(tv,"_send_power_key",AsyncMock(return_value=True)) as key, patch.object(tv,"_wait_power",AsyncMock(return_value=True)):
        await tv.power_off()
    key.assert_awaited_once_with("on")


async def test_native_failure_falls_back_to_original_stock_state(hass):
    tv=StockTv(async_get_clientsession(hass),HOST,MAC,"192.0.2.255",display_power=AsyncMock(side_effect=QuietThingsUnavailable("asleep")))
    with patch.object(tv,"device_info",AsyncMock(return_value={"PowerState":"Standby"})):
        assert await tv.power_state()=="standby"
