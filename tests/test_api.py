"""QuietThings API client."""

from __future__ import annotations

from unittest.mock import patch

import aiohttp
import pytest
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from custom_components.quietthings.api import (
    QuietThingsApi,
    QuietThingsAuthError,
    QuietThingsRejected,
    QuietThingsUnavailable,
)

from custom_components.quietthings.const import API_HDMI_TIMEOUT, API_TIMEOUT

BASE = "http://192.0.2.10:18765"
TOKEN = "tok-SECRET"


@pytest.fixture
def api(hass):
    return QuietThingsApi(async_get_clientsession(hass), "192.0.2.10", 18765, TOKEN)


async def test_health_sends_bearer_and_close(hass, aioclient_mock, api):
    aioclient_mock.get(f"{BASE}/health", json={"ok": True, "api_version": 1})
    assert (await api.health())["ok"] is True
    headers = aioclient_mock.mock_calls[0][3]
    assert headers["Authorization"] == f"Bearer {TOKEN}"
    assert headers["Connection"] == "close"


async def test_state(hass, aioclient_mock, api):
    aioclient_mock.get(f"{BASE}/state", json={"ok": True, "volume": None})
    assert (await api.state())["volume"] is None


async def test_unauthorized(hass, aioclient_mock, api):
    aioclient_mock.get(
        f"{BASE}/state", status=401, json={"ok": False, "error": "unauthorized"}
    )
    with pytest.raises(QuietThingsAuthError):
        await api.state()


@pytest.mark.parametrize("exc", [TimeoutError(), aiohttp.ClientConnectionError("x")])
async def test_unreachable(hass, aioclient_mock, api, exc):
    aioclient_mock.get(f"{BASE}/state", exc=exc)
    with pytest.raises(QuietThingsUnavailable):
        await api.state()


async def test_rejected_with_message(hass, aioclient_mock, api):
    aioclient_mock.post(
        f"{BASE}/hdmi/3", status=504, json={"ok": False, "error": "not confirmed"}
    )
    with pytest.raises(QuietThingsRejected, match="not confirmed") as err:
        await api.select_hdmi(3)
    assert err.value.status == 504


async def test_ok_false_on_200_is_rejected(hass, aioclient_mock, api):
    aioclient_mock.get(f"{BASE}/state", json={"ok": False, "error": "busy"})
    with pytest.raises(QuietThingsRejected, match="busy"):
        await api.state()


async def test_malformed_json(hass, aioclient_mock, api):
    aioclient_mock.get(f"{BASE}/state", text="<html>not json")
    with pytest.raises(QuietThingsRejected):
        await api.state()


async def test_volume_and_mute_payloads(hass, aioclient_mock, api):
    aioclient_mock.post(f"{BASE}/volume", json={"ok": True, "volume": {"level": 7}})
    aioclient_mock.post(f"{BASE}/mute", json={"ok": True, "volume": {"mute": True}})
    await api.set_volume(7)
    await api.set_mute(True)
    assert aioclient_mock.mock_calls[0][2] == {"level": 7}
    assert aioclient_mock.mock_calls[1][2] == {"mute": True}


async def test_hdmi_has_no_body(hass, aioclient_mock, api):
    aioclient_mock.post(f"{BASE}/hdmi/4", json={"ok": True})
    await api.select_hdmi(4)
    assert aioclient_mock.mock_calls[0][2] is None


async def test_hdmi_uses_longer_timeout(hass, aioclient_mock, api):
    aioclient_mock.post(f"{BASE}/hdmi/2", json={"ok": True})
    aioclient_mock.get(f"{BASE}/state", json={"ok": True})
    aioclient_mock.post(f"{BASE}/volume", json={"ok": True})
    with patch.object(api._session, "request", wraps=api._session.request) as req:
        await api.select_hdmi(2)
        await api.state()
        await api.set_volume(10)
    totals = [c.kwargs["timeout"].total for c in req.call_args_list]
    assert totals == [API_HDMI_TIMEOUT, API_TIMEOUT, API_TIMEOUT]
    assert API_HDMI_TIMEOUT == 10
    assert API_TIMEOUT == 5
