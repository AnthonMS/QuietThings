"""Exercise the shipped example in isolated HA, never against a physical lock."""
from pathlib import Path

import pytest
import yaml
from homeassistant.core import SupportsResponse
from homeassistant.exceptions import HomeAssistantError
from homeassistant.setup import async_setup_component


@pytest.fixture
async def example(hass):
    config=yaml.safe_load(Path("examples/doorbell-tv.yaml").read_text())
    notices, unlocks = [], []
    fail_camera = {"enabled":False}
    async def notify(call):
        notices.append(dict(call.data))
        if fail_camera["enabled"] and "camera_entity" in call.data:
            raise HomeAssistantError("Test camera unavailable")
        return {"displayed":True,"id":call.data["id"]}
    async def unlock(call):
        unlocks.append(dict(call.data))
    hass.services.async_register("quietthings","notify",notify,supports_response=SupportsResponse.OPTIONAL)
    hass.services.async_register("lock","unlock",unlock)
    hass.states.async_set("media_player.quietthings_living_room","playing")
    hass.states.async_set("media_player.samsung_livingroom","on")
    hass.states.async_set("lock.front_door","locked")
    assert await async_setup_component(hass,"script",{"script":config["script"]})
    assert await async_setup_component(hass,"automation",{"automation":config["automation"]})
    await hass.async_start()
    await hass.async_block_till_done()
    return notices,unlocks,fail_camera


@pytest.mark.parametrize("press",[1004,1006,1001])
async def test_actual_doorbell_press_calls_tv_script(hass,example,press):
    notices,unlocks,_=example
    hass.bus.async_fire("deconz_event",{"id":"door_bell","event":press})
    await hass.async_block_till_done()
    assert len(notices)==1
    assert notices[0]["camera_entity"]=="camera.front_door"
    assert notices[0]["actions"]==[{"action":"UNLOCK_FRONT_DOOR","title":"Unlock Door"}]
    assert notices[0]["id"].startswith("doorbell-tv-")
    assert unlocks==[]


async def test_tv_off_does_not_send_or_wake(hass,example):
    notices,unlocks,_=example
    hass.states.async_set("media_player.quietthings_living_room","off")
    hass.states.async_set("media_player.samsung_livingroom","off")
    hass.bus.async_fire("deconz_event",{"id":"door_bell","event":1004})
    await hass.async_block_till_done()
    assert notices==unlocks==[]


async def test_unlocked_door_omits_unlock_button(hass,example):
    notices,unlocks,_=example
    hass.states.async_set("lock.front_door","unlocked")
    hass.bus.async_fire("deconz_event",{"id":"door_bell","event":1004})
    await hass.async_block_till_done()
    assert notices[0]["actions"]==[]
    assert unlocks==[]


async def test_triple_press_not_a_tv_doorbell(hass,example):
    notices,unlocks,_=example
    hass.bus.async_fire("deconz_event",{"id":"door_bell","event":1010})
    await hass.async_block_till_done()
    assert notices==unlocks==[]


async def test_camera_failure_falls_back_to_text(hass,example):
    notices,unlocks,fail=example;fail["enabled"]=True
    hass.bus.async_fire("deconz_event",{"id":"door_bell","event":1004})
    await hass.async_block_till_done()
    assert len(notices)==2 and "camera_entity" not in notices[1]
    assert notices[0]["id"]==notices[1]["id"]
    assert unlocks==[]


@pytest.mark.parametrize("override",[{}, {"entry_id":"wrong"}, {"action":"OTHER"}, {"notification_id":"other-notice"}])
async def test_callback_matches_entry_action_and_notification(hass,example,override):
    _,unlocks,_=example
    data={"entry_id":"QUIETTHINGS_ENTRY_ID","action":"UNLOCK_FRONT_DOOR","notification_id":"doorbell-tv-test","event_id":"test-event",**override}
    hass.bus.async_fire("quietthings_notification_action",data)
    await hass.async_block_till_done()
    assert len(unlocks)==(0 if override else 1)


