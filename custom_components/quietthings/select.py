"""HDMI input select (QuietThings API)."""

from __future__ import annotations

import re
from typing import Any

from homeassistant.components.select import SelectEntity
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError, ServiceValidationError
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from . import QuietThingsConfigEntry
from .api import QuietThingsError
from .const import HDMI_PORTS
from .entity import QuietThingsApiEntity

PARALLEL_UPDATES = 1

OPTIONS = [f"HDMI {port}" for port in HDMI_PORTS]


async def async_setup_entry(
    hass: HomeAssistant,
    entry: QuietThingsConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    async_add_entities([HdmiInputSelect(entry)])


class HdmiInputSelect(QuietThingsApiEntity, SelectEntity):
    """Choose HDMI 1 to 4."""

    _attr_name = "HDMI input"
    _attr_options = OPTIONS

    def __init__(self, entry: QuietThingsConfigEntry) -> None:
        super().__init__(entry, "hdmi_input")
        self._api = entry.runtime_data.api

    @property
    def current_option(self) -> str | None:
        selected = self.state_data.get("selected")
        port = selected.get("port") if selected else None
        return f"HDMI {port}" if port in HDMI_PORTS else None

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        inputs = self.state_data.get("inputs") or []
        return {
            "labels": {f"HDMI {i['port']}": i.get("name") for i in inputs},
            "connected": {f"HDMI {i['port']}": i.get("connected") for i in inputs},
        }

    async def async_select_option(self, option: str) -> None:
        match = re.fullmatch(r"HDMI ([1-4])", option)
        if not match:
            raise ServiceValidationError(f"Unknown input {option}")
        try:
            patch = await self._api.select_hdmi(int(match.group(1)))
        except QuietThingsError as err:
            raise HomeAssistantError(f"QuietThings API: {err}") from err
        self.coordinator.apply(patch)
