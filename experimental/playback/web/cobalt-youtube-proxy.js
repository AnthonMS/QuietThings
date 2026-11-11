// SPDX-License-Identifier: MIT
// Ordinary YouTube uses a separate loopback proxy without TizenTube's UI/ad scripts.
'use strict';
var http = require('http'), https = require('https'), tls = require('tls'), net = require('net');
var fs = require('fs'), crypto = require('crypto');
function start(options) {
  var forge = require('node-forge');
  // Read the existing app-local trust pair on the TV. Never export or replace it.
  var ca = forge.pki.certificateFromPem(fs.readFileSync(options.caCertificate, 'utf8'));
  var caKey = forge.pki.privateKeyFromPem(fs.readFileSync(options.caKey, 'utf8'));
  var certificates = {};
  ['www.youtube.com', 'youtube.com'].forEach(function (host) {
    var keys = forge.pki.rsa.generateKeyPair(2048), cert = forge.pki.createCertificate();
    cert.publicKey = keys.publicKey; cert.serialNumber = '01' + crypto.randomBytes(15).toString('hex');
    cert.validity.notBefore = new Date(Date.now() - 60000); cert.validity.notAfter = new Date(Date.now() + 30 * 86400000);
    cert.setSubject([{ name: 'commonName', value: host }]); cert.setIssuer(ca.subject.attributes);
    cert.setExtensions([{ name: 'basicConstraints', cA: false, critical: true },
      { name: 'keyUsage', digitalSignature: true, keyEncipherment: true, critical: true },
      { name: 'extKeyUsage', serverAuth: true }, { name: 'subjectAltName', altNames: [{ type: 2, value: host }] }]);
    cert.sign(caKey, forge.md.sha256.create());
    certificates[host] = { key: forge.pki.privateKeyToPem(keys.privateKey), cert: forge.pki.certificateToPem(cert) + forge.pki.certificateToPem(ca) };
  });
  var server = http.createServer(function (req, res) { res.writeHead(405); res.end(); });
  server.on('connect', function (req, socket, initial) {
    var match = /^([a-zA-Z0-9.-]+):443$/.exec(req.url || '');
    if (!match) { socket.destroy(); return; }
    var host = match[1].toLowerCase();
    if (!certificates[host]) {
      // Unmodified end-to-end TLS for the rest of the app's destinations.
      if (net.isIP(host) || host.indexOf('.') < 0) { socket.destroy(); return; }
      var target = net.connect(443, host, function () {
        socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        if (initial.length) target.write(initial); socket.pipe(target); target.pipe(socket);
      });
      target.on('error', function () { socket.destroy(); }); socket.on('error', function () { target.destroy(); }); return;
    }
    socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    var secure = new tls.Server(certificates[host]);
    secure.on('tlsClientError', function () { socket.destroy(); });
    secure.on('secureConnection', function (client) {
      var inner = http.createServer(function (request, response) {
        var isPage = /^\/tv(?:\?|$)/.test(request.url), headers = Object.assign({}, request.headers);
        headers.host = host; delete headers['proxy-authorization']; delete headers['proxy-connection'];
        if (isPage) headers['accept-encoding'] = 'identity';
        var outgoing = https.request({ hostname: host, port: 443, servername: host, method: request.method, path: request.url,
          headers: headers, rejectUnauthorized: true, maxHeaderSize: 1024 * 1024 }, function (incoming) {
          var replyHeaders = Object.assign({}, incoming.headers);
          if (!isPage || !/text\/html/i.test(replyHeaders['content-type'] || '') || !/^(identity)?$/.test(replyHeaders['content-encoding'] || '')) {
            response.writeHead(incoming.statusCode, replyHeaders); incoming.pipe(response); return;
          }
          var chunks = [], length = 0;
          incoming.on('data', function (chunk) { length += chunk.length; if (length > 2 * 1024 * 1024) { incoming.destroy(); response.destroy(); } else chunks.push(chunk); });
          incoming.on('end', function () {
            delete replyHeaders['content-length']; delete replyHeaders['transfer-encoding'];
            replyHeaders['cache-control'] = 'no-store'; replyHeaders.connection = 'close';
            response.writeHead(incoming.statusCode, replyHeaders); response.end(Buffer.concat(chunks));
          });
          incoming.on('error', function () { response.destroy(); });
        });
        outgoing.setTimeout(15000, function () { outgoing.destroy(); });
        outgoing.on('error', function () { response.destroy(); }); request.pipe(outgoing);
      });
      inner.emit('connection', client);
    });
    if (initial.length) socket.unshift(initial); secure.emit('connection', socket);
  });
  server.on('error', function () { /* Existing service remains available if optional port is unavailable. */ });
  // Match the existing Cobalt/TizenTube loopback route. This TV's SMACK
  // networking policy rejects the cross-package flow through 127.0.0.1.
  server.listen(options.port || 8102, '127.0.0.2'); return server;
}
module.exports = { start: start };
