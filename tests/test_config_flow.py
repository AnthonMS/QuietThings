"""Config, pairing, reauth and options flows."""

from __future__ import annotations

from unittest.mock import AsyncMock, patch

import pytest
import voluptuous as vol
from homeassistant import config_entries
from homeassistant.data_entry_flow import FlowResultType

from custom_components.quietthings import netinfo
from custom_components.quietthings.const import (
    CONF_API_TOKEN,
    CONF_BROADCAST,
    CONF_MAC,
    CONF_SCAN_INTERVAL,
    CONF_WS_TOKEN,
    DOMAIN,
)

from .conftest import API_TOKEN, API_URL, DEVICE_ID, HOST, MAC, STOCK_URL, WS_TOKEN

USER_INPUT = {
    "name": "Living Room TV",
    "host": HOST,
    "api_port": 18765,
    "api_token": API_TOKEN,
}

REAL_READ_ARP_MAC = netinfo.read_arp_mac

ADAPTERS = [
    {
        "name": "eth0",
        "enabled": True,
        "ipv4": [{"address": "192.0.2.5", "network_prefix": 24}],
        "ipv6": [],
    }
]


@pytest.fixture(autouse=True)
def detection():
    """No ARP entry and one adapter on the TV's network by default."""
    with (
        patch.object(netinfo, "read_arp_mac", return_value=None) as arp,
        patch(
            "homeassistant.components.network.async_get_adapters",
            new=AsyncMock(return_value=ADAPTERS),
        ) as adapters,
    ):
        yield arp, adapters


async def start(hass):
    return await hass.config_entries.flow.async_init(
        DOMAIN, context={"source": config_entries.SOURCE_USER}
    )


async def test_name_has_no_default(hass, mock_tv):
    result = await start(hass)
    assert result["type"] is FlowResultType.FORM
    keys = {str(k): k for k in result["data_schema"].schema}
    assert keys["name"].default is vol.UNDEFINED
    assert keys["api_port"].default() == 18765


async def test_user_form_has_only_the_four_fields(hass, mock_tv):
    result = await start(hass)
    assert {str(k) for k in result["data_schema"].schema} == {
        "name",
        "host",
        "api_port",
        "api_token",
    }


async def test_happy_path(hass, mock_tv, mock_remote, mock_wol):
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "pair"
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == "Living Room TV"
    assert result["data"][CONF_WS_TOKEN] == WS_TOKEN
    assert result["data"][CONF_API_TOKEN] == API_TOKEN
    assert result["data"][CONF_MAC] == MAC
    assert result["data"][CONF_BROADCAST] == "192.0.2.255"
    assert result["result"].unique_id == DEVICE_ID


async def test_custom_name_becomes_title(hass, mock_tv, mock_remote):
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {**USER_INPUT, "name": "Bedroom TV"}
    )
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["title"] == "Bedroom TV"


@pytest.mark.parametrize("name", ["", "   "])
async def test_blank_name_rejected(hass, mock_tv, name):
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {**USER_INPUT, "name": name}
    )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "user"
    assert result["errors"] == {"name": "name_required"}


async def test_unsupported_api_version(hass, mock_tv):
    mock_tv.api_version = 2
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "user"
    assert result["errors"] == {"base": "unsupported_api"}
    assert result["description_placeholders"] == {"version": "2", "supported": "1"}


async def test_bad_host(hass, mock_tv):
    mock_tv.stock_down = True
    mock_tv.api_down = True
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {"base": "cannot_connect"}


async def test_api_down_is_cannot_connect(hass, mock_tv):
    mock_tv.api_down = True
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["errors"] == {"base": "cannot_connect"}


async def test_stock_down_is_cannot_connect(hass, mock_tv):
    mock_tv.stock_down = True
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["errors"] == {"base": "cannot_connect"}


async def test_bad_token(hass, mock_tv):
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {**USER_INPUT, "api_token": "wrong"}
    )
    assert result["errors"] == {"base": "invalid_auth"}


async def test_mac_from_arp_table(hass, mock_tv, mock_remote, detection):
    detection[0].return_value = "11:22:33:44:55:66"
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["step_id"] == "pair"
    detection[0].assert_called_once_with(HOST)
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["data"][CONF_MAC] == "11:22:33:44:55:66"


