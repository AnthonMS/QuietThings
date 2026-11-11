// SPDX-License-Identifier: MIT
const assert = require('node:assert/strict'), http = require('node:http'), fs = require('node:fs'), os = require('node:os'), path = require('node:path'), crypto = require('node:crypto');
const { install, digest, mac } = require('../web/cobalt-proxy.js');
(async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'cobalt-proxy-')), secret = crypto.randomBytes(32).toString('hex');
  fs.writeFileSync(path.join(directory, 'key'), secret);
  let report, forgeResponse = false;
  const bridge = http.createServer((req, res) => {
    let body = ''; req.on('data', x => body += x); req.on('end', () => {
      report = JSON.parse(body); const auth = req.headers.authorization, parts = auth.slice(17).split(':');
      assert.equal(parts[2], mac(secret, `qt-web-request-v1\n${parts[0]}:${parts[1]}\nPOST\n/v1/cobalt/report\n${digest(body)}`));
      const reply = JSON.stringify({ ok: true, command: null });
      res.writeHead(200, { 'X-QuietThings-Response': forgeResponse ? '0'.repeat(64) : mac(secret, `qt-web-response-v1\n${auth}\n200\n${digest(reply)}`) }); res.end(reply);
    });
  });
  await new Promise(r => bridge.listen(0, '127.0.0.1', r));
  const addon = install({ keyPath: path.join(directory, 'key'), probe: '/* probe */', client: '/* client */', bridgePort: bridge.address().port });
  let upstream = 0;
  const wrapped = addon.wrap((req, res) => { upstream++; res.writeHead(200, { 'content-type': 'text/html', 'content-length': 26 }); res.end('<html><body></body></html>'); });
  const server = addon.originalCreate((req, res) => {
    Object.defineProperty(req.socket, 'encrypted', { value: true });
    Object.defineProperty(req.socket, 'localPort', { value: 8101, configurable: true });
    wrapped(req, res);
  });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  async function request(url, body) {
    return new Promise((resolve, reject) => {
      const req = http.request({ host: '127.0.0.1', port: server.address().port, path: url, method: body ? 'POST' : 'GET', headers: body ? { 'content-type': 'application/json' } : {} }, res => {
        let text = ''; res.on('data', b => text += b); res.on('end', () => resolve({ status: res.statusCode, text }));
      }); req.on('error', reject); req.end(body && JSON.stringify(body));
    });
  }
  try {
    const page = await request('/tv?test'); assert.equal(page.status, 200);
    const doc = /__quietthings_cobalt\/([a-f0-9]{32})/.exec(page.text)[1];
    const client = await request(`/__quietthings_cobalt/${doc}/client.js`); assert.equal(client.status, 200); assert.ok(!client.text.includes(secret));
    const payload = { document: doc, sequence: 1, probe: { state: 'playing', backend: 'html5', instance: '1', commands: ['play', 'pause'], position_seconds: 12, duration_seconds: 120, live: false }, secret_media_url: 'must-not-forward' };
    const reply = await request(`/__quietthings_cobalt/${doc}/report`, payload); assert.equal(reply.status, 200); assert.equal(report.worker_pid, process.pid);
    assert.ok(!JSON.stringify(report).includes('must-not-forward')); assert.equal(report.peer.local_port, 8101);
    assert.equal((await request(`/__quietthings_cobalt/${doc}/report`, payload)).status, 400);
    forgeResponse = true; payload.sequence++;
    assert.equal((await request(`/__quietthings_cobalt/${doc}/report`, payload)).status, 503);
    assert.equal((await request(`/__quietthings_cobalt/${'f'.repeat(32)}/report`, payload)).status, 404); assert.equal(upstream, 1);
    console.log('Cobalt proxy HTTP framing, private schema, request/response HMAC and reserved-route tests passed');
  } finally { server.close(); bridge.close(); fs.rmSync(directory, { recursive: true }); }
})().catch(error => { console.error(error); process.exitCode = 1; });
