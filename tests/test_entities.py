"""Entities, services, availability, naming, diagnostics and log hygiene."""

from __future__ import annotations

import json
import logging
from unittest.mock import AsyncMock, patch

import pytest
from homeassistant.config_entries import ConfigEntryState
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers import device_registry as dr
from homeassistant.helpers import entity_registry as er

from custom_components.quietthings.diagnostics import (
    async_get_config_entry_diagnostics,
)

from .conftest import API_TOKEN, DEVICE_ID, MAC, WS_TOKEN


def get_device(registry, entry):
    if hasattr(registry, "async_get_device_by_identifier"):
        return registry.async_get_device_by_identifier(
            ("quietthings", DEVICE_ID), entry.entry_id
        )
    return registry.async_get_device(identifiers={("quietthings", DEVICE_ID)})


MP = "media_player.living_room_tv"
SEL = "select.living_room_tv_hdmi_input"
CUR = "sensor.living_room_tv_current_input"
SIG = "sensor.living_room_tv_input_signal"


async def call(hass, domain, service, **data):
    await hass.services.async_call(domain, service, data, blocking=True)


async def refresh(hass, entry, which="api"):
    runtime = entry.runtime_data
    coord = runtime.api_coordinator if which == "api" else runtime.stock_coordinator
    await coord.async_refresh()
    await hass.async_block_till_done()


def api_posts(aioclient_mock):
    return [("post", str(c[1].path), c[2]) for c in aioclient_mock.mock_calls if c[0].lower() == "post"]


# ---- naming and ids ---------------------------------------------------------


async def test_name_and_unique_ids(hass, mock_tv, setup_integration):
    entry = await setup_integration()
    assert entry.title == "Living Room TV"
    device = get_device(dr.async_get(hass), entry)
    assert device.name == "Living Room TV"
    assert device.manufacturer == "Samsung"
    assert device.model == "QN65QN90BAFXZA"

    registry = er.async_get(hass)
    entries = er.async_entries_for_config_entry(registry, entry.entry_id)
    assert {e.entity_id for e in entries} == {MP, SEL, CUR, SIG}
    assert all(e.unique_id and e.unique_id.startswith(DEVICE_ID) for e in entries)
    assert len({e.unique_id for e in entries}) == 4
    assert all(e.has_entity_name for e in entries)
    assert hass.states.get(MP).name == "Living Room TV"
    assert hass.states.get(SEL).name == "Living Room TV HDMI input"
    assert hass.states.get(CUR).name == "Living Room TV Current input"
    assert hass.states.get(SIG).name == "Living Room TV Input signal"


async def test_user_renames_survive_reload(hass, mock_tv, setup_integration):
    entry = await setup_integration()
    dev_reg = dr.async_get(hass)
    device = get_device(dev_reg, entry)
    dev_reg.async_update_device(device.id, name_by_user="Den TV")
    er.async_get(hass).async_update_entity(SEL, name="Input picker")

    assert await hass.config_entries.async_reload(entry.entry_id)
    await hass.async_block_till_done()

    device = dev_reg.async_get(device.id)
    assert device.name_by_user == "Den TV"
    assert er.async_get(hass).async_get(SEL).name == "Input picker"
    assert hass.states.get(SEL).name == "Input picker"


# ---- state mapping ----------------------------------------------------------


async def test_media_player_state(hass, mock_tv, setup_integration):
    await setup_integration()
    state = hass.states.get(MP)
    assert state.state == "on"
    assert state.attributes["volume_level"] == 0.2
    assert state.attributes["is_volume_muted"] is False
    assert state.attributes["source"] == "HDMI 2 (Cable Box)"
    assert state.attributes["source_list"] == [
        "HDMI 1",
        "HDMI 2 (Cable Box)",
        "HDMI 3 (HW-Q800B)",
        "HDMI 4 (PC)",
    ]


