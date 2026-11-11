"""Stock Samsung transports: REST 8001, WebSocket 8002 and Wake-on-LAN."""

from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Callable
import logging
import re
from typing import Any

import aiohttp
from homeassistant.exceptions import HomeAssistantError
from samsungtvws.async_remote import SamsungTVWSAsyncRemote
from samsungtvws.remote import SendRemoteKey

from .const import STOCK_REST_PORT, STOCK_WS_PORT, WOL_PORT, WS_CLIENT_NAME
from .api import QuietThingsError

_LOGGER = logging.getLogger(__name__)

# Module level so tests can shorten them.
POLL_INTERVAL = 1.0
WAKE_WAIT = 5.0
KEY_WAKE_WAIT = 15.0
STANDBY_WAIT = 20.0
WS_TIMEOUT = 8.0


class StockError(HomeAssistantError):
    """The stock TV interface failed."""


def parse_mac(mac: str) -> bytes:
    """Return the 6 raw MAC bytes or raise ValueError."""
    raw = bytes.fromhex(re.sub(r"[:\-.]", "", mac))
    if len(raw) != 6 or not any(raw):
        raise ValueError("invalid MAC address")
    return raw


def magic_packet(mac: str) -> bytes:
    """Wake-on-LAN magic packet: 6x 0xff then the MAC 16 times."""
    return b"\xff" * 6 + parse_mac(mac) * 16


async def send_magic_packet(mac: str, broadcast: str) -> None:
    """Send the magic packet by UDP to the broadcast address, port 9."""
    packet = magic_packet(mac)
    loop = asyncio.get_running_loop()
    transport, _ = await loop.create_datagram_endpoint(
        asyncio.DatagramProtocol,
        remote_addr=(broadcast, WOL_PORT),
        allow_broadcast=True,
    )
    try:
        transport.sendto(packet)
    finally:
        transport.close()


class StockTv:
    """Power state and power control through the stock Samsung interfaces."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        host: str,
        mac: str,
        broadcast: str,
        ws_token: str | None = None,
        token_callback: Callable[[str], None] | None = None,
        display_power: Callable[[], Awaitable[str | None]] | None = None,
    ) -> None:
        self._session = session
        self.host = host
        self.mac = mac
        self.broadcast = broadcast
        self.ws_token = ws_token
        self._token_callback = token_callback
        self._display_power = display_power

    async def native_power_state(self) -> str | None:
        """Use the panel's actual state when the optional host API supports it."""
        if self._display_power is None:
            return None
        try:
            state = await self._display_power()
        except QuietThingsError:
            return None
        return {"on": "on", "off": "standby"}.get(state)

    async def device_info(self) -> dict[str, Any]:
        """GET /api/v2/ and return the `device` object."""
        try:
            async with self._session.get(
                f"http://{self.host}:{STOCK_REST_PORT}/api/v2/",
                timeout=aiohttp.ClientTimeout(total=5),
            ) as resp:
                resp.raise_for_status()
                data = await resp.json(content_type=None)
            device = data["device"]
            if not isinstance(device, dict):
                raise TypeError
        except (aiohttp.ClientError, TimeoutError, ValueError, KeyError, TypeError):
            raise StockError("TV did not answer on port 8001") from None
        return device

    async def power_state(self) -> str:
        """Lower-case PowerState, `unreachable` when 8001 does not answer."""
        if (native := await self.native_power_state()) is not None:
            return native
        try:
            device = await self.device_info()
        except StockError:
            return "unreachable"
        return str(device.get("PowerState", "unknown")).lower()

    async def _wait_power(self, expected: str, seconds: float) -> bool:
        loop = asyncio.get_running_loop()
        deadline = loop.time() + seconds
        while True:
            if await self.power_state() == expected:
                return True
            if loop.time() >= deadline:
                return False
            await asyncio.sleep(POLL_INTERVAL)

    def _remote(self):

        return SamsungTVWSAsyncRemote(
            host=self.host,
            port=STOCK_WS_PORT,
            token=self.ws_token,
            timeout=WS_TIMEOUT,
            name=WS_CLIENT_NAME,
        )

    def _store_token(self, token: str | None) -> None:
        if token and token != self.ws_token:
            self.ws_token = token
            if self._token_callback:
                self._token_callback(token)

    async def pair(self, timeout: float) -> str:
        """Open 8002 so the TV shows Allow; return the issued token."""
        remote = self._remote()
        remote.timeout = timeout
        try:
            async with asyncio.timeout(timeout):
                await remote.open()
        except Exception as err:  # noqa: BLE001
            raise StockError(f"Pairing failed ({type(err).__name__})") from None
        finally:
            await remote.close()
        if not remote.token:
            raise StockError("TV did not issue a token")
        self._store_token(remote.token)
        return remote.token

    async def _send_power_key(self, only_if: str) -> bool:
        """Open 8002 and send KEY_POWER once if the state is still `only_if`."""

        remote = self._remote()
        sent = False
        try:
            await remote.open()
            self._store_token(remote.token)
            if await self.power_state() == only_if:
                await remote.send_command(SendRemoteKey.click("KEY_POWER"))
                sent = True
        except Exception as err:  # noqa: BLE001
            raise StockError(f"Power key failed ({type(err).__name__})") from None
        finally:
            await remote.close()
        return sent

    async def power_on(self) -> None:
        """Wake: WoL, then KEY_POWER once if the TV stays in standby."""
        if await self.power_state() == "on":
            return
        try:
            await send_magic_packet(self.mac, self.broadcast)
        except (OSError, ValueError) as err:
            raise StockError(f"Wake packet failed ({type(err).__name__})") from None
        if await self._wait_power("on", WAKE_WAIT):
            return
        if await self.power_state() != "standby":
            raise StockError("Wake packet sent; TV power state is unknown")
        await self._send_power_key("standby")
        if not await self._wait_power("on", KEY_WAKE_WAIT):
            raise StockError("TV did not confirm power on")

    async def power_off(self) -> None:
        """KEY_POWER only when the TV is on; wait for standby."""
        if await self.power_state() != "on":
            return
        await self._send_power_key("on")
        if not await self._wait_power("standby", STANDBY_WAIT):
            raise StockError("Power key sent; standby could not be verified")
