"""Local TV overlays and explicit, short-lived remote action events."""

from __future__ import annotations

import asyncio
from datetime import UTC, datetime, timedelta
import logging
import re
from uuid import uuid4

import voluptuous as vol

from homeassistant.core import HomeAssistant, ServiceCall, SupportsResponse
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers.event import async_track_time_interval
from homeassistant.helpers.storage import Store

from .api import QuietThingsApi, QuietThingsError, QuietThingsRejected
from .const import DOMAIN
from .notification_media import prepare_image, fetch_image, image_url

_LOGGER = logging.getLogger(__name__)

EVENT_ACTION = "quietthings_notification_action"
IDENTIFIER = re.compile(r"[A-Za-z0-9_:-]{1,64}\Z")


def identifier(value):
    """Use bounded opaque identifiers, never executable instructions."""
    if not isinstance(value, str) or not IDENTIFIER.fullmatch(value):
        raise vol.Invalid("Use 1 to 64 letters, digits, underscores, colons or hyphens")
    return value


def text(limit):
    def validate(value):
        if not isinstance(value, str) or len(value) > limit or any(
            ord(c) < 32 and c not in "\n\t" for c in value
        ):
            raise vol.Invalid("Invalid or oversized notification text")
        return value
    return validate


ACTION_SCHEMA = vol.Schema({
    vol.Required("action"): vol.All(identifier, vol.NotIn(["DISMISS"])),
    vol.Required("title"): vol.All(text(40), vol.Length(min=1)),
})
def validate_image_url(value):
    try:
        return image_url(value)
    except ValueError as error:
        raise vol.Invalid(str(error)) from None


NOTIFY_SCHEMA = vol.Schema({
    vol.Required("entry_id"): cv.string,
    vol.Optional("id"): identifier,
    vol.Optional("title", default="Home Assistant"): vol.All(text(100), vol.Length(min=1)),
    vol.Required("message"): text(500),
    vol.Optional("duration_seconds", default=15): vol.All(vol.Coerce(int), vol.Range(min=5, max=120)),
    vol.Optional("camera_entity"): vol.All(cv.entity_id, cv.entity_domain("camera")),
    vol.Optional("camera_mode", default="live"): vol.In(["live", "snapshot"]),
    vol.Optional("camera_refresh_seconds", default=1): vol.All(vol.Coerce(float), vol.Range(min=0.5, max=10)),
    vol.Optional("image_url"): validate_image_url,
    vol.Optional("actions", default=[]): vol.All([ACTION_SCHEMA], vol.Length(max=3)),
})
DISMISS_SCHEMA = vol.Schema({vol.Required("entry_id"): cv.string, vol.Required("id"): identifier})


class NotificationClient(QuietThingsApi):
    async def _request(self, *args, **kwargs):
        # A confirmed 429 has not reached the UI. Retry that explicit refusal only;
        # never repeat an uncertain POST after a connection failure or timeout.
        for attempt in range(4):
            try:
                return await super()._request(*args, **kwargs)
            except QuietThingsRejected as error:
                if error.status != 429 or attempt == 3:
                    raise
                await asyncio.sleep(0.4 * (attempt + 1))

    async def show(self, payload):
        return await self._request("POST", "/v1/notifications", payload)

    async def dismiss(self, notification_id):
        return await self._request("POST", "/v1/notifications/dismiss", {"id": notification_id})

    async def events(self, after):
        return await self._request("GET", f"/v1/notifications/events?after={after}")

    async def capabilities(self):
        return await self._request("GET", "/v1/capabilities")

    async def status(self):
        return await self._request("GET", "/v1/notifications/status")

    async def image(self, notification_id, sequence, data):
        return await self._request("POST", "/v1/notifications/image", {"id": notification_id, "sequence": sequence, "image_base64": data})


