"""The QuietThings integration: power, volume and HDMI input for a Samsung TV."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import timedelta

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import Platform
from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import QuietThingsApi
from .const import (
    CONF_API_PORT,
    CONF_API_TOKEN,
    CONF_BROADCAST,
    CONF_HOST,
    CONF_MAC,
    CONF_PLAYBACK_PORT,
    CONF_SCAN_INTERVAL,
    CONF_WS_TOKEN,
    DEFAULT_API_PORT,
    DEFAULT_BROADCAST,
    DEFAULT_SCAN_INTERVAL,
)
from .coordinator import ApiCoordinator, StockCoordinator
from .playback import PlaybackApi, PlaybackCoordinator
from .stock import StockTv

PLATFORMS = [Platform.MEDIA_PLAYER, Platform.SELECT, Platform.SENSOR]


@dataclass
class QuietThingsData:
    """Runtime objects of one config entry."""

    stock: StockTv
    api: QuietThingsApi
    stock_coordinator: StockCoordinator
    api_coordinator: ApiCoordinator
    playback_coordinator: PlaybackCoordinator | None = None


type QuietThingsConfigEntry = ConfigEntry[QuietThingsData]


async def async_setup_entry(hass: HomeAssistant, entry: QuietThingsConfigEntry) -> bool:
    """Set up from a config entry. Succeeds even when the API is down."""
    session = async_get_clientsession(hass)
    data = entry.data

    def store_token(token: str) -> None:
        hass.config_entries.async_update_entry(
            entry, data={**entry.data, CONF_WS_TOKEN: token}
        )

    stock = StockTv(
        session,
        data[CONF_HOST],
        data[CONF_MAC],
        data.get(CONF_BROADCAST, DEFAULT_BROADCAST),
        data.get(CONF_WS_TOKEN),
        store_token,
    )
    api = QuietThingsApi(
        session,
        data[CONF_HOST],
        data.get(CONF_API_PORT, DEFAULT_API_PORT),
        data[CONF_API_TOKEN],
    )
    interval = entry.options.get(CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL)
    stock_coordinator = StockCoordinator(hass, entry, stock, interval)
    api_coordinator = ApiCoordinator(hass, entry, api, interval)

    # Neither refresh may block setup: stock power must work without the API.
    await stock_coordinator.async_refresh()
    await api_coordinator.async_refresh()

    playback_coordinator = None
    if port := entry.options.get(CONF_PLAYBACK_PORT, 0):
        playback_api = PlaybackApi(session, data[CONF_HOST], port, data[CONF_API_TOKEN])
        playback_coordinator = PlaybackCoordinator(
            hass, entry, playback_api, timedelta(seconds=interval)
        )
        await playback_coordinator.async_refresh()

    entry.runtime_data = QuietThingsData(
        stock, api, stock_coordinator, api_coordinator, playback_coordinator
    )
    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    return True


async def async_unload_entry(
    hass: HomeAssistant, entry: QuietThingsConfigEntry
) -> bool:
    """Unload a config entry."""
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)
