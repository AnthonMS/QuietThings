// SPDX-License-Identifier: MIT
const assert = require('node:assert/strict');
const start = require('../web/cobalt-client.js');
const create = require('../web/media-probe.js');
let requests = [], timers = [], events = {}, pending, executed = 0, resolvePlay;
const video = { currentSrc: 'blob:private-source', readyState: 4, ended: false, paused: false, currentTime: 10, duration: 100,
  seekable: { length: 1, start: () => 0, end: () => 100 },
  pause() { executed++; this.paused = true; }, play() { executed++; return new Promise(r => { resolvePlay = () => { this.paused = false; r(); }; }); } };
function XHR() { pending = this; this.status = 200; this.open = () => {}; this.setRequestHeader = () => {}; this.send = text => requests.push(JSON.parse(text)); }
const env = { createQuietThingsMediaProbe: create, navigator: {}, XMLHttpRequest: XHR,
  document: { title: 'Example', querySelectorAll: () => [video], addEventListener: (name, fn) => events[name] = fn },
  addEventListener: (name, fn) => events[name] = fn, setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout: () => {} };
const doc = 'a'.repeat(32);
start(env, { document: doc, report: '/local/report' });
assert.equal(requests[0].probe.state, 'playing');
assert.ok(!JSON.stringify(requests).includes('private-source'));
const command = { id: '1'.repeat(32), document: doc, instance: requests[0].probe.instance, expires: Date.now() + 2000, action: 'pause' };
pending.responseText = JSON.stringify({ command }); pending.onload(); timers.shift()();
assert.equal(executed, 1); assert.equal(requests[1].probe.state, 'paused'); assert.deepEqual(requests[1].acknowledgement, { id: command.id, ok: true });
pending.responseText = JSON.stringify({ command: { ...command, action: 'play', id: '2'.repeat(32) } }); pending.onload();
assert.equal(timers.length, 1); timers.shift(); assert.equal(requests.length, 2); // Only the promise deadline, no new report yet.
resolvePlay();
setImmediate(() => {
  timers.shift()(); assert.equal(requests[2].probe.state, 'playing');
  events.loadstart(); // Same URL reopened still replaces the player instance.
  pending.responseText = JSON.stringify({ command }); pending.onload(); timers.shift()();
  assert.equal(requests[3].acknowledgement.ok, false); assert.equal(executed, 2);
  pending.responseText = JSON.stringify({ command: { ...command, document: 'b'.repeat(32) } }); pending.onload(); timers.shift()();
  assert.equal(executed, 2); assert.equal(requests[4].acknowledgement, null);
  pending.responseText = JSON.stringify({ command: { ...command, expires: Date.now() - 100 } }); pending.onload(); timers.shift()(); assert.equal(executed, 2);
  console.log('Cobalt client actual-state, promise, source generation and stale-command tests passed');
});

// Only observed empty/ended complete documents become idle.
function menuSample(ready, videos, media) {
  let value;
  const document = { readyState: ready, title: 'Menu', addEventListener() {},
    querySelectorAll: selector => selector === 'video' ? videos : media };
  function Request() { this.open = this.setRequestHeader = function() {}; this.send = body => { value = JSON.parse(body).probe; }; }
  start({ document, navigator: {}, createQuietThingsMediaProbe: create, XMLHttpRequest: Request,
    addEventListener() {}, setTimeout() {}, clearTimeout() {} }, {document: doc, report: '/local/report'});
  return value;
}
assert.equal(menuSample('complete', [], []).state, 'idle');
assert.equal(menuSample('loading', [], []).state, 'unknown');
assert.equal(menuSample('complete', [], [{ended:true}]).state, 'idle');
assert.equal(menuSample('complete', [], [{networkState:0}]).state, 'idle');
assert.equal(menuSample('complete', [], [{networkState:2, src:'loading'}]).state, 'unknown');
assert.equal(menuSample('complete', [video, {...video}], [video, {...video}]).state, 'unknown');

assert.equal(menuSample('complete', [], [{paused:true, readyState:0, src:'blob:empty-placeholder', networkState:2}]).state, 'idle');
assert.equal(menuSample('complete', [], [{paused:false, readyState:0, src:'blob:loading', networkState:2}]).state, 'unknown');
assert.equal(menuSample('complete', [], [{paused:true, readyState:0, error:{code:4}}]).state, 'unknown');
