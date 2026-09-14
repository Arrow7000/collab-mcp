#!/usr/bin/env python3
"""Optional real OC2 integration check with a local scripted provider and private storage.
Build Release first. Requires OC2 2.0.3+ and Python 3. No paid model calls.
"""
import sqlite3,argparse,pty,fcntl,termios,struct,tempfile,shutil,signal,os,json,subprocess,time,urllib.request,socket,re,base64,threading,http.server
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--oc2',required=True,help='Path to the compatible OC2 binary')
parser.add_argument('--mode',choices=['codemode','direct','tui','bootstrap'],default='codemode')
args=parser.parse_args()
os.environ['PROBE_REAL']='1'
os.environ['PROBE_DIRECT']='1' if args.mode=='direct' else '0'
os.environ['PROBE_TUI']='1' if args.mode=='tui' else '0'
root=os.path.realpath(tempfile.mkdtemp(prefix='cm-oc2-',dir='/tmp'));env=os.environ.copy()
for kind in ['CONFIG','DATA','STATE','CACHE']:env['XDG_'+kind+'_HOME']=root+'/'+kind.lower()
env['COLLAB_CAPTURE']=root+'/mcp.jsonl'
if os.path.exists(env['COLLAB_CAPTURE']):os.remove(env['COLLAB_CAPTURE'])
requests=[]
busyStarted=threading.Event()
class Handler(http.server.BaseHTTPRequestHandler):
 def log_message(self,*args):pass
 def do_POST(self):
  body=json.loads(self.rfile.read(int(self.headers['Content-Length'])));requests.append(body)
  open(root+'/model-requests.json','w').write(json.dumps(requests,indent=2))
  tools=body.get('tools',[]);name=next((t['function']['name'] for t in tools if 'hello' in t['function']['name']),None)
  execute=next((t['function']['name'] for t in tools if t['function']['name']=='execute'),None)
  if len(requests)==1 and (name or execute):
   callArgs={'name':'Red'} if name else {'code':'const result = await tools.collab.hello({name: "Red"}); return result'}
   if args.mode=='bootstrap':callArgs={'code':'return await tools.collab.roster({})'}
   msg={'role':'assistant','content':None,'tool_calls':[{'id':'probe_call','type':'function','function':{'name':name or execute,'arguments':json.dumps(callArgs)}}]};finish='tool_calls'
  else:
   last=json.dumps(body['messages'][-1])
   text='Probe complete.'
   if 'external idle TUI check' in last:text='Idle peer response streamed live.'
   if 'busy start' in last:text='Busy peer response streamed live.'
   if 'busy followup' in last:text='Queued peer response streamed live.'
   msg={'role':'assistant','content':text};finish='stop'
  if body.get('stream'):
   delta={k:v for k,v in msg.items() if k!='role'}
   if 'tool_calls' in delta:delta['tool_calls'][0]['index']=0
   chunks=[{'id':'probe','object':'chat.completion.chunk','created':int(time.time()),'model':'mock','choices':[{'index':0,'delta':delta,'finish_reason':None}]},{'id':'probe','object':'chat.completion.chunk','created':int(time.time()),'model':'mock','choices':[{'index':0,'delta':{},'finish_reason':finish}],'usage':{'prompt_tokens':1,'completion_tokens':1,'total_tokens':2}}]
   raw=(''.join('data: '+json.dumps(v)+'\n\n' for v in chunks)+'data: [DONE]\n\n').encode();content='text/event-stream'
  else:raw=json.dumps({'id':'probe','object':'chat.completion','created':int(time.time()),'model':'mock','choices':[{'index':0,'message':msg,'finish_reason':finish}],'usage':{'prompt_tokens':1,'completion_tokens':1,'total_tokens':2}}).encode();content='application/json'
  self.send_response(200);self.send_header('Content-Type',content);
  if not (body.get('stream') and msg.get('content') and len(requests)>2):self.send_header('Content-Length',str(len(raw)))
  self.send_header('Connection','close');self.end_headers()
  if body.get('stream') and msg.get('content') and len(requests)>2:
   for index,word in enumerate(msg['content'].split(' ')):
    chunk={'id':'probe','object':'chat.completion.chunk','created':int(time.time()),'model':'mock','choices':[{'index':0,'delta':{'content':word+' '},'finish_reason':None}]}
    self.wfile.write(('data: '+json.dumps(chunk)+'\n\n').encode());self.wfile.flush()
    if index==0 and msg['content'].startswith('Busy'):
     busyStarted.set();time.sleep(2)
    time.sleep(.2)
   tail={'id':'probe','object':'chat.completion.chunk','created':int(time.time()),'model':'mock','choices':[{'index':0,'delta':{},'finish_reason':'stop'}]}
   # Content length must match this streamed representation; use close framing instead.
   self.wfile.write(('data: '+json.dumps(tail)+'\n\ndata: [DONE]\n\n').encode());self.wfile.flush()
  else:self.wfile.write(raw)
