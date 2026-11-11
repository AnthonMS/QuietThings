// SPDX-License-Identifier: MIT
// Observe AVPlay ownership and cache current-channel metadata from the app's API.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory;
  else root.createQuietThingsKickContext = factory;
}(typeof globalThis !== 'undefined' ? globalThis : this, function (env) {
  'use strict';
  var av = env.webapis && env.webapis.avplay, original = av && av.open;
  if (typeof original !== 'function') throw new Error('AVPlay open unavailable');
  var generation = 0, source = null, cache = null, pending = null, retryAt = 0;
  var clock = env.now || Date.now;
  function channel() {
    var element = env.document.getElementById('nowPlaying');
    var match = /^kick\.com\/([A-Za-z0-9_-]+)(?:\s|$)/.exec(element && element.textContent || '');
    return match ? match[1].toLowerCase() : null;
  }
  function ready() {
    var view = env.document.getElementById('playerView');
    return !!view && !!view.classList && view.classList.contains('player-ready') &&
      !view.classList.contains('hidden') && !view.classList.contains('offline-state') && !view.classList.contains('player-error');
  }
  function refresh(name) {
    var now = clock(), current = generation;
    if (typeof env.XMLHttpRequest !== 'function' || pending || now < retryAt ||
        cache && cache.name === name && cache.generation === current && now - cache.at < 30000) return;
    var xhr = new env.XMLHttpRequest(); pending = xhr; retryAt = now + 5000;
    function finish() { if (pending === xhr) pending = null; }
    xhr.onload = function () {
      finish();
      if (current !== generation || channel() !== name || !ready() || xhr.status < 200 || xhr.status >= 300) return;
      try {
        if (xhr.responseText.length > 262144) return;
        var data = JSON.parse(xhr.responseText), stream = data.livestream;
        if (typeof data.slug !== 'string' || data.slug.toLowerCase() !== name) return;
        var title = stream && stream.is_live !== false && typeof stream.session_title === 'string' ? stream.session_title.trim().slice(0, 512) : null;
        cache = { name: name, generation: current, at: clock(), title: title || null };
      } catch (_) { /* A failed lookup must not affect native playback. */ }
    };
    xhr.onerror = xhr.ontimeout = xhr.onabort = finish;
    xhr.onprogress = function (e) { if (e.loaded > 262144) xhr.abort(); };
    try {
      xhr.open('GET', 'https://kick.com/api/v2/channels/' + encodeURIComponent(name), true);
      xhr.timeout = 5000; xhr.setRequestHeader('Accept', 'application/json'); xhr.send();
    } catch (_) { finish(); }
  }
  function open() {
    var result = original.apply(this, arguments);
    generation++;
    source = typeof arguments[0] === 'string' ? arguments[0] : null;
    return result;
  }
  av.open = open;
  if (av.open !== open) throw new Error('AVPlay open cannot be observed');
  function metadata() {
    var name = channel(), title = null;
    if (cache && (cache.name !== name || cache.generation !== generation)) cache = null;
    if (name && ready()) {
      refresh(name);
      if (cache && clock() - cache.at <= 90000) title = cache.title;
    } else cache = null;
    // A signed source URL is used only as an in-page generation key.
    return { title: title, live: true, key: (name || '') + '|' + generation + '|' + (source || 'initial') };
  }
  metadata.isMenu = function () {
    var home = env.document.getElementById('home'), view = env.document.getElementById('playerView');
    return env.document.readyState === 'complete' && !!home && !!view &&
      !home.classList.contains('hidden') && view.classList.contains('hidden');
  };
  return metadata;
}));
