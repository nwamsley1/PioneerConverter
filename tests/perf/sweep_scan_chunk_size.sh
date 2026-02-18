#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
RUN_SNAPSHOT="${SCRIPT_DIR}/run_perf_snapshot.sh"

BASELINE_TAG="${1:-baseline_current}"
THREADS="${2:-2}"
SCAN_THREADS="${3:-3}"
BATCH_SIZE="${4:-10000}"

BASELINE_SUMS="${REPO_ROOT}/tests/perf/results/${BASELINE_TAG}/checksums.sha256"
if [[ ! -f "${BASELINE_SUMS}" ]]; then
  echo "Missing baseline checksums: ${BASELINE_SUMS}" >&2
  exit 1
fi

RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)"
RESULT_DIR="${REPO_ROOT}/tests/perf/results/chunk_sweeps"
SUMMARY_TSV="${RESULT_DIR}/${RUN_ID}_summary.tsv"
SUMMARY_SORTED_TSV="${RESULT_DIR}/${RUN_ID}_summary_sorted.tsv"

mkdir -p "${RESULT_DIR}"
{
  echo -e "tag\treal_s\tuser_s\tsys_s\tthreads\tscan_threads\tbatch_size\tscan_chunk_size\tchecksums_match"
} > "${SUMMARY_TSV}"

CHUNK_SIZES=(128 256 512 1024 2048)

for chunk_size in "${CHUNK_SIZES[@]}"; do
  tag="chunks_${RUN_ID}_n${THREADS}_s${SCAN_THREADS}_b${BATCH_SIZE}_c${chunk_size}"
  echo "Running ${tag} ..."
  "${RUN_SNAPSHOT}" "${tag}" "${BATCH_SIZE}" "${THREADS}" --threads-per-file "${SCAN_THREADS}" --scan-chunk-size "${chunk_size}"

  candidate_dir="${REPO_ROOT}/tests/perf/results/${tag}"
  candidate_sums="${candidate_dir}/checksums.sha256"
  time_file="${candidate_dir}/time.txt"

  if diff -q "${BASELINE_SUMS}" "${candidate_sums}" >/dev/null; then
    checksums_match="PASS"
  else
    checksums_match="FAIL"
  fi

  real_s="$(awk '/^real /{print $2}' "${time_file}")"
  user_s="$(awk '/^user /{print $2}' "${time_file}")"
  sys_s="$(awk '/^sys /{print $2}' "${time_file}")"

  {
    echo -e "${tag}\t${real_s}\t${user_s}\t${sys_s}\t${THREADS}\t${SCAN_THREADS}\t${BATCH_SIZE}\t${chunk_size}\t${checksums_match}"
  } >> "${SUMMARY_TSV}"
done

{
  head -n 1 "${SUMMARY_TSV}"
  tail -n +2 "${SUMMARY_TSV}" | sort -t$'\t' -k2,2n
} > "${SUMMARY_SORTED_TSV}"

echo "Chunk sweep complete."
echo "Summary: ${SUMMARY_TSV}"
echo "Sorted summary: ${SUMMARY_SORTED_TSV}"
echo
column -t -s $'\t' "${SUMMARY_SORTED_TSV}"
