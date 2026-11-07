"""Media player: power (stock), volume and HDMI source (QuietThings API)."""

from __future__ import annotations

import re
from typing import Any

from homeassistant.components.media_player import (
    MediaPlayerDeviceClass,
    MediaPlayerEntity,
    MediaPlayerEntityFeature,
    MediaPlayerState,
)
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import QuietThingsConfigEntry
from .api import QuietThingsError
from .const import HDMI_PORTS
from .coordinator import StockCoordinator
from .entity import device_info
from .stock import StockError

PARALLEL_UPDATES = 1

# Accepts "HDMI 4" and the labeled form "HDMI 4 (PC)".
SOURCE_RE = re.compile(r"HDMI ([1-4])(?: \(.*\))?")


def source_label(port: int, name: str | None) -> str:
    """`HDMI 2 (Cable Box)` when the input name is known."""
    if name and name.strip() and name.strip() != f"HDMI {port}":
        return f"HDMI {port} ({name.strip()})"
    return f"HDMI {port}"


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QuietThingsConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    async_add_entities([QuietThingsMediaPlayer(entry)])


class QuietThingsMediaPlayer(CoordinatorEntity[StockCoordinator], MediaPlayerEntity):
    """The TV. Name None: it takes the device name."""

    _attr_has_entity_name = True
    _attr_name = None
    _attr_device_class = MediaPlayerDeviceClass.TV
    _attr_supported_features = (
        MediaPlayerEntityFeature.TURN_ON
        | MediaPlayerEntityFeature.TURN_OFF
        | MediaPlayerEntityFeature.VOLUME_SET
        | MediaPlayerEntityFeature.VOLUME_MUTE
        | MediaPlayerEntityFeature.VOLUME_STEP
        | MediaPlayerEntityFeature.SELECT_SOURCE
    )

    def __init__(self, entry: QuietThingsConfigEntry) -> None:
        runtime = entry.runtime_data
        super().__init__(runtime.stock_coordinator)
        self._stock = runtime.stock
        self._api = runtime.api
        self._api_coordinator = runtime.api_coordinator
        self._attr_unique_id = f"{entry.unique_id}_media_player"
        self._attr_device_info = device_info(entry)

    async def async_added_to_hass(self) -> None:
        await super().async_added_to_hass()
        self.async_on_remove(
            self._api_coordinator.async_add_listener(self._handle_api_update)
        )

    @callback
    def _handle_api_update(self) -> None:
        self.async_write_ha_state()

    @property
    def _api_ok(self) -> bool:
        return self._api_coordinator.last_update_success and bool(
            self._api_coordinator.data
        )

    @property
    def _api_state(self) -> dict[str, Any]:
        return (self._api_coordinator.data or {}) if self._api_ok else {}

    @property
    def available(self) -> bool:
        return not (self.coordinator.lost and not self._api_coordinator.last_update_success)

    @property
    def state(self) -> MediaPlayerState:
        return MediaPlayerState.ON if self.coordinator.is_on else MediaPlayerState.OFF

    @property
    def volume_level(self) -> float | None:
        volume = self._api_state.get("volume")
        if not volume or volume.get("level") is None:
            return None
        return volume["level"] / 100

    @property
    def is_volume_muted(self) -> bool | None:
        volume = self._api_state.get("volume")
        if not volume:
            return None
        return bool(volume.get("mute"))

    @property
    def source_list(self) -> list[str]:
        names = {
            item.get("port"): item.get("name")
            for item in self._api_state.get("inputs") or []
        }
        return [source_label(port, names.get(port)) for port in HDMI_PORTS]

    @property
    def source(self) -> str | None:
        selected = self._api_state.get("selected")
        if not selected:
            return None
        port = selected.get("port")
        if port not in HDMI_PORTS:
            return None
        return self.source_list[port - 1]

    async def async_turn_on(self) -> None:
        try:
            await self._stock.power_on()
        except StockError as err:
            raise HomeAssistantError(str(err)) from err
        await self.coordinator.async_request_refresh()

    async def async_turn_off(self) -> None:
        try:
            await self._stock.power_off()
        except StockError as err:
            raise HomeAssistantError(str(err)) from err
        await self.coordinator.async_request_refresh()

    async def _call(self, coro) -> None:
        try:
            patch = await coro
        except QuietThingsError as err:
            raise HomeAssistantError(f"QuietThings API: {err}") from err
        self._api_coordinator.apply(patch)

    def _require_api(self) -> None:
        if not self._api_ok:
            raise HomeAssistantError("QuietThings API is unavailable")

    async def async_set_volume_level(self, volume: float) -> None:
        self._require_api()
        await self._call(self._api.set_volume(round(volume * 100)))

    async def async_mute_volume(self, mute: bool) -> None:
        self._require_api()
        await self._call(self._api.set_mute(mute))

    async def _step(self, delta: int) -> None:
        self._require_api()
        volume = self._api_state.get("volume")
        if not volume or volume.get("level") is None:
            raise HomeAssistantError("TV volume is unknown")
        level = max(0, min(100, int(volume["level"]) + delta))
        await self._call(self._api.set_volume(level))

    async def async_volume_up(self) -> None:
        await self._step(1)

    async def async_volume_down(self) -> None:
        await self._step(-1)

    async def async_select_source(self, source: str) -> None:
        match = SOURCE_RE.fullmatch(source.strip())
        if not match:
            raise ServiceValidationError(f"Unknown source {source}")
        self._require_api()
        await self._call(self._api.select_hdmi(int(match.group(1))))