class TvNotifications:
    """Persist acknowledgements before firing events, favoring safety over replay."""

    def __init__(self, hass: HomeAssistant, entry, api: NotificationClient):
        self.hass, self.entry, self.api = hass, entry, api
        self.store = Store(hass, 1, f"{DOMAIN}.notifications.{entry.entry_id}")
        self.state = {"epoch": None, "sequence": 0, "pending": {}}
        self.lock = asyncio.Lock()
        self.preview_task = None
        self.closed = False

    async def start(self):
        saved = await self.store.async_load()
        if isinstance(saved, dict) and isinstance(saved.get("pending"), dict):
            self.state = saved
        self.entry.async_on_unload(self.close)
        self.entry.async_on_unload(async_track_time_interval(
            self.hass, self.poll, timedelta(seconds=1)
        ))

    def stop_preview(self):
        if self.preview_task is not None:
            self.preview_task.cancel()
            self.preview_task = None

    def close(self):
        self.closed = True
        self.stop_preview()

    async def camera_frame(self, camera):
        from homeassistant.components.camera import async_get_image
        image = await async_get_image(self.hass, camera, timeout=5, width=960, height=540)
        return await self.hass.async_add_executor_job(prepare_image, image.content, False)

    async def live_preview(self, notification_id, camera, duration, interval):
        deadline = asyncio.get_running_loop().time() + duration
        sequence, failures = 0, 0
        while not self.closed and asyncio.get_running_loop().time() < deadline:
            await asyncio.sleep(interval)
            if self.closed or asyncio.get_running_loop().time() >= deadline:
                return
            try:
                status = await self.api.status()
                if not status.get("active") or status.get("id") != notification_id:
                    return
                frame = await self.camera_frame(camera)
                sequence += 1
                result = await self.api.image(notification_id, sequence, frame)
                if result.get("updated") is not True:
                    return
                failures = 0
            except (QuietThingsError, HomeAssistantError, ValueError, OSError):
                failures += 1
                if failures >= 3:
                    _LOGGER.warning("QuietThings live camera preview stopped after repeated frame failures")
                    return

    def prune(self):
        now = datetime.now(UTC).timestamp()
        self.state["pending"] = {
            key: value for key, value in self.state["pending"].items()
            if isinstance(value, dict) and isinstance(value.get("expires"), (int, float))
            and value["expires"] > now
        }

    async def show(self, data):
        payload = {k: v for k, v in data.items() if k not in {"entry_id", "camera_entity", "camera_mode", "camera_refresh_seconds", "image_url"}}
        payload.setdefault("id", uuid4().hex)
        actions = payload["actions"]
        if len({action["action"] for action in actions}) != len(actions):
            raise HomeAssistantError("Notification actions must be unique")
        camera, url = data.get("camera_entity"), data.get("image_url")
        if camera and url:
            raise HomeAssistantError("Choose a camera entity or an image URL")
        live = bool(camera and data.get("camera_mode", "live") == "live")
        if camera:
            payload["image_base64"] = await self.camera_frame(camera)
            if live:
                payload["image_live"] = True
        elif url:
            content = await fetch_image(self.hass, url)
            payload["image_base64"] = await self.hass.async_add_executor_job(prepare_image, content)
        if live or url:
            capabilities = await self.api.capabilities()
            if capabilities.get("notification_media_version", 0) < 2:
                raise HomeAssistantError("Update Quiet Home to use live cameras or image URLs")
        async with self.lock:
            self.prune()
            if payload["id"] in self.state["pending"]:
                raise HomeAssistantError("Use a new notification ID for each actionable notification")
            if len(self.state["pending"]) >= 32:
                raise HomeAssistantError("Too many pending TV notifications; wait for them to expire")
            # Establish a cursor before accepting any actions for the new notification.
            await self._poll()
            if actions:
                self.state["pending"][payload["id"]] = {
                    "actions": [action["action"] for action in actions],
                    "expires": datetime.now(UTC).timestamp() + payload["duration_seconds"],
                }
                await self.store.async_save(self.state)
            try:
                result = await self.api.show(payload)
            except QuietThingsError as err:
                self.state["pending"].pop(payload["id"], None)
                await self.store.async_save(self.state)
                raise HomeAssistantError(str(err)) from None
            if actions and result.get("actions_available") is not True:
                self.state["pending"].pop(payload["id"], None)
                await self.store.async_save(self.state)
                raise HomeAssistantError("TV displayed the notice but remote actions are unavailable")
            self.stop_preview()
            if live and not self.closed:
                self.preview_task = self.hass.async_create_background_task(
                    self.live_preview(payload["id"], camera, payload["duration_seconds"], data.get("camera_refresh_seconds", 1)),
                    "QuietThings live camera preview",
                )
            return {"id": payload["id"], "displayed": True}

    async def dismiss(self, notification_id):
        async with self.lock:
            self.state["pending"].pop(notification_id, None)
            await self.store.async_save(self.state)
            try:
                return await self.api.dismiss(notification_id)
            except QuietThingsError as err:
                raise HomeAssistantError(str(err)) from None

    async def poll(self, _now=None):
        if self.closed or self.lock.locked():
            return
        async with self.lock:
            had_pending = bool(self.state["pending"])
            self.prune()
            if not self.state["pending"]:
                if had_pending:
                    await self.store.async_save(self.state)
                return
            try:
                await self._poll()
            except (QuietThingsError, ValueError, TypeError):
                # TV can be asleep or access disabled. Actions expire rather than queue forever.
                return

    async def _poll(self):
        body = await self.api.events(self.state.get("sequence", 0))
        epoch, sequence, events = body.get("epoch"), body.get("sequence"), body.get("events")
        if not isinstance(epoch, str) or len(epoch) != 32 or type(sequence) is not int or sequence < 0 or not isinstance(events, list) or len(events) > 32:
            raise ValueError("Invalid notification event response")
        if epoch != self.state.get("epoch"):
            self.state.update(epoch=epoch, sequence=sequence)
            await self.store.async_save(self.state)
            return
        if sequence < self.state["sequence"]:
            raise ValueError("Notification cursor moved backwards")
        accepted = []
        previous = self.state["sequence"]
        now = datetime.now(UTC)
        for event in events:
            if not isinstance(event, dict):
                raise ValueError("Invalid notification event")
            number = event.get("sequence")
            if type(number) is not int or not previous < number <= sequence:
                raise ValueError("Invalid event sequence")
            previous = number
            pending = self.state["pending"].get(event.get("notification_id"))
            try:
                created = datetime.fromisoformat(event["created_at"])
                expires = datetime.fromisoformat(event["expires_at"])
                age = (now - created).total_seconds()
                fresh = 0 <= age <= 30 and now <= expires <= created + timedelta(seconds=31)
            except (KeyError, TypeError, ValueError):
                fresh = False
            if pending and fresh and event.get("action") in pending["actions"]:
                self.state["pending"].pop(event["notification_id"])
                accepted.append({
                    "entry_id": self.entry.entry_id,
                    "notification_id": event["notification_id"],
                    "action": event["action"],
                    "event_id": event.get("id"),
                })
        self.state["sequence"] = sequence
        # If persistence fails no HA action fires. A restart cannot replay a saved action.
        await self.store.async_save(self.state)
        for event in accepted if not self.closed else []:
            self.hass.bus.async_fire(EVENT_ACTION, event)


def async_register_services(hass: HomeAssistant):
    """One service pair shared by entries; require an explicit TV destination."""
    if hass.services.has_service(DOMAIN, "notify"):
        return

    async def handle(call: ServiceCall):
        entry = hass.config_entries.async_get_entry(call.data["entry_id"])
        runtime = getattr(entry, "runtime_data", None)
        notifications = getattr(runtime, "notifications", None)
        if entry is None or entry.domain != DOMAIN or notifications is None:
            raise HomeAssistantError("Select a loaded QuietThings entry with playback companion enabled")
        if call.service == "dismiss_notification":
            return await notifications.dismiss(call.data["id"])
        try:
            return await notifications.show(call.data)
        except (QuietThingsError, ValueError, TypeError) as err:
            raise HomeAssistantError(str(err)) from None

    hass.services.async_register(DOMAIN, "notify", handle, schema=NOTIFY_SCHEMA, supports_response=SupportsResponse.OPTIONAL)
    hass.services.async_register(DOMAIN, "dismiss_notification", handle, schema=DISMISS_SCHEMA, supports_response=SupportsResponse.OPTIONAL)
