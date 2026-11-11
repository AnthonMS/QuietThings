"""Client for the QuietThings API running on the TV."""

from __future__ import annotations

from typing import Any
from datetime import UTC, datetime

import aiohttp

from .const import API_HDMI_TIMEOUT, API_TIMEOUT


class QuietThingsError(Exception):
    """Base error for the QuietThings API."""


class QuietThingsAuthError(QuietThingsError):
    """The bearer token was rejected (HTTP 401)."""


class QuietThingsUnavailable(QuietThingsError):
    """The API could not be reached (connect error or timeout)."""


class QuietThingsRejected(QuietThingsError):
    """The API answered with an error or an unusable body."""

    def __init__(self, message: str, status: int | None = None) -> None:
        super().__init__(message)
        self.status = status


class QuietThingsApi:
    """Thin aiohttp client. Never logs the token or the request headers."""

    def __init__(
        self,
        session: aiohttp.ClientSession,
        host: str,
        port: int,
        token: str,
    ) -> None:
        self._session = session
        self._base = f"http://{host}:{port}"
        self._token = token

    async def _request(
        self,
        method: str,
        path: str,
        payload: dict[str, Any] | None = None,
        timeout: float = API_TIMEOUT,
    ) -> dict[str, Any]:
        headers = {
            "Authorization": f"Bearer {self._token}",
            "Connection": "close",
        }
        try:
            async with self._session.request(
                method,
                f"{self._base}{path}",
                headers=headers,
                json=payload,
                timeout=aiohttp.ClientTimeout(total=timeout),
            ) as resp:
                status = resp.status
                try:
                    body = await resp.json(content_type=None)
                except (ValueError, aiohttp.ContentTypeError):
                    body = None
        except (aiohttp.ClientError, TimeoutError) as err:
            raise QuietThingsUnavailable(
                f"QuietThings API unreachable ({type(err).__name__})"
            ) from None

        if status == 401:
            raise QuietThingsAuthError("QuietThings API token rejected")
        message = body.get("error") if isinstance(body, dict) else None
        if status >= 400:
            raise QuietThingsRejected(
                str(message or f"HTTP {status}")[:200], status=status
            )
        if not isinstance(body, dict):
            raise QuietThingsRejected("Unexpected response from QuietThings API", status)
        if body.get("ok") is False:
            raise QuietThingsRejected(str(message or "request failed")[:200], status)
        return body

    async def health(self) -> dict[str, Any]:
        """GET /health."""
        return await self._request("GET", "/health")

    async def state(self) -> dict[str, Any]:
        """GET /state."""
        return await self._request("GET", "/state")

    async def display_power(self) -> str | None:
        """Optional fresh native panel observation; legacy hosts return 404."""
        body = await self._request("GET", "/power")
        power = body.get("power")
        if body.get("power_source") != "tizen-display" or not isinstance(power, str) or power not in {"on", "off"}:
            return None
        try:
            observed = datetime.fromisoformat(body["observed_at"])
            if observed.tzinfo is None or not -5 <= (datetime.now(UTC) - observed).total_seconds() <= 10:
                return None
        except (KeyError, TypeError, ValueError):
            return None
        return power

    async def select_hdmi(self, port: int) -> dict[str, Any]:
        """POST /hdmi/{port}."""
        return await self._request(
            "POST", f"/hdmi/{int(port)}", timeout=API_HDMI_TIMEOUT
        )

    async def set_volume(self, level: int) -> dict[str, Any]:
        """POST /volume."""
        return await self._request("POST", "/volume", {"level": int(level)})

    async def set_mute(self, mute: bool) -> dict[str, Any]:
        """POST /mute."""
        return await self._request("POST", "/mute", {"mute": bool(mute)})
