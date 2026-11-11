// SPDX-License-Identifier: MIT
// Read the active channel's own hidden player headings, not recommendation cards.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory;
  else root.createQuietThingsTwitchContext = factory;
}(typeof globalThis !== 'undefined' ? globalThis : this, function (env) {
  'use strict';
  function clean(value) { return typeof value === 'string' ? value.trim().slice(0, 512) : ''; }
  function route() { return env.location && env.location.pathname || ''; }
  function channel() {
    var match = /^\/([A-Za-z0-9_]{1,25})\/?$/.exec(route());
    return match && !/^(following|directory|search|settings|login|activate)$/i.test(match[1]) ? match[1].toLowerCase() : null;
  }
  function metadata() {
    var login = channel(), title = null, matches = 0;
    if (login && env.document.readyState === 'complete') {
      // The live player contains a channel heading followed by its stream title.
      // Use semantic structure and exact channel identity, not generated CSS names.
      var headings = env.document.querySelectorAll('h3[title]');
      if (headings.length <= 64) for (var i = 0; i < headings.length; i++) {
        var name = headings[i], next = name.nextElementSibling;
        if (clean(name.textContent).toLowerCase() !== login ||
            clean(name.getAttribute('title')).toLowerCase() !== login ||
            !next || next.tagName !== 'H3' || next.parentElement !== name.parentElement) continue;
        var candidate = clean(next.getAttribute('title'));
        if (!candidate || candidate !== clean(next.textContent)) continue;
        matches++; title = candidate;
      }
    }
    // A title edit is metadata, not a replacement player/command lease.
    return { title: matches === 1 ? title : null, key: login ? 'twitch:' + login : '', live: true };
  }
  metadata.isMenu = function () {
    return env.document.readyState === 'complete' &&
      /^(\/$|\/(following|directory|search|settings)(\/|$))/.test(route());
  };
  return metadata;
}));
