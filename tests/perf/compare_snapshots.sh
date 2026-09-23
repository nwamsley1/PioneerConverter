#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 BASELINE_TAG CANDIDATE_TAG" >&2
  exit 1
fi
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
RESULT_ROOT="${PERF_RESULTS_DIR:-${REPO_ROOT}/tests/perf/results}"
BASELINE_DIR="${RESULT_ROOT}/$1"
CANDIDATE_DIR="${RESULT_ROOT}/$2"
for snapshot in "${BASELINE_DIR}" "${CANDIDATE_DIR}"; do
  if [[ ! -f "${snapshot}/checksums.sha256" ]]; then
    echo "Missing snapshot checksums: ${snapshot}" >&2
    exit 1
  fi
done
if diff -q "${BASELINE_DIR}/checksums.sha256" "${CANDIDATE_DIR}/checksums.sha256" >/dev/null; then
  echo "Output byte identity: PASS"
else
  echo "Arrow bytes differ; comparing every column and peak independently of batch boundaries..."
  "${DOTNET:-dotnet}" build "${REPO_ROOT}/tests/ArrowCompare/ArrowCompare.csproj" -c Release --nologo -p:NuGetAudit=false --ignore-failed-sources >/dev/null
  COMPARE_DLL="${REPO_ROOT}/tests/ArrowCompare/bin/Release/net8.0/ArrowCompare.dll"
  shopt -s nullglob
  BASE_FILES=("${BASELINE_DIR}/arrow_out/"*.arrow)
  CANDIDATE_FILES=("${CANDIDATE_DIR}/arrow_out/"*.arrow)
  if (( ${#BASE_FILES[@]} == 0 || ${#BASE_FILES[@]} != ${#CANDIDATE_FILES[@]} )); then
    echo "Snapshots must retain the same set of Arrow files for column comparison." >&2
    exit 1
  fi
  for base in "${BASE_FILES[@]}"; do
    "${DOTNET:-dotnet}" "${COMPARE_DLL}" "${base}" "${CANDIDATE_DIR}/arrow_out/$(basename "${base}")"
  done
fi

echo "Timing summary:"
echo "baseline ($1):"
cat "${BASELINE_DIR}/time.txt"
echo "candidate ($2):"
cat "${CANDIDATE_DIR}/time.txt"
