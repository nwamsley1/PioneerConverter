#!/usr/bin/env python3
"""Summarize local profile artifacts; never open a RAW file or run a converter.

Optional managed-event-summary.json is produced by TraceSummary. Profiler overhead
is included; sampled stacks and summed worker wall times are not CPU attribution.
"""
import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path
from statistics import median


def distribution(values):
    return dict(count=len(values), minimum=min(values), median=median(values),
                maximum=max(values), sum=sum(values)) if values else dict(count=0)


def summarize(path):
    metrics = json.loads((path / 'metrics.json').read_text())
    result = dict(directory=str(path.resolve()), metrics=metrics,
                  process_cpu_core_equivalents=(metrics['user_s'] + metrics['system_s']) / metrics['elapsed_s'],
                  interpretation=[
                      'Diagnostic run: profiling and instrumentation overhead included.',
                      'Process CPU core equivalents = user+system CPU divided by elapsed time; profiler processes excluded.',
                      'Counter sums are sums of reported samples, not integrals. Rate samples are approximately one second; startup/final partial intervals may be absent.',
                      'Worker SDK durations overlap. They are summed wall time and cannot be added to elapsed time.',
                      'Sampled thread stacks, including waits, are not on-CPU samples.'])
    counters = path / 'runtime-counters.json'
    if counters.exists():
        grouped = defaultdict(list)
        for event in json.loads(counters.read_text())['Events']:
            grouped[(event['name'], event['counterType'])].append(event['value'])
        result['runtime_counters'] = {name: dict(counter_type=kind, **distribution(values))
                                      for (name, kind), values in grouped.items()}
    scan = path / 'scan-profile.json'
    if scan.exists():
        data = json.loads(scan.read_text())
        workers, chunks = data['workers'], data['chunks']
        result['scan_workers'] = dict(
            requested=data['requested_workers'], active_reads_high_water=data['active_reads_high_water'],
            active_reads_at_end=data['active_reads_at_end'],
            scans_completed=sum(w['scans_completed'] for w in workers),
            participating_workers_per_chunk=dict(Counter(c['participating_workers'] for c in chunks)),
            worker_scan_counts=[w['scans_completed'] for w in workers],
            managed_thread_ids=sorted({t for w in workers for t in w['managed_thread_ids']}),
            sdk_summed_wall_ms={name: sum(w['sdk_ms'][name] for w in workers) for name in workers[0]['sdk_ms']},
            chunk_extraction_ms=distribution([c['extraction_ms'] for c in chunks]),
            last_worker_tail_ms=distribution([c['last_worker_tail_ms'] for c in chunks]),
            producer_queue_add_ms=data['producer_add_wait_ms'],
            assembler_queue_read_ms=data['assembly_read_wait_ms'],
            thread_pool_initial=data['initial_thread_pool'], thread_pool_final=data['final_thread_pool'])
    events = path / 'managed-event-summary.json'
    if events.exists():
        result['managed_trace'] = json.loads(events.read_text())
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('profile_directory', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    text = json.dumps(summarize(args.profile_directory), indent=2) + '\n'
    if args.output:
        args.output.write_text(text)
    else:
        print(text, end='')


if __name__ == '__main__':
    main()
