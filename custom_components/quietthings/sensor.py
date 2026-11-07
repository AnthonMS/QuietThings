"""Input state sensors (QuietThings API)."""

from __future__ import annotations

from typing import Any

from homeassistant.components.sensor import SensorDeviceClass, SensorEntity
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from . import QuietThingsConfigEntry
from .const import SIGNAL_ACTIVE, SIGNAL_NONE, SIGNAL_UNKNOWN
from .entity import QuietThingsApiEntity

PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QuietThingsConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    async_add_entities([CurrentInputSensor(entry), InputSignalSensor(entry)])


class CurrentInputSensor(QuietThingsApiEntity, SensorEntity):
    """Name of the selected input."""

    _attr_name = "Current input"

    def __init__(self, entry: QuietThingsConfigEntry) -> None:
        super().__init__(entry, "current_input")

    @property
    def native_value(self) -> str | None:
        selected = self.state_data.get("selected")
        return selected.get("name") if selected else None

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        selected = self.state_data.get("selected") or {}
        return {"port": selected.get("port"), "id": selected.get("id")}


class InputSignalSensor(QuietThingsApiEntity, SensorEntity):
    """Whether the selected input has a signal."""

    _attr_name = "Input signal"
    _attr_device_class = SensorDeviceClass.ENUM
    _attr_options = [SIGNAL_ACTIVE, SIGNAL_NONE, SIGNAL_UNKNOWN]
    _attr_translation_key = "input_signal"

    def __init__(self, entry: QuietThingsConfigEntry) -> None:
        super().__init__(entry, "input_signal")

    @property
    def native_value(self) -> str:
        data = self.state_data
        signal = data.get("signal")
        if not data.get("selected") or not isinstance(signal, dict):
            return SIGNAL_UNKNOWN
        return SIGNAL_ACTIVE if signal.get("active") else SIGNAL_NONE
