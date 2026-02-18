#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 BASELINE_TAG CANDIDATE_TAG" >&2
  exit 1
fi

BASELINE_TAG="$1"
CANDIDATE_TAG="$2"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
RESULT_ROOT="${REPO_ROOT}/tests/perf/results"
BASELINE_DIR="${RESULT_ROOT}/${BASELINE_TAG}"
CANDIDATE_DIR="${RESULT_ROOT}/${CANDIDATE_TAG}"

BASELINE_SUMS="${BASELINE_DIR}/checksums.sha256"
CANDIDATE_SUMS="${CANDIDATE_DIR}/checksums.sha256"

if [[ ! -f "${BASELINE_SUMS}" ]]; then
  echo "Missing baseline checksums: ${BASELINE_SUMS}" >&2
  exit 1
fi

if [[ ! -f "${CANDIDATE_SUMS}" ]]; then
  echo "Missing candidate checksums: ${CANDIDATE_SUMS}" >&2
  exit 1
fi

echo "Comparing checksums..."
if diff -u "${BASELINE_SUMS}" "${CANDIDATE_SUMS}"; then
  echo "Output identity check: PASS"
else
  echo "Output identity check: FAIL" >&2
  exit 1
fi

echo
echo "Timing summary:"
echo "baseline (${BASELINE_TAG}):"
cat "${BASELINE_DIR}/time.txt"
echo "candidate (${CANDIDATE_TAG}):"
cat "${CANDIDATE_DIR}/time.txt"
