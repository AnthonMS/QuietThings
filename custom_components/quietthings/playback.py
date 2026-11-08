"""Optional companion playback protocol; never interpret launcher pause as media pause."""

from __future__ import annotations

import logging
import math
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Any

from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator, UpdateFailed

from .api import QuietThingsApi, QuietThingsError

STATES = frozenset({"unknown", "idle", "playing", "paused", "buffering"})
COMMANDS = frozenset({"play", "pause", "stop", "seek"})
MAX_AGE_SECONDS = 15


def timestamp(value: Any) -> datetime | None:
    if not isinstance(value, str):
        return None
    try:
        result = datetime.fromisoformat(value)
    except ValueError:
        return None
    return result if result.tzinfo is not None else None


def seconds(value: Any) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (float, int)):
        return None
    return float(value) if math.isfinite(value) and value >= 0 else None


@dataclass(frozen=True)
class PlaybackSample:
    app_id: str | None
    state: str
    adapter: str | None
    title: str | None
    media_type: str | None
    position: float | None
    duration: float | None
    observed_at: datetime
    position_updated_at: datetime | None
    commands: frozenset[str]

    def fresh(self, now: datetime | None = None) -> bool:
        age = ((now or datetime.now(UTC)) - self.observed_at).total_seconds()
        return -5 <= age <= MAX_AGE_SECONDS


def parse_sample(body: dict[str, Any]) -> PlaybackSample:
    """Reject incompatible/invalid data and clear unsupported or stale media fields."""
    if (
        body.get("ok") is not True
        or type(body.get("extension_version")) is not int
        or body.get("extension_version") != 1
        or not isinstance(body.get("playback"), dict)
    ):
        raise ValueError("Unsupported playback extension")
    data = body["playback"]
    observed = timestamp(data.get("observed_at"))
    state = data.get("state")
    if observed is None or state not in STATES:
        raise ValueError("Invalid playback observation")
    app = data.get("app_id")
    if app is not None and (not isinstance(app, str) or not app or len(app) > 256):
        raise ValueError("Invalid app identity")
    commands = data.get("commands", [])
    if not isinstance(commands, list) or any(
        not isinstance(c, str) or c not in COMMANDS for c in commands
    ):
        raise ValueError("Invalid playback capabilities")
    media = state in {"playing", "paused", "buffering"}
    # A transport command requires an identified media owner and adapter.
    adapter = data.get("adapter")
    if adapter is not None and (not isinstance(adapter, str) or len(adapter) > 128):
        raise ValueError("Invalid playback adapter")
    if media and (not app or not adapter):
        raise ValueError("Media owner missing")
    title = data.get("title")
    if title is not None and (not isinstance(title, str) or len(title) > 512):
        raise ValueError("Invalid playback title")
    kind = data.get("media_type")
    if kind is not None and not isinstance(kind, str):
        raise ValueError("Invalid media type")
    sample = PlaybackSample(
        app,
        state,
        adapter,
        title if media else None,
        kind if media else None,
        seconds(data.get("position_seconds")) if media else None,
        seconds(data.get("duration_seconds")) if media else None,
        observed,
        timestamp(data.get("position_updated_at")) if media else None,
        frozenset(commands) if media and app and adapter else frozenset(),
    )
    if not sample.fresh():
        raise ValueError("Stale playback observation")
    if (
        sample.position is not None
        and sample.duration is not None
        and sample.position > sample.duration
    ):
        raise ValueError("Position exceeds duration")
    if sample.position is not None and sample.position_updated_at is None:
        raise ValueError("Position timestamp missing")
    if sample.position_updated_at and sample.position_updated_at > observed + timedelta(
        seconds=5
    ):
        raise ValueError("Position timestamp is in the future")
    if "seek" in sample.commands and sample.duration is None:
        raise ValueError("Seek duration missing")
    return sample


class PlaybackApi(QuietThingsApi):
    """Separate port and protocol; the original API v1 remains unchanged."""

    async def playback(self) -> PlaybackSample:
        return parse_sample(await self._request("GET", "/v1/playback", timeout=12))

    async def command(self, action: str, position: float | None = None) -> None:
        if action not in COMMANDS:
            raise ValueError("Unsupported command")
        payload: dict[str, Any] = {"action": action}
        if action == "seek":
            if seconds(position) is None:
                raise ValueError("Invalid seek position")
            payload["position_seconds"] = position
        await self._request("POST", "/v1/command", payload, timeout=20)


class PlaybackCoordinator(DataUpdateCoordinator[PlaybackSample]):
    def __init__(self, hass, entry, api: PlaybackApi, interval):
        super().__init__(
            hass,
            logging.getLogger(__name__),
            config_entry=entry,
            name="quietthings playback",
            update_interval=interval,
        )
        self.api = api

    async def _async_update_data(self) -> PlaybackSample:
        try:
            return await self.api.playback()
        except (QuietThingsError, ValueError) as err:
            # Extension failure must clear its media, while hardware control survives.
            raise UpdateFailed("Playback observation unavailable") from err

    @property
    def sample(self) -> PlaybackSample | None:
        return (
            self.data
            if self.last_update_success and self.data and self.data.fresh()
            else None
        )

    async def command(self, action: str, position: float | None = None) -> None:
        sample = self.sample
        if sample is None or action not in sample.commands:
            raise HomeAssistantError("Playback command unavailable")
        try:
            await self.api.command(action, position)
        except (QuietThingsError, ValueError) as err:
            raise HomeAssistantError("Playback command rejected") from err
        await self.async_request_refresh()
