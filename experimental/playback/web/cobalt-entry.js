// SPDX-License-Identifier: MIT
'use strict';
var path = require('path'), fs = require('fs');
var counts = {}, lastDiagnostic = 0;
function diagnostic(name, code) {
  if (!fs.existsSync(path.join(__dirname, 'quiet-cobalt-diagnostic.enabled'))) return;
  var key = name + (typeof code === 'number' ? '_' + code : '');
  counts[key] = Math.min(1000000, (counts[key] || 0) + 1);
  if (Date.now() - lastDiagnostic < 2000) return;
  lastDiagnostic = Date.now();
  fs.writeFileSync('/opt/usr/home/owner/apps_rw/xvvl3S1TT1/data/quiet-cobalt-diagnostic.json', JSON.stringify(counts), { mode: 384 });
}
try {
  require('./cobalt-proxy').install({ keyPath: path.join(__dirname, 'quiet-cobalt-key'),
    diagnostic: diagnostic, probe: fs.readFileSync(path.join(__dirname, 'quiet-cobalt-media-probe.js'), 'utf8'),
    client: fs.readFileSync(path.join(__dirname, 'quiet-cobalt-client.js'), 'utf8') });
  require('./cobalt-youtube-proxy').start({ caCertificate: '/home/owner/share/tizentube-ca.crt', caKey: '/home/owner/share/tizentube-ca.key' });
} catch (_) { /* Fail closed for observation while preserving the original service. */ }
