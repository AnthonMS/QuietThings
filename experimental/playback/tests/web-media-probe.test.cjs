// SPDX-License-Identifier: MIT
const assert = require('node:assert/strict');
const createProbe = require('../web/media-probe.js');

function video(changes = {}) {
  return { currentSrc: 'https://example.invalid/signed-media?private=never-export',
    readyState: 4, ended: false, paused: false, currentTime: 12, duration: 120,
    seekable: { length: 1, start: () => 0, end: () => 120 }, play() { this.paused = false; },
    pause() { this.paused = true; }, ...changes };
}
let v = video();
let probe = createProbe({ document: { querySelectorAll: () => [v] },
  metadata: () => ({ title: 'Example', key: 'media-1' }) });
let s = probe.read();
assert.equal(s.state, 'playing');
assert.equal(s.position_seconds, 12);
assert.deepEqual(s.commands, ['play', 'pause', 'seek']);
assert.ok(!JSON.stringify(s).includes('private'));
probe.command('pause', null, s.instance);
assert.equal(probe.read().state, 'paused');
probe.command('play', null, s.instance);
assert.equal(probe.read().state, 'playing');
probe.command('seek', 30, s.instance);
assert.equal(probe.read().position_seconds, 30);
assert.throws(() => probe.command('seek', 121, s.instance));
assert.throws(() => probe.command('seek', NaN, s.instance));
v.currentSrc = 'https://example.invalid/replaced';
assert.throws(() => probe.command('pause', null, s.instance));
assert.notEqual(probe.read().instance, s.instance);
v.seekable = { length: 2, start: i => i === 0 ? 0 : 60, end: i => i === 0 ? 20 : 120 };
s = probe.read();
assert.throws(() => probe.command('seek', 30, s.instance), /seekable/);
probe.command('seek', 80, s.instance);
assert.equal(v.currentTime, 80);
v.srcObject = {};
assert.throws(() => probe.command('pause', null, s.instance));
v.duration = Infinity;
assert.equal(probe.read().live, true);
assert.equal(probe.read().duration_seconds, null);
assert.ok(!probe.read().commands.includes('seek'));
v.ended = true;
assert.equal(probe.read().state, 'unknown');
probe = createProbe({ document: { querySelectorAll: () => [video(), video()] } });
assert.equal(probe.read().state, 'unknown');
probe = createProbe({ document: { querySelectorAll: () => [video({ readyState: 0 })] } });
assert.equal(probe.read().state, 'unknown');
let state = 'PLAYING', seekPosition;
const av = { getState: () => state, getDuration: () => 150000,
  getCurrentTime: () => 45000, play: () => { state = 'PLAYING'; },
  pause: () => { state = 'PAUSED'; }, stop: () => { state = 'IDLE'; },
  seekTo: n => { seekPosition = n; } };
probe = createProbe({ avplay: av, metadata: () => ({ title: 'Live channel', live: true }) });
s = probe.read();
assert.equal(s.state, 'playing');
assert.equal(s.position_seconds, 45);
assert.equal(s.duration_seconds, null);
assert.ok(!s.commands.includes('seek'));
probe.command('pause', null, s.instance);
assert.equal(probe.read().state, 'paused');
probe = createProbe({ avplay: av, metadata: () => ({ title: 'VOD', key: 'vod-1', live: false }) });
s = probe.read();
assert.ok(s.commands.includes('seek'));
probe.command('seek', 10, s.instance);
assert.equal(seekPosition, 10000);
probe.command('stop', null, s.instance);
assert.equal(probe.read().state, 'idle');
state = 'UNRECOGNIZED';
assert.equal(probe.read().state, 'unknown');
state = 'NONE';
probe = createProbe({ avplay: av, isMenu: () => false, document: { querySelectorAll: () => [] } });
assert.equal(probe.read().state, 'unknown', 'a failed/loading player is not a confirmed menu');
probe = createProbe({ avplay: av, isMenu: () => true, document: { querySelectorAll: () => [video()] } });
assert.equal(probe.read().state, 'idle');
assert.equal(probe.read().title, null);
console.log('Web media probe checks passed');
