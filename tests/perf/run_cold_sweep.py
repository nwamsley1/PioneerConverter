#!/usr/bin/env python3
"""Sequential, verified cold-client-cache worker/chunk/batch parameter sweep.

Each file/repetition block runs every setting once, in a seeded counterbalanced
order. RAW files are never copied or prefetched. Server cache is uncontrolled.
"""
import argparse
import json
import math
import os
from pathlib import Path
import platform
import random
import re
import signal
import statistics
import subprocess
import sys
import time

from cache_control import cache_operation, evict_with_retries, identity, require_cold, save
from run_cold_compare import binary_identity, command

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def load_settings(value):
    text = value if value.lstrip().startswith('[') else Path(value).read_text()
    settings = json.loads(text)
    if not isinstance(settings, list) or not settings:
        raise ValueError('Settings must be a nonempty JSON list')
    names = set()
    for item in settings:
        if not isinstance(item, dict) or set(item) != {'name', 'threads', 'chunk', 'batch'}:
            raise ValueError('Each setting must contain exactly name, threads, chunk and batch')
        name = item['name']
        if not isinstance(name, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{0,63}', name):
            raise ValueError('Setting names must be 1-64 letters/digits/underscores/hyphens, beginning with a letter or digit')
        if name in names:
            raise ValueError(f'Duplicate setting name: {name}')
        names.add(name)
        for key in ['threads', 'chunk', 'batch']:
            if type(item[key]) is not int or not 1 <= item[key] <= 2147483647:
                raise ValueError(f'{name}: {key} must be a positive Int32')
    return settings


def rotation_stride(file_count, setting_count):
    step = file_count
    while math.gcd(step, setting_count) != 1:
        step += 1
    return step


def schedule(raws, references, settings, repetitions, seed):
    randomizer = random.Random(seed)
    base = list(range(len(settings)))
    randomizer.shuffle(base)
    step = rotation_stride(len(raws), len(settings))
    result = []
    for repetition in range(repetitions):
        files = list(range(len(raws)))
        randomizer.shuffle(files)
        for index in files:
            # A stride coprime to the setting count visits every position for
            # EACH file over N repetitions, even when file/setting counts share
            # factors. File offsets also counterbalance positions across files.
            rotation = (repetition * step + index) % len(settings)
            order = base[rotation:] + base[:rotation]
            for position, setting_index in enumerate(order):
                setting = settings[setting_index]
                result.append(dict(repetition=repetition + 1, file_index=index + 1,
                                   position=position + 1, raw=str(raws[index]),
                                   reference_arrow=str(references[index]), setting=dict(setting),
                                   tag=f'rep{repetition + 1:02d}-file{index + 1:02d}-{setting["name"]}'))
    return result


def median(values):
    values = [value for value in values if value is not None]
    return statistics.median(values) if values else None


def summarize(runs, raws, references, settings, repetitions):
    accepted = [run for run in runs if run.get('accepted') and run.get('arrow_equal')]
    reference_name = settings[0]['name']
    result = dict(reference_setting=reference_name, files=[], overall_settings=[])
    all_ratios = {setting['name']: [] for setting in settings}
    for raw, reference in zip(raws, references):
        file_runs = [run for run in accepted if run['raw'] == str(raw)]
        reference_runs = {run['repetition']: run for run in file_runs if run['setting']['name'] == reference_name}
        file_summary = dict(raw=str(raw), reference_arrow=str(reference), settings=[])
        for setting in settings:
            selected = sorted((r for r in file_runs if r['setting']['name'] == setting['name']), key=lambda r: r['repetition'])
            individuals = [dict(repetition=r['repetition'], tag=r['tag'], **r['metrics']) for r in selected]
            ratios = [dict(repetition=r['repetition'], ratio=r['metrics']['elapsed_s'] / reference_runs[r['repetition']]['metrics']['elapsed_s'])
                      for r in selected if r['repetition'] in reference_runs]
            all_ratios[setting['name']].extend(r['ratio'] for r in ratios)
            stage_names = sorted({key for row in individuals for stage in row.get('pipeline_files', [])
                                  for key in stage if key.endswith('_ms')})
            values = dict(setting, accepted_runs=len(selected), requested_runs=repetitions,
                          complete=len(selected) == repetitions, individuals=individuals,
                          median_stages_ms={key: median([row['pipeline_files'][0].get(key) for row in individuals if row.get('pipeline_files')])
                                            for key in stage_names},
                          elapsed_ratios_to_reference_setting=ratios,
                          geometric_mean_elapsed_ratio_to_reference_setting=math.exp(statistics.mean(math.log(r['ratio']) for r in ratios)) if ratios else None)
            for metric in ['elapsed_s', 'peak_rss_bytes', 'user_s', 'system_s', 'interface_received_bytes', 'interface_sent_bytes']:
                values['median_' + metric] = median([row.get(metric) for row in individuals])
            file_summary['settings'].append(values)
        result['files'].append(file_summary)
    for setting in settings:
        ratios = all_ratios[setting['name']]
        result['overall_settings'].append(dict(setting, matched_blocks=len(ratios),
            requested_blocks=len(raws) * repetitions,
            geometric_mean_elapsed_ratio_to_reference_setting=math.exp(statistics.mean(map(math.log, ratios))) if ratios else None))
    return result


def measured_process(arguments, environment, timeout):
    process = subprocess.Popen(arguments, env=environment, start_new_session=(os.name == 'posix'))
    try:
        return process.wait(timeout=timeout)
    except BaseException:
        # A timed-out wrapper must not leave a converter reading RAW in the
        # background. Kill the entire group even if the wrapper exits first.
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


def run_key(run):
    setting = run['setting']
    return (run['raw'], run['reference_arrow'], run['repetition'], setting['name'],
            setting['threads'], setting['chunk'], setting['batch'])


def previous_identities(manifest, output):
    recovered = {}
    for run in manifest['runs']:
        stored = run.get('input_identity')
        if stored is None:
            # Older failures may have occurred before assigning input_identity.
            artifact = output / run['tag'] / 'eviction.json'
            if artifact.is_file():
                snapshot = json.loads(artifact.read_text())
                if snapshot.get('metadata_unchanged') and snapshot.get('eviction_errno') == 0:
                    observed = identity(snapshot)
                    stored = dict(device=observed[0], inode=observed[1], size_bytes=observed[2], mtime_ns=observed[3])
        if stored is not None:
            observed = tuple(stored[key] for key in ['device', 'inode', 'size_bytes', 'mtime_ns'])
            if recovered.setdefault(run['raw'], observed) != observed:
                raise ValueError(f'Prior attempts disagree about input identity: {run["raw"]}')
    return recovered


def resume_manifest(args, settings, planned, fingerprints, references):
    path = args.output / 'manifest.json'
    manifest = json.loads(path.read_text())
    if manifest.get('status') != 'failed':
        raise ValueError('--resume requires an existing FAILED sweep; completed/running studies cannot be resumed')
    expected = dict(settings=settings, repetitions=args.repetitions, seed=args.seed,
                    schedule=planned, binaries=fingerprints, references=references,
                    candidate_environment=args.candidate_environment,
                    network_interface=args.interface, min_interface_rx_fraction=.75,
                    discard_output_after_compare=args.discard_output_after_compare)
    for field, value in expected.items():
        if manifest.get(field) != value:
            raise ValueError(f'Resume configuration/provenance differs: {field}')
    planned_keys = {run_key(entry) for entry in planned}
    completed = set()
    for run in manifest['runs']:
        key = run_key(run)
        if key not in planned_keys:
            raise ValueError('Existing attempt does not match the saved schedule/settings')
        if run.get('accepted') and run.get('arrow_equal'):
            if key in completed:
                raise ValueError('Multiple accepted attempts exist for the same scheduled run')
            completed.add(key)
    identities = previous_identities(manifest, args.output)
    number = len(manifest.get('resume_history', [])) + 1
    snapshot = args.output / f'manifest-before-resume{number:02d}.json'
    while snapshot.exists():
        number += 1
        snapshot = args.output / f'manifest-before-resume{number:02d}.json'
    with snapshot.open('x') as stream:
        json.dump(manifest, stream, indent=2)
        stream.write('\n')
    manifest.setdefault('resume_history', []).append(dict(
        resumed_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
        prior_failure=manifest.get('failure'), previous_manifest=str(snapshot),
        accepted_runs_skipped=len(completed), prior_attempts=len(manifest['runs']),
        driver=binary_identity(Path(__file__))))
    manifest.pop('failure', None)
    manifest['status'] = 'running'
    return manifest, identities, completed


def next_attempt(entry, output):
    base_tag = entry['tag']
    tag = base_tag
    number = 1
    while (output / tag).exists():
        number += 1
        tag = f'{base_tag}-attempt{number:02d}'
    return dict(entry, tag=tag, scheduled_tag=base_tag, run_attempt=number)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--settings', required=True, help='JSON list or path to JSON file: [{"name":"default","threads":3,"chunk":128,"batch":1000}, ...]')
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--cache-helper', type=Path, required=True)
    parser.add_argument('--raw', type=Path, action='append', required=True)
    parser.add_argument('--reference-arrow', type=Path, action='append', required=True, help='Original output for each --raw, paired by argument index')
    parser.add_argument('--output', type=Path, required=True, help='New results directory on local SSD')
    parser.add_argument('--comparator', type=Path, default=ROOT / 'tests/ArrowCompare/bin/Release/net8.0/ArrowCompare.dll')
    parser.add_argument('--repetitions', type=int, default=3)
    parser.add_argument('--interface', default='en0')
    parser.add_argument('--candidate-env', action='append', default=[], metavar='NAME=VALUE',
                        help='Environment override applied to every candidate run (repeatable; recorded in the manifest)')
    parser.add_argument('--seed', type=int, default=20260915)
    parser.add_argument('--timeout', type=float, default=300, help='Per-operation timeout seconds')
    parser.add_argument('--discard-output-after-compare', action='store_true')
    parser.add_argument('--plan-only', action='store_true')
    parser.add_argument('--resume', action='store_true', help='Explicitly resume an existing failed sweep with exactly matching configuration/provenance')
    args = parser.parse_args()
    try:
        settings = load_settings(args.settings)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    if len(args.raw) != len(args.reference_arrow):
        parser.error('Supply one --reference-arrow for each --raw, in the same order')
    if args.repetitions < 1 or not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error('Repetitions and timeout must be positive')
    args.candidate_environment = {}
    for assignment in args.candidate_env:
        name, separator, value = assignment.partition('=')
        if not separator or not name or not all(character.isalnum() or character == '_' for character in name):
            parser.error(f'Invalid --candidate-env assignment: {assignment!r}')
        args.candidate_environment[name] = value
    # abspath is lexical; plan-only performs no RAW or reference-file inspection.
    raws = [Path(os.path.abspath(path)) for path in args.raw]
    references = [Path(os.path.abspath(path)) for path in args.reference_arrow]
    if len(set(raws)) != len(raws):
        parser.error('RAW paths must be distinct')
    planned = schedule(raws, references, settings, args.repetitions, args.seed)
    if args.plan_only:
        print(json.dumps(planned, indent=2))
        return 0
    for path in [args.candidate, args.cache_helper, args.comparator] + references:
        if not path.is_file():
            parser.error(f'Required executable/reference is missing: {path}')
    args.output = args.output.resolve()
    candidate_fingerprint = binary_identity(args.candidate)
    fingerprints = dict(candidate=candidate_fingerprint, cache_helper=binary_identity(args.cache_helper), comparator=binary_identity(args.comparator))
    reference_metadata = [dict(path=str(path), size_bytes=path.stat().st_size, mtime_ns=path.stat().st_mtime_ns) for path in references]
    if args.resume:
        try:
            manifest, identities, completed = resume_manifest(args, settings, planned, fingerprints, reference_metadata)
        except (OSError, ValueError, KeyError) as error:
            parser.error(str(error))
    else:
        args.output.mkdir(parents=True, exist_ok=False)
        manifest = dict(platform=platform.platform(), created_utc=time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
            settings=settings, repetitions=args.repetitions, seed=args.seed, schedule=planned,
            candidate_environment=args.candidate_environment,
            schedule_method='Seeded shuffled setting base order; rotation stride is smallest integer >= file count coprime to setting count, balancing each file over complete setting-count repetition cycles; shuffled file order; every setting once per block.',
            schedule_rotation_stride=rotation_stride(len(raws), len(settings)),
            binaries=fingerprints, references=reference_metadata,
            cache_method='Per-file invalidation ending in mincore immediately before each conversion; zero resident and paged-out pages required; no extra RAW open before converter.',
            server_cache='uncontrolled', network_interface=args.interface, min_interface_rx_fraction=.75,
            interface_note='Aggregate interface counters include other processes, retries, protocol overhead; they are not attributable RAW bytes.',
            discard_output_after_compare=args.discard_output_after_compare, runs=[], status='running')
        identities, completed = {}, set()
    manifest['eviction_retry_policy'] = 'At most 5 attempts; only errno=0, unchanged-identity residency failures retry; strict zero pages before timing; converters never retry automatically.'
    save(args.output / 'manifest.json', manifest)
    try:
        for scheduled in planned:
            if run_key(scheduled) in completed:
                continue
            entry = next_attempt(scheduled, args.output)
            run_dir = args.output / entry['tag']
            run_dir.mkdir()
            run = dict(entry, accepted=False, arrow_equal=False, output_discarded=False)
            manifest['runs'].append(run)
            if binary_identity(args.candidate) != candidate_fingerprint:
                raise RuntimeError('Candidate executable or managed DLL changed during the sweep')
            raw = Path(entry['raw'])
            eviction = evict_with_retries(args.cache_helper.resolve(), raw, run_dir, args.timeout, run, identities.get(str(raw)))
            before = identity(eviction)
            run['input_identity'] = dict(device=before[0], inode=before[1], size_bytes=before[2], mtime_ns=before[3])
            if identities.setdefault(str(raw), before) != before:
                raise RuntimeError(f'Input identity/size/mtime changed between runs: {raw}')
            setting = entry['setting']
            output = run_dir / 'arrow_out' / (raw.stem + '.arrow')
            run['output_arrow'] = str(output)
            converter_command = command(args.candidate) + [str(raw), '-o', str(output.parent),
                '--threads-per-file', str(setting['threads']), '--scan-chunk-size', str(setting['chunk']), '--batch-size', str(setting['batch'])]
            environment = dict(os.environ, PERF_NETWORK_INTERFACE=args.interface,
                               PERF_CACHE_LABEL='verified zero-resident cold client cache; server cache uncontrolled')
            environment.update(args.candidate_environment)
            code = measured_process([sys.executable, str(HERE / 'measure_run.py'), str(run_dir), '--'] + converter_command,
                                    environment, args.timeout)
            metrics = json.loads((run_dir / 'metrics.json').read_text())
            run['metrics'] = metrics
            if code or metrics['exit_code']:
                raise RuntimeError(f'Conversion failed; see {run_dir / "converter.log"}')
            converter_log = (run_dir / 'converter.log').read_text(errors='replace')
            if 'scan-thread mode unavailable' in converter_log.lower() or 'falling back to single-thread scan extraction' in converter_log.lower():
                raise RuntimeError(f'Requested scan worker count fell back to serial extraction; see {run_dir / "converter.log"}')
            if not math.isfinite(metrics['elapsed_s']) or metrics['elapsed_s'] <= 0:
                raise RuntimeError('Invalid process elapsed measurement')
            received = metrics.get('interface_received_bytes')
            if received is None or received <= 0:
                raise RuntimeError('Cannot verify network receive activity; check interface/permissions')
            run['interface_rx_fraction_of_raw_size'] = received / before[2] if before[2] else None
            if before[2] and received < .75 * before[2]:
                raise RuntimeError('Cold residency verified but interface RX below 75% of RAW size requires review; selective reads, caching or wrong interface may explain it')
            after = cache_operation(args.cache_helper.resolve(), 'inspect', raw, run_dir / 'post-run-cache.json', args.timeout)
            if not after['metadata_unchanged'] or identity(after) != before:
                raise RuntimeError(f'Input identity/size/mtime changed during conversion: {raw}')
            comparison = subprocess.run(command(args.comparator) + [entry['reference_arrow'], str(output)],
                                        capture_output=True, text=True, timeout=args.timeout)
            (run_dir / 'comparison.log').write_text(comparison.stdout + comparison.stderr)
            run['comparison_exit_code'] = comparison.returncode
            if comparison.returncode:
                raise RuntimeError(f'Arrow comparison failed; see {run_dir / "comparison.log"}')
            run['arrow_equal'] = True
            run['accepted'] = True
            if args.discard_output_after_compare:
                # Unlink only this generated file, never a reference or a tree.
                if output.parent.resolve() != output.parent:
                    raise RuntimeError('Refusing to discard output through a redirected directory')
                run['output_bytes_before_discard'] = output.stat().st_size
                output.unlink()
                run['output_discarded'] = True
            save(args.output / 'manifest.json', manifest)
            save(args.output / 'summary.json', summarize(manifest['runs'], raws, references, settings, args.repetitions))
            print(f'{entry["tag"]}: {metrics["elapsed_s"]:.3f}s, RSS {metrics.get("peak_rss_bytes")}, interface RX {received}; cold verified; Arrow exact', flush=True)
        manifest['status'] = 'complete'
    except (Exception, KeyboardInterrupt) as error:
        manifest['status'] = 'failed'
        manifest['failure'] = str(error)
        print(str(error), file=sys.stderr)
        return 1
    finally:
        save(args.output / 'manifest.json', manifest)
        save(args.output / 'summary.json', summarize(manifest['runs'], raws, references, settings, args.repetitions))
    return 0


if __name__ == '__main__':
    sys.exit(main())
