#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
RUN_SNAPSHOT="${SCRIPT_DIR}/run_perf_snapshot.sh"

BASELINE_TAG="${1:-baseline_current}"
BASELINE_SUMS="${PERF_RESULTS_DIR:-${REPO_ROOT}/tests/perf/results}/${BASELINE_TAG}/checksums.sha256"

if [[ ! -f "${BASELINE_SUMS}" ]]; then
  echo "Missing baseline checksums: ${BASELINE_SUMS}" >&2
  exit 1
fi

RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
SWEEP_DIR="${PERF_RESULTS_DIR:-${REPO_ROOT}/tests/perf/results}/sweeps"
SUMMARY_TSV="${SWEEP_DIR}/${RUN_ID}_summary.tsv"
SUMMARY_SORTED_TSV="${SWEEP_DIR}/${RUN_ID}_summary_sorted.tsv"

mkdir -p "${SWEEP_DIR}"

{
  echo -e "tag\treal_s\tuser_s\tsys_s\tscan_threads\tbatch_size\tcolumns_match"
} > "${SUMMARY_TSV}"

CONFIGS=(
  "1 1000"
  "2 1000"
  "3 1000"
  "4 1000"
  "8 1000"
  "3 500"
  "3 5000"
)

for cfg in "${CONFIGS[@]}"; do
  read -r SCAN_THREADS BATCH_SIZE <<< "${cfg}"
  TAG="sweep_${RUN_ID}_s${SCAN_THREADS}_b${BATCH_SIZE}"

  echo "Running ${TAG} ..."
  "${RUN_SNAPSHOT}" "${TAG}" "${BATCH_SIZE}" "${SCAN_THREADS}"

  CANDIDATE_DIR="${PERF_RESULTS_DIR:-${REPO_ROOT}/tests/perf/results}/${TAG}"
  TIME_FILE="${CANDIDATE_DIR}/time.txt"

  if "${SCRIPT_DIR}/compare_snapshots.sh" "${BASELINE_TAG}" "${TAG}" > "${CANDIDATE_DIR}/comparison.txt" 2>&1; then
    COLUMNS_MATCH="PASS"
  else
    cat "${CANDIDATE_DIR}/comparison.txt" >&2
    exit 1
  fi

  REAL_S="$(awk '/^real /{print $2}' "${TIME_FILE}")"
  USER_S="$(awk '/^user /{print $2}' "${TIME_FILE}")"
  SYS_S="$(awk '/^sys /{print $2}' "${TIME_FILE}")"

  {
    echo -e "${TAG}\t${REAL_S}\t${USER_S}\t${SYS_S}\t${SCAN_THREADS}\t${BATCH_SIZE}\t${COLUMNS_MATCH}"
  } >> "${SUMMARY_TSV}"
done

{
  head -n 1 "${SUMMARY_TSV}"
  tail -n +2 "${SUMMARY_TSV}" | sort -t$'\t' -k2,2n
} > "${SUMMARY_SORTED_TSV}"

echo "Sweep complete."
echo "Summary: ${SUMMARY_TSV}"
echo "Sorted summary: ${SUMMARY_SORTED_TSV}"
echo
column -t -s $'\t' "${SUMMARY_SORTED_TSV}"