async def test_mac_falls_back_to_wifi_mac(hass, mock_tv, mock_remote):
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["step_id"] == "pair"
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["data"][CONF_MAC] == MAC


async def test_manual_mac_step_when_detection_fails(
    hass, mock_tv, mock_remote, aioclient_mock
):
    # A TV that reports no wifiMac, and nothing in the ARP table.
    aioclient_mock.clear_requests()
    aioclient_mock.get(
        STOCK_URL, json={"device": {"id": DEVICE_ID, "modelName": "QN65QN90BAFXZA"}}
    )
    aioclient_mock.get(f"{API_URL}/health", json={"ok": True, "api_version": 1})

    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "mac"

    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {"mac": "nope"}
    )
    assert result["step_id"] == "mac"
    assert result["errors"] == {"mac": "invalid_mac"}

    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], {"mac": "AA-BB-CC-DD-EE-01"}
    )
    assert result["step_id"] == "pair"
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["data"][CONF_MAC] == "aa:bb:cc:dd:ee:01"
    assert result["data"][CONF_BROADCAST] == "192.0.2.255"


async def test_broadcast_computed_from_adapter(hass, detection):
    detection[1].return_value = [
        {
            "name": "eth0",
            "enabled": True,
            "ipv4": [{"address": "192.0.2.1", "network_prefix": 24}],
            "ipv6": [],
        }
    ]
    assert await netinfo.async_detect_broadcast(hass, "192.0.2.99") == (
        "192.0.2.255"
    )


async def test_broadcast_skips_adapters_not_on_the_tv_network(hass, detection):
    detection[1].return_value = [
        {"name": "a", "ipv4": [{"address": "198.51.100.5", "network_prefix": 24}]},
        {"name": "b", "ipv4": [{"address": "192.0.2.1", "network_prefix": 25}]},
    ]
    assert await netinfo.async_detect_broadcast(hass, "192.0.2.99") == (
        "192.0.2.127"
    )


async def test_broadcast_fallback_when_no_adapter_matches(hass, detection):
    assert await netinfo.async_detect_broadcast(hass, "198.51.100.99") == (
        "255.255.255.255"
    )


async def test_broadcast_fallback_on_error(hass, detection):
    detection[1].side_effect = RuntimeError("boom")
    assert await netinfo.async_detect_broadcast(hass, "192.0.2.99") == (
        "255.255.255.255"
    )


async def test_flow_stores_fallback_broadcast(hass, mock_tv, mock_remote, detection):
    detection[1].return_value = []
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["data"][CONF_BROADCAST] == "255.255.255.255"


def test_read_arp_mac(tmp_path):
    table = tmp_path / "arp"
    table.write_text(
        "IP address       HW type     Flags       HW address            Mask     Device\n"
        "192.0.2.1        0x1         0x2         00:00:00:00:00:00     *        eth0\n"
        "192.0.2.10       0x1         0x2         AA:BB:CC:DD:EE:0A     *        eth0\n"
        "192.0.2.11       0x1         0x0         00:00:00:00:00:00     *        eth0\n"
    )
    assert REAL_READ_ARP_MAC("192.0.2.10", str(table)) == "aa:bb:cc:dd:ee:0a"
    assert REAL_READ_ARP_MAC("192.0.2.11", str(table)) is None
    assert REAL_READ_ARP_MAC("192.0.2.99", str(table)) is None
    assert REAL_READ_ARP_MAC("192.0.2.10", str(tmp_path / "missing")) is None


async def test_pairing_timeout_then_retry(hass, mock_tv, mock_remote):
    mock_remote[1].open.side_effect = TimeoutError()
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "pair"
    assert result["errors"] == {"base": "pairing_failed"}

    mock_remote[1].open.side_effect = None
    result = await hass.config_entries.flow.async_configure(result["flow_id"], {})
    assert result["type"] is FlowResultType.CREATE_ENTRY


async def test_already_configured(hass, mock_tv, mock_remote, config_entry):
    config_entry.add_to_hass(hass)
    result = await start(hass)
    result = await hass.config_entries.flow.async_configure(
        result["flow_id"], USER_INPUT
    )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"


