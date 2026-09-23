#!/usr/bin/env python3
"""Profile one cold-client RAW conversion. Diagnostic timings include profiler overhead.

Managed samples describe sampled thread time, not pure on-CPU attribution.
Correlate them with process CPU, native blocked stacks, GC and contention events.
"""
import argparse
import json
import os
from pathlib import Path
import platform
import re
import signal
import subprocess
import sys
import time
import uuid

from measure_run import interface_counters
from run_cold_compare import binary_identity, cache_operation, command, save
from run_cold_sweep import evict_with_retries, identity

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
PROVIDERS = ('Microsoft-Windows-DotNETRuntime:0x4003C039:5,'
             'System.Threading.Tasks.TplEventSource:0xFFFFFFFFFFFFFFFF:5')


def stop(process, sig=signal.SIGTERM):
    try:
        os.killpg(process.pid, sig)
    except ProcessLookupError:
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--cache-helper', type=Path, required=True)
    parser.add_argument('--raw', type=Path, required=True)
    parser.add_argument('--reference-arrow', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--threads', type=int, default=3)
    parser.add_argument('--chunk', type=int, default=128)
    parser.add_argument('--batch', type=int, default=1000)
    parser.add_argument('--interface', default='en0')
    parser.add_argument('--trace-tool', type=Path, default=Path('/private/tmp/pioneer-profiler-tools/dotnet-trace'))
    parser.add_argument('--counters-tool', type=Path, default=Path('/private/tmp/pioneer-profiler-tools/dotnet-counters'))
    parser.add_argument('--sample-tool', type=Path, default=Path('/usr/bin/sample'))
    parser.add_argument('--comparator', type=Path, default=ROOT / 'tests/ArrowCompare/bin/Release/net8.0/ArrowCompare.dll')
    parser.add_argument('--native-duration', type=int, default=6)
    parser.add_argument('--native-interval-ms', type=int, default=5)
    parser.add_argument('--attach-delay', type=float, default=.5)
    parser.add_argument('--timeout', type=float, default=180)
    parser.add_argument('--skip-native', action='store_true')
    parser.add_argument('--skip-counters', action='store_true')
    args = parser.parse_args()
    if os.name != 'posix' or not hasattr(os, 'wait4'):
        parser.error('This profiling harness requires POSIX wait4 and process groups')
    if min(args.threads, args.chunk, args.batch, args.native_duration, args.native_interval_ms, args.timeout) <= 0 or args.attach_delay < 0:
        parser.error('Counts/durations must be positive; attach delay must be nonnegative')
    for path in [args.candidate, args.cache_helper, args.trace_tool, args.comparator, args.reference_arrow]:
        if not path.is_file():
            parser.error(f'Required tool/reference missing: {path}')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    raw = Path(os.path.abspath(args.raw))
    diagnostic_port = Path('/private/tmp') / ('pc-diag-' + uuid.uuid4().hex[:16] + '.sock')
    environment = dict(os.environ)
    converter_environment = dict(environment, DOTNET_DiagnosticPorts=f'{diagnostic_port},connect,suspend')
    converter_command = command(args.candidate) + [str(raw), '-o', str(output / 'arrow_out'),
        '--threads-per-file', str(args.threads), '--scan-chunk-size', str(args.chunk), '--batch-size', str(args.batch)]
    manifest = dict(status='running', platform=platform.platform(), raw=str(raw), reference_arrow=str(args.reference_arrow.resolve()),
        candidate=binary_identity(args.candidate), command=converter_command, collectors={},
        timing_note='DIAGNOSTIC ONLY: startup diagnostic-port suspension and concurrent profilers add overhead; these elapsed/CPU/RSS numbers are not clean benchmark results.',
        sample_note='The .NET 8 profile is named cpu-sampling, but sampled thread-time stacks include waiting/suspended threads. Do not equate topN weights to on-CPU time.',
        providers=PROVIDERS, network_note='Interface deltas include all processes and protocol overhead; server cache remains uncontrolled.')
    save(output / 'profile.json', manifest)
    processes = {}
    handles = []
    converter = None
    reaped = False

    def launch_collector(name, argv):
        log = (output / (name + '.log')).open('wb')
        handles.append(log)
        process = subprocess.Popen([str(value) for value in argv], stdout=log, stderr=subprocess.STDOUT,
                                   env=environment, start_new_session=True)
        processes[name] = process
        manifest['collectors'][name] = dict(command=[str(v) for v in argv], pid=process.pid, started_monotonic=time.monotonic())
        return process

    try:
        # Start EventPipe listening before launching the converter, so its runtime
        # waits for tracing to attach before running managed code or opening RAW.
        trace = launch_collector('managed-trace', [args.trace_tool, 'collect', '--diagnostic-port', diagnostic_port,
            '--profile', 'cpu-sampling', '--providers', PROVIDERS, '--buffersize', '64',
            '--output', output / 'managed.nettrace', '--format', 'NetTrace'])
        eviction = evict_with_retries(args.cache_helper.resolve(), raw, output, args.timeout, manifest)
        before_identity = identity(eviction)
        before_network = interface_counters(args.interface)
        if before_network is None:
            raise RuntimeError('Interface counters unavailable; run with the required permissions and correct interface')
        converter_log = (output / 'converter.log').open('wb')
        handles.append(converter_log)
        started = time.monotonic()
        converter = subprocess.Popen(converter_command, stdout=converter_log, stderr=subprocess.STDOUT,
                                     env=converter_environment, start_new_session=True)
        manifest['converter_pid'] = converter.pid
        save(output / 'profile.json', manifest)
        (output / 'converter.pid').write_text(str(converter.pid) + '\n')
        attached = False
        while True:
            pid, status, usage = os.wait4(converter.pid, os.WNOHANG)
            if pid:
                converter.returncode = os.waitstatus_to_exitcode(status)
                reaped = True
                break
            if time.monotonic() - started > args.timeout:
                raise TimeoutError('Profiled converter timed out')
            if not attached and time.monotonic() - started >= args.attach_delay:
                attached = True
                if not args.skip_counters and args.counters_tool.is_file():
                    launch_collector('runtime-counters', [args.counters_tool, 'collect', '--process-id', converter.pid,
                        '--counters', 'System.Runtime', '--refresh-interval', '1', '--format', 'json',
                        '--output', output / 'runtime-counters.json'])
                else:
                    manifest['collectors']['runtime-counters'] = dict(status='skipped or unavailable')
                if not args.skip_native and args.sample_tool.is_file() and sys.platform == 'darwin':
                    launch_collector('native-sample', [args.sample_tool, converter.pid, args.native_duration,
                        args.native_interval_ms, '-mayDie', '-file', output / 'native-sample.txt'])
                else:
                    manifest['collectors']['native-sample'] = dict(status='skipped or unavailable')
            time.sleep(.02)
        elapsed = time.monotonic() - started
        after_network = interface_counters(args.interface)
        received = after_network[0] - before_network[0] if after_network else None
        metrics = dict(converter_pid=converter.pid, exit_code=converter.returncode, elapsed_s=elapsed,
            user_s=usage.ru_utime, system_s=usage.ru_stime,
            peak_rss_bytes=usage.ru_maxrss * (1 if sys.platform == 'darwin' else 1024),
            input_block_operations=usage.ru_inblock, output_block_operations=usage.ru_oublock,
            network_interface=args.interface, interface_received_bytes=received,
            interface_sent_bytes=after_network[1] - before_network[1] if after_network else None,
            measurement_role='profiled diagnostic run; profiler overhead included; not an unprofiled benchmark')
        manifest['metrics'] = metrics
        save(output / 'metrics.json', metrics)
        for name, process in processes.items():
            try:
                code = process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                stop(process, signal.SIGINT)
                try:
                    code = process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    stop(process, signal.SIGKILL)
                    code = process.wait()
            manifest['collectors'][name]['exit_code'] = code
        if converter.returncode:
            raise RuntimeError('Profiled conversion failed; see converter.log')
        log = (output / 'converter.log').read_text(errors='replace')
        metrics['pipeline_files'] = [{key: float(value) for key, value in re.findall(r'(\w+)=([0-9.]+)', line)}
                                     for line in log.splitlines() if line.startswith('PERF ')]
        save(output / 'metrics.json', metrics)
        if 'falling back' in log.lower():
            raise RuntimeError('Requested scan worker count fell back; profile is not the requested configuration')
        if received is None or received < .75 * eviction['size_bytes']:
            raise RuntimeError('Insufficient interface receive evidence for a cold network profile')
        after = cache_operation(args.cache_helper.resolve(), 'inspect', raw, output / 'post-run-cache.json', args.timeout)
        if not after['metadata_unchanged'] or identity(after) != before_identity:
            raise RuntimeError('RAW identity/size/mtime changed')
        comparison = subprocess.run(command(args.comparator) + [str(args.reference_arrow.resolve()), str(output / 'arrow_out' / (raw.stem + '.arrow'))],
                                    capture_output=True, text=True, timeout=args.timeout)
        (output / 'comparison.log').write_text(comparison.stdout + comparison.stderr)
        manifest['arrow_equal'] = comparison.returncode == 0
        if comparison.returncode:
            raise RuntimeError('Profiled Arrow output differs from the reference')
        for mode in ['exclusive', 'inclusive']:
            report = command(args.trace_tool) + ['report', str(output / 'managed.nettrace'), 'topN', '-n', '60', '-v']
            if mode == 'inclusive':
                report.append('--inclusive')
            result = subprocess.run(report, capture_output=True, text=True, timeout=args.timeout)
            (output / f'managed-sampled-thread-time-{mode}.txt').write_text(result.stdout + result.stderr)
            manifest['collectors']['managed-trace'][mode + '_report_exit_code'] = result.returncode
        failures = [name for name, result in manifest['collectors'].items() if result.get('exit_code', 0) != 0]
        if failures or not (output / 'managed.nettrace').is_file():
            raise RuntimeError(f'Conversion matched but profiling is incomplete; inspect collector logs: {failures}')
        manifest['status'] = 'complete'
        print(f'Profile complete: {output}; PID {converter.pid}; Arrow exact. Timings include profiling overhead.')
        return 0
    except (Exception, KeyboardInterrupt) as error:
        manifest['status'] = 'failed'
        manifest['failure'] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        if converter is not None and not reaped:
            stop(converter, signal.SIGTERM)
            stop(converter, signal.SIGKILL)
            try:
                _, status, _ = os.wait4(converter.pid, 0)
                converter.returncode = os.waitstatus_to_exitcode(status)
            except ChildProcessError:
                pass
        for process in processes.values():
            if process.poll() is None:
                stop(process, signal.SIGINT)
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    stop(process, signal.SIGKILL)
                    process.wait()
        for handle in handles:
            handle.close()
        diagnostic_port.unlink(missing_ok=True)
        save(output / 'profile.json', manifest)


if __name__ == '__main__':
    sys.exit(main())
