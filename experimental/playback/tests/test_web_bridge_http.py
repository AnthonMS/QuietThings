"""Run the actual loopback bridge server, without a TV or privileged helper."""
import json
import hashlib
import hmac
import time
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
DLL=ROOT.parent.parent/'.local/twitch-bridge-build/QuietThingsWebBridge.dll'

@unittest.skipUnless(DLL.is_file(), 'build bridge into .local/twitch-bridge-build first')
class BridgeHttp(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory=tempfile.TemporaryDirectory()
        base=Path(cls.directory.name)
        cls.token=secrets.token_hex(16)
        cls.observer_token=secrets.token_hex(32)
        secret=base/'token';secret.write_text(cls.token)
        (base/'cobalt-key').write_text(cls.observer_token)
        env=dict(os.environ,DOTNET_ROLL_FORWARD='Major')
        cls.process=subprocess.Popen(['dotnet',str(DLL),str(secret),str(base/'no-helper'),str(base/'no-state'),'0'],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        line=cls.process.stdout.readline().strip()
        if not line.isdigit():raise RuntimeError('bridge did not bind: '+line)
        cls.port=int(line)
    @classmethod
    def tearDownClass(cls):
        cls.process.terminate()
        try:cls.process.communicate(timeout=5)
        except subprocess.TimeoutExpired:cls.process.kill();cls.process.communicate()
        cls.directory.cleanup()
    def proof(self,method,path,body,key):
        identity=str(int(time.time()*1000))+':'+secrets.token_hex(16)
        message='qt-web-request-v1\n'+identity+'\n'+method+'\n'+path+'\n'+hashlib.sha256(body).hexdigest()
        return 'QuietThings-HMAC '+identity+':'+hmac.new(key.encode(),message.encode(),hashlib.sha256).hexdigest()
    def request(self,path='/v1/playback',auth=None,extra='',method='GET',body='',proof=None):
        header=f'{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n'
        if auth is not None:
            proof=proof or self.proof(method,path,body.encode(),auth)
            header+='Authorization: '+proof+'\r\n'
        request=header+extra+'\r\n'+body
        with socket.create_connection(('127.0.0.1',self.port),timeout=3) as client:
            client.settimeout(4);client.sendall(request.encode())
            data=b''
            while True:
                chunk=client.recv(4096)
                if not chunk:break
                data+=chunk
        head,content=data.split(b'\r\n\r\n',1)
        status=int(head.split()[1])
        if auth in [self.token,self.observer_token] and status not in [400,401,413]:
            fields={line.split(b':',1)[0].lower():line.split(b':',1)[1].strip().decode() for line in head.split(b'\r\n')[1:] if b':' in line}
            message='qt-web-response-v1\n'+proof+'\n'+str(status)+'\n'+hashlib.sha256(content).hexdigest()
            expected=hmac.new(auth.encode(),message.encode(),hashlib.sha256).hexdigest()
            self.assertEqual(fields.get(b'x-quietthings-response'),expected)
        return status,json.loads(content)
    def test_unauthenticated_request_cannot_read_player(self):
        status,body=self.request()
        self.assertEqual(status,401)
        self.assertNotIn('probe',body)
        self.assertNotIn(self.token,str(body))
    def test_wrong_token_rejected(self):
        self.assertEqual(self.request(auth='incorrect-token-000000')[0],401)
    def test_missing_player_returns_sanitized_failure(self):
        status,body=self.request(auth=self.token)
        self.assertEqual(status,409)
        self.assertNotIn(self.directory.name,str(body))
    def test_kicktv_route_is_fixed_and_separately_authenticated(self):
        self.assertEqual(self.request('/v1/kicktv/playback',self.token)[0],409)
        self.assertEqual(self.request('/v1/otherapp/playback',self.token)[0],404)
        proof=self.proof('GET','/v1/playback',b'',self.token)
        self.assertEqual(self.request('/v1/kicktv/playback',self.token,proof=proof)[0],401)
    def test_transfer_encoding_rejected(self):
        self.assertEqual(self.request(auth=self.token,extra='Transfer-Encoding: chunked\r\n')[0],400)
    def test_duplicate_authorization_rejected(self):
        self.assertEqual(self.request(auth=self.token,extra='Authorization: Bearer second\r\n')[0],400)
    def test_duplicate_content_length_rejected(self):
        self.assertEqual(self.request(auth=self.token,extra='Content-Length: 0\r\nContent-Length: 1\r\n')[0],400)
    def test_oversized_command_body_rejected_before_read(self):
        self.assertEqual(self.request('/v1/command',self.token,'Content-Length: 1025\r\n','POST')[0],413)
    def test_arbitrary_route_never_reaches_helper(self):
        self.assertEqual(self.request('/v1/shell',self.token,method='POST')[0],404)
    def test_replayed_request_rejected(self):
        proof=self.proof('GET','/v1/playback',b'',self.token)
        self.assertEqual(self.request(auth=self.token,proof=proof)[0],409)
        self.assertEqual(self.request(auth=self.token,proof=proof)[0],401)
    def test_signature_cannot_authorize_modified_body(self):
        proof=self.proof('POST','/v1/launch',b'',self.token)
        self.assertEqual(self.request('/v1/launch',self.token,'Content-Length: 2\r\n','POST','{}',proof)[0],401)
    def test_launch_requires_post(self):
        self.assertEqual(self.request('/v1/launch',self.token)[0],404)
    def test_cobalt_report_has_separate_credential_and_process_attestation(self):
        self.assertEqual(self.request('/v1/cobalt/report',self.token,method='POST')[0],401)
        self.assertEqual(self.request('/v1/cobalt/playback',self.observer_token)[0],401)
        # Knowing the app credential still cannot fabricate a Cobalt socket owner.
        self.assertEqual(self.request('/v1/cobalt/report',self.observer_token,extra='Content-Length: 2\r\n',method='POST',body='{}')[0],409)
        self.assertEqual(self.request('/v1/cobalt/playback',self.token)[0],409)
    def test_slow_client_does_not_block_report_or_command_connections(self):
        with socket.create_connection(('127.0.0.1',self.port),timeout=3) as slow:
            slow.sendall(b'POST /v1/command HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\nAuthorization: QuietThings-HMAC unfinished\r\n\r\nx')
            before=time.monotonic()
            self.assertEqual(self.request('/v1/no-route',self.token)[0],404)
            self.assertLess(time.monotonic()-before,1.0)

if __name__=='__main__':unittest.main()
