"""Notification delivery, action provenance and crash/replay safeguards."""
from datetime import UTC, datetime, timedelta
from io import BytesIO
from types import SimpleNamespace
from unittest.mock import AsyncMock, MagicMock, patch
import asyncio
import base64

from PIL import Image
import pytest
import voluptuous as vol
from homeassistant.exceptions import HomeAssistantError

from custom_components.quietthings.api import QuietThingsUnavailable
from custom_components.quietthings.notifications import (
    EVENT_ACTION, NOTIFY_SCHEMA, TvNotifications, NotificationClient, prepare_image,
)
from custom_components.quietthings.api import QuietThingsApi, QuietThingsRejected

EPOCH = "a" * 32


@pytest.fixture
async def client(hass):
    api = SimpleNamespace(events=AsyncMock(), show=AsyncMock(return_value={"ok": True, "actions_available": True}), dismiss=AsyncMock(return_value={"ok": True}), capabilities=AsyncMock(return_value={"notification_media_version":2}), status=AsyncMock(return_value={"active":False}), image=AsyncMock(return_value={"ok":True,"updated":True}))
    entry = SimpleNamespace(entry_id="tv-one", async_on_unload=MagicMock())
    result = TvNotifications(hass, entry, api)
    result.store = SimpleNamespace(async_load=AsyncMock(return_value=None), async_save=AsyncMock())
    api.events.return_value = {"ok": True, "epoch": EPOCH, "sequence": 0, "events": []}
    yield result
    result.close()
    await asyncio.sleep(0)


def payload(**extra):
    return NOTIFY_SCHEMA({"entry_id": "tv-one", "id": "notice-one", "message": "Visitor", "actions": [{"action": "TEST", "title": "Test"}], **extra})


def event(sequence=1, **extra):
    now = datetime.now(UTC)
    return {"sequence": sequence, "id": "event-one", "notification_id": "notice-one", "action": "TEST", "created_at": now.isoformat(), "expires_at": (now+timedelta(seconds=30)).isoformat(), **extra}


async def deliver(client, *events, sequence=1, epoch=EPOCH):
    client.api.events.return_value = {"ok": True, "epoch": epoch, "sequence": sequence, "events": list(events)}
    await client.poll()


async def test_action_persisted_before_event_and_not_replayed(client, hass):
    await client.show(payload())
    received = []
    hass.bus.async_listen(EVENT_ACTION, lambda e: received.append(e.data))
    saved = []
    async def save(state):
        saved.append((state["sequence"], dict(state["pending"])))
    client.store.async_save.side_effect = save
    await deliver(client, event())
    await hass.async_block_till_done()
    assert saved == [(1, {})]
    assert received[0]["action"] == "TEST"
    assert received[0]["entry_id"] == "tv-one"
    await client.poll()
    assert len(received) == 1


@pytest.mark.parametrize("override", [
    {"action": "UNLOCK_OTHER"}, {"notification_id": "other"},
    {"created_at": (datetime.now(UTC)-timedelta(seconds=40)).isoformat()},
    {"expires_at": (datetime.now(UTC)-timedelta(seconds=1)).isoformat()},
    {"created_at": "invalid"}, {"created_at": "2026-11-11T09:00:00"},
])
async def test_unmatched_or_stale_actions_ignored(client, hass, override):
    await client.show(payload())
    with patch.object(type(hass.bus), "async_fire") as fire:
        await deliver(client, event(**override))
    fire.assert_not_called()


async def test_restart_restores_cursor_and_pending(client, hass):
    await client.show(payload())
    saved = {**client.state, "pending": dict(client.state["pending"])}
    fresh = TvNotifications(hass, client.entry, client.api)
    fresh.store = SimpleNamespace(async_load=AsyncMock(return_value=saved), async_save=AsyncMock())
    await fresh.start()
    fresh.entry.async_on_unload.call_args.args[0]()
    with patch.object(type(hass.bus), "async_fire") as fire:
        await deliver(fresh, event())
        assert fire.call_count == 1
    assert fresh.state["sequence"] == 1


async def test_changed_tv_epoch_skips_old_actions(client, hass):
    await client.show(payload())
    with patch.object(type(hass.bus), "async_fire") as fire:
        await deliver(client, event(), epoch="b"*32)
    fire.assert_not_called()
    assert client.state["sequence"] == 1


async def test_failed_persistence_never_fires_action(client, hass):
    await client.show(payload())
    client.store.async_save.side_effect = OSError("disk failure")
    with patch.object(type(hass.bus), "async_fire") as fire:
        with pytest.raises(OSError):
            await deliver(client, event())
    fire.assert_not_called()


async def test_transport_failure_and_expiry(client, hass):
    await client.show(payload())
    client.api.events.side_effect = QuietThingsUnavailable("asleep")
    await client.poll()
    assert client.state["pending"]
    client.state["pending"]["notice-one"]["expires"] = 0
    await client.poll()
    assert not client.state["pending"]


async def test_send_rejection_clears_pending(client):
    client.api.show.side_effect = QuietThingsUnavailable("asleep")
    with pytest.raises(HomeAssistantError):
        await client.show(payload())
    assert client.state["pending"] == {}


async def test_dismiss_cancels_actions(client):
    await client.show(payload())
    await client.dismiss("notice-one")
    assert client.state["pending"] == {}
    client.api.dismiss.assert_awaited_once_with("notice-one")


async def test_unavailable_action_keys_is_error(client):
    client.api.show.return_value = {"ok": True, "actions_available": False}
    with pytest.raises(HomeAssistantError):
        await client.show(payload())
    assert not client.state["pending"]


