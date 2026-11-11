// SPDX-License-Identifier: MIT
const assert = require('node:assert/strict');
const create = require('../web/twitch-context.js');
const probe = require('../web/media-probe.js');
function pair(login, title) {
  const parent = {}, second = { tagName: 'H3', parentElement: parent, textContent: title,
    getAttribute: () => title };
  return { tagName: 'H3', parentElement: parent, textContent: login,
    getAttribute: () => login, nextElementSibling: second };
}
let headings = [pair('ExampleChannel', 'Actual live title')];
const location = { pathname: '/examplechannel' };
const video = { readyState: 4, paused: false, ended: false, currentSrc: 'https://private.invalid/media',
  duration: Infinity, currentTime: 4, play() {}, pause() {} };
const document = { readyState: 'complete', querySelectorAll: selector => selector === 'h3[title]' ? headings : [video] };
const metadata = create({ document, location });
assert.equal(metadata().title, 'Actual live title');
let p = probe({ document, metadata, isMenu: metadata.isMenu });
const before = p.read();
headings = [pair('ExampleChannel', 'Edited title')];
assert.equal(p.read().title, 'Edited title');
assert.equal(p.read().instance, before.instance, 'title edits do not retire the actual player');
headings.push(pair('Recommendation', 'Unrelated title'));
assert.equal(metadata().title, 'Edited title');
location.pathname = '/another';
assert.equal(metadata().title, null, 'old DOM cannot label a different route');
headings = [pair('Another', 'New channel title')];
assert.equal(metadata().title, 'New channel title');
headings.push(pair('Another', 'Ambiguous second title'));
assert.equal(metadata().title, null);
location.pathname = '/';
assert.equal(metadata().title, null);
assert.equal(metadata.isMenu(), true);
assert.equal(p.read().state, 'idle', 'browse previews are not the primary player');
assert.equal(p.read().title, null);
assert.deepEqual(p.read().commands, []);
location.pathname = '/directory/game';
assert.equal(metadata.isMenu(), true);
location.pathname = '/another';
document.readyState = 'loading';
assert.equal(metadata().title, null);
assert.equal(metadata.isMenu(), false);
headings = [pair('Another', '<untrusted title>')];
document.readyState = 'complete';
assert.equal(metadata().title, '<untrusted title>', 'return text, never injected markup');
console.log('Twitch current-channel title and menu checks passed');
