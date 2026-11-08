"""Config flow for QuietThings."""

from __future__ import annotations

import ipaddress
from collections.abc import Mapping
from typing import Any

import voluptuous as vol
from homeassistant.config_entries import (
    ConfigEntry,
    ConfigFlow,
    ConfigFlowResult,
    OptionsFlowWithReload,
)
from homeassistant.core import callback
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import QuietThingsApi, QuietThingsAuthError, QuietThingsError
from .const import (
    CONF_API_PORT,
    CONF_API_TOKEN,
    CONF_BROADCAST,
    CONF_HOST,
    CONF_MAC,
    CONF_MODEL,
    CONF_NAME,
    CONF_PLAYBACK_PORT,
    CONF_SCAN_INTERVAL,
    CONF_WS_TOKEN,
    DEFAULT_API_PORT,
    DEFAULT_BROADCAST,
    DEFAULT_SCAN_INTERVAL,
    DOMAIN,
    MAX_SCAN_INTERVAL,
    MIN_SCAN_INTERVAL,
    PAIR_TIMEOUT,
    SUPPORTED_API_VERSION,
)
from .netinfo import (
    async_detect_broadcast,
    async_detect_mac,
    format_mac,
)
from .stock import StockError, StockTv


def _api_version_error(health: Mapping[str, Any]) -> dict[str, str]:
    """Placeholders for the unsupported_api error text."""
    return {
        "version": str(health.get("api_version")),
        "supported": str(SUPPORTED_API_VERSION),
    }


def _user_schema(defaults: Mapping[str, Any]) -> vol.Schema:
    # The name has no default on a fresh form; it is only pre-filled to keep
    # what the user typed when the form is shown again after an error.
    name_key = (
        vol.Required(CONF_NAME, default=defaults[CONF_NAME])
        if defaults.get(CONF_NAME)
        else vol.Required(CONF_NAME)
    )
    return vol.Schema(
        {
            name_key: str,
            vol.Required(CONF_HOST, default=defaults.get(CONF_HOST, "")): str,
            vol.Required(
                CONF_API_PORT,
                default=defaults.get(CONF_API_PORT, DEFAULT_API_PORT),
            ): vol.All(int, vol.Range(min=1, max=65535)),
            vol.Required(CONF_API_TOKEN, default=defaults.get(CONF_API_TOKEN, "")): str,
        }
    )


class QuietThingsConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle the config flow."""

    VERSION = 1

    def __init__(self) -> None:
        self._data: dict[str, Any] = {}
        self._stock: StockTv | None = None

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> OptionsFlowWithReload:
        return QuietThingsOptionsFlow()

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        placeholders: dict[str, str] = {}
        if user_input is not None:
            user_input = {
                **user_input,
                CONF_NAME: user_input[CONF_NAME].strip(),
                CONF_HOST: user_input[CONF_HOST].strip(),
            }
            if not user_input[CONF_NAME]:
                errors[CONF_NAME] = "name_required"
            if not errors:
                session = async_get_clientsession(self.hass)
                api = QuietThingsApi(
                    session,
                    user_input[CONF_HOST],
                    user_input[CONF_API_PORT],
                    user_input[CONF_API_TOKEN],
                )
                stock = StockTv(
                    session,
                    user_input[CONF_HOST],
                    "",
                    DEFAULT_BROADCAST,
                )
                device: dict[str, Any] = {}
                try:
                    health = await api.health()
                    device = await stock.device_info()
                except QuietThingsAuthError:
                    errors["base"] = "invalid_auth"
                except (QuietThingsError, StockError):
                    errors["base"] = "cannot_connect"
                else:
                    if health.get("api_version") != SUPPORTED_API_VERSION:
                        errors["base"] = "unsupported_api"
                        placeholders = _api_version_error(health)
                    elif not device.get("id"):
                        errors["base"] = "cannot_connect"
            if not errors:
                await self.async_set_unique_id(str(device["id"]))
                self._abort_if_unique_id_configured()
                self._data = {
                    **user_input,
                    CONF_MODEL: device.get("modelName"),
                }
                self._stock = stock
                mac = await async_detect_mac(self.hass, user_input[CONF_HOST], device)
                if mac is None:
                    return await self.async_step_mac()
                return await self._finish_addressing(mac)
        return self.async_show_form(
            step_id="user",
            data_schema=_user_schema(user_input or {}),
            errors=errors,
            description_placeholders=placeholders or None,
        )

    async def _finish_addressing(self, mac: str) -> ConfigFlowResult:
        """Store the MAC, work out the broadcast address, go on to pairing."""
        broadcast = await async_detect_broadcast(self.hass, self._data[CONF_HOST])
        self._data = {**self._data, CONF_MAC: mac, CONF_BROADCAST: broadcast}
        if self._stock is not None:
            self._stock.mac = mac
            self._stock.broadcast = broadcast
        return await self.async_step_pair()

    async def async_step_mac(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Ask for the MAC when it could not be detected."""
        errors: dict[str, str] = {}
        if user_input is not None:
            try:
                mac = format_mac(user_input[CONF_MAC].strip())
            except ValueError:
                errors[CONF_MAC] = "invalid_mac"
            else:
                return await self._finish_addressing(mac)
        return self.async_show_form(
            step_id="mac",
            data_schema=vol.Schema(
                {
                    vol.Required(
                        CONF_MAC,
                        default=(user_input or {}).get(CONF_MAC, ""),
                    ): str
                }
            ),
            errors=errors,
        )

    async def async_step_pair(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Open the WebSocket so the TV shows Allow, then store the token."""
        errors: dict[str, str] = {}
        if user_input is not None and self._stock is not None:
            try:
                token = await self._stock.pair(PAIR_TIMEOUT)
            except StockError:
                errors["base"] = "pairing_failed"
            else:
                return self.async_create_entry(
                    title=self._data[CONF_NAME],
                    data={**self._data, CONF_WS_TOKEN: token},
                )
        return self.async_show_form(
            step_id="pair", data_schema=vol.Schema({}), errors=errors
        )

    async def async_step_reauth(
        self, entry_data: Mapping[str, Any]
    ) -> ConfigFlowResult:
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        placeholders: dict[str, str] = {}
        entry = self._get_reauth_entry()
        if user_input is not None:
            api = QuietThingsApi(
                async_get_clientsession(self.hass),
                entry.data[CONF_HOST],
                entry.data.get(CONF_API_PORT, DEFAULT_API_PORT),
                user_input[CONF_API_TOKEN],
            )
            try:
                health = await api.health()
            except QuietThingsAuthError:
                errors["base"] = "invalid_auth"
            except QuietThingsError:
                errors["base"] = "cannot_connect"
            else:
                if health.get("api_version") != SUPPORTED_API_VERSION:
                    errors["base"] = "unsupported_api"
                    placeholders = _api_version_error(health)
            if not errors:
                return self.async_update_reload_and_abort(
                    entry,
                    data_updates={CONF_API_TOKEN: user_input[CONF_API_TOKEN]},
                )
        return self.async_show_form(
            step_id="reauth_confirm",
            data_schema=vol.Schema({vol.Required(CONF_API_TOKEN): str}),
            errors=errors,
            description_placeholders=placeholders or None,
        )


class QuietThingsOptionsFlow(OptionsFlowWithReload):
    """Poll interval, MAC address and broadcast address.

    The MAC and broadcast live in entry.data (as set by the config flow), so
    they are written there with async_update_entry; the poll interval stays
    in entry.options.
    """

    async def async_step_init(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        entry = self.config_entry
        if user_input is not None:
            mac = user_input[CONF_MAC].strip()
            broadcast = user_input[CONF_BROADCAST].strip()
            try:
                mac = format_mac(mac)
            except ValueError:
                errors[CONF_MAC] = "invalid_mac"
            try:
                ipaddress.IPv4Address(broadcast)
            except ValueError:
                errors[CONF_BROADCAST] = "invalid_broadcast"
            if not errors:
                options = {CONF_SCAN_INTERVAL: user_input[CONF_SCAN_INTERVAL]}
                if playback_port := user_input.get(CONF_PLAYBACK_PORT, 0):
                    options[CONF_PLAYBACK_PORT] = playback_port
                data = {**entry.data, CONF_MAC: mac, CONF_BROADCAST: broadcast}
                data_changed = data != dict(entry.data)
                options_changed = options != dict(entry.options)
                if data_changed:
                    self.hass.config_entries.async_update_entry(entry, data=data)
                    if not options_changed:
                        # The automatic reload only fires on changed options.
                        self.hass.config_entries.async_schedule_reload(entry.entry_id)
                return self.async_create_entry(data=options)
        source = user_input or {}
        current = source.get(
            CONF_SCAN_INTERVAL,
            entry.options.get(CONF_SCAN_INTERVAL, DEFAULT_SCAN_INTERVAL),
        )
        return self.async_show_form(
            step_id="init",
            data_schema=vol.Schema(
                {
                    vol.Optional(
                        CONF_PLAYBACK_PORT,
                        default=source.get(
                            CONF_PLAYBACK_PORT, entry.options.get(CONF_PLAYBACK_PORT, 0)
                        ),
                    ): vol.All(int, vol.Range(min=0, max=65535)),
                    vol.Required(CONF_SCAN_INTERVAL, default=current): vol.All(
                        int, vol.Range(min=MIN_SCAN_INTERVAL, max=MAX_SCAN_INTERVAL)
                    ),
                    vol.Required(
                        CONF_MAC,
                        default=source.get(CONF_MAC, entry.data.get(CONF_MAC, "")),
                    ): str,
                    vol.Required(
                        CONF_BROADCAST,
                        default=source.get(
                            CONF_BROADCAST,
                            entry.data.get(CONF_BROADCAST, DEFAULT_BROADCAST),
                        ),
                    ): str,
                }
            ),
            errors=errors,
        )
