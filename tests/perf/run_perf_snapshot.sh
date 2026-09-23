#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 SNAPSHOT_TAG [BATCH_SIZE] [SCAN_THREADS] [-- converter args...]" >&2
  echo "Environment: PERF_INPUT_PATH, PERF_RESULTS_DIR (local SSD), CONVERTER_BIN, PERF_CACHE_LABEL" >&2
  exit 1
fi

SNAPSHOT_TAG="$1"
BATCH_SIZE="${2:-1000}"
SCAN_THREADS="${3:-3}"
shift $(( $# >= 3 ? 3 : $# ))
if [[ "${1:-}" == "--" ]]; then shift; fi
EXTRA_ARGS=("$@")
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
INPUT_PATH="${PERF_INPUT_PATH:-${REPO_ROOT}/tests/fixtures/raw/smoke.raw}"
RESULT_ROOT="${PERF_RESULTS_DIR:-${REPO_ROOT}/tests/perf/results}"
BIN_PATH="${CONVERTER_BIN:-${REPO_ROOT}/bin/Release/net8.0/PioneerConverter}"

if [[ ! "${SNAPSHOT_TAG}" =~ ^[A-Za-z0-9_.-]+$ || "${SNAPSHOT_TAG}" == "." || "${SNAPSHOT_TAG}" == ".." ]]; then
  echo "Snapshot tag must be a single safe directory name." >&2
  exit 1
fi
if [[ ! -f "${INPUT_PATH}" ]]; then
  echo "Benchmark input must be one RAW file: ${INPUT_PATH}" >&2
  exit 1
fi
if [[ ! -x "${BIN_PATH}" ]]; then
  echo "Converter binary not found (build Release first): ${BIN_PATH}" >&2
  exit 1
fi
mkdir -p "${RESULT_ROOT}"
RESULT_ROOT="$(cd "${RESULT_ROOT}" && pwd)"
RESULT_DIR="${RESULT_ROOT}/${SNAPSHOT_TAG}"
if [[ -e "${RESULT_DIR}" ]]; then
  echo "Snapshot already exists: ${RESULT_DIR}" >&2
  exit 1
fi
mkdir "${RESULT_DIR}"

COMMAND=("${BIN_PATH}" "${INPUT_PATH}" -b "${BATCH_SIZE}" -t "${SCAN_THREADS}" -o "${RESULT_DIR}/arrow_out")
if (( ${#EXTRA_ARGS[@]} > 0 )); then COMMAND+=("${EXTRA_ARGS[@]}"); fi
{
  echo "snapshot_tag=${SNAPSHOT_TAG}"
  echo "batch_size=${BATCH_SIZE}"
  echo "scan_threads=${SCAN_THREADS}"
  echo "input_path=${INPUT_PATH}"
  echo "cache_label=${PERF_CACHE_LABEL:-unspecified; no cache eviction performed}"
  printf 'command='; printf '%q ' "${COMMAND[@]}"; printf '\n'
  echo "started_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > "${RESULT_DIR}/metadata.txt"

"${PYTHON:-python3}" "${SCRIPT_DIR}/measure_run.py" "${RESULT_DIR}" -- "${COMMAND[@]}"

shopt -s nullglob
ARROW_FILES=("${RESULT_DIR}/arrow_out/"*.arrow)
if (( ${#ARROW_FILES[@]} == 0 )); then
  echo "No Arrow files produced: ${RESULT_DIR}/arrow_out" >&2
  exit 1
fi
{
  echo "ended_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "arrow_files=${#ARROW_FILES[@]}"
  echo "arrow_out_retained=true"
} >> "${RESULT_DIR}/metadata.txt"
(
  cd "${RESULT_DIR}/arrow_out"
  shasum -a 256 ./*.arrow | sed 's# \./# #'
) | sort > "${RESULT_DIR}/checksums.sha256"
(
  cd "${RESULT_DIR}/arrow_out"
  wc -c ./*.arrow | sort -n
) > "${RESULT_DIR}/sizes_bytes.txt"
echo "Snapshot complete: ${RESULT_DIR}"
