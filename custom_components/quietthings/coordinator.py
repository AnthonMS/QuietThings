"""Data coordinators: stock 8001 power state and the QuietThings API /state."""

from __future__ import annotations

import logging
from datetime import timedelta
from typing import Any

from homeassistant.config_entries import ConfigEntry
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import QuietThingsApi, QuietThingsAuthError, QuietThingsError
from .const import DOMAIN, STOCK_FAILURES_BEFORE_UNAVAILABLE
from .stock import StockError, StockTv

_LOGGER = logging.getLogger(__name__)


class StockCoordinator(DataUpdateCoordinator[dict[str, Any]]):
    """Polls stock REST, preserving unreachable as a distinct power observation."""

    def __init__(
        self,
        hass: HomeAssistant,
        entry: ConfigEntry,
        stock: StockTv,
        interval: int,
    ) -> None:
        super().__init__(
            hass,
            _LOGGER,
            config_entry=entry,
            name=f"{DOMAIN} stock",
            update_interval=timedelta(seconds=interval),
        )
        self.stock = stock
        self.consecutive_failures = 0

    async def _async_update_data(self) -> dict[str, Any]:
        try:
            device = await self.stock.device_info()
        except StockError:
            self.consecutive_failures += 1
            if (native := await self.stock.native_power_state()) is not None:
                return {"power": native, "device": None, "power_source": "tizen-display"}
            return {"power": "unreachable", "device": None}
        self.consecutive_failures = 0
        native = await self.stock.native_power_state()
        return {
            "power": native if native is not None else str(device.get("PowerState", "unknown")).lower(),
            "device": device,
            "power_source": "tizen-display" if native is not None else "samsung-rest",
        }

    @property
    def is_on(self) -> bool:
        """True when the TV reports PowerState on."""
        return bool(self.data) and self.data["power"] == "on"

    @property
    def lost(self) -> bool:
        """True when 8001 has failed enough polls in a row."""
        return self.consecutive_failures >= STOCK_FAILURES_BEFORE_UNAVAILABLE and (self.data or {}).get("power_source") != "tizen-display"


class ApiCoordinator(DataUpdateCoordinator[dict[str, Any]]):
    """Polls the QuietThings API /state."""

    def __init__(
        self,
        hass: HomeAssistant,
        entry: ConfigEntry,
        api: QuietThingsApi,
        interval: int,
    ) -> None:
        super().__init__(
            hass,
            _LOGGER,
            config_entry=entry,
            name=f"{DOMAIN} api",
            update_interval=timedelta(seconds=interval),
        )
        self.api = api

    async def _async_update_data(self) -> dict[str, Any]:
        try:
            return await self.api.state()
        except QuietThingsAuthError as err:
            raise ConfigEntryAuthFailed("QuietThings API token rejected") from err
        except QuietThingsError as err:
            raise UpdateFailed(str(err)) from err

    def apply(self, patch: dict[str, Any]) -> None:
        """Merge a write response (selected, signal, volume) into the data."""
        if self.data is None:
            return
        merged = dict(self.data)
        for key in ("selected", "signal", "volume"):
            if key in patch:
                merged[key] = patch[key]
        self.async_set_updated_data(merged)
