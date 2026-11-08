"""Diagnostics with every secret redacted."""

from __future__ import annotations

from typing import Any

from homeassistant.components.diagnostics import async_redact_data
from homeassistant.core import HomeAssistant

from . import QuietThingsConfigEntry
from .const import CONF_API_TOKEN, CONF_MAC, CONF_WS_TOKEN

TO_REDACT = {CONF_API_TOKEN, CONF_WS_TOKEN, CONF_MAC, "wifiMac", "token"}


async def async_get_config_entry_diagnostics(
    hass: HomeAssistant, entry: QuietThingsConfigEntry
) -> dict[str, Any]:
    """Return diagnostics for a config entry."""
    runtime = entry.runtime_data
    playback = runtime.playback_coordinator
    sample = playback.sample if playback else None
    return {
        "playback": {
            "enabled": playback is not None,
            "available": sample is not None,
            "state": sample.state if sample else None,
            "adapter": sample.adapter if sample else None,
        },
        "entry": async_redact_data(dict(entry.data), TO_REDACT),
        "options": dict(entry.options),
        "stock": async_redact_data(runtime.stock_coordinator.data or {}, TO_REDACT),
        "stock_consecutive_failures": runtime.stock_coordinator.consecutive_failures,
        "api": async_redact_data(runtime.api_coordinator.data or {}, TO_REDACT),
        "api_last_update_success": runtime.api_coordinator.last_update_success,
    }
