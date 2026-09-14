"""Real Claude channel check, invoked by check-oc2.py --mode claude.

Uses private Claude settings and a local scripted Anthropic provider. The cached
channel feature is seeded for offline testing; outbound HTTPS is blocked by a
local proxy. No user auth, settings, or paid model requests are used.
"""
import http.server,json,os,pty,re,select,signal,sqlite3,struct,subprocess,sys,termios,fcntl,threading,time
from pathlib import Path

def run(root,env,api,session,collab,oc2,serverUrl,password):
 root=Path(root);config=root/'claude-config';config.mkdir()
 oc2Master,oc2Slave=pty.openpty();fcntl.ioctl(oc2Slave,termios.TIOCSWINSZ,struct.pack('HHHH',35,120,0,0))
 oc2Env=dict(env);oc2Env.update(TERM='xterm-256color',OPENCODE_SERVER_PASSWORD=password,OPENCODE_PASSWORD=password)
 oc2Tui=subprocess.Popen([oc2,'--server',serverUrl,'--session',session,str(root)],cwd=root,env=oc2Env,stdin=oc2Slave,stdout=oc2Slave,stderr=oc2Slave,start_new_session=True)
 os.close(oc2Slave);oc2Terminal=[]
 def oc2Drain():
  try:
   while True:
    chunk=os.read(oc2Master,65536);oc2Terminal.append(chunk.decode(errors='replace'))
    if b'\x1b[6n' in chunk:os.write(oc2Master,b'\x1b[1;1R')
  except OSError:pass
 threading.Thread(target=oc2Drain,daemon=True).start()
 def snapshot():
  with sqlite3.connect('file:'+env['COLLAB_MCP_HOME']+'/state.sqlite?mode=ro',uri=True) as db:
   return json.loads(db.execute('SELECT payload FROM state WHERE id=1').fetchone()[0])
 def wait(predicate,label,seconds=20):
  deadline=time.monotonic()+seconds
  while time.monotonic()<deadline:
   if predicate():return
   time.sleep(.1)
  raise AssertionError(label)
 plan=[];seen=[];lock=threading.Lock();busy=threading.Event();release=threading.Event();followupDone=threading.Event()
 class Provider(http.server.BaseHTTPRequestHandler):
  def log_message(self,*args):pass
  def do_CONNECT(self):self.send_error(403) # No external traffic during this check.
  def do_POST(self):
   body=json.loads(self.rfile.read(int(self.headers.get('Content-Length','0'))))
   if 'count_tokens' in self.path:
    raw=b'{"input_tokens":1}';self.send_response(200);self.send_header('Content-Length',str(len(raw)));self.end_headers();self.wfile.write(raw);return
   primary=bool(body.get('tools'))
   with lock:
    if primary:seen.append(body)
    operation=plan.pop(0) if plan and primary else None
   if operation:
    verb,arguments=operation
    tool=next(t['name'] for t in body.get('tools',[]) if t['name'].endswith('__'+verb))
    block={'type':'tool_use','id':'tool_'+str(len(seen)),'name':tool,'input':arguments};reason='tool_use'
   else:
    text='Claude peer response streamed live.'
    if any('OC2 to busy Claude payload' in json.dumps(m) for m in body['messages']):text='Queued Claude peer response streamed live.'
    block={'type':'text','text':text};reason='end_turn'
   message={'id':'msg_probe_'+str(len(seen)),'type':'message','role':'assistant','model':body.get('model','claude-sonnet-4-6'),'content':[],'stop_reason':None,'stop_sequence':None,'usage':{'input_tokens':1,'output_tokens':1}}
   if not body.get('stream'):
    message.update(content=[block],stop_reason=reason);raw=json.dumps(message).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(raw)));self.end_headers();self.wfile.write(raw);return
   self.send_response(200);self.send_header('Content-Type','text/event-stream');self.send_header('Connection','close');self.end_headers()
   def emit(kind,value):self.wfile.write(('event: '+kind+'\ndata: '+json.dumps(value)+'\n\n').encode());self.wfile.flush()
   emit('message_start',{'type':'message_start','message':message})
   start=dict(block);start['text' if block['type']=='text' else 'input']='' if block['type']=='text' else {}
   emit('content_block_start',{'type':'content_block_start','index':0,'content_block':start})
   if block['type']=='text':delta={'type':'text_delta','text':block['text']}
   else:delta={'type':'input_json_delta','partial_json':json.dumps(block['input'])}
   if block['type']=='text':
    for word in block['text'].split():
     emit('content_block_delta',{'type':'content_block_delta','index':0,'delta':{'type':'text_delta','text':word+' '}});time.sleep(.1)
   else:emit('content_block_delta',{'type':'content_block_delta','index':0,'delta':delta})
   if primary and block['type']=='text' and any('busy probe' in json.dumps(m) for m in body['messages']):busy.set();release.wait(10)
   emit('content_block_stop',{'type':'content_block_stop','index':0})
   emit('message_delta',{'type':'message_delta','delta':{'stop_reason':reason,'stop_sequence':None},'usage':{'output_tokens':1}})
   emit('message_stop',{'type':'message_stop'})
   if primary and any('OC2 to busy Claude payload' in json.dumps(m) for m in body['messages']):followupDone.set()
 class LocalServer(http.server.ThreadingHTTPServer):
  def handle_error(self,request,address):
   if isinstance(sys.exc_info()[1],(BrokenPipeError,ConnectionResetError)):return # Normal CLI shutdown/cancel.
   super().handle_error(request,address)
 server=LocalServer(('127.0.0.1',0),Provider);server.daemon_threads=True
 threading.Thread(target=server.serve_forever,daemon=True).start()
 claudeEnv=dict(env)
 for key in ['CLAUDECODE','CLAUDE_CODE_SESSION_ID','ANTHROPIC_AUTH_TOKEN','CLAUDE_CODE_OAUTH_TOKEN','DISABLE_TELEMETRY','DO_NOT_TRACK','CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC']:claudeEnv.pop(key,None)
 claudeEnv.update(CLAUDE_CONFIG_DIR=str(config),ANTHROPIC_API_KEY='local-probe',ANTHROPIC_BASE_URL=f'http://127.0.0.1:{server.server_port}',DISABLE_ERROR_REPORTING='1',DISABLE_AUTOUPDATER='1',HTTPS_PROXY=f'http://127.0.0.1:{server.server_port}',HTTP_PROXY=f'http://127.0.0.1:{server.server_port}',NO_PROXY='127.0.0.1,localhost')
 (config/'.claude.json').write_text(json.dumps({'hasCompletedOnboarding':True,'cachedGrowthBookFeatures':{'tengu_harbor':True},'projects':{str(root):{'hasTrustDialogAccepted':True}},'customApiKeyResponses':{'approved':['local-probe']}}))
 mcp=root/'claude-mcp.json';mcp.write_text(json.dumps({'mcpServers':{'collab':{'command':'dotnet','args':[collab]}}}))
 master,slave=pty.openpty();fcntl.ioctl(slave,termios.TIOCSWINSZ,struct.pack('HHHH',40,140,0,0))
 process=subprocess.Popen(['claude','--strict-mcp-config','--mcp-config',str(mcp),'--dangerously-load-development-channels','server:collab','--setting-sources','','--allowedTools','mcp__collab__*','--debug-file',str(root/'claude-debug.log')],cwd=root,env=claudeEnv,stdin=slave,stdout=slave,stderr=slave,start_new_session=True)
 os.close(slave);terminal=[];reading=True
 def drain():
  while reading:
   if select.select([master],[],[],.1)[0]:
    try:terminal.append(os.read(master,65536).decode(errors='replace'))
    except OSError:return
 reader=threading.Thread(target=drain,daemon=True);reader.start()
 def prompt(text):os.write(master,(text+'\r').encode())
 def sendFromOC2(to,body):
  # Existing OC2 fixture scripts native execute calls from this prompt marker.
  api('POST',f'/api/session/{session}/prompt',{'text':'claude send '+json.dumps({'to':to,'body':body})})
 try:
  wait(lambda:'Loading' in ''.join(terminal) and 'development' in ''.join(terminal),'Claude channel opt-in prompt did not appear')
  os.write(master,b'\r') # This is our locally built development channel.
  wait(lambda:any(r['binding']=='bound' and r['endpoint']['harness']=='claude' for r in snapshot()['registrations']),'Claude did not register before hello')
  initial=next(r for r in snapshot()['registrations'] if r['binding']=='bound' and r['endpoint']['harness']=='claude')
  assert initial['name'].startswith('claude-') and re.fullmatch('[0-9a-f]{8}',initial['shortId'])
  assert not seen,'Registration ran a model'
  print('Claude automatic native identity and generated name before any prompt: passed.')
  with lock:plan.extend([('roster',{}),('hello',{'name':'ClaudeAlpha'}),('send',{'to':'Red','body':'Claude to OC2 payload'})])
  prompt('Run the collaboration integration steps.')
  wait(lambda:'Claude to OC2 payload' in json.dumps(api('GET',f'/api/session/{session}/message?limit=100')),'Claude did not send to OC2')
  changed=next(r for r in snapshot()['registrations'] if r['peerId']==initial['peerId'])
  assert changed['shortId']==initial['shortId'] and changed['name']=='ClaudeAlpha' and initial['name'] in changed['aliases']
  wait(lambda:"Tool 'roster' completed successfully" in (root/'claude-debug.log').read_text(),'Native Claude roster was not called')
  print('Claude roster, rename continuity and delivery to real OC2: passed.')
  wait(lambda:all(word in ''.join(oc2Terminal) for word in ['OC2','streamed','live.']),'OC2 TUI did not display its streamed peer response')
  before=len(seen);sendFromOC2(initial['shortId'],'OC2 to idle Claude payload')
  wait(lambda:len(seen)>before and any('OC2 to idle Claude payload' in json.dumps(b['messages']) for b in seen[before:]),'Idle Claude did not receive channel input')
  wait(lambda:not snapshot()['pending'],'Channel admission was not reconciled')
  wait(lambda:'streamed' in ''.join(terminal) and 'live' in ''.join(terminal),'Claude TUI did not display its streamed response')
  print('Real OC2 -> idle Claude channel -> live terminal response and positive receipt: passed.')
  prompt('busy probe');wait(busy.is_set,'Claude busy stream did not start')
  before=len(seen);sendFromOC2(initial['name'],'OC2 to busy Claude payload')
  time.sleep(1);assert len(seen)==before,'Channel interrupted the current turn'
  release.set();wait(lambda:any('OC2 to busy Claude payload' in json.dumps(b['messages']) for b in seen[before:]),'Busy Claude did not consume queued input')
  wait(lambda:not snapshot()['pending'],'Busy channel admission was not reconciled')
  wait(followupDone.is_set,'Queued Claude response did not finish streaming')
  wait(lambda:'Queued' in ''.join(terminal),'Queued response was not visible in the live Claude TUI')
  assert process.poll() is None and oc2Tui.poll() is None,'A live TUI exited'
  print('Busy Claude queue preserved current turn and streamed the follow-up: passed.')
 except Exception:
  print('OC2 terminal at failure:',re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]','', ''.join(oc2Terminal)[-3000:]))
  print('Claude terminal at failure:',re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]','', ''.join(terminal)[-3000:]))
  debug=root/'claude-debug.log'
  if debug.exists():
   for line in debug.read_text().splitlines():
    if 'channel' in line.lower() or 'collab' in line.lower():print(line[:700])
  raise
 finally:
  release.set();os.killpg(process.pid,signal.SIGTERM)
  try:process.wait(timeout=5)
  except subprocess.TimeoutExpired:os.killpg(process.pid,signal.SIGKILL);process.wait(timeout=5)
  reading=False;reader.join(timeout=2);os.close(master);server.shutdown();server.server_close()
  os.killpg(oc2Tui.pid,signal.SIGTERM)
  try:oc2Tui.wait(timeout=5)
  except subprocess.TimeoutExpired:os.killpg(oc2Tui.pid,signal.SIGKILL);oc2Tui.wait(timeout=5)
  os.close(oc2Master)
