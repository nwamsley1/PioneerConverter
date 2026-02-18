#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 SNAPSHOT_TAG [BATCH_SIZE] [THREADS] [-- converter args...]" >&2
  exit 1
fi

SNAPSHOT_TAG="$1"
BATCH_SIZE="${2:-10000}"
THREADS="${3:-2}"
shift $(( $# >= 3 ? 3 : $# ))
EXTRA_ARGS=("$@")

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
INPUT_DIR="${REPO_ROOT}/tests/perf/convert_test"
LIVE_OUT_DIR="${INPUT_DIR}/arrow_out"
RESULT_DIR="${REPO_ROOT}/tests/perf/results/${SNAPSHOT_TAG}"
BIN_PATH="${REPO_ROOT}/bin/Release/net8.0/PioneerConverter"

if [[ ! -d "${INPUT_DIR}" ]]; then
  echo "Input directory not found: ${INPUT_DIR}" >&2
  exit 1
fi

if [[ ! -x "${BIN_PATH}" ]]; then
  echo "Converter binary not found (build Release first): ${BIN_PATH}" >&2
  exit 1
fi

if [[ -e "${RESULT_DIR}" ]]; then
  echo "Snapshot already exists: ${RESULT_DIR}" >&2
  exit 1
fi

mkdir -p "${RESULT_DIR}"

if [[ -d "${LIVE_OUT_DIR}" ]]; then
  mv "${LIVE_OUT_DIR}" "${RESULT_DIR}/preexisting_arrow_out"
fi

{
  echo "snapshot_tag=${SNAPSHOT_TAG}"
  echo "batch_size=${BATCH_SIZE}"
  echo "threads=${THREADS}"
  echo "extra_args=${EXTRA_ARGS[*]:-}"
  echo "input_dir=${INPUT_DIR}"
  echo "command=${BIN_PATH} ${INPUT_DIR} -b ${BATCH_SIZE} -n ${THREADS} ${EXTRA_ARGS[*]:-}"
  echo "started_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > "${RESULT_DIR}/metadata.txt"

if (( ${#EXTRA_ARGS[@]} > 0 )); then
  /usr/bin/time -p -o "${RESULT_DIR}/time.txt" \
    "${BIN_PATH}" "${INPUT_DIR}" -b "${BATCH_SIZE}" -n "${THREADS}" "${EXTRA_ARGS[@]}" \
    > "${RESULT_DIR}/converter.log" 2>&1
else
  /usr/bin/time -p -o "${RESULT_DIR}/time.txt" \
    "${BIN_PATH}" "${INPUT_DIR}" -b "${BATCH_SIZE}" -n "${THREADS}" \
    > "${RESULT_DIR}/converter.log" 2>&1
fi

if [[ ! -d "${LIVE_OUT_DIR}" ]]; then
  echo "Expected output directory missing after run: ${LIVE_OUT_DIR}" >&2
  exit 1
fi

mv "${LIVE_OUT_DIR}" "${RESULT_DIR}/arrow_out"

{
  echo "ended_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "arrow_files=$(find "${RESULT_DIR}/arrow_out" -maxdepth 1 -name '*.arrow' | wc -l | tr -d ' ')"
} >> "${RESULT_DIR}/metadata.txt"

(
  cd "${RESULT_DIR}/arrow_out"
  shasum -a 256 ./*.arrow | sed 's# \./# #'
) | sort > "${RESULT_DIR}/checksums.sha256"

(
  cd "${RESULT_DIR}/arrow_out"
  wc -c ./*.arrow | sort -n
) > "${RESULT_DIR}/sizes_bytes.txt"

if [[ "${KEEP_ARROW_OUT:-0}" != "1" ]]; then
  rm -rf "${RESULT_DIR}/arrow_out"
  echo "arrow_out_retained=false" >> "${RESULT_DIR}/metadata.txt"
else
  echo "arrow_out_retained=true" >> "${RESULT_DIR}/metadata.txt"
fi

echo "Snapshot complete: ${RESULT_DIR}"