async def test_camera_prepared_without_credentials_in_payload(client, hass):
    image = Image.new("RGB", (1920, 1080), "blue")
    data = BytesIO(); image.save(data, format="PNG")
    with patch("homeassistant.components.camera.async_get_image", AsyncMock(return_value=SimpleNamespace(content=data.getvalue()))) as camera:
        await client.show(payload(camera_entity="camera.door"))
    camera.assert_awaited_once()
    sent = client.api.show.call_args.args[0]
    assert "camera_entity" not in sent and "entry_id" not in sent
    assert sent["image_base64"]


@pytest.mark.parametrize("extra", [{"actions": [{"action":"X", "title":"t"}]*4}, {"id":"bad id"}, {"duration_seconds":121}, {"camera_entity":"lock.door"}, {"image_url":"file:///etc/passwd"}])
def test_service_rejects_bad_input(extra):
    with pytest.raises(vol.Invalid):
        payload(**extra)


def test_oversized_or_invalid_image_rejected():
    for data in (b"bad", b"0"*(8*1024*1024+1)):
        with pytest.raises(HomeAssistantError):
            prepare_image(data)


@pytest.mark.parametrize("format", ["JPEG","PNG","BMP","WEBP","TIFF","ICO"])
def test_common_image_formats_converted(format):
    output=BytesIO();Image.new("RGB",(128,128),"green").save(output,format=format)
    encoded=prepare_image(output.getvalue())
    with Image.open(BytesIO(base64.b64decode(encoded))) as image:
        assert image.format=="JPEG" and image.width<=960


def test_animation_preserved_and_resized():
    output=BytesIO();frames=[Image.new("RGB",(640,360),c) for c in ["red","green","blue"]]
    frames[0].save(output,format="GIF",save_all=True,append_images=frames[1:],duration=100,loop=0)
    with Image.open(BytesIO(base64.b64decode(prepare_image(output.getvalue())))) as image:
        assert image.format=="GIF" and image.n_frames==3 and image.width<=520 and image.height<=285


async def test_url_image_fetched_in_ha(client,hass,aioclient_mock):
    output=BytesIO();Image.new("RGB",(320,180),"red").save(output,format="PNG")
    aioclient_mock.get("https://example.com/snapshot.png",content=output.getvalue())
    await client.show(payload(image_url="https://example.com/snapshot.png"))
    sent=client.api.show.call_args.args[0]
    assert "image_url" not in sent and sent["image_base64"]
    assert "Authorization" not in (aioclient_mock.mock_calls[0][3] or {})


async def test_local_www_image_and_escape(client,hass,tmp_path):
    hass.config.config_dir=str(tmp_path)
    www=tmp_path/"www";www.mkdir();output=BytesIO();Image.new("RGB",(40,40),"blue").save(output,format="BMP")
    (www/"test.bmp").write_bytes(output.getvalue())
    await client.show(payload(image_url="/local/test.bmp?cache=1"))
    with pytest.raises(HomeAssistantError):
        await client.show(payload(id="outside",image_url="/local/%2e%2e/private.bmp"))


async def test_image_redirect_scheme_rejected(client,aioclient_mock):
    aioclient_mock.get("https://example.com/image",status=302,headers={"Location":"file:///etc/passwd"})
    with pytest.raises(HomeAssistantError):
        await client.show(payload(image_url="https://example.com/image"))
    client.api.show.assert_not_called()


async def test_camera_and_url_mutually_exclusive(client):
    with pytest.raises(HomeAssistantError):
        await client.show(payload(camera_entity="camera.front",image_url="https://example.com/a.png"))


async def test_live_preview_stops_on_replacement(client):
    client.api.status.side_effect=[{"active":True,"id":"live"},{"active":True,"id":"live"},{"active":True,"id":"replacement"}]
    client.camera_frame=AsyncMock(return_value="encoded-frame")
    with patch("custom_components.quietthings.notifications.asyncio.sleep",AsyncMock()):
        await client.live_preview("live","camera.front",30,1)
    assert client.camera_frame.await_count==2
    assert [c.args[1] for c in client.api.image.call_args_list]==[1,2]


async def test_live_preview_stops_on_dismiss_and_deadline(client):
    client.api.status.return_value={"active":False}
    client.camera_frame=AsyncMock()
    with patch("custom_components.quietthings.notifications.asyncio.sleep",AsyncMock()):
        await client.live_preview("live","camera.front",30,1)
    client.camera_frame.assert_not_called()
    client.api.status.reset_mock()
    await client.live_preview("live","camera.front",0,1)
    client.api.status.assert_not_called()


async def test_snapshot_mode_does_not_start_live_task(client):
    client.camera_frame=AsyncMock(return_value="encoded")
    await client.show(payload(camera_entity="camera.front",camera_mode="snapshot"))
    assert client.preview_task is None
    assert "image_live" not in client.api.show.call_args.args[0]


async def test_confirmed_rate_limit_retried_but_uncertain_post_not_repeated():
    client=NotificationClient(None,"192.0.2.1",18766,"test-token")
    with patch.object(QuietThingsApi,"_request",AsyncMock(side_effect=[QuietThingsRejected("rate limited",429),{"ok":True}])) as request, patch("custom_components.quietthings.notifications.asyncio.sleep",AsyncMock()) as sleep:
        assert (await client.show({"title":"Test"}))["ok"]
        assert request.await_count==2 and sleep.await_count==1
    with patch.object(QuietThingsApi,"_request",AsyncMock(side_effect=QuietThingsUnavailable("timeout"))) as request:
        with pytest.raises(QuietThingsUnavailable):
            await client.show({"title":"Test"})
        assert request.await_count==1