socket.getfqdn = lambda name='': name or 'localhost'
mock=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler);threading.Thread(target=mock.serve_forever,daemon=True).start()
configdir=env['XDG_CONFIG_HOME']+'/opencode';os.makedirs(configdir,exist_ok=True)
config={'model':'mock/mock','providers':{'mock':{'package':'@opencode/ai/providers/openai-compatible','settings':{'baseURL':f'http://127.0.0.1:{mock.server_port}/v1','apiKey':'local-probe'},'models':{'mock':{'name':'Mock','tool_call':True}}}},'mcp':{'servers':{'collab':{'type':'local','codemode':os.environ.get('PROBE_DIRECT')!='1','command':['dotnet',os.path.abspath(os.path.join(os.path.dirname(__file__),'..','src','Collab.Shim','bin','Release','net10.0','collab-mcp.dll'))]}}}}
open(configdir+'/opencode.json','w').write(json.dumps(config))
exe=os.path.abspath(args.oc2)
with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
log=open(root+'/server.log','w');p=subprocess.Popen([exe,'serve','--hostname','127.0.0.1','--port',str(port)],env=env,cwd=root,stdout=log,stderr=log,start_new_session=True)
try:
 auth=None
 for _ in range(100):
  m=re.search(r'server password (\S+)',open(root+'/server.log').read())
  if m:auth='Basic '+base64.b64encode(('opencode:'+m[1]).encode()).decode();break
  if p.poll() is not None:raise RuntimeError('server exited')
  time.sleep(.1)
 def api(method,path,body=None):
  req=urllib.request.Request(f'http://127.0.0.1:{port}'+path,data=json.dumps(body).encode() if body is not None else None,method=method,headers={'Authorization':auth,'Content-Type':'application/json'})
  raw=urllib.request.urlopen(req,timeout=15).read()
  return json.loads(raw) if raw else None
 daemon=None
 if os.environ.get('PROBE_REAL')=='1':
  collab=os.path.abspath(os.path.join(os.path.dirname(__file__),'..','src','Collab.Shim','bin','Release','net10.0','collab-mcp.dll'))
  os.makedirs(root+'/bin',exist_ok=True)
  stub=root+'/bin/opencode2';open(stub,'w').write(f'#!/bin/sh\necho http://127.0.0.1:{port}\n');os.chmod(stub,0o700)
  env['PATH']=root+'/bin:'+env['PATH'];env['COLLAB_MCP_HOME']=tempfile.mkdtemp(prefix='cm-proof-',dir='/tmp')
  os.makedirs(env['COLLAB_MCP_HOME'],exist_ok=True)
  open(configdir+'/service.json','w').write(json.dumps({'password':m[1]}));os.chmod(configdir+'/service.json',0o600)
  daemonLog=open(root+'/daemon.log','w')
  daemon=subprocess.Popen(['dotnet',collab,'--daemon'],env=env,stdout=daemonLog,stderr=daemonLog,start_new_session=True)
  for _ in range(100):
   status=subprocess.run(['dotnet',collab,'--status'],env=env,capture_output=True,text=True)
   if 'Event stream: Connected' in status.stdout:break
   time.sleep(.1)
  config['mcp']['servers']['collab']['command']=['dotnet',collab]
  config['mcp']['servers']['collab']['environment']={'PATH':env['PATH'],'COLLAB_MCP_HOME':env['COLLAB_MCP_HOME']}
 api('PUT','/api/mcp/collab?location[directory]='+root,{'config':config['mcp']['servers']['collab']})
 api('GET','/api/mcp?location[directory]='+root)
 session=api('POST','/api/session',{'title':'metadata probe','location':{'directory':root}})['data']['id']
 expected=1
 if args.mode=='bootstrap':
  second=api('POST','/api/session',{'title':'second automatic peer','location':{'directory':root}})['data']['id']
  expected=2
  for _ in range(100):
   status=subprocess.run(['dotnet',collab,'--status'],env=env,capture_output=True,text=True)
   if 'Registrations: 2 (2 bound)' in status.stdout:break
   time.sleep(.1)
  assert 'Registrations: 2 (2 bound)' in status.stdout,status.stdout
  assert not requests,'registration must not run a model'
  with sqlite3.connect('file:'+env['COLLAB_MCP_HOME']+'/state.sqlite?mode=ro',uri=True) as db:
   stored=json.loads(db.execute('SELECT payload FROM state WHERE id=1').fetchone()[0])
  names=[r['name'] for r in stored['registrations']]
  assert len(set(names))==2 and all(n.startswith('oc2-') for n in names),names
  print('Two loaded sessions automatically registered before any model or hello call: passed.')
  disabled=root+'/disabled';os.makedirs(disabled)
  open(disabled+'/opencode.json','w').write(json.dumps({'mcp':{'servers':{'collab':{'type':'local','disabled':True,'command':['dotnet',collab]}}}}))
  api('POST','/api/session',{'title':'disabled collaboration','location':{'directory':disabled}})
  time.sleep(4)
  status=subprocess.run(['dotnet',collab,'--status'],env=env,capture_output=True,text=True)
  assert 'Registrations: 2 (2 bound)' in status.stdout,status.stdout
  assert not requests,'bootstrap must not run a model'
  print('Session in a collab-disabled project was not registered: passed.')
 events=[]
 def streamEvents():
  req=urllib.request.Request(f'http://127.0.0.1:{port}/api/event',headers={'Authorization':auth,'Accept':'text/event-stream'})
  try:
   with urllib.request.urlopen(req,timeout=10) as response:
    for line in response:
     if line.startswith(b'data:'):
      event=json.loads(line[5:]);events.append(event)
      with open(root+'/events.jsonl','a') as f:f.write(json.dumps(event)+'\n')
  except Exception:pass
 eventThread=threading.Thread(target=streamEvents,daemon=True);eventThread.start();time.sleep(.2)
 api('POST',f'/api/session/{session}/prompt',{'text':'Announce Red with collab hello, then stop.'})
 for _ in range(200):
  if os.path.exists(env['COLLAB_CAPTURE']) or (os.environ.get('PROBE_REAL')=='1' and len(requests)>1):break
  time.sleep(.1)
 if os.environ.get('PROBE_REAL')=='1':
  for _ in range(150):
   status=subprocess.run(['dotnet',collab,'--status'],env=env,capture_output=True,text=True)
   if f'Registrations: {expected} ({expected} bound)' in status.stdout:break
   time.sleep(.1)
  assert f'Registrations: {expected} ({expected} bound)' in status.stdout,status.stdout
  print('Real OC2 -> MCP shim -> metadata/event attribution -> durable registration: passed.')
 if os.environ.get('PROBE_TUI')=='1':
  time.sleep(.5)
  master,slave=pty.openpty();fcntl.ioctl(slave,termios.TIOCSWINSZ,struct.pack('HHHH',35,120,0,0))
  tuiEnv=env.copy();tuiEnv['TERM']='xterm-256color';tuiEnv['OPENCODE_SERVER_PASSWORD']=m[1];tuiEnv['OPENCODE_PASSWORD']=m[1]
  tui=subprocess.Popen([exe,'--server',f'http://127.0.0.1:{port}','--session',session,root],env=tuiEnv,cwd=root,stdin=slave,stdout=slave,stderr=slave,start_new_session=True)
  os.close(slave);screen=bytearray()
  def readTerminal():
   try:
    while True:
     chunk=os.read(master,65536);screen.extend(chunk)
     if b'\x1b[6n' in chunk:os.write(master,b'\x1b[1;1R')
   except OSError:pass
  threading.Thread(target=readTerminal,daemon=True).start()
  try:
   time.sleep(3)
   before=len(requests)
   api('POST',f'/api/session/{session}/synthetic',{'text':'[peer Blue]: external idle TUI check','description':'peer message from Blue','delivery':'queue'})
   time.sleep(2)
   busyStarted.clear()
   api('POST',f'/api/session/{session}/synthetic',{'text':'[peer Blue]: busy start','description':'busy work from Blue','delivery':'queue'})
   assert busyStarted.wait(5),'busy response did not start'
   api('POST',f'/api/session/{session}/synthetic',{'text':'[peer Green]: busy followup','description':'peer message from Green','delivery':'queue'})
   time.sleep(.4)
   during=screen.decode(errors='replace');open(root+'/tui-busy-raw.txt','w').write(during)
   time.sleep(5)
   raw=screen.decode(errors='replace');open(root+'/tui-raw.txt','w').write(raw)
   plain=re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]','',raw);plain=re.sub(r'\x1b\][^\x07]*(?:\x07|\x1b\\)','',plain)
   open(root+'/tui-text.txt','w').write(plain)
   assert tui.poll() is None,'TUI exited'
   assert 'peer message from Blue' in plain,'idle notice missing'
   assert 'Idle peer response streamed live.' in plain,'idle reply missing'
   assert 'peer message from Green' in plain,'busy notice missing'
   assert 'Queued peer response streamed live.' in plain,'queued reply missing'
   duringPlain=re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]','',during)
   assert 'peer message from Green' in duringPlain,'queued notice not shown while busy'
   assert 'Queued peer response' not in duringPlain,'queued reply overtook active turn'
   print('TUI process running:',tui.poll() is None)
   print('Incoming notice seen:', 'peer message from Blue' in plain)
   print('Idle streamed reply seen:', 'Idle peer response streamed live.' in plain)
   print('Busy sender notice seen:', 'peer message from Green' in plain)
   print('Queued streamed reply seen:', 'Queued peer response streamed live.' in plain)
   print('External input caused another model request:',len(requests)>before)
  finally:
   try:os.killpg(tui.pid,signal.SIGTERM)
   except ProcessLookupError:pass
   tui.wait(timeout=10);os.close(master)

finally:
 if 'daemon' in locals() and daemon is not None:
  os.killpg(daemon.pid,signal.SIGTERM);daemon.wait(timeout=10);daemonLog.close();shutil.rmtree(env['COLLAB_MCP_HOME'])
 try:os.killpg(p.pid,signal.SIGTERM)
 except ProcessLookupError:pass
 p.wait(timeout=10);mock.shutdown();log.close();shutil.rmtree(root)
