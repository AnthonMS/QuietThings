"""Exercise launch safety ordering and failure cleanup with fake OS commands."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
BASH = shutil.which("bash") if os.name != "nt" else r"C:/Program Files/Git/bin/bash.exe"
MOCK = r"""import json, os, sys
from pathlib import Path
name=sys.argv[1]; a=sys.argv[2:]; p=Path(os.environ['QT_TEST_STATE'])
s=json.loads(p.read_text()); s['events'].append([name]+a)
profile=os.environ.get('QT_WEB_APP','twitch');profile_chain='QT_KICKTV' if profile=='kicktv' else 'QT_TWITCH';gate=profile_chain+'_BOOT';prefix='CrKckTV001.Kic' if profile=='kicktv' else 'Jz7aUrV2Q4.Tw'
def save():p.write_text(json.dumps(s))
def fail():save();sys.exit(1)
if name=='id':print('0')
elif name=='netstat':
 print('Proto Recv-Q Send-Q Local Foreign State PID')
 print('tcp 0 0 0.0.0.0:18766 0.0.0.0:* LISTEN 565/BixbyPromotion')
 if s['inspector']:print('tcp 0 0 0.0.0.0:37305 0.0.0.0:* LISTEN 42/'+prefix)
elif name=='app_launcher':
 if '-k' in a:
  s['running']=False;s['inspector']=False
 elif '-w' in a:
  for family in ['iptables','ip6tables']:
   rules=s['tables'][family]['OUTPUT']
   if not any(gate in r for r in rules):raise RuntimeError('launch before both guards')
  s['running']=True;s['inspector']=True
  print('... successfully launched pid = 42 with debug 1 port: '+('bad' if s['fail']=='port' else '37305'))
 else:s['running']=True
else:
 t=s['tables'][name];op=a[0]; chain=a[1];rule=a[2:]
 # The tested TV kernel has no owner-match module.
 if '--uid-owner' in rule:fail()
 if '--reject-with' in rule and rule[rule.index('--reject-with')+1]=='tcp-reset':
  if '-p' not in rule or rule[rule.index('-p')+1]!='tcp':fail()
 if op=='-S':
  if chain not in t:fail()
  for r in t[chain]:print('-A '+chain+' '+' '.join(r))
 elif op=='-N':
  if chain in t:fail()
  t[chain]=[]
 elif op in ['-A','-I']:
  if op=='-I' and rule and rule[0]=='1':rule=rule[1:]
  if s['fail']=='ipv6_guard' and name=='ip6tables' and chain=='OUTPUT':fail()
  if s['fail']=='ipv6_protect' and name=='ip6tables' and chain=='INPUT':fail()
  if chain not in t:fail()
  if op=='-I':t[chain].insert(0,rule)
  else:t[chain].append(rule)
 elif op=='-C':
  if chain not in t or rule not in t[chain]:fail()
 elif op=='-D':
  if chain not in t or rule not in t[chain]:fail()
  if chain=='OUTPUT' and s['inspector']:
   for family in ['iptables','ip6tables']:
    if not any(profile_chain in r and '37305' in r for r in s['tables'][family]['INPUT']):raise RuntimeError('unguarded inspector at guard removal')
  t[chain].remove(rule)
 elif op=='-F':
  if chain not in t:fail()
  t[chain]=[]
 elif op=='-X':
  if chain not in t:fail()
  del t[chain]
 else:raise RuntimeError('unexpected operation')
save()
"""

@unittest.skipUnless(Path(BASH or "").is_file(), "bash required")
class LaunchSafety(unittest.TestCase):
    def run_case(self, failure="", after=None, profile="twitch"):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory); tools=base/'tools'; tools.mkdir()
            state=base/'state.json'
            state.write_text(json.dumps({'tables':{f:{'INPUT':[],'OUTPUT':[]} for f in ['iptables','ip6tables']},'running':True,'inspector':False,'events':[],'fail':failure}))
            mock=base/'mock.py'; mock.write_text(MOCK)
            for name in ['id','netstat','app_launcher','iptables','ip6tables']:
                command=tools/name
                # Forward arguments literally; no shell interpolation of input data.
                command.write_text('#!/bin/sh\nexec "'+sys_executable()+'" "'+mock.as_posix()+'" '+name+' "$@"\n')
                command.chmod(0o755)
            env=dict(os.environ,QT_WEB_APP=profile,QT_TEST_STATE=str(state),PATH=str(tools)+os.pathsep+os.environ['PATH'])
            if os.name=='nt':
                env['QT_TEST_TOOLS']=tools.as_posix()
                args=[BASH,'-c','export PATH="$(cygpath -u "$QT_TEST_TOOLS"):$PATH"; exec /bin/sh -s -- "$1"','test','launch']
            else:args=[BASH,'-s','--','launch']
            result=subprocess.run(args,input=(ROOT/'bridge/manage-twitch.sh').read_text(),env=env,capture_output=True,text=True,timeout=30)
            if after is not None and result.returncode == 0:
                continuation=args[:-1]+[after]
                result=subprocess.run(continuation,input=(ROOT/'bridge/manage-twitch.sh').read_text(),env=env,capture_output=True,text=True,timeout=30)
            final=json.loads(state.read_text())
            self.assertNotIn('RuntimeError',result.stderr,result.stderr)
            self.assertTrue(all(not any('QT_TWITCH_BOOT' in r or 'QT_KICKTV_BOOT' in r for r in t['OUTPUT']) for t in final['tables'].values()))
            return result,final
    def test_success_protects_both_families_before_removing_gate(self):
        result,state=self.run_case()
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertIn('QT_ENDPOINT 42 37305',result.stdout)
        self.assertTrue(state['inspector'])
        for table in state['tables'].values():
            self.assertTrue(any('37305' in r and 'QT_TWITCH' in r for r in table['INPUT']))
    def test_kicktv_has_separate_guards_and_lifecycle(self):
        result,state=self.run_case(profile="kicktv",after="stop")
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertFalse(state['inspector'])
        self.assertTrue(all(not t['INPUT'] for t in state['tables'].values()))
        self.assertTrue(any('QT_KICKTV' in event for event in state['events']))
    def test_unknown_profile_never_launches_or_changes_firewall(self):
        result,state=self.run_case(profile="unknown")
        self.assertNotEqual(result.returncode,0)
        self.assertFalse(state['events'])
    def test_ipv6_guard_failure_preserves_existing_normal_app(self):
        result,state=self.run_case('ipv6_guard')
        self.assertNotEqual(result.returncode,0)
        self.assertTrue(state['running'])
        self.assertFalse(state['inspector'])
    def test_partial_port_protection_failure_closes_debug_app(self):
        result,state=self.run_case('ipv6_protect')
        self.assertNotEqual(result.returncode,0)
        self.assertFalse(state['running'])
        self.assertFalse(state['inspector'])
    def test_restart_keeps_completed_session_private_and_playing(self):
        result,state=self.run_case(after='recover')
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertTrue(state['running'] and state['inspector'])
        self.assertTrue(all(any('37305' in r for r in t['INPUT']) for t in state['tables'].values()))
    def test_explicit_stop_closes_inspector_and_retires_port_rules(self):
        result,state=self.run_case(after='stop')
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertFalse(state['inspector'])
        self.assertTrue(all(not t['INPUT'] for t in state['tables'].values()))
    def test_invalid_inspector_port_cannot_escape_guard(self):
        result,state=self.run_case('port')
        self.assertNotEqual(result.returncode,0)
        self.assertFalse(state['inspector'])

def sys_executable():
    import sys
    return Path(sys.executable).as_posix()

if __name__=='__main__':unittest.main()
