// SPDX-License-Identifier: MIT
const path = require('node:path');
const fs = require('node:fs');
const destination = path.resolve(process.argv[2] || path.join(__dirname, '../../../.local/cobalt-bundle'));
fs.mkdirSync(destination, { recursive: true });
require('esbuild').buildSync({ entryPoints: [path.join(__dirname, 'cobalt-entry.js')], bundle: true,
  platform: 'node', target: 'node8', outfile: path.join(destination, 'quiet-cobalt.js'), legalComments: 'eof' });
for (const [source, target] of [['media-probe.js', 'quiet-cobalt-media-probe.js'], ['cobalt-client.js', 'quiet-cobalt-client.js'], ['node_modules/node-forge/LICENSE', 'quiet-cobalt-forge-LICENSE']]) {
  fs.copyFileSync(path.join(__dirname, source), path.join(destination, target));
}
console.log('Built optional Cobalt observer and third-party license files');
