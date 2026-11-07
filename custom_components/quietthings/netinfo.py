"""Detect the TV's MAC address and the LAN broadcast address."""

from __future__ import annotations

import ipaddress
import logging
from typing import Any

from homeassistant.core import HomeAssistant

from .const import DEFAULT_BROADCAST
from .stock import parse_mac

_LOGGER = logging.getLogger(__name__)

ARP_TABLE = "/proc/net/arp"


def format_mac(mac: str) -> str:
    """Lower-case colon separated form of a valid MAC, or raise ValueError."""
    return ":".join(f"{b:02x}" for b in parse_mac(mac))


def read_arp_mac(host: str, path: str = ARP_TABLE) -> str | None:
    """MAC for `host` from the Linux neighbour table, or None. Blocking I/O."""
    try:
        with open(path, encoding="ascii", errors="replace") as table:
            lines = table.read().splitlines()[1:]
    except OSError:
        return None
    for line in lines:
        parts = line.split()
        # IP address, HW type, Flags, HW address, Mask, Device
        if len(parts) < 4 or parts[0] != host:
            continue
        try:
            return format_mac(parts[3])
        except ValueError:
            continue  # incomplete entry; another interface may have the real one
    return None


def device_wifi_mac(device: dict[str, Any]) -> str | None:
    """The `wifiMac` reported by the TV on port 8001, normalised, or None."""
    value = device.get("wifiMac")
    if not isinstance(value, str):
        return None
    try:
        return format_mac(value)
    except ValueError:
        return None


async def async_detect_mac(
    hass: HomeAssistant, host: str, device: dict[str, Any]
) -> str | None:
    """ARP table first (read off the event loop), then the reported wifiMac."""
    return await hass.async_add_executor_job(read_arp_mac, host) or device_wifi_mac(
        device
    )


async def async_detect_broadcast(hass: HomeAssistant, host: str) -> str:
    """Directed broadcast of the local IPv4 network containing `host`."""
    try:
        from homeassistant.components.network import async_get_adapters

        tv = ipaddress.IPv4Address(host)
        for adapter in await async_get_adapters(hass):
            for ipv4 in adapter.get("ipv4", []):
                network = ipaddress.ip_interface(
                    f"{ipv4['address']}/{ipv4['network_prefix']}"
                ).network
                if tv in network and network.prefixlen < 32:
                    return str(network.broadcast_address)
    except Exception:  # noqa: BLE001
        _LOGGER.debug("Broadcast detection failed", exc_info=True)
    return DEFAULT_BROADCAST
