#!/usr/bin/env python3
"""End-to-end RAW/Arrow regression checks; optional pre-change binary is the oracle."""
import argparse
import pathlib
import re
import shutil
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]

def run(command, success=True, timeout=120):
    result = subprocess.run([str(x) for x in command], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, timeout=timeout)
    if (result.returncode == 0) != success:
        raise AssertionError(f"Unexpected status {result.returncode}: {command}\n{result.stdout}")
    return result.stdout

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', default=ROOT / 'bin/Release/net8.0/PioneerConverter.dll', type=pathlib.Path)
    parser.add_argument('--baseline', type=pathlib.Path, help='Original converter DLL or executable; strongly recommended')
    parser.add_argument('--raw', default=ROOT / 'tests/fixtures/raw/smoke.raw', type=pathlib.Path)
    options = parser.parse_args()
    def command(path):
        return ['dotnet', path.resolve()] if path.suffix.lower() == '.dll' else [path.resolve()]
    candidate = command(options.candidate)
    baseline = command(options.baseline) if options.baseline else candidate
    compare_project = ROOT / 'tests/ArrowCompare/ArrowCompare.csproj'
    run(['dotnet', 'build', compare_project, '-c', 'Release', '--nologo'])
    compare = ['dotnet', ROOT / 'tests/ArrowCompare/bin/Release/net8.0/ArrowCompare.dll']
    raw = options.raw.resolve()
    with tempfile.TemporaryDirectory(prefix='pioneer-regression-') as tmp:
        work = pathlib.Path(tmp)
        def convert(binary, out, threads, chunk, batch, source=raw):
            log = run(binary + [source, '-o', out, '-t', str(threads), '--scan-chunk-size', str(chunk), '-b', str(batch)])
            if 'falling back' in log.lower():
                raise AssertionError('Requested worker path fell back to serial: ' + log)
            return log
        reference = work / 'reference'
        convert(baseline, reference, 1, 17, 101)
        oracle = reference / (raw.stem + '.arrow')
        if options.baseline:
            parallel_reference = work / 'parallel-reference'
            convert(baseline, parallel_reference, 3, 17, 101)
            print(run(compare + [oracle, parallel_reference / oracle.name]).strip())
        for threads, chunk, batch in [(1,1,1), (1,23,17), (2,3,7), (3,17,23), (8,1024,10000)]:
            out = work / f't{threads}-c{chunk}-b{batch}'
            convert(candidate, out, threads, chunk, batch)
            print(f'threads={threads} chunk={chunk} batch={batch}: ' + run(compare + [oracle, out / oracle.name]).strip())
        for flag in ['--concurrent-files', '-n']:
            for extra in [[], ['--help'], ['--version'], [str(raw)]]:
                log = run(candidate + extra + [flag, '1'], success=False)
                if flag not in log or 'option' not in log.lower():
                    raise AssertionError('Removed flag lacks clear CLI error: ' + log)
        for removed in ['--concurrent-files=1', '-n=1']:
            log = run(candidate + [removed], success=False)
            assert removed in log and 'option' in log.lower(), log
        assert '--concurrent-files' not in run(candidate + ['--help'])
        source_dir = work / 'directory'
        source_dir.mkdir()
        for name in ['first.raw', 'second.raw']:
            shutil.copyfile(raw, source_dir / name)
        directory_out = work / 'directory-out'
        log = convert(candidate, directory_out, 3, 17, 23, source_dir)
        events = re.findall(r'Starting Conversion For: ([^\n]+)|Execution Time: [^\n]+ for ([^\n]+)', log)
        if len(events) != 4 or not events[0][0] or not events[1][1] or not events[2][0] or not events[3][1]:
            raise AssertionError('Directory conversions overlap or failed to complete: ' + log)
        for name in ['first.arrow', 'second.arrow']:
            run(compare + [oracle, directory_out / name])
        before = {p.name: p.stat().st_mtime_ns for p in directory_out.glob('*.arrow')}
        run(candidate + [source_dir, '-o', directory_out, '--skip-existing'])
        assert before == {p.name: p.stat().st_mtime_ns for p in directory_out.glob('*.arrow')}
        corrupt = work / 'corrupt.raw'
        corrupt.write_bytes(b'This is not a RAW file.\n')
        log = run(candidate + [corrupt, '-o', work / 'corrupt-out'], success=False)
        if 'Execution Time:' in log:
            raise AssertionError('Corrupt input reported success: ' + log)
        blocked_out = work / 'blocked-out'
        blocked_out.mkdir()
        (blocked_out / oracle.name).mkdir()
        log = run(candidate + [raw, '-o', blocked_out], success=False)
        if 'Execution Time:' in log:
            raise AssertionError('Output failure reported success: ' + log)
        assert not list(work.rglob('*.tmp')), 'Failure left partial output'
        failing_dir = work / 'failing-directory'
        failing_dir.mkdir()
        (failing_dir / '01-invalid.raw').write_bytes(b'Not a RAW file.')
        shutil.copyfile(raw, failing_dir / '02-valid.raw')
        failing_out = work / 'failing-directory-out'
        log = run(candidate + [failing_dir, '-o', failing_out], success=False)
        assert 'Starting Conversion For: 01-invalid' in log, log
        assert 'Starting Conversion For: 02-valid' not in log, log
        assert not (failing_out / '02-valid.arrow').exists(), 'Opened subsequent file after failure'
        assert not list(failing_out.glob('*.tmp')), 'Failed directory left partial output'
        print('PASS: removed CLI flags, sequential directory completion, stop-on-failure, skip-existing, invalid input and output failure.')
    if not options.baseline:
        print('NOTE: no pre-change oracle supplied; only candidate worker/boundary consistency checked.')

if __name__ == '__main__':
    main()