async def test_standby_is_off(hass, mock_tv, setup_integration):
    mock_tv.power = "standby"
    entry = await setup_integration()
    assert hass.states.get(MP).state == "off"
    assert hass.states.get(SEL).state == "HDMI 2"


async def test_select_and_sensors_with_signal(hass, mock_tv, setup_integration):
    await setup_integration()
    assert hass.states.get(SEL).state == "HDMI 2"
    assert hass.states.get(SEL).attributes["options"] == [
        "HDMI 1",
        "HDMI 2",
        "HDMI 3",
        "HDMI 4",
    ]
    assert hass.states.get(SEL).attributes["labels"]["HDMI 2"] == "Cable Box"
    assert hass.states.get(SEL).attributes["connected"]["HDMI 1"] is False
    assert hass.states.get(CUR).state == "Cable Box"
    assert hass.states.get(CUR).attributes["port"] == 2
    assert hass.states.get(CUR).attributes["id"] == 14
    assert hass.states.get(SIG).state == "active"


async def test_selected_with_no_signal(hass, mock_tv, setup_integration):
    mock_tv.selected_port = 4
    mock_tv.no_signal_ports = {4}
    await setup_integration()
    assert hass.states.get(SEL).state == "HDMI 4"
    assert hass.states.get(CUR).state == "PC"
    assert hass.states.get(SIG).state == "no_signal"


async def test_nothing_selected_is_unknown(hass, mock_tv, setup_integration):
    mock_tv.selected_port = None
    await setup_integration()
    assert hass.states.get(SIG).state == "unknown"
    assert hass.states.get(SEL).state == "unknown"
    assert hass.states.get(CUR).state == "unknown"
    assert hass.states.get(MP).attributes.get("source") is None


# ---- volume -----------------------------------------------------------------


async def test_volume_set(hass, aioclient_mock, mock_tv, setup_integration):
    await setup_integration()
    await call(hass, "media_player", "volume_set", entity_id=MP, volume_level=0.35)
    assert ("post", "/volume", {"level": 35}) in api_posts(aioclient_mock)
    assert hass.states.get(MP).attributes["volume_level"] == 0.35


async def test_volume_up_down_step_one(hass, aioclient_mock, mock_tv, setup_integration):
    await setup_integration()
    await call(hass, "media_player", "volume_up", entity_id=MP)
    assert hass.states.get(MP).attributes["volume_level"] == 0.21
    await call(hass, "media_player", "volume_down", entity_id=MP)
    await call(hass, "media_player", "volume_down", entity_id=MP)
    assert hass.states.get(MP).attributes["volume_level"] == 0.19
    levels = [b["level"] for m, p, b in api_posts(aioclient_mock) if p == "/volume"]
    assert levels == [21, 20, 19]


async def test_mute(hass, aioclient_mock, mock_tv, setup_integration):
    await setup_integration()
    await call(hass, "media_player", "volume_mute", entity_id=MP, is_volume_muted=True)
    assert ("post", "/mute", {"mute": True}) in api_posts(aioclient_mock)
    assert hass.states.get(MP).attributes["is_volume_muted"] is True


# ---- source -----------------------------------------------------------------


@pytest.mark.parametrize(
    ("source", "port"),
    [("HDMI 4", 4), ("HDMI 4 (PC)", 4), ("HDMI 1", 1), ("HDMI 3 (Old Label)", 3)],
)
async def test_select_source_forms(hass, aioclient_mock, mock_tv, setup_integration, source, port):
    await setup_integration()
    await call(hass, "media_player", "select_source", entity_id=MP, source=source)
    assert ("post", f"/hdmi/{port}", None) in api_posts(aioclient_mock)
    assert hass.states.get(SEL).state == f"HDMI {port}"


@pytest.mark.parametrize("source", ["HDMI 5", "HDMI", "Netflix", "HDMI 2x", "hdmi 2 (a) b"])
async def test_select_source_invalid(hass, aioclient_mock, mock_tv, setup_integration, source):
    await setup_integration()
    with pytest.raises(ServiceValidationError):
        await call(hass, "media_player", "select_source", entity_id=MP, source=source)
    assert api_posts(aioclient_mock) == []


