"""Real Pi/OC2 TUI check, invoked by check-oc2.py --mode pi."""
import fcntl, http.server, json, os, pty, signal, sqlite3, struct, subprocess, termios, threading, time
from pathlib import Path

def run(root, env, api, session, oc2, server_url, password):
    root = Path(root); repo = Path(__file__).resolve().parent.parent
    config = root / 'pi-config'; config.mkdir()
    terminals = {}; processes = []; masters = []; requests = []; plan = []
    class Provider(http.server.BaseHTTPRequestHandler):
        def log_message(self, *_): pass
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers['Content-Length']))); requests.append(body)
            if plan:
                verb, arguments = plan.pop(0)
                delta = {'tool_calls': [{'index': 0, 'id': 'call_' + str(len(requests)), 'type': 'function', 'function': {'name': 'collab_' + verb, 'arguments': json.dumps(arguments)}}]}; finish = 'tool_calls'; words = []
            else: delta = {'role': 'assistant'}; finish = 'stop'; words = ['Piword', 'response', 'streamed', 'live.']
            self.send_response(200); self.send_header('Content-Type', 'text/event-stream'); self.send_header('Connection', 'close'); self.end_headers()
            def emit(value, reason=None):
                chunk = {'id': 'probe', 'object': 'chat.completion.chunk', 'created': int(time.time()), 'model': 'mock', 'choices': [{'index': 0, 'delta': value, 'finish_reason': reason}]}
                self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode()); self.wfile.flush()
            try:
                emit(delta)
                for word in words: emit({'content': word + ' '}); time.sleep(.15)
                emit({}, finish); self.wfile.write(b'data: [DONE]\n\n'); self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError): pass
    provider = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Provider); provider.daemon_threads = True
    threading.Thread(target=provider.serve_forever, daemon=True).start()
    (config / 'models.json').write_text(json.dumps({'providers': {'mock': {'baseUrl': f'http://127.0.0.1:{provider.server_port}/v1', 'api': 'openai-completions', 'apiKey': 'local-probe', 'models': [{'id': 'mock', 'reasoning': False, 'input': ['text'], 'contextWindow': 32000, 'maxTokens': 1024}]}}}))
    def launch(name, command, local):
        master, slave = pty.openpty(); fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 40, 140, 0, 0))
        process = subprocess.Popen(command, cwd=root, env=local, stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
        os.close(slave); processes.append(process); masters.append(master); terminals[name] = []
        def drain():
            try:
                while True:
                    chunk = os.read(master, 65536); terminals[name].append(chunk.decode(errors='replace'))
                    if b'\x1b[6n' in chunk: os.write(master, b'\x1b[1;1R')
            except OSError: pass
        threading.Thread(target=drain, daemon=True).start()
        return master
    def snapshot():
        with sqlite3.connect('file:' + env['COLLAB_MCP_HOME'] + '/state.sqlite?mode=ro', uri=True) as db:
            return json.loads(db.execute('SELECT payload FROM state WHERE id=1').fetchone()[0])
    def wait(predicate, label):
        deadline = time.monotonic() + 25
        while time.monotonic() < deadline:
            if predicate(): return
            time.sleep(.1)
        raise AssertionError(label)
    try:
        local = dict(env, PI_CODING_AGENT_DIR=str(config), PI_OFFLINE='1', PI_TELEMETRY='0', TERM='xterm-256color', COLLAB_MCP_BINARY=str(repo / 'src/Collab.Shim/bin/Release/net10.0/collab-mcp'))
        for key in list(local):
            if key.endswith(('_API_KEY', '_AUTH_TOKEN', '_OAUTH_TOKEN')): local.pop(key)
        pi_master = launch('pi', ['pi', '--offline', '--no-extensions', '-e', str(repo / 'integrations/pi/index.ts'), '--no-skills', '--no-prompt-templates', '--no-context-files', '--no-builtin-tools', '--provider', 'mock', '--model', 'mock', '--thinking', 'off'], local)
        launch('oc2', [oc2, '--server', server_url, '--session', session, str(root)], dict(env, TERM='xterm-256color', OPENCODE_SERVER_PASSWORD=password, OPENCODE_PASSWORD=password))
        wait(lambda: len(snapshot()['registrations']) == 2, 'Pi/OC2 automatic shared roster')
        assert not requests, 'Pi registration must not run a model'
        pi_peer = next(r for r in snapshot()['registrations'] if r['endpoint']['harness'] == 'pi')
        oc2_peer = next(r for r in snapshot()['registrations'] if r['name'] == 'Red')
        plan.extend([('roster', {}), ('send', {'to': oc2_peer['shortId'], 'body': 'Pi to OC2 payload'})])
        os.write(pi_master, b'Check the roster, send your peer message, then end your turn.\r')
        wait(lambda: 'Pi to OC2 payload' in json.dumps(api('GET', f'/api/session/{session}/message?limit=100')), 'Pi to OC2 delivery')
        wait(lambda: 'OC2Piword' in ''.join(terminals['oc2']) and 'streamed' in ''.join(terminals['oc2']), 'idle OC2 live response')
        wait(lambda: not snapshot()['pending'], 'Pi to OC2 receipt')
        assert any(m.get('role') == 'tool' and '(you)' in str(m.get('content')) and 'Red' in str(m.get('content')) for body in requests for m in body['messages'])
        time.sleep(1)
        api('POST', f'/api/session/{session}/prompt', {'text': 'pi send ' + json.dumps({'to': pi_peer['shortId'], 'body': 'OC2 to Pi payload'})})
        wait(lambda: any('OC2 to Pi payload' in json.dumps(body['messages']) for body in requests), 'OC2 to idle Pi wake-up')
        wait(lambda: 'Agent Red [' + oc2_peer['shortId'] + ']' in ''.join(terminals['pi']), 'distinct peer renderer')
        wait(lambda: not snapshot()['pending'], 'OC2 to Pi native receipt')
        assert all(process.poll() is None for process in processes)
        print('Real Pi/OC2 TUIs: automatic shared roster, native attribution, both delivery directions, idle wake-up, peer rendering, streamed output, positive receipts: passed.', flush=True)
    finally:
        for name, chunks in terminals.items(): (root / (name + '-cross-terminal.txt')).write_text(''.join(chunks))
        for process in processes:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try: process.wait(timeout=5)
                except subprocess.TimeoutExpired: os.killpg(process.pid, signal.SIGKILL); process.wait()
        for master in masters: os.close(master)
        provider.shutdown()
