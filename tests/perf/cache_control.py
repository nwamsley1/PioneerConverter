"""Strict per-file cache verification shared by cold benchmark drivers.

Retries only errno-zero invalidations that leave cached pages, with stable file
identity across attempts. No conversion is permitted until residency is zero.
"""
import json
import subprocess


def save(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def cache_operation(helper, operation, raw, artifact, timeout):
    result = subprocess.run([str(helper), operation, str(raw)], capture_output=True, text=True, timeout=timeout)
    artifact.with_suffix('.log').write_text(result.stdout + result.stderr)
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise RuntimeError(f'Cache helper did not return JSON; see {artifact.with_suffix(".log")}') from error
    save(artifact, data)
    if result.returncode:
        raise RuntimeError(f'Cache {operation} failed ({result.returncode}); see {artifact}')
    return data


def require_cold(data):
    after = data['after']
    if (not data.get('cold_client_verified') or not data.get('metadata_unchanged') or
            after['resident_pages'] != 0 or after['paged_out_pages'] != 0):
        raise RuntimeError('Refusing to label this conversion cold: zero residency/unchanged metadata was not verified')


def identity(snapshot):
    return (snapshot['device_after'], snapshot['inode_after'],
            snapshot['size_bytes'], snapshot['mtime_after_ns'])


def evict_with_retries(helper, raw, run_dir, timeout, run, expected_identity=None):
    """Retry only a successful invalidation that still reports cached pages."""
    attempts = run.setdefault('eviction_attempts', [])
    for number in range(1, 6):
        artifact = run_dir / f'eviction-attempt{number:02d}.json'
        log_path = artifact.with_suffix('.log')
        attempt = dict(attempt=number, artifact=str(artifact), log=str(log_path))
        attempts.append(attempt)
        try:
            result = subprocess.run([str(helper), 'evict', str(raw)], capture_output=True,
                                    text=True, timeout=timeout)
        except subprocess.TimeoutExpired as error:
            attempt['status'] = 'timeout'
            partial = error.stdout or b''
            log_path.write_text(partial.decode(errors='replace') if isinstance(partial, bytes) else partial)
            raise
        attempt['exit_code'] = result.returncode
        log_path.write_text(result.stdout + result.stderr)
        try:
            snapshot = json.loads(result.stdout)
        except json.JSONDecodeError as error:
            raise RuntimeError(f'Eviction helper returned invalid JSON; see {log_path}') from error
        save(artifact, snapshot)
        if not snapshot.get('metadata_unchanged') or snapshot.get('eviction_errno') != 0:
            raise RuntimeError(f'Eviction syscall or metadata check failed; see {artifact}')
        observed = identity(snapshot)
        if expected_identity is None:
            expected_identity = observed
        if observed != expected_identity:
            raise RuntimeError(f'Input identity/size/mtime changed during eviction; see {artifact}')
        run['input_identity'] = dict(device=observed[0], inode=observed[1], size_bytes=observed[2], mtime_ns=observed[3])
        if result.returncode == 0:
            require_cold(snapshot)
            save(run_dir / 'eviction.json', snapshot)
            (run_dir / 'eviction.log').write_text(result.stdout + result.stderr)
            return snapshot
        after = snapshot['after']
        retryable = (result.returncode == 1 and snapshot.get('operation') == 'evict' and
                     not snapshot.get('cold_client_verified') and
                     (after['resident_pages'] > 0 or after['paged_out_pages'] > 0))
        attempt['retryable_residency_failure'] = retryable
        if not retryable:
            raise RuntimeError(f'Eviction helper failed without a retryable residency result; see {artifact}')
    raise RuntimeError(f'Cold-cache verification still failed after 5 eviction attempts; conversion was not started: {raw}')
