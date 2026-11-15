#!/usr/bin/env python3
"""Run against the configured local model endpoint using disposable workspaces."""
import errno
import os
from pathlib import Path
import pty
import select
import shutil
import subprocess
import tempfile
import time

base = Path(__file__).resolve().parent.parent / 'examples'
scratch = Path(tempfile.mkdtemp(prefix='harness-model-e2e-'))
try:
    workspace = scratch / 'workspace'
    memory = scratch / 'memory'
    workspace.mkdir()
    memory.mkdir()
    os.environ['TMPDIR'] = str(scratch)
    cases = [
        ('03-files-harness.cs', workspace, 'NO', 'denied.txt'),
        ('03-files-harness.cs', workspace, 'YES', 'approved.txt'),
        ('04-memory-harness.cs', memory, 'YES', None),
    ]
    for example, folder, decision, name in cases:
        print(f'\n=== {example}: {decision} ===', flush=True)
        master, slave = pty.openpty()
        args = ['dotnet', 'run', str(base / example), '--']
        args += [str(folder)] if name else ['--memory', str(folder)]
        process = subprocess.Popen(args, stdin=slave, stdout=slave, stderr=slave)
        os.close(slave)
        buffer = ''
        transcript = ''
        sent = False
        count = 0
        deadline = time.monotonic() + 300
        try:
            while time.monotonic() < deadline:
                ready, _, _ = select.select([master], [], [], 1)
                if ready:
                    try:
                        data = os.read(master, 65536).decode(errors='replace')
                    except OSError as exc:
                        if exc.errno == errno.EIO:
                            break
                        raise
                    if not data:
                        break
                    print(data, end='', flush=True)
                    transcript += data
                    buffer += data
                    if not sent and 'You (empty to exit):' in buffer:
                        prompt = (f'Use the file tool to write exactly the text hello harness into {name}. Do not read or list files.' if name else
                                  'Remember in persistent memory: my favorite fictional color is teal. Use the memory tools to store it now.')
                        os.write(master, (prompt + '\n').encode())
                        sent = True
                        buffer = ''
                    elif 'Type YES:' in buffer:
                        if name:
                            assert not (folder / name).exists(), 'file changed before approval'
                        os.write(master, (decision + '\n').encode())
                        count += 1
                        buffer = ''
                    elif sent and 'You (empty to exit):' in buffer:
                        os.write(master, b'\n')
                        buffer = ''
                if process.poll() is not None:
                    break
            if process.poll() is None:
                raise TimeoutError(f'{example} did not complete')
            assert process.returncode == 0, f'{example} exited {process.returncode}'
            assert count > 0, 'no approval was requested'
            if name:
                assert 'Requested: file_access_write' in transcript
                if decision == 'NO':
                    assert not (folder / name).exists(), 'denied write occurred'
                else:
                    assert (folder / name).read_text(encoding='utf-8-sig') == 'hello harness'
            else:
                contents = [p.read_text(encoding='utf-8-sig') for p in folder.rglob('*') if p.is_file()]
                assert any('teal' in c.lower() for c in contents), 'expected memory not persisted'
            print(f'PASS: {count} approval(s)', flush=True)
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
            os.close(master)
finally:
    shutil.rmtree(scratch)
