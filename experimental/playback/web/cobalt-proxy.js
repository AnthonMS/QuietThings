// SPDX-License-Identifier: MIT
// Optional app-local addon, loaded before the owner's existing TizenTube service.
// Neither the page nor the LAN receives the dedicated observer credential.
'use strict';
var http = require('http'), https = require('https'), net = require('net'), tls = require('tls');
var fs = require('fs'), crypto = require('crypto');
function digest(value) { return crypto.createHash('sha256').update(value).digest('hex'); }
function mac(key, value) { return crypto.createHmac('sha256', key).update(value).digest('hex'); }
function equal(a, b) { return typeof a === 'string' && /^[a-f0-9]{64}$/.test(a) && crypto.timingSafeEqual(Buffer.from(a), Buffer.from(b)); }
function loopback(value) { return /^(127\.|::ffff:127\.|::1$)/.test(value || ''); }
function install(options) {
  var documents = new Map(), originalCreate = http.createServer;
  function diagnostic(name, code) { if (typeof options.diagnostic === 'function') try { options.diagnostic(name, code); } catch (_) {} }
  var key = fs.readFileSync(options.keyPath, 'utf8').trim();
  if (!/^[a-f0-9]{64}$/.test(key)) throw new Error('Invalid observer credential');
  function forward(value, callback) {
    var body = JSON.stringify(value), path = '/v1/cobalt/report';
    var identity = String(Date.now()) + ':' + crypto.randomBytes(16).toString('hex');
    var proof = 'QuietThings-HMAC ' + identity + ':' + mac(key, 'qt-web-request-v1\n' + identity + '\nPOST\n' + path + '\n' + digest(body));
    var request = http.request({ hostname: '127.0.0.1', port: options.bridgePort || 18767, method: 'POST', path: path,
      agent: false, headers: { Authorization: proof, 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) } }, function (response) {
      var chunks = [], size = 0;
      response.on('data', function (chunk) { size += chunk.length; if (size > 8192) response.destroy(); else chunks.push(chunk); });
      response.on('error', function () { callback(null); });
      response.on('end', function () {
        var bytes = Buffer.concat(chunks), expected = mac(key, 'qt-web-response-v1\n' + proof + '\n' + response.statusCode + '\n' + digest(bytes));
        diagnostic('bridge_response', response.statusCode);
        if (!equal(response.headers['x-quietthings-response'], expected) || response.statusCode !== 200) { callback(null); return; }
        try { callback(JSON.parse(bytes.toString('utf8'))); } catch (_) { callback(null); }
      });
    });
    request.setTimeout(1800, function () { request.destroy(); });
    request.on('error', function () { diagnostic('bridge_error'); callback(null); }); request.end(body);
  }
  function mode(request) {
    if (!request.socket.encrypted || !loopback(request.socket.remoteAddress)) return null;
    return request.socket.localPort === 8101 ? 'tizentube' : request.socket.localPort === 8102 ? 'youtube' : null;
  }
  function wrap(listener) {
    return function (req, res) {
      var app = mode(req), found = /^\/__quietthings_cobalt\/([a-f0-9]{32})\/(client\.js|report)$/.exec(req.url || '');
      req.__quietCobaltMode = app;
      if (!found) return listener.apply(this, arguments);
      // Reserved paths must never fall through to the real YouTube server.
      var doc = documents.get(found[1]);
      res.setHeader('Cache-Control', 'no-store');
      if (!app || !doc || doc.mode !== app) { diagnostic('retired_route'); res.writeHead(404); res.end(); return; }
      if (found[2] === 'client.js' && req.method === 'GET') {
        diagnostic('client');
        var client = options.probe + '\n' + options.client + '\nstartQuietThingsCobaltObserver(this,' + JSON.stringify({ document: found[1], report: '/__quietthings_cobalt/' + found[1] + '/report' }) + ');';
        res.writeHead(200, { 'Content-Type': 'application/javascript; charset=utf-8' }); res.end(client); return;
      }
      if (found[2] !== 'report' || req.method !== 'POST' || req.headers['content-type'] !== 'application/json') { res.writeHead(400); res.end(); return; }
      var chunks = [], length = 0, finished = false;
      var limit = setTimeout(function () { req.destroy(); }, 3000);
      req.on('close', function () { clearTimeout(limit); });
      req.on('data', function (chunk) { length += chunk.length; if (length > 3072) req.destroy(); else chunks.push(chunk); });
      req.on('end', function () {
        clearTimeout(limit);
        var value;
        try {
          value = JSON.parse(Buffer.concat(chunks).toString('utf8'));
          if (value.document !== found[1] || !Number.isSafeInteger(value.sequence) || value.sequence < 1 || value.sequence <= doc.sequence || doc.pending) throw new Error();
          // Only the public probe schema crosses this boundary, never arbitrary page data.
          var p = value.probe, commands = p.commands;
          if (!Array.isArray(commands) || commands.length > 4) throw new Error();
          value = { document: value.document, sequence: value.sequence, page_ready: value.page_ready === true, probe: {
            probe_version: 1, backend: p.backend, instance: p.instance, state: p.state,
            title: typeof p.title === 'string' ? p.title.slice(0, 512) : null,
            position_seconds: p.position_seconds, duration_seconds: p.duration_seconds,
            live: p.live, commands: commands }, acknowledgement: value.acknowledgement,
            mode: app, worker_pid: process.pid,
            peer: { address: req.socket.remoteAddress, port: req.socket.remotePort, local_address: req.socket.localAddress, local_port: req.socket.localPort } };
          doc.sequence = value.sequence; doc.pending = true;
        } catch (_) { res.writeHead(400); res.end(); return; }
        diagnostic('report');
        // Keep this browser TCP connection open until root attests its socket owner.
        forward(value, function (reply) {
          if (finished) return; finished = true; doc.pending = false;
          res.writeHead(reply ? 200 : 503, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify(reply || { ok: false }));
        });
      });
    };
  }
  http.createServer = function () {
    var args = Array.prototype.slice.call(arguments), i = args.length - 1;
    if (typeof args[i] === 'function') args[i] = wrap(args[i]);
    return originalCreate.apply(this, args);
  };
  function decorate(response, headers) {
    var req = response.req, path = req && req.url || '';
    if (!req || !req.__quietCobaltMode || !/^\/tv(?:\?|$)/.test(path) || !/text\/html/i.test(headers['content-type'] || response.getHeader('content-type') || '')) return;
    var id = crypto.randomBytes(16).toString('hex');
    if (documents.size >= 16) documents.delete(documents.keys().next().value);
    documents.set(id, { mode: req.__quietCobaltMode, sequence: 0, pending: false });
    response.__quietCobaltDocument = id; diagnostic('page');
    delete headers['content-length']; response.removeHeader('Content-Length');
    // Official mode preserves the page policy and adds only our single script nonce.
    if (req.__quietCobaltMode === 'youtube') {
      ['content-security-policy', 'content-security-policy-report-only'].forEach(function (name) {
        if (headers[name]) headers[name] = String(headers[name]).split(';').map(function (rule) {
          return /^\s*script-src(?:-elem)?\s/.test(rule) ? rule + " 'nonce-" + id + "'" : rule;
        }).join(';');
      });
    }
  }
  var originalHead = http.ServerResponse.prototype.writeHead;
  http.ServerResponse.prototype.writeHead = function () {
    var args = Array.prototype.slice.call(arguments), i = typeof args[1] === 'object' ? 1 : 2;
    var headers = args[i] || {};
    if (!Array.isArray(headers)) decorate(this, headers);
    args[i] = headers; return originalHead.apply(this, args);
  };
  var originalEnd = http.ServerResponse.prototype.end;
  http.ServerResponse.prototype.end = function (data, encoding, callback) {
    if (this.__quietCobaltDocument && (typeof data === 'string' || Buffer.isBuffer(data))) {
      var text = Buffer.isBuffer(data) ? data.toString('utf8') : data;
      if (text.length <= 2 * 1024 * 1024 && text.indexOf('</body>') >= 0) {
        var id = this.__quietCobaltDocument;
        data = text.replace('</body>', '<script nonce="' + id + '" src="/__quietthings_cobalt/' + id + '/client.js"></script></body>'); encoding = 'utf8';
      }
    }
    return originalEnd.call(this, data, encoding, callback);
  };
  return { forward: forward, originalCreate: originalCreate, wrap: wrap };
}
module.exports = { install: install, digest: digest, mac: mac };
