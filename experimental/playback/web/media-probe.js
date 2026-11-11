// SPDX-License-Identifier: MIT
// Read actual app-owned players. This module does not install hooks or send data.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory;
  else root.createQuietThingsMediaProbe = factory;
}(typeof globalThis !== 'undefined' ? globalThis : this, function (environment) {
  'use strict';
  var env = environment || {}, lastOwner, lastKey, lastSource, generation = 0;
  function number(value) {
    return typeof value === 'number' && isFinite(value) && value >= 0 ? value : null;
  }
  function text(value) { return typeof value === 'string' && value.trim() ? value.slice(0, 512) : null; }
  function metadata() {
    try {
      if (env.metadata) return env.metadata() || {};
      var m = env.navigator && env.navigator.mediaSession && env.navigator.mediaSession.metadata;
      return { title: m && m.title || env.document && env.document.title };
    } catch (_) { return {}; }
  }
  function unknown(state) {
    return { probe_version: 1, state: state || 'unknown', backend: null, instance: null,
      title: null, position_seconds: null, duration_seconds: null, live: null, commands: [] };
  }
  function select() {
    var av, state, videos = [], active = [], paused = [], i, v;
    try { av = env.avplay || env.webapis && env.webapis.avplay; state = av && av.getState(); }
    catch (_) { av = null; }
    if (av && (state === 'PLAYING' || state === 'PAUSED')) {
      return { owner: av, backend: 'avplay', state: state === 'PLAYING' ? 'playing' : 'paused' };
    }
    try { videos = env.document.querySelectorAll('video'); } catch (_) { /* No DOM. */ }
    for (i = 0; i < videos.length; i++) {
      v = videos[i];
      if (!v.ended && v.readyState >= 1 && (v.currentSrc || v.srcObject)) {
        (v.paused ? paused : active).push(v);
      }
    }
    // Multiple playing elements are ambiguous; never choose an arbitrary owner.
    if (active.length > 1 || !active.length && paused.length > 1) return null;
    v = active[0] || paused[0];
    if (v) return { owner: v, backend: 'html5', state: v.paused ? 'paused' : 'playing' };
    return !env.isMenu && av && (state === 'NONE' || state === 'IDLE' || state === 'READY') ? { idle: true } : null;
  }
  function read() {
    // App-specific menu evidence excludes incidental browse previews. Loading or
    // failed player views remain unknown even when an unused AVPlay reports NONE.
    if (env.isMenu && env.isMenu() === true) {
      lastOwner = null; lastKey = null; lastSource = null; return unknown('idle');
    }
    var selected = select(), m, owner, key, duration, position, live, commands;
    if (!selected) { lastOwner = null; lastKey = null; lastSource = null; return unknown(); }
    if (selected.idle) { lastOwner = null; lastKey = null; lastSource = null; return unknown('idle'); }
    owner = selected.owner; m = metadata();
    // Source URLs are used only in page memory to detect replacement, never returned.
    key = selected.backend === 'html5' ? String(owner.currentSrc || '') : '';
    key += '|' + String(m.key || m.title || '');
    if (lastOwner !== owner || lastKey !== key ||
        selected.backend === 'html5' && lastSource !== owner.srcObject) generation++;
    lastOwner = owner; lastKey = key;
    lastSource = selected.backend === 'html5' ? owner.srcObject : null;
    try {
      duration = number(selected.backend === 'html5' ? owner.duration : owner.getDuration() / 1000);
      position = number(selected.backend === 'html5' ? owner.currentTime : owner.getCurrentTime() / 1000);
    } catch (_) { duration = position = null; }
    if (duration === 0) duration = null;
    live = typeof m.live === 'boolean' ? m.live : selected.backend === 'html5' && owner.duration === Infinity ? true : null;
    if (live === true) duration = null;
    if (position !== null && duration !== null) position = Math.min(position, duration);
    commands = [];
    if (typeof owner.play === 'function') commands.push('play');
    if (typeof owner.pause === 'function') commands.push('pause');
    if (selected.backend === 'avplay' && typeof owner.stop === 'function') commands.push('stop');
    if (duration !== null && live !== true) {
      if (selected.backend === 'html5' && owner.seekable && owner.seekable.length > 0 ||
          selected.backend === 'avplay' && live === false && m.key && typeof owner.seekTo === 'function') commands.push('seek');
    }
    return { probe_version: 1, backend: selected.backend, instance: String(generation),
      state: selected.state, title: text(m.title), position_seconds: position,
      duration_seconds: duration, live: live, commands: commands };
  }
  function command(action, position, expectedInstance) {
    var snapshot = read(), selected = select();
    if (!selected || selected.idle || selected.owner !== lastOwner ||
        snapshot.instance !== expectedInstance ||
        snapshot.commands.indexOf(action) < 0) throw new Error('Playback owner or capability changed');
    var owner = selected.owner;
    if (action === 'play') return owner.play();
    if (action === 'pause') return owner.pause();
    if (action === 'stop') return owner.stop();
    if (action === 'seek') {
      if (number(position) === null || position > snapshot.duration_seconds) throw new Error('Invalid seek position');
      if (selected.backend === 'html5') {
        // A finite duration does not guarantee every position is currently seekable.
        var ranges = owner.seekable, allowed = false, i;
        for (i = 0; ranges && i < ranges.length; i++) {
          if (position >= ranges.start(i) && position <= ranges.end(i)) allowed = true;
        }
        if (!allowed) throw new Error('Position is outside seekable ranges');
        owner.currentTime = position;
      }
      else return owner.seekTo(position * 1000);
    }
  }
  return { read: read, command: command };
}));
