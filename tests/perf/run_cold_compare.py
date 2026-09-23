#!/usr/bin/env python3
"""Paired same-file cold-client-cache comparison, verified before every conversion.

Requires cache_file.c's macOS helper and prebuilt converters/ArrowCompare. No RAW
is copied by this driver. Network mode requires aggregate interface receive
evidence; local mode disables that network-only gate. Server caches remain
uncontrolled in network mode.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import random
import signal
import statistics
import subprocess
import sys
import time

from cache_control import cache_operation, evict_with_retries, require_cold, save

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def command(binary):
    path = str(Path(binary).resolve())
    return ['dotnet', path] if path.lower().endswith('.dll') else [path]


def wait_measured_process(process, timeout):
    try:
        return process.wait(timeout=timeout)
    except BaseException:
        # The wrapper can exit before its converter. Always terminate the
        # remaining process group so no RAW reader survives a failed run.
        if os.name == 'posix':
            def stop_group(sig):
                try:
                    os.killpg(process.pid, sig)
                except ProcessLookupError:
                    pass
            stop_group(signal.SIGTERM)
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                pass
            finally:
                stop_group(signal.SIGKILL)
        else:
            process.kill()
        process.wait()
        raise


def binary_identity(path):
    path = path.resolve()
    identity = {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
    # .NET apphosts can be identical while the adjacent managed code changes.
    assembly = path.with_suffix('.dll')
    if path.suffix.lower() != '.dll' and assembly.is_file():
        identity['managed_assembly'] = {'path': str(assembly), 'sha256': hashlib.sha256(assembly.read_bytes()).hexdigest()}
    return identity


def schedule(raws, pairs, seed):
    randomizer = random.Random(seed)
    planned = []
    for pair in range(pairs):
        files = list(enumerate(raws))
        randomizer.shuffle(files)
        for index, raw in files:
            # Balance start order between files and alternate it across pairs.
            order = ['original', 'candidate'] if (pair + index) % 2 == 0 else ['candidate', 'original']
            for variant in order:
                planned.append(dict(pair=pair + 1, file_index=index + 1,
                                    raw=str(raw), variant=variant,
                                    tag=f'pair{pair + 1:02d}-file{index + 1:02d}-{variant}'))
    return planned


def summaries(runs):
    grouped = {}
    for run in runs:
        if run.get('accepted') and run.get('arrow_equal'):
            grouped.setdefault(run['raw'], {}).setdefault(run['pair'], {})[run['variant']] = run['metrics']
    result = []
    for raw, pairs in grouped.items():
        complete = [(number, pair) for number, pair in sorted(pairs.items()) if len(pair) == 2]
        ratios = [p['candidate']['elapsed_s'] / p['original']['elapsed_s'] for _, p in complete]
        variants = {}
        for variant in ['original', 'candidate']:
            values = [pair[variant] for _, pair in complete]
            stage_names = ['extraction_ms', 'assembly_ms', 'writing_ms', 'elapsed_ms']
            variants[variant] = {
                'elapsed_s': [v['elapsed_s'] for v in values],
                'median_elapsed_s': statistics.median([v['elapsed_s'] for v in values]) if values else None,
                'median_peak_rss_bytes': statistics.median([v['peak_rss_bytes'] for v in values]) if values else None,
                'interface_received_bytes': [v['interface_received_bytes'] for v in values],
                'median_stages_ms': {name: statistics.median([v['pipeline_files'][0][name] for v in values])
                                     for name in stage_names if values and all(v['pipeline_files'] and name in v['pipeline_files'][0] for v in values)}
            }
        result.append(dict(raw=raw, complete_pairs=len(complete), variants=variants,
                           paired_elapsed_ratios=ratios,
                           median_paired_elapsed_ratio=statistics.median(ratios) if ratios else None,
                           geometric_mean_paired_elapsed_ratio=math.exp(statistics.mean(map(math.log, ratios))) if ratios else None))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--original', type=Path, required=True)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--cache-helper', type=Path, required=True)
    parser.add_argument('--raw', type=Path, action='append', required=True, help='Repeat for two or three representative RAWs')
    parser.add_argument('--output', type=Path, required=True, help='New results directory on local SSD')
    parser.add_argument('--comparator', type=Path, default=ROOT / 'tests/ArrowCompare/bin/Release/net8.0/ArrowCompare.dll')
    parser.add_argument('--pairs', type=int, default=3)
    parser.add_argument('--seed', type=int, default=20260915)
    parser.add_argument('--input-kind', choices=['network', 'local'], default='network',
                        help='Input storage kind; local still requires verified zero resident pages')
    parser.add_argument('--interface', default='en0')
    parser.add_argument('--threads', type=int, default=3)
    parser.add_argument('--chunk', type=int, default=128)
    parser.add_argument('--batch', type=int, default=1000)
    parser.add_argument('--timeout', type=int, default=300, help='Per-operation timeout seconds')
    parser.add_argument('--min-interface-rx-fraction', type=float, default=.75,
                        help='Stop for review if interface RX is less than this fraction of RAW size (default .75; diagnostic, not a cold-cache proof)')
    parser.add_argument('--candidate-env', action='append', default=[], metavar='NAME=VALUE',
                        help='Environment override applied only to the candidate (repeatable; recorded in the manifest)')
    parser.add_argument('--plan-only', action='store_true', help='Print schedule without accessing input RAWs or running tools')
    args = parser.parse_args()
    if args.min_interface_rx_fraction < 0:
        parser.error('Minimum RX fraction must be nonnegative')
    if args.pairs < 1 or min(args.threads, args.chunk, args.batch) < 1:
        parser.error('Pairs, threads, chunk and batch must be positive')
    candidate_environment = {}
    for assignment in args.candidate_env:
        name, separator, value = assignment.partition('=')
        if not separator or not name or not all(character.isalnum() or character == '_' for character in name):
            parser.error(f'Invalid --candidate-env assignment: {assignment!r}')
        candidate_environment[name] = value
    planned = schedule(args.raw, args.pairs, args.seed)
    if args.plan_only:
        print(json.dumps(planned, indent=2))
        return 0
    for binary in [args.original, args.candidate, args.cache_helper, args.comparator]:
        if not binary.is_file():
            parser.error(f'Build the required executable first: {binary}')
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = {
        'platform': platform.platform(), 'created_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        'schedule': planned, 'seed': args.seed, 'flags': {'threads': args.threads, 'chunk': args.chunk, 'batch': args.batch},
        'candidate_environment': candidate_environment,
        'binaries': {name: binary_identity(path)
                     for name, path in [('original', args.original), ('candidate', args.candidate), ('cache_helper', args.cache_helper)]},
        'cache_method': 'Per-file invalidation ending in mincore immediately before every conversion; require zero resident and paged-out pages. No additional RAW open between the verified eviction and converter launch.',
        'input_kind': args.input_kind,
        'server_cache': ('uncontrolled; same-file pairs share server and run order alternates'
                         if args.input_kind == 'network' else 'not applicable to local input'),
        'network_interface': args.interface if args.input_kind == 'network' else None,
        'min_interface_rx_fraction': args.min_interface_rx_fraction,
        'interface_note': 'Aggregate interface counters include other processes, retries and protocol overhead; not per-file transferred bytes.',
        'runs': [], 'comparisons': [], 'status': 'running'
    }
    save(args.output / 'manifest.json', manifest)
    identities = {}
    pairs = {}
    try:
        for entry in planned:
            run_dir = args.output / entry['tag']
            run_dir.mkdir()
            run = dict(entry, accepted=False)
            manifest['runs'].append(run)
            raw = Path(entry['raw'])
            evicted = evict_with_retries(args.cache_helper.resolve(), raw, run_dir, args.timeout,
                                         run, identities.get(str(raw)))
            require_cold(evicted)
            # The eviction helper ends with mincore on the same mapping. An
            # extra open/inspection may itself repopulate a header page on SMB,
            # so its verified zero-page result is the final cache observation.
            inspected = evicted
            identity = (inspected['device_after'], inspected['inode_after'], inspected['size_bytes'], inspected['mtime_after_ns'])
            run['input_identity'] = dict(device=identity[0], inode=identity[1], size_bytes=identity[2], mtime_ns=identity[3])
            if identities.setdefault(str(raw), identity) != identity:
                raise RuntimeError(f'Input size/mtime changed between runs: {raw}')
            binary = args.original if entry['variant'] == 'original' else args.candidate
            converter_command = command(binary) + [str(raw.resolve()), '-o', str(run_dir / 'arrow_out'),
                '--threads-per-file', str(args.threads), '--scan-chunk-size', str(args.chunk), '--batch-size', str(args.batch)]
            cache_label = ('verified zero-resident cold client cache; server cache uncontrolled'
                           if args.input_kind == 'network'
                           else 'verified zero-resident cold local file cache')
            environment = dict(os.environ,
                               PERF_NETWORK_INTERFACE=args.interface if args.input_kind == 'network' else '',
                               PERF_CACHE_LABEL=cache_label)
            if entry['variant'] == 'candidate':
                environment.update(candidate_environment)
            # Run the existing process measurement wrapper in a fresh process so
            # kernel child-RSS accounting cannot include prior converter runs.
            process = subprocess.Popen([sys.executable, str(HERE / 'measure_run.py'), str(run_dir), '--'] + converter_command,
                                       env=environment, start_new_session=(os.name == 'posix'))
            measured_status = wait_measured_process(process, args.timeout)
            metrics = json.loads((run_dir / 'metrics.json').read_text())
            run['metrics'] = metrics
            if measured_status or metrics['exit_code']:
                raise RuntimeError(f'Conversion failed; see {run_dir / "converter.log"}')
            if args.input_kind == 'network':
                if metrics['interface_received_bytes'] is None or metrics['interface_received_bytes'] <= 0:
                    raise RuntimeError(f'Cannot verify network receive activity for {entry["tag"]}; check interface/permissions')
                run['interface_rx_fraction_of_raw_size'] = metrics['interface_received_bytes'] / inspected['size_bytes'] if inspected['size_bytes'] else None
                if inspected['size_bytes'] and run['interface_rx_fraction_of_raw_size'] < args.min_interface_rx_fraction:
                    raise RuntimeError(f'Cold residency verified but low interface receive traffic requires review: {entry["tag"]}; '
                                       f'{run["interface_rx_fraction_of_raw_size"]:.3f} times RAW size. '
                                       'This diagnostic cannot distinguish selective reads, caching, or the wrong interface.')
            after = cache_operation(args.cache_helper.resolve(), 'inspect', raw, run_dir / 'post-run-cache.json', args.timeout)
            if not after['metadata_unchanged'] or (after['device_after'], after['inode_after'], after['size_bytes'], after['mtime_after_ns']) != identity:
                raise RuntimeError(f'Input size/mtime changed during conversion: {raw}')
            run['accepted'] = True
            pair = pairs.setdefault((str(raw), entry['pair']), {})
            pair[entry['variant']] = run_dir / 'arrow_out' / (raw.stem + '.arrow')
            if len(pair) == 2:
                comparison = subprocess.run(command(args.comparator) + [str(pair['original']), str(pair['candidate'])],
                                             capture_output=True, text=True, timeout=args.timeout)
                log = run_dir / 'comparison.log'
                log.write_text(comparison.stdout + comparison.stderr)
                manifest['comparisons'].append(dict(raw=str(raw), pair=entry['pair'], exit_code=comparison.returncode, log=str(log)))
                if comparison.returncode:
                    raise RuntimeError(f'Arrow comparison failed; see {log}')
                for completed in manifest['runs']:
                    if completed['raw'] == str(raw) and completed['pair'] == entry['pair']:
                        completed['arrow_equal'] = True
            save(args.output / 'manifest.json', manifest)
            save(args.output / 'summary.json', summaries(manifest['runs']))
            transfer = (f'interface RX {metrics["interface_received_bytes"]}'
                        if args.input_kind == 'network' else 'local input')
            print(f'{entry["tag"]}: {metrics["elapsed_s"]:.3f}s, RSS {metrics["peak_rss_bytes"]}, {transfer}; cold client verified', flush=True)
        manifest['status'] = 'complete'
    except (Exception, KeyboardInterrupt) as error:
        manifest['status'] = 'failed'
        manifest['failure'] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        save(args.output / 'manifest.json', manifest)
        save(args.output / 'summary.json', summaries(manifest['runs']))
    return 0


if __name__ == '__main__':
    sys.exit(main())