async def test_callback_rechecks_lock_state(hass,example):
    _,unlocks,_=example
    hass.states.async_set("lock.front_door","unlocked")
    hass.bus.async_fire("quietthings_notification_action",{"entry_id":"QUIETTHINGS_ENTRY_ID","action":"UNLOCK_FRONT_DOOR","notification_id":"doorbell-tv-test"})
    await hass.async_block_till_done()
    assert unlocks==[]


async def person_detected(hass):
    hass.states.async_set("binary_sensor.detection_zone_person_occupancy", "off")
    await hass.async_block_till_done()
    hass.states.async_set("binary_sensor.detection_zone_person_occupancy", "on")
    await hass.async_block_till_done()


async def test_person_detection_uses_camera_without_light_or_daylight_gate(hass,example):
    notices,unlocks,_=example
    hass.states.async_set("sun.sun", "above_horizon", {"elevation":45})
    hass.states.async_set("light.front_door_spots", "on")
    hass.states.async_set("input_boolean.motion_front_door_enabled", "off")
    await person_detected(hass)
    assert len(notices)==1
    assert notices[0]["title"]=="Person at the front door"
    assert notices[0]["message"]=="A person was detected by the front door camera."
    assert notices[0]["id"].startswith("person-tv-")
    assert notices[0]["camera_entity"]=="camera.front_door"
    assert notices[0]["actions"]==[{"action":"UNLOCK_FRONT_DOOR","title":"Unlock Door"}]
    assert unlocks==[]


async def test_person_detection_tv_off_does_not_send_or_wake(hass,example):
    notices,unlocks,_=example
    hass.states.async_set("media_player.quietthings_living_room", "off")
    hass.states.async_set("media_player.samsung_livingroom", "off")
    await person_detected(hass)
    assert notices==unlocks==[]


async def test_person_detection_does_not_repeat_until_zone_clears(hass,example):
    notices,unlocks,_=example
    await person_detected(hass)
    hass.states.async_set("binary_sensor.detection_zone_person_occupancy", "on", {"count":2})
    await hass.async_block_till_done()
    assert len(notices)==1
    await person_detected(hass)
    assert len(notices)==2 and notices[0]["id"]!=notices[1]["id"]
    assert unlocks==[]


async def test_person_camera_failure_keeps_person_text(hass,example):
    notices,unlocks,fail=example
    fail["enabled"]=True
    await person_detected(hass)
    assert len(notices)==2
    assert "camera_entity" not in notices[1]
    assert notices[1]["title"]=="Person at the front door"
    assert "Camera preview is unavailable" in notices[1]["message"]
    assert notices[0]["id"]==notices[1]["id"]
    assert unlocks==[]


async def test_person_notice_callback_uses_existing_guarded_unlock(hass,example):
    _,unlocks,_=example
    hass.bus.async_fire("quietthings_notification_action", {
        "entry_id":"QUIETTHINGS_ENTRY_ID", "action":"UNLOCK_FRONT_DOOR",
        "notification_id":"person-tv-test", "event_id":"test-person-event",
    })
    await hass.async_block_till_done()
    assert len(unlocks)==1  # Only the isolated fixture's mocked service is called.


async def test_confirmed_native_off_wins_over_stale_samsung_on(hass,example):
    notices,unlocks,_=example
    hass.states.async_set("media_player.quietthings_living_room", "off")
    hass.states.async_set("media_player.samsung_livingroom", "on")
    await person_detected(hass)
    hass.bus.async_fire("deconz_event", {"id":"door_bell","event":1004})
    hass.bus.async_fire("quietthings_notification_action", {
        "entry_id":"QUIETTHINGS_ENTRY_ID", "action":"UNLOCK_FRONT_DOOR",
        "notification_id":"person-tv-test", "event_id":"test-stale-stock",
    })
    await hass.async_block_till_done()
    assert notices==unlocks==[]
