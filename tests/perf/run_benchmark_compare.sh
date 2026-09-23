#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
RUN_SNAPSHOT="${SCRIPT_DIR}/run_perf_snapshot.sh"
COMPARE_SNAPSHOTS="${SCRIPT_DIR}/compare_snapshots.sh"

BASELINE_TAG="baseline_current"
CANDIDATE_TAG="candidate_$(date -u +%Y%m%dT%H%M%SZ)"
BATCH_SIZE="1000"
SCAN_THREADS="3"
REFRESH_BASELINE="0"

print_usage() {
  cat <<EOF
Usage: $0 [options]

Options:
  --baseline-tag TAG       Baseline snapshot tag (default: ${BASELINE_TAG})
  --candidate-tag TAG      Candidate snapshot tag (default: timestamped tag)
  --batch-size N           Batch size passed to converter (default: ${BATCH_SIZE})
  --threads-per-file N     Scan extraction threads per file (default: ${SCAN_THREADS})
  --refresh-baseline       Create the baseline snapshot in this run (fails if tag exists)
  -h, --help               Show this help
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --baseline-tag)
      BASELINE_TAG="${2:-}"
      shift 2
      ;;
    --candidate-tag)
      CANDIDATE_TAG="${2:-}"
      shift 2
      ;;
    --batch-size)
      BATCH_SIZE="${2:-}"
      shift 2
      ;;
    --threads-per-file)
      SCAN_THREADS="${2:-}"
      shift 2
      ;;
    --refresh-baseline)
      REFRESH_BASELINE="1"
      shift
      ;;
    -h|--help)
      print_usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      print_usage >&2
      exit 1
      ;;
  esac
done

if [[ -z "${BASELINE_TAG}" || -z "${CANDIDATE_TAG}" ]]; then
  echo "Baseline and candidate tags must be non-empty." >&2
  exit 1
fi

if [[ "${BASELINE_TAG}" == "${CANDIDATE_TAG}" ]]; then
  echo "Baseline and candidate tags must be different." >&2
  exit 1
fi

cd "${REPO_ROOT}"

echo "Building Release binary..."
"${DOTNET:-dotnet}" build -c Release

if [[ "${REFRESH_BASELINE}" == "1" ]]; then
  echo "Creating baseline snapshot: ${BASELINE_TAG}"
  "${RUN_SNAPSHOT}" "${BASELINE_TAG}" "${BATCH_SIZE}" "${SCAN_THREADS}"
else
  if [[ ! -f "${PERF_RESULTS_DIR:-${REPO_ROOT}/tests/perf/results}/${BASELINE_TAG}/checksums.sha256" ]]; then
    echo "Baseline snapshot missing checksums: tests/perf/results/${BASELINE_TAG}/checksums.sha256" >&2
    echo "Use --refresh-baseline or create that snapshot first." >&2
    exit 1
  fi
fi

echo "Creating candidate snapshot: ${CANDIDATE_TAG}"
"${RUN_SNAPSHOT}" "${CANDIDATE_TAG}" "${BATCH_SIZE}" "${SCAN_THREADS}"

echo
"${COMPARE_SNAPSHOTS}" "${BASELINE_TAG}" "${CANDIDATE_TAG}"

echo
echo "Done."
echo "Baseline: tests/perf/results/${BASELINE_TAG}"
echo "Candidate: tests/perf/results/${CANDIDATE_TAG}"