async def test_select_entity_option(hass, aioclient_mock, mock_tv, setup_integration):
    await setup_integration()
    await call(hass, "select", "select_option", entity_id=SEL, option="HDMI 3")
    assert ("post", "/hdmi/3", None) in api_posts(aioclient_mock)
    assert hass.states.get(SEL).state == "HDMI 3"
    assert hass.states.get(CUR).state == "HW-Q800B"
    with pytest.raises(ServiceValidationError):
        await call(hass, "select", "select_option", entity_id=SEL, option="HDMI 4 (PC)")


async def test_hdmi_not_confirmed_raises(hass, mock_tv, setup_integration):
    await setup_integration()
    mock_tv.hdmi_fail = True
    with pytest.raises(HomeAssistantError, match="not confirmed"):
        await call(hass, "media_player", "select_source", entity_id=MP, source="HDMI 4")
    with pytest.raises(HomeAssistantError, match="not confirmed"):
        await call(hass, "select", "select_option", entity_id=SEL, option="HDMI 4")
    assert hass.states.get(SEL).state == "HDMI 2"


# ---- power ------------------------------------------------------------------


async def test_turn_on_off_use_stock(hass, mock_tv, setup_integration):
    await setup_integration()
    with (
        patch("custom_components.quietthings.stock.StockTv.power_on", new=AsyncMock()) as on,
        patch("custom_components.quietthings.stock.StockTv.power_off", new=AsyncMock()) as off,
    ):
        await call(hass, "media_player", "turn_on", entity_id=MP)
        await call(hass, "media_player", "turn_off", entity_id=MP)
    on.assert_awaited_once()
    off.assert_awaited_once()


async def test_turn_on_failure_raises(hass, mock_tv, mock_wol, mock_remote, setup_integration, monkeypatch):
    from custom_components.quietthings import stock as stock_mod

    monkeypatch.setattr(stock_mod, "POLL_INTERVAL", 0.01)
    monkeypatch.setattr(stock_mod, "WAKE_WAIT", 0.03)
    monkeypatch.setattr(stock_mod, "KEY_WAKE_WAIT", 0.03)
    mock_tv.power = "standby"
    await setup_integration()
    with pytest.raises(HomeAssistantError, match="did not confirm"):
        await call(hass, "media_player", "turn_on", entity_id=MP)
    mock_wol.assert_awaited_once()


# ---- API down and availability ---------------------------------------------


async def test_setup_with_api_down(hass, aioclient_mock, mock_tv, setup_integration):
    mock_tv.api_down = True
    entry = await setup_integration()
    assert entry.state is ConfigEntryState.LOADED
    mp = hass.states.get(MP)
    assert mp.state == "on"
    assert mp.attributes.get("volume_level") is None
    assert mp.attributes.get("source") is None
    assert mp.attributes.get("is_volume_muted") is None
    for entity in (SEL, CUR, SIG):
        assert hass.states.get(entity).state == "unavailable"

    with pytest.raises(HomeAssistantError):
        await call(hass, "media_player", "volume_set", entity_id=MP, volume_level=0.5)
    with pytest.raises(HomeAssistantError):
        await call(hass, "media_player", "volume_up", entity_id=MP)
    with pytest.raises(HomeAssistantError):
        await call(hass, "media_player", "volume_mute", entity_id=MP, is_volume_muted=True)
    with pytest.raises(HomeAssistantError):
        await call(hass, "media_player", "select_source", entity_id=MP, source="HDMI 1")

    # Power still works through the stock path.
    with patch("custom_components.quietthings.stock.StockTv.power_on", new=AsyncMock()) as on:
        await call(hass, "media_player", "turn_on", entity_id=MP)
    on.assert_awaited_once()