async def test_reauth(hass, mock_tv, config_entry, setup_integration):
    mock_tv.valid_token = "rotated-token"
    await setup_integration()
    flows = hass.config_entries.flow.async_progress_by_handler(DOMAIN)
    assert [f["context"]["source"] for f in flows] == [config_entries.SOURCE_REAUTH]

    result = await hass.config_entries.flow.async_configure(
        flows[0]["flow_id"], {CONF_API_TOKEN: "still-wrong"}
    )
    assert result["errors"] == {"base": "invalid_auth"}

    result = await hass.config_entries.flow.async_configure(
        flows[0]["flow_id"], {CONF_API_TOKEN: "rotated-token"}
    )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "reauth_successful"
    assert config_entry.data[CONF_API_TOKEN] == "rotated-token"
    await hass.async_block_till_done()
    assert config_entry.state is config_entries.ConfigEntryState.LOADED


async def test_reauth_unsupported_api_version(
    hass, mock_tv, config_entry, setup_integration
):
    mock_tv.valid_token = "rotated-token"
    await setup_integration()
    flows = hass.config_entries.flow.async_progress_by_handler(DOMAIN)
    mock_tv.api_version = 2

    result = await hass.config_entries.flow.async_configure(
        flows[0]["flow_id"], {CONF_API_TOKEN: "rotated-token"}
    )
    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {"base": "unsupported_api"}
    assert result["description_placeholders"]["version"] == "2"
    assert result["description_placeholders"]["supported"] == "1"
    assert config_entry.data[CONF_API_TOKEN] != "rotated-token"


async def test_options_interval(hass, mock_tv, config_entry, setup_integration):
    await setup_integration()
    result = await hass.config_entries.options.async_init(config_entry.entry_id)
    assert result["type"] is FlowResultType.FORM
    result = await hass.config_entries.options.async_configure(
        result["flow_id"],
        {CONF_SCAN_INTERVAL: 30, "mac": MAC, "broadcast": "192.0.2.255"},
    )
    assert result["type"] is FlowResultType.CREATE_ENTRY
    await hass.async_block_till_done()
    runtime = config_entry.runtime_data
    assert runtime.api_coordinator.update_interval.total_seconds() == 30
    assert runtime.stock_coordinator.update_interval.total_seconds() == 30


async def test_options_edit_mac_and_broadcast(
    hass, mock_tv, config_entry, setup_integration
):
    await setup_integration()
    result = await hass.config_entries.options.async_init(config_entry.entry_id)
    keys = {str(k): k for k in result["data_schema"].schema}
    assert keys["mac"].default() == MAC
    assert keys["broadcast"].default() == "192.0.2.255"
    assert keys["scan_interval"].default() == 10

    result = await hass.config_entries.options.async_configure(
        result["flow_id"],
        {"scan_interval": 10, "mac": "11-22-33-44-55-66", "broadcast": "192.0.2.127"},
    )
    assert result["type"] is FlowResultType.CREATE_ENTRY
    await hass.async_block_till_done()
    assert config_entry.data[CONF_MAC] == "11:22:33:44:55:66"
    assert config_entry.data[CONF_BROADCAST] == "192.0.2.127"
    # The entry was reloaded, so the live transport uses the new values.
    assert config_entry.runtime_data.stock.mac == "11:22:33:44:55:66"
    assert config_entry.runtime_data.stock.broadcast == "192.0.2.127"


async def test_options_invalid_mac_and_broadcast(
    hass, mock_tv, config_entry, setup_integration
):
    await setup_integration()
    result = await hass.config_entries.options.async_init(config_entry.entry_id)
    result = await hass.config_entries.options.async_configure(
        result["flow_id"],
        {"scan_interval": 10, "mac": "nope", "broadcast": "x"},
    )
    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {
        "mac": "invalid_mac",
        "broadcast": "invalid_broadcast",
    }
    assert config_entry.data[CONF_MAC] == MAC


def test_read_arp_mac_skips_incomplete_line_for_same_ip(tmp_path):
    table = tmp_path / "arp"
    table.write_text(
        "IP address       HW type     Flags       HW address            Mask     Device\n"
        "192.0.2.10       0x1         0x0         00:00:00:00:00:00     *        wlan0\n"
        "192.0.2.10       0x1         0x2         aa:bb:cc:dd:ee:0b     *        eth0\n"
    )
    assert REAL_READ_ARP_MAC("192.0.2.10", str(table)) == "aa:bb:cc:dd:ee:0b"
