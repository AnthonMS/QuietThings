"""Shared fixtures: a fake TV behind aioclient_mock, mocked samsungtvws and WoL."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any
from unittest.mock import AsyncMock, MagicMock, patch

import aiohttp
import pytest
from pytest_homeassistant_custom_component.common import MockConfigEntry
from pytest_homeassistant_custom_component.test_util.aiohttp import (
    AiohttpClientMockResponse,
)

from custom_components.quietthings.const import (
    CONF_API_PORT,
    CONF_API_TOKEN,
    CONF_BROADCAST,
    CONF_HOST,
    CONF_MAC,
    CONF_MODEL,
    CONF_NAME,
    CONF_WS_TOKEN,
    DOMAIN,
)

HOST = "192.0.2.10"
API_TOKEN = "api-token-SECRET-123"
WS_TOKEN = "ws-token-SECRET-456"
MAC = "aa:bb:cc:dd:ee:ff"
DEVICE_ID = "uuid:tv-device-1"
STOCK_URL = f"http://{HOST}:8001/api/v2/"
API_URL = f"http://{HOST}:18765"


@pytest.fixture(autouse=True)
def _enable_custom_integrations(enable_custom_integrations):
    """Let the harness load custom_components/quietthings."""


@dataclass
class FakeTv:
    """Mutable TV behind the mocked HTTP endpoints."""

    power: str = "on"
    stock_down: bool = False
    api_down: bool = False
    valid_token: str = API_TOKEN
    api_version: int = 1
    hdmi_fail: bool = False
    selected_port: int | None = 2
    no_signal_ports: set[int] = field(default_factory=set)
    level: int = 20
    mute: bool = False
    names: dict[int, str] = field(
        default_factory=lambda: {1: "HDMI 1", 2: "Cable Box", 3: "HW-Q800B", 4: "PC"}
    )

    def inputs(self) -> list[dict[str, Any]]:
        return [
            {"port": p, "id": 12 + p, "name": self.names[p], "connected": p != 1}
            for p in (1, 2, 3, 4)
        ]

    def signal(self) -> dict[str, Any]:
        port = self.selected_port
        return {
            "source_id": 12 + port if port else -1,
            "active": bool(port) and port not in self.no_signal_ports,
        }

    def selected(self) -> dict[str, Any] | None:
        port = self.selected_port
        if port is None:
            return None
        return {
            "port": port,
            "id": 12 + port,
            "name": self.names[port],
            "connected": port != 1,
        }

    def volume(self) -> dict[str, Any]:
        return {"level": self.level, "mute": self.mute}

    def state(self) -> dict[str, Any]:
        return {
            "ok": True,
            "api_version": 1,
            "paused": False,
            "selected": self.selected(),
            "signal": self.signal(),
            "inputs": self.inputs(),
            "volume": self.volume(),
        }


def install_tv(aioclient_mock, tv: FakeTv) -> None:
    """Register the 8001 and QuietThings endpoints, driven by `tv`."""

    def reply(method, url, status=200, body=None):
        return AiohttpClientMockResponse(method, url, status=status, json=body)

    async def stock(method, url, data):
        if tv.stock_down:
            return AiohttpClientMockResponse(
                method, url, exc=aiohttp.ClientConnectionError("down")
            )
        return reply(
            method,
            url,
            body={
                "device": {
                    "id": DEVICE_ID,
                    "name": "QN90B",
                    "modelName": "QN65QN90BAFXZA",
                    "wifiMac": MAC,
                    "PowerState": tv.power,
                }
            },
        )

    def guard(method, url):
        if tv.api_down:
            return AiohttpClientMockResponse(
                method, url, exc=aiohttp.ClientConnectionError("down")
            )
        headers = aioclient_mock.mock_calls[-1][3] or {}
        if headers.get("Authorization") != f"Bearer {tv.valid_token}":
            return reply(method, url, 401, {"ok": False, "error": "unauthorized"})
        return None

    async def health(method, url, data):
        return guard(method, url) or reply(
            method,
            url,
            body={
                "ok": True,
                "app": "quiet-home",
                "api_version": tv.api_version,
                "uptime_s": 5,
                "paused": False,
            },
        )

    async def state(method, url, data):
        return guard(method, url) or reply(method, url, body=tv.state())

    def hdmi_for(port):
        async def handler(method, url, data):
            if (bad := guard(method, url)) is not None:
                return bad
            if tv.hdmi_fail:
                return reply(method, url, 504, {"ok": False, "error": "not confirmed"})
            tv.selected_port = port
            return reply(
                method,
                url,
                body={"ok": True, "selected": tv.selected(), "signal": tv.signal()},
            )

        return handler

    async def volume(method, url, data):
        if (bad := guard(method, url)) is not None:
            return bad
        tv.level = data["level"]
        return reply(method, url, body={"ok": True, "volume": tv.volume()})

    async def mute(method, url, data):
        if (bad := guard(method, url)) is not None:
            return bad
        tv.mute = data["mute"]
        return reply(method, url, body={"ok": True, "volume": tv.volume()})

    aioclient_mock.get(STOCK_URL, side_effect=stock)
    aioclient_mock.get(f"{API_URL}/health", side_effect=health)
    aioclient_mock.get(f"{API_URL}/state", side_effect=state)
    for port in (1, 2, 3, 4):
        aioclient_mock.post(f"{API_URL}/hdmi/{port}", side_effect=hdmi_for(port))
    aioclient_mock.post(f"{API_URL}/volume", side_effect=volume)
    aioclient_mock.post(f"{API_URL}/mute", side_effect=mute)


@pytest.fixture
def tv() -> FakeTv:
    return FakeTv()


@pytest.fixture
def mock_tv(aioclient_mock, tv) -> FakeTv:
    install_tv(aioclient_mock, tv)
    return tv


@pytest.fixture
def mock_remote():
    """Patch the samsungtvws async remote; yields (class_mock, instance)."""
    instance = MagicMock()
    instance.open = AsyncMock()
    instance.close = AsyncMock()
    instance.send_command = AsyncMock()
    instance.token = WS_TOKEN
    with patch(
        "custom_components.quietthings.stock.SamsungTVWSAsyncRemote", return_value=instance
    ) as cls:
        yield cls, instance


@pytest.fixture
def mock_wol():
    with patch(
        "custom_components.quietthings.stock.send_magic_packet", new=AsyncMock()
    ) as wol:
        yield wol


@pytest.fixture
def entry_data() -> dict[str, Any]:
    return {
        CONF_NAME: "Living Room TV",
        CONF_HOST: HOST,
        CONF_API_PORT: 18765,
        CONF_API_TOKEN: API_TOKEN,
        CONF_MAC: MAC,
        CONF_BROADCAST: "192.0.2.255",
        CONF_WS_TOKEN: WS_TOKEN,
        CONF_MODEL: "QN65QN90BAFXZA",
    }


@pytest.fixture
def config_entry(entry_data) -> MockConfigEntry:
    return MockConfigEntry(
        domain=DOMAIN,
        title="Living Room TV",
        unique_id=DEVICE_ID,
        data=entry_data,
    )


@pytest.fixture
def setup_integration(hass, config_entry):
    """Return a coroutine function that sets the entry up."""

    async def _setup() -> MockConfigEntry:
        config_entry.add_to_hass(hass)
        await hass.config_entries.async_setup(config_entry.entry_id)
        await hass.async_block_till_done()
        return config_entry

    return _setup