async def test_api_availability_flips(hass, mock_tv, setup_integration):
    entry = await setup_integration()
    assert hass.states.get(SIG).state == "active"

    mock_tv.api_down = True
    await refresh(hass, entry)
    for entity in (SEL, CUR, SIG):
        assert hass.states.get(entity).state == "unavailable"
    mp = hass.states.get(MP)
    assert mp.state == "on"
    assert mp.attributes.get("volume_level") is None

    mock_tv.api_down = False
    await refresh(hass, entry)
    assert hass.states.get(SIG).state == "active"
    assert hass.states.get(MP).attributes["volume_level"] == 0.2


async def test_stock_loss_is_unknown_when_api_remains_reachable(hass, mock_tv, setup_integration):
    entry = await setup_integration()
    mock_tv.stock_down = True
    for _ in range(5):
        await refresh(hass, entry, "stock")
    assert hass.states.get(MP).state == "unknown"
    assert hass.states.get(SEL).state == "HDMI 2"


async def test_wake_remains_callable_when_stock_and_api_are_asleep(hass, mock_tv, setup_integration):
    entry = await setup_integration()
    mock_tv.stock_down = True
    mock_tv.api_down = True
    await refresh(hass, entry)
    await refresh(hass, entry, "stock")
    await refresh(hass, entry, "stock")
    assert hass.states.get(MP).state == "unknown"
    await refresh(hass, entry, "stock")
    assert hass.states.get(MP).state == "unknown"
    assert hass.states.get(MP).attributes.get("volume_level") is None
    with patch("custom_components.quietthings.stock.StockTv.power_on", new=AsyncMock()) as on:
        await call(hass, "media_player", "turn_on", entity_id=MP)
        await call(hass, "media_player", "toggle", entity_id=MP)
    assert on.await_count == 2

    mock_tv.stock_down = False
    await refresh(hass, entry, "stock")
    assert hass.states.get(MP).state == "on"


# ---- secrets ----------------------------------------------------------------


async def test_tokens_not_in_logs_or_diagnostics(hass, aioclient_mock, mock_tv, caplog, setup_integration):
    caplog.set_level(logging.DEBUG)
    entry = await setup_integration()
    await call(hass, "media_player", "volume_set", entity_id=MP, volume_level=0.3)
    await call(hass, "media_player", "select_source", entity_id=MP, source="HDMI 4")
    mock_tv.api_down = True
    await refresh(hass, entry)
    mock_tv.api_down = False
    mock_tv.hdmi_fail = True
    with pytest.raises(HomeAssistantError):
        await call(hass, "media_player", "select_source", entity_id=MP, source="HDMI 1")

    assert API_TOKEN not in caplog.text
    assert WS_TOKEN not in caplog.text
    # The token does go out in the Authorization header, and only there.
    assert aioclient_mock.mock_calls[2][3]["Authorization"] == f"Bearer {API_TOKEN}"

    diag = await async_get_config_entry_diagnostics(hass, entry)
    dumped = json.dumps(diag, default=str)
    assert API_TOKEN not in dumped
    assert WS_TOKEN not in dumped
    assert MAC not in dumped
    assert diag["entry"]["api_token"] == "**REDACTED**"
    assert diag["entry"]["ws_token"] == "**REDACTED**"
    assert diag["entry"]["host"] == "192.0.2.10"


async def test_rotated_ws_token_is_merged_into_entry(hass, mock_tv, setup_integration):
    entry = await setup_integration()
    before = dict(entry.data)
    entry.runtime_data.stock._store_token("rotated-ws-token")
    await hass.async_block_till_done()
    assert entry.data["ws_token"] == "rotated-ws-token"
    assert {k: v for k, v in entry.data.items() if k != "ws_token"} == {
        k: v for k, v in before.items() if k != "ws_token"
    }
    assert entry.state is ConfigEntryState.LOADED
    # An unchanged token does not rewrite the entry.
    entry.runtime_data.stock._store_token("rotated-ws-token")
    assert entry.data["ws_token"] == "rotated-ws-token"
