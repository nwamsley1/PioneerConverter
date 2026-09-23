#!/usr/bin/env python3
"""Measure one converter process without including build or Arrow comparison time."""
import argparse
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import time


def interface_counters(name):
    """Optional aggregate interface counters; these cannot attribute bytes to RAW."""
    if not name:
        return None
    try:
        if sys.platform.startswith("linux"):
            if not re.fullmatch(r"[A-Za-z0-9_.:-]+", name):
                return None
            base = Path("/sys/class/net") / name / "statistics"
            return int((base / "rx_bytes").read_text()), int((base / "tx_bytes").read_text())
        if sys.platform == "darwin":
            result = subprocess.run(["/usr/sbin/netstat", "-ibdn"], capture_output=True, text=True, timeout=10)
            if result.returncode != 0:
                return None
            for line in result.stdout.splitlines():
                fields = line.split()
                if fields and fields[0] == name and "<Link#" in line:
                    return int(fields[-6]), int(fields[-3])
    except (OSError, ValueError, subprocess.TimeoutExpired):
        pass
    return None


def windows_memory_and_io(process):
    import ctypes
    from ctypes import wintypes

    class MemoryCounters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in (
                "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
                "PagefileUsage", "PeakPagefileUsage")]

    counters = MemoryCounters()
    counters.cb = ctypes.sizeof(counters)
    handle = wintypes.HANDLE(int(process._handle))
    ok = ctypes.windll.psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb)
    return counters.PeakWorkingSetSize if ok else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("result_dir", type=Path)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("converter command required after --")
    args.result_dir.mkdir(parents=True, exist_ok=True)
    interface = os.environ.get("PERF_NETWORK_INTERFACE")
    network_before = interface_counters(interface)
    if os.name == "posix":
        import resource
        usage_before = resource.getrusage(resource.RUSAGE_CHILDREN)
    started = time.perf_counter()
    with (args.result_dir / "converter.log").open("wb") as log:
        child = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
        code = child.wait()
    wall_s = time.perf_counter() - started
    user_s = system_s = peak_rss = None
    read_blocks = written_blocks = None
    if os.name == "posix":
        usage = resource.getrusage(resource.RUSAGE_CHILDREN)
        user_s = usage.ru_utime - usage_before.ru_utime
        system_s = usage.ru_stime - usage_before.ru_stime
        peak_rss = usage.ru_maxrss * (1 if sys.platform == "darwin" else 1024)
        read_blocks = usage.ru_inblock - usage_before.ru_inblock
        written_blocks = usage.ru_oublock - usage_before.ru_oublock
    elif os.name == "nt":
        peak_rss = windows_memory_and_io(child)
    network_after = interface_counters(interface)
    metrics = {
        "command": command,
        "platform": platform.platform(),
        "exit_code": code,
        "elapsed_s": wall_s,
        "user_s": user_s,
        "system_s": system_s,
        "peak_rss_bytes": peak_rss,
        "input_block_operations": read_blocks,
        "output_block_operations": written_blocks,
        "network_transferred_bytes": None,
        "network_interface": interface,
        "interface_received_bytes": (network_after[0] - network_before[0]) if network_before and network_after else None,
        "interface_sent_bytes": (network_after[1] - network_before[1]) if network_before and network_after else None,
        "interface_note": "Aggregate interface deltas include other processes, background traffic and protocol overhead; not bytes attributable to this RAW file.",
        "io_note": "Block operation counters are OS-specific and are not network byte counts.",
        "cache_label": os.environ.get("PERF_CACHE_LABEL", "unspecified; no cache eviction performed"),
    }
    log_text = (args.result_dir / "converter.log").read_text(errors="replace")
    metrics["pipeline_files"] = [
        {key: float(value) for key, value in re.findall(r"(\w+)=([0-9.]+)", line)}
        for line in log_text.splitlines() if line.startswith("PERF ")
    ]
    (args.result_dir / "metrics.json").write_text(json.dumps(metrics, indent=2) + "\n")
    with (args.result_dir / "time.txt").open("w") as time_file:
        for key, value in (("real", wall_s), ("user", user_s), ("sys", system_s)):
            time_file.write(f"{key} {value:.6f}\n" if value is not None else f"{key} unavailable\n")
        time_file.write(f"peak_rss_bytes {peak_rss}\n")
    if code:
        print(log_text, file=sys.stderr)
    return code if code >= 0 else 128 - code


if __name__ == "__main__":
    sys.exit(main())
