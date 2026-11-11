// SPDX-License-Identifier: MIT
// Served inside the app-owned document. This client never receives a bridge key.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory;
  else root.startQuietThingsCobaltObserver = factory;
}(typeof globalThis !== 'undefined' ? globalThis : this, function (env, options) {
  'use strict';
  var mediaGeneration = 0;
  var context = { document: env.document, navigator: env.navigator, metadata: function () {
    var m;
    try { m = env.navigator && env.navigator.mediaSession && env.navigator.mediaSession.metadata; } catch (_) {}
    return { title: m && m.title || env.document.title, key: String(mediaGeneration) };
  } };
  env.document.addEventListener('loadstart', function () { mediaGeneration++; }, true);
  var probe = env.createQuietThingsMediaProbe(context), sequence = 0, timer, pending = false;
  var stopped = false, acknowledgement = null;
  function schedule(delay) { if (!stopped) { env.clearTimeout(timer); timer = env.setTimeout(report, delay); } }
  function report() {
    if (stopped || pending) return;
    pending = true;
    var x = new env.XMLHttpRequest(), snapshot;
    try {
      snapshot = probe.read();
      // A complete, readable Cobalt document with no media elements is a
      // confirmed empty menu. Paused empty placeholders have no loaded media;
      // an unreadable, failed or actively loading player stays unknown.
      if (snapshot.state === 'unknown' && env.document.readyState === 'complete') {
        var elements = env.document.querySelectorAll('video, audio'), empty = true;
        for (var i = 0; i < elements.length; i++) {
          var element = elements[i];
          if (element.error || !(element.ended || element.paused === true && element.readyState === 0 ||
              element.networkState === 0 && !element.currentSrc && !element.src && !element.srcObject)) empty = false;
        }
        if (empty) snapshot.state = 'idle';
      }
    } catch (_) { pending = false; schedule(1000); return; }
    var ack = acknowledgement; acknowledgement = null;
    function done() { pending = false; schedule(750); }
    x.open('POST', options.report, true); x.timeout = 2500;
    x.setRequestHeader('Content-Type', 'application/json');
    x.onerror = x.ontimeout = done;
    x.onload = function () {
      var command;
      try { if (x.status === 200) command = JSON.parse(x.responseText).command; } catch (_) {}
      if (!command) { done(); return; }
      // The root lease and page instance must both still belong to this document.
      if (typeof command.id !== 'string' || command.id.length !== 32 ||
          command.document !== options.document || command.expires < Date.now() ||
          ['play', 'pause', 'seek'].indexOf(command.action) < 0) { done(); return; }
      var settled = false, commandLimit;
      function finish(ok) { if (settled) return; settled = true; env.clearTimeout(commandLimit); acknowledgement = { id: command.id, ok: ok }; pending = false; schedule(50); }
      try {
        var result = probe.command(command.action, command.position, command.instance);
        if (result && typeof result.then === 'function') {
          commandLimit = env.setTimeout(function () { finish(false); }, 1500);
          result.then(function () { finish(true); }, function () { finish(false); });
        }
        else finish(true);
      } catch (_) { finish(false); }
    };
    x.send(JSON.stringify({ document: options.document, sequence: ++sequence, page_ready: env.document.readyState === 'complete', probe: snapshot, acknowledgement: ack }));
  }
  function changed() { if (!pending) schedule(20); }
  ['play', 'pause', 'seeked', 'ended', 'loadedmetadata', 'emptied'].forEach(function (name) {
    env.document.addEventListener(name, changed, true);
  });
  env.addEventListener('pagehide', function () { stopped = true; env.clearTimeout(timer); });
  report();
  return { sample: changed };
}));
