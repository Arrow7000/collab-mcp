#!/usr/bin/env python3
"""Exercise two real Pi TUIs with private state and a local scripted provider.

Build Release first. Requires Pi 0.85.1+ and Python 3; no paid model calls.
"""
import argparse, fcntl, http.server, json, os, pty, select, shutil, signal
import socket, sqlite3, struct, subprocess, tempfile, termios, threading, time
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', default='pi')
parser.add_argument('--keep', action='store_true', help='Keep private artifacts for debugging')
parser.add_argument('--package', action='store_true', help='Discover the installed package instead of loading the extension with -e')
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
binary = repo / 'src/Collab.Shim/bin/Release/net10.0/collab-mcp'
extension = repo / 'integrations/pi/index.ts'
root = Path(tempfile.mkdtemp(prefix='cm-pi-', dir='/tmp')).resolve()
project = root / 'project'; project.mkdir()
home = root / 'collab'; home.mkdir(mode=0o700)
env = dict(os.environ, COLLAB_MCP_HOME=str(home), COLLAB_MCP_BINARY=str(binary), PI_OFFLINE='1', PI_TELEMETRY='0', TERM='xterm-256color')
for key in list(env):
    if key.endswith(('_API_KEY', '_AUTH_TOKEN', '_OAUTH_TOKEN')): env.pop(key)
# Keep the Pi-only test independent of the user's OC2 daemon and credentials.
stub = root / 'bin'; stub.mkdir()
(stub / 'opencode2').write_text('#!/bin/sh\nexit 1\n'); (stub / 'opencode2').chmod(0o700)
env['PATH'] = str(stub) + ':' + env['PATH']
seen = []; busy = threading.Event(); release = threading.Event()

def text(message):
    value = message.get('content', '')
    return value if isinstance(value, str) else '\n'.join(block.get('text', '') for block in value or [] if isinstance(block, dict))

class Provider(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        seen.append(body)
        (root / 'requests.json').write_text(json.dumps(seen, indent=2))
        last = body['messages'][-1]
        content = text(last)
        operation = json.loads(content[len('PROBE_TOOL '):]) if last['role'] == 'user' and content.startswith('PROBE_TOOL ') else None
        if operation:
            tool_name = next(t['function']['name'] for t in body['tools'] if t['function']['name'] == 'collab_' + operation['verb'])
            delta = {'tool_calls': [{'index': 0, 'id': 'call_' + str(len(seen)), 'type': 'function', 'function': {'name': tool_name, 'arguments': json.dumps(operation['input'])}}]}
            finish = 'tool_calls'; words = []
        else:
            word = 'Queuedword' if 'Queuedword' in content else 'Idleword' if 'Idleword' in content else 'Busyword' if content == 'PROBE_BUSY' else 'Doneword'
            words = [word, 'response', 'streamed', 'live.']; finish = 'stop'; delta = {'role': 'assistant'}
        self.send_response(200); self.send_header('Content-Type', 'text/event-stream'); self.send_header('Connection', 'close'); self.end_headers()
        def emit(value, reason=None):
            chunk = {'id': 'probe', 'object': 'chat.completion.chunk', 'created': int(time.time()), 'model': 'mock', 'choices': [{'index': 0, 'delta': value, 'finish_reason': reason}]}
            self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode()); self.wfile.flush()
        try:
            emit(delta)
            for index, word in enumerate(words):
                emit({'content': word + ' '})
                if index == 0 and word == 'Busyword': busy.set(); release.wait(20)
                time.sleep(.12)
            emit({}, finish)
            self.wfile.write(b'data: [DONE]\n\n'); self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError): pass

socket.getfqdn = lambda name='': name or 'localhost'
provider = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Provider); provider.daemon_threads = True
threading.Thread(target=provider.serve_forever, daemon=True).start()
processes = []; terminals = {}; masters = {}; daemon = None

def wait(predicate, label, seconds=25):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            if predicate(): return
        except (FileNotFoundError, sqlite3.OperationalError, TypeError, IndexError): pass
        time.sleep(.1)
    raise AssertionError(label)

def snapshot():
    with sqlite3.connect('file:' + str(home / 'state.sqlite') + '?mode=ro', uri=True) as db:
        return json.loads(db.execute('SELECT payload FROM state WHERE id=1').fetchone()[0])

