#!/usr/bin/env python3
"""Offline example regression tests. Build CLI, RepoDoctor and EmbeddedV2 first."""
import contextlib
import http.server
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
CONFIGURATION = os.environ.get('QRE_EXAMPLE_CONFIGURATION', 'Debug')
QRE = Path(os.environ.get('QRE_BIN', ROOT / f'CodexFlow.QueryRuntime.Cli/bin/{CONFIGURATION}/net10.0/qre'))


class Model(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            body = bytearray()
            while True:
                size = int(self.rfile.readline().split(b';')[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                body.extend(self.rfile.read(size))
                self.rfile.read(2)
        else:
            body = self.rfile.read(int(self.headers['Content-Length']))
        request = json.loads(body)
        choice = request.get('tool_choice')
        name = choice.get('function', {}).get('name') if isinstance(choice, dict) else None
        if choice == 'required':
            name = request['tools'][0]['function']['name']
        delta = {'content': 'EXAMPLE_SMOKE_OK'}
        finish = 'stop'
        if name:
            delta = {'tool_calls': [{'index': 0, 'id': 'call_example', 'type': 'function',
                                    'function': {'name': name, 'arguments': '{}'}}]}
            finish = 'tool_calls'
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.end_headers()
        for item, reason in [(delta, None), ({}, finish)]:
            chunk = {'id': 'chatcmpl-example', 'object': 'chat.completion.chunk',
                     'created': 1, 'model': 'qwen3',
                     'choices': [{'index': 0, 'delta': item, 'finish_reason': reason}]}
            self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode())
        self.wfile.write(b'data: [DONE]\n\n')


class Examples(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='qre-example-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.workspace = self.root / 'workspace'
        self.workspace.mkdir()
        for name in ['a.py', 'b.py', 'a.js', 'b.js']:
            (self.workspace / name).write_text('fixture', encoding='utf-8')
        self.env = {k: v for k, v in os.environ.items() if not k.startswith('QRE_')}
        self.env.update(QRE_BIN=str(QRE), QRE_API_KEY='local-fixture', QRE_MODEL='qwen3',
                        QRE_API_URL='http://127.0.0.1:1/v1', QRE_API_MODE='chat-completions')

    def run_command(self, args, *, expected=0, request=None):
        result = subprocess.run([str(x) for x in args], cwd=ROOT, env=self.env,
                                input=json.dumps(request) if request else None,
                                capture_output=True, text=True, timeout=300)
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        return result.stdout

    @contextlib.contextmanager
    def model(self):
        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Model)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.env['QRE_API_URL'] = f'http://127.0.0.1:{server.server_port}/v1'
        try:
            yield
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_repodoctor_offline(self):
        output = self.run_command(['dotnet', ROOT / f'examples/RepoDoctor/bin/{CONFIGURATION}/net10.0/RepoDoctor.dll',
                                   '--offline', self.workspace])
        self.assertIn('replay digest:', output)
        self.assertIn('audit.v1.jsonl', output)

    def test_repodoctor_live_protocol(self):
        with self.model():
            output = self.run_command(['dotnet', ROOT / f'examples/RepoDoctor/bin/{CONFIGURATION}/net10.0/RepoDoctor.dll',
                                       self.workspace])
        self.assertIn('invoked tool: repodoctor_workspace_summary', output)
        self.assertIn('replay digest:', output)

    def test_echo_tool(self):
        manifest = json.loads((ROOT / 'examples/ExternalTools/echo_tool.manifest.json').read_text())
        manifest['command'] = sys.executable
        manifest['args'] = [str(ROOT / 'examples/ExternalTools/echo_tool.py')]
        path = self.root / 'echo.json'
        path.write_text(json.dumps(manifest))
        self.run_command([QRE, 'tool', 'register', '--workspace', self.workspace, '--manifest', path])
        output = self.run_command([QRE, 'tool', 'invoke', '--workspace', self.workspace, '--name', 'demo_echo_tool',
                                   '--arguments', '{"message":"ECHO_OK"}', '--json'])
        self.assertEqual(json.loads(json.loads(output)['result'])['message'], 'ECHO_OK')
        with self.model():
            output = self.run_command([QRE, 'run', '--workspace', self.workspace, '--profile', 'readonly',
                                       '--external', '--required-tool', 'demo_echo_tool',
                                       '--approve-risk', 'Reviewed echo fixture', '--json', 'Echo'])
        self.assertEqual(json.loads(output)['totalToolCalls'], 1)

    def test_python_doctor_v2(self):
        with self.model():
            output = self.run_command([sys.executable, ROOT / 'examples/PythonToolDoctor/doctor.py', self.workspace])
        self.assertIn('Verified required tool run by strict replay: qre_list_files', output)

    def test_embedded_v2(self):
        output = self.run_command(['dotnet', ROOT / f'examples/EmbeddedV2/bin/{CONFIGURATION}/net10.0/EmbeddedV2.dll'])
        self.assertIn('status: Completed', output)

    def test_external_functions(self):
        for interpreter, folder, name, extension in [
            (sys.executable, 'PythonFunctionTools', 'py_count_files', '.py'),
            ('node', 'NodeFunctionTools', 'node_count_files', '.js')]:
            with self.subTest(name=name):
                script = ROOT / 'examples' / folder / ('repo_tools.py' if interpreter == sys.executable else 'repo_tools.mjs')
                manifests = self.root / name
                self.run_command([interpreter, script, '--manifest-dir', manifests])
                manifest = manifests / (name + '.json')
                self.assertEqual(json.loads(manifest.read_text())['inputSchema']['properties']['max_files']['type'], 'integer')
                self.run_command([QRE, 'tool', 'register', '--workspace', self.workspace, '--manifest', manifest])
                output = self.run_command([QRE, 'tool', 'invoke', '--workspace', self.workspace, '--name', name,
                                           '--arguments', json.dumps({'extension': extension, 'max_files': 1}), '--json'])
                self.assertEqual(json.loads(json.loads(output)['result'])['count'], 1)
                with self.model():
                    args = [QRE, 'run', '--workspace', self.workspace, '--profile', 'readonly', '--external',
                            '--required-tool', name, '--trace-data', 'sanitized', '--json']
                    denied = json.loads(self.run_command(args + ['Count files'], expected=1))
                    self.assertNotEqual(denied['status'], 'Completed')
                    output = self.run_command(args + ['--approve-risk', 'Reviewed local test fixture', 'Count files'])
                    self.assertEqual(json.loads(output)['totalToolCalls'], 1)

    def test_workspace_boundaries(self):
        outside = self.root / 'outside'
        outside.mkdir()
        (outside / 'fixture.txt').write_text('OUTSIDE_FIXTURE')
        (self.workspace / 'fixture.txt').write_text('INSIDE_FIXTURE')
        request = {'name': 'py_read_text_file', 'workspacePath': str(self.workspace),
                   'arguments': {'workspace_path': str(outside), 'path': 'fixture.txt'}}
        output = self.run_command([sys.executable, ROOT / 'examples/PythonFunctionTools/repo_tools.py'], request=request)
        self.assertEqual(json.loads(output)['result']['text'], 'INSIDE_FIXTURE')
        try:
            (self.workspace / 'link.txt').symlink_to(outside / 'fixture.txt')
        except OSError:
            self.skipTest('Symlink creation unavailable')
        for interpreter, script, name in [
            ('node', 'NodeFunctionTools/repo_tools.mjs', 'node_read_text_file'),
            (sys.executable, 'PythonFunctionTools/repo_tools.py', 'py_read_text_file')]:
            request = {'name': name, 'workspacePath': str(self.workspace), 'arguments': {'path': 'link.txt'}}
            self.run_command([interpreter, ROOT / 'examples' / script], request=request, expected=1)


if __name__ == '__main__':
    unittest.main(verbosity=2)
