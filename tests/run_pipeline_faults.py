#!/usr/bin/env python3
"""POSIX integration faults: disk-full-style write failure and SIGINT while stages run.

Use a RAW large enough for multiple batches (e.g. a network file). Output is local
and temporary. RLIMIT_FSIZE is per child; no filesystem or machine limits change.
For a small fixture whose output stays buffered, --cancel-trigger temp-create
signals after the writer creates its temporary file instead of its first write.
"""
import argparse
import pathlib
import signal
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--candidate', type=pathlib.Path, required=True)
parser.add_argument('--raw', type=pathlib.Path, required=True)
parser.add_argument('--cancel-trigger', choices=['first-write', 'temp-create'], default='first-write',
                    help='Test-only SIGINT trigger: first nonempty temporary output (default), '
                         'or creation of the temporary output for small buffered fixtures')
args = parser.parse_args()
try:
    import resource
except ImportError:
    raise SystemExit('SKIP: resource limits and Unix signals require POSIX; queue failure tests run everywhere')
binary = args.candidate.resolve()
command = (['dotnet', str(binary)] if binary.suffix == '.dll' else [str(binary)]) + [str(args.raw.resolve())]
with tempfile.TemporaryDirectory(prefix='pioneer-faults-') as tmp:
    output = pathlib.Path(tmp)
    final = output / (args.raw.stem + '.arrow')
    sentinel = b'Existing output must survive a failed replacement.'
    final.write_bytes(sentinel)
    command += ['-o', str(output), '-t', '3', '--scan-chunk-size', '17', '-b', '257']
    def limit_file_size():
        signal.signal(signal.SIGXFSZ, signal.SIG_IGN)
        resource.setrlimit(resource.RLIMIT_FSIZE, (131072, 131072))
    failed = subprocess.run(command, preexec_fn=limit_file_size, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, timeout=60)
    assert failed.returncode > 0, (failed.returncode, failed.stdout)
    assert 'Conversion failed:' in failed.stdout and 'Execution Time:' not in failed.stdout, failed.stdout
    assert final.read_bytes() == sentinel, 'Write failure replaced existing output'
    assert not list(output.glob('*.tmp')), 'Write failure left partial output'
    print('PASS: injected write failure exits nonzero, joins stages and preserves existing output.')
    def cancellation_ready():
        for path in output.glob('*.tmp'):
            try:
                size = path.stat().st_size
            except FileNotFoundError:
                continue  # Conversion may publish/remove a file between glob and stat.
            if args.cancel_trigger == 'temp-create' or size > 0:
                return True
        return False

    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    deadline = time.monotonic() + 30
    while process.poll() is None and time.monotonic() < deadline:
        if cancellation_ready():
            process.send_signal(signal.SIGINT)
            break
        time.sleep(.01)
    else:
        if process.poll() is None:
            process.kill()
        process.communicate()
        raise AssertionError('Conversion ended or timed out before cancellation could be sent; '
                             'use a larger RAW or --cancel-trigger temp-create for a small buffered fixture')
    log, _ = process.communicate(timeout=30)
    assert process.returncode == 130, (process.returncode, log)
    assert 'Conversion cancelled.' in log and 'Execution Time:' not in log, log
    assert final.read_bytes() == sentinel, 'Cancellation replaced existing output'
    assert not list(output.glob('*.tmp')), 'Cancellation left partial output'
    print(f'PASS: SIGINT ({args.cancel_trigger}) exits130, joins stages, removes partial output and preserves existing output.')