def start(name, session=None):
    config = root / name; config.mkdir(exist_ok=True)
    settings = {'quietStartup': True, 'enableBashMode': False}
    if args.package: settings['packages'] = [str(repo / 'integrations/pi')]
    (config / 'settings.json').write_text(json.dumps(settings))
    (config / 'models.json').write_text(json.dumps({'providers': {'mock': {'baseUrl': f'http://127.0.0.1:{provider.server_port}/v1', 'api': 'openai-completions', 'apiKey': 'local-probe', 'models': [{'id': 'mock', 'name': 'Mock', 'reasoning': False, 'input': ['text'], 'contextWindow': 32000, 'maxTokens': 1024}]}}}))
    local = dict(env, PI_CODING_AGENT_DIR=str(config))
    master, slave = pty.openpty(); fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 40, 140, 0, 0))
    command = [args.pi, '--offline', '--no-skills', '--no-prompt-templates', '--no-context-files', '--no-builtin-tools', '--provider', 'mock', '--model', 'mock', '--thinking', 'off']
    if not args.package: command += ['--no-extensions', '-e', str(extension)]
    if session: command += ['--session', str(session)]
    process = subprocess.Popen(command, cwd=project, env=local, stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
    os.close(slave); processes.append(process); terminals[name] = []; masters[name] = master
    def drain():
        try:
            while True:
                chunk = os.read(master, 65536); terminals[name].append(chunk.decode(errors='replace'))
                if b'\x1b[6n' in chunk: os.write(master, b'\x1b[1;1R')
        except OSError: pass
    threading.Thread(target=drain, daemon=True).start()
    return process

def prompt(name, value): os.write(masters[name], value.encode() + b'\r')
def tool(name, verb, arguments): prompt(name, 'PROBE_TOOL ' + json.dumps({'verb': verb, 'input': arguments}))
def terminal(name): return ''.join(terminals[name])
def outbox_empty(): return not snapshot()['pending']
def stop_process(process):
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try: process.wait(timeout=5)
        except subprocess.TimeoutExpired: os.killpg(process.pid, signal.SIGKILL); process.wait()

try:
    log = open(root / 'daemon.log', 'w')
    daemon = subprocess.Popen([str(binary), '--daemon'], cwd=project, env=env, stdout=log, stderr=log, start_new_session=True)
    first = start('a'); second = start('b')
    wait(lambda: len(snapshot()['registrations']) == 2, 'two automatic Pi registrations')
    assert not seen, 'registration must not invoke a model'
    original = snapshot()['registrations']; assert all(r['name'].startswith('pi-') for r in original)
    assert len({r['shortId'] for r in original}) == 2
    print('Two real Pi TUIs registered automatically before any prompt/model/hello: passed.', flush=True)
    tool('a', 'hello', {'name': 'Red'})
    wait(lambda: any(r['name'] == 'Red' for r in snapshot()['registrations']), 'Red rename')
    wait(lambda: 'Doneword' in terminal('a'), 'Red tool response')
    tool('b', 'hello', {'name': 'Blue'})
    wait(lambda: any(r['name'] == 'Blue' for r in snapshot()['registrations']), 'Blue rename')
    wait(lambda: 'Doneword' in terminal('b'), 'Blue tool response')
    red = next(r for r in snapshot()['registrations'] if r['name'] == 'Red')
    blue = next(r for r in snapshot()['registrations'] if r['name'] == 'Blue')
    tool('a', 'roster', {})
    wait(lambda: any(m['role'] == 'tool' and '(you)' in text(m) and 'Blue' in text(m) for body in seen for m in body['messages']), 'native-session attributed roster')
    time.sleep(.8)
    tool('a', 'send', {'to': blue['shortId'], 'body': 'Idleword peer payload'})
    wait(lambda: 'Idleword' in terminal('b') and 'streamed' in terminal('b'), 'idle peer live response')
    wait(outbox_empty, 'native Pi receipt for idle message')
    assert any('Peer message from Red [' + red['shortId'] + ']' in text(m) for body in seen for m in body['messages'])
    print('ID addressing, peer provenance, idle wake-up, live response, native receipt: passed.', flush=True)
    time.sleep(.8)
    prompt('b', 'PROBE_BUSY'); wait(busy.is_set, 'busy stream start')
    count = len(seen)
    tool('a', 'send', {'to': 'Blue', 'body': 'Queuedword peer payload'})
    wait(lambda: bool(snapshot()['pending']), 'busy message retained pending receipt')
    time.sleep(.7)
    assert not any('Queuedword' in text(m) and text(m).startswith('Peer message from') for body in seen[count:] for m in body['messages']), 'busy follow-up must not interrupt'
    release.set()
    wait(lambda: 'Queuedword' in terminal('b') and any('Queuedword' in text(m) and text(m).startswith('Peer message from') for body in seen for m in body['messages']), 'queued follow-up wake-up')
    wait(outbox_empty, 'busy native receipt')
    print('Busy recipient finished its turn before queued message; receipt cleared outbox: passed.', flush=True)
    time.sleep(.8)
    tool('b', 'hello', {'name': 'Azure'})
    wait(lambda: any(r['name'] == 'Azure' for r in snapshot()['registrations']), 'Azure rename')
    renamed = next(r for r in snapshot()['registrations'] if r['name'] == 'Azure')
    assert renamed['shortId'] == blue['shortId'] and 'Blue' in renamed['aliases']
    time.sleep(.8)
    tool('a', 'send', {'to': 'Blue', 'body': 'Aliasword peer payload'})
    wait(lambda: any('Aliasword' in text(m) and text(m).startswith('Peer message from') for body in seen for m in body['messages']), 'old-name delivery')
    wait(outbox_empty, 'old-name receipt')
    print('Rename retained immutable ID and old-name routing: passed.', flush=True)
    time.sleep(.8)
    tool('a', 'send', {'to': 'Azure', 'body': 'Forbiddeninterruptword', 'urgency': 'interrupt'})
    wait(lambda: any(m['role'] == 'tool' and 'Pi does not support interrupt' in text(m) for body in seen for m in body['messages']), 'explicit interrupt refusal')
    assert not any('Forbiddeninterruptword' in text(m) and 'Peer message from' in text(m) for body in seen for m in body['messages'])
    print('Unsupported interrupt refused without delivery: passed.', flush=True)
    receipts = json.loads((home / 'pi-receipts.json').read_text())
    blue_file = next(Path(r['file']) for r in receipts if r['session'] == renamed['endpoint']['session'])
    # Native quit runs the extension's shutdown hook; a crash falls back to its lease.
    os.write(masters['b'], b'\x04')
    try: second.wait(timeout=5)
    except subprocess.TimeoutExpired: pass
    stop_process(second)
    second = start('b-resumed', blue_file)
    wait(lambda: 'collab: Azure [' + blue['shortId'] + ']' in terminal('b-resumed'), 'native-session resume identity', 30)
    assert len(snapshot()['registrations']) == 2
    time.sleep(.8)
    tool('a', 'send', {'to': blue['shortId'], 'body': 'Resumeword peer payload'})
    wait(lambda: any('Resumeword' in text(m) and text(m).startswith('Peer message from') for body in seen for m in body['messages']), 'resumed-session wake-up')
    wait(outbox_empty, 'resumed-session receipt')
    print('Closing and resuming Pi preserved ID/name/aliases and restored push delivery: passed.', flush=True)
    time.sleep(.8)
    sockets = set(home.glob('pi-*.sock')); count = len(seen)
    prompt('b-resumed', '/reload')
    wait(lambda: len(set(home.glob('pi-*.sock'))) == 2 and set(home.glob('pi-*.sock')) != sockets, 'reload replaced transport')
    time.sleep(.8)
    assert len(snapshot()['registrations']) == 2 and len(seen) == count
    tool('a', 'send', {'to': blue['shortId'], 'body': 'Reloadword peer payload'})
    wait(lambda: any('Reloadword' in text(m) and text(m).startswith('Peer message from') for body in seen for m in body['messages']), 'reloaded-session push')
    wait(outbox_empty, 'reload receipt')
    print('Extension reload replaced its socket without invoking a model or changing identity: passed.', flush=True)
    time.sleep(.8)
    prompt('b-resumed', '/new')
    wait(lambda: len(snapshot()['registrations']) == 3, 'new native conversation registration')
    replacement = next(r for r in snapshot()['registrations'] if r['shortId'] not in [red['shortId'], blue['shortId']])
    tool('b-resumed', 'roster', {})
    wait(lambda: any(m['role'] == 'tool' and replacement['shortId'] + '] (you)' in text(m) and 'extension disconnected' in text(m) for body in seen for m in body['messages']), 'new-session attribution and disconnected prior conversation')
    time.sleep(.8)
    tool('a', 'send', {'to': replacement['shortId'], 'body': 'Newword peer payload'})
    wait(lambda: any('Newword' in text(m) and text(m).startswith('Peer message from') for body in seen for m in body['messages']), 'replacement-session push')
    wait(outbox_empty, 'new-session receipt')
    print('New conversation received a distinct ID; prior resumable identity stayed disconnected: passed.', flush=True)
except Exception:
    print('Private diagnostic artifacts:', root, flush=True)
    args.keep = True
    for name in terminals: (root / (name + '-terminal.txt')).write_text(terminal(name))
    raise
finally:
    release.set()
    for process in processes: stop_process(process)
    if daemon: stop_process(daemon)
    for master in masters.values():
        try: os.close(master)
        except OSError: pass
    provider.shutdown()
    if not args.keep: shutil.rmtree(root)
