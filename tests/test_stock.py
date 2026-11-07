"""Stock transport: REST power state, Wake-on-LAN, KEY_POWER."""

from __future__ import annotations

from unittest.mock import AsyncMock, MagicMock, patch

import pytest
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from custom_components.quietthings import stock as stock_mod
from custom_components.quietthings.stock import (
    StockError,
    StockTv,
    magic_packet,
    send_magic_packet,
)

MAC = "aa:bb:cc:dd:ee:ff"


@pytest.fixture(autouse=True)
def fast(monkeypatch):
    monkeypatch.setattr(stock_mod, "POLL_INTERVAL", 0.01)
    monkeypatch.setattr(stock_mod, "WAKE_WAIT", 0.05)
    monkeypatch.setattr(stock_mod, "KEY_WAKE_WAIT", 0.05)
    monkeypatch.setattr(stock_mod, "STANDBY_WAIT", 0.05)


def make_tv(hass, states, token_callback=None):
    """StockTv whose power_state walks `states`, repeating the last."""
    tv = StockTv(
        async_get_clientsession(hass),
        "192.0.2.10",
        MAC,
        "192.0.2.255",
        "tok",
        token_callback,
    )
    queue = list(states)

    async def power_state():
        return queue.pop(0) if len(queue) > 1 else queue[0]

    tv.power_state = power_state
    return tv


def test_magic_packet_bytes():
    packet = magic_packet(MAC)
    mac = bytes.fromhex("aabbccddeeff")
    assert packet == b"\xff" * 6 + mac * 16
    assert len(packet) == 102


async def test_send_magic_packet_udp_9_broadcast():
    transport = MagicMock()
    loop_mock = AsyncMock(return_value=(transport, None))
    with patch("asyncio.BaseEventLoop.create_datagram_endpoint", loop_mock):
        await send_magic_packet(MAC, "192.0.2.255")
    assert loop_mock.call_args.kwargs["remote_addr"] == ("192.0.2.255", 9)
    assert loop_mock.call_args.kwargs["allow_broadcast"] is True
    transport.sendto.assert_called_once_with(magic_packet(MAC))
    transport.close.assert_called_once()


async def test_device_info_and_power_state(hass, aioclient_mock):
    aioclient_mock.get(
        "http://192.0.2.10:8001/api/v2/",
        json={"device": {"id": "x", "PowerState": "Standby"}},
    )
    tv = StockTv(async_get_clientsession(hass), "192.0.2.10", MAC, "b")
    assert (await tv.device_info())["id"] == "x"
    assert await tv.power_state() == "standby"


async def test_power_state_unreachable(hass, aioclient_mock):
    aioclient_mock.get("http://192.0.2.10:8001/api/v2/", exc=TimeoutError())
    tv = StockTv(async_get_clientsession(hass), "192.0.2.10", MAC, "b")
    assert await tv.power_state() == "unreachable"
    with pytest.raises(StockError):
        await tv.device_info()


async def test_power_on_already_on(hass, mock_wol, mock_remote):
    tv = make_tv(hass, ["on"])
    await tv.power_on()
    mock_wol.assert_not_called()
    mock_remote[1].open.assert_not_called()


async def test_power_on_wol_success(hass, mock_wol, mock_remote):
    tv = make_tv(hass, ["standby", "standby", "on"])
    await tv.power_on()
    mock_wol.assert_awaited_once_with(MAC, "192.0.2.255")
    mock_remote[1].open.assert_not_called()
    mock_remote[1].send_command.assert_not_called()


async def test_power_on_key_power_fallback(hass, mock_wol, mock_remote):
    # Standby through the WoL wait and when 8002 opens; on once KEY_POWER was sent.
    tv = make_tv(hass, ["standby"])

    async def power_state():
        return "on" if mock_remote[1].send_command.await_count else "standby"

    tv.power_state = power_state
    await tv.power_on()
    mock_wol.assert_awaited_once()
    mock_remote[1].open.assert_awaited_once()
    assert mock_remote[1].send_command.await_count == 1
    cmd = mock_remote[1].send_command.await_args.args[0]
    assert cmd.params["DataOfCmd"] == "KEY_POWER"
    mock_remote[1].close.assert_awaited()


async def test_power_on_failure_not_confirmed(hass, mock_wol, mock_remote):
    tv = make_tv(hass, ["standby"])
    with pytest.raises(StockError, match="did not confirm"):
        await tv.power_on()
    assert mock_remote[1].send_command.await_count == 1


async def test_power_on_unknown_state_no_key(hass, mock_wol, mock_remote):
    tv = make_tv(hass, ["unreachable"])
    with pytest.raises(StockError, match="unknown"):
        await tv.power_on()
    mock_remote[1].send_command.assert_not_called()


async def test_power_off_sends_key_when_on(hass, mock_remote):
    tv = make_tv(hass, ["on", "on", "standby"])
    await tv.power_off()
    assert mock_remote[1].send_command.await_count == 1


async def test_power_off_noop_in_standby(hass, mock_remote):
    tv = make_tv(hass, ["standby"])
    await tv.power_off()
    mock_remote[1].open.assert_not_called()


async def test_power_off_not_verified(hass, mock_remote):
    tv = make_tv(hass, ["on"])
    with pytest.raises(StockError, match="standby"):
        await tv.power_off()


async def test_pair_returns_and_reports_token(hass, mock_remote):
    seen = []
    tv = make_tv(hass, ["on"], seen.append)
    tv.ws_token = None
    assert await tv.pair(1) == "ws-token-SECRET-456"
    assert seen == ["ws-token-SECRET-456"]


async def test_pair_failure(hass, mock_remote):
    mock_remote[1].open.side_effect = TimeoutError()
    tv = make_tv(hass, ["on"])
    with pytest.raises(StockError, match="Pairing failed"):
        await tv.pair(1)
