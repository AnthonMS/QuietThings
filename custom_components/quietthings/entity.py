"""Shared entity base classes."""

from __future__ import annotations

from typing import Any

from homeassistant.helpers.device_registry import DeviceInfo
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import QuietThingsConfigEntry
from .const import CONF_MODEL, CONF_NAME, DOMAIN
from .coordinator import ApiCoordinator


def device_info(entry: QuietThingsConfigEntry) -> DeviceInfo:
    """One device per TV. `name` is only a default; a user rename is kept."""
    stock = entry.runtime_data.stock_coordinator.data or {}
    device = stock.get("device") or {}
    return DeviceInfo(
        identifiers={(DOMAIN, entry.unique_id)},
        name=entry.data[CONF_NAME],
        manufacturer="Samsung",
        model=device.get("modelName") or entry.data.get(CONF_MODEL),
    )


class QuietThingsApiEntity(CoordinatorEntity[ApiCoordinator]):
    """Entity fed by the QuietThings API; unavailable when /state failed."""

    _attr_has_entity_name = True

    def __init__(self, entry: QuietThingsConfigEntry, key: str) -> None:
        super().__init__(entry.runtime_data.api_coordinator)
        self._attr_unique_id = f"{entry.unique_id}_{key}"
        self._attr_device_info = device_info(entry)

    @property
    def available(self) -> bool:
        return self.coordinator.last_update_success and bool(self.coordinator.data)

    @property
    def state_data(self) -> dict[str, Any]:
        """The last /state body."""
        return self.coordinator.data or {}
