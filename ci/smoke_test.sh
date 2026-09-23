#!/bin/bash
set -euo pipefail

PUBLISH_DIR="${1:?publish directory is required}"
FIXTURE_PATH="${2:?fixture path is required}"
EXECUTABLE="${PUBLISH_DIR}/PioneerConverter"

if [[ ! -x "${EXECUTABLE}" ]]; then
    echo "Expected executable not found: ${EXECUTABLE}" >&2
    exit 1
fi

echo "Running startup check"
"${EXECUTABLE}" >/tmp/pioneerconverter-startup.log 2>&1

if [[ ! -f "${FIXTURE_PATH}" ]]; then
    echo "Fixture missing: ${FIXTURE_PATH}" >&2
    exit 1
fi

if [[ ! -s "${FIXTURE_PATH}" ]]; then
    echo "Fixture is empty, skipping conversion smoke test: ${FIXTURE_PATH}"
    exit 0
fi

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

TMP_FIXTURE="${TMP_DIR}/smoke.raw"
cp "${FIXTURE_PATH}" "${TMP_FIXTURE}"

echo "Running conversion smoke test"
OUTPUT_DIR="${TMP_DIR}/custom_out"
for removed_flag in --concurrent-files -n; do
    if "${EXECUTABLE}" "${TMP_FIXTURE}" "${removed_flag}" 1 -o "${OUTPUT_DIR}" >"${TMP_DIR}/removed-flag.log" 2>&1; then
        echo "Removed option unexpectedly accepted: ${removed_flag}" >&2
        exit 1
    fi
    if ! grep -F -- "${removed_flag}" "${TMP_DIR}/removed-flag.log" >/dev/null; then
        echo "Removed option error did not identify ${removed_flag}" >&2
        exit 1
    fi
done
"${EXECUTABLE}" "${TMP_FIXTURE}" -b 50 -t 2 --scan-chunk-size 17 -o "${OUTPUT_DIR}"

OUTPUT_FILE="${OUTPUT_DIR}/smoke.arrow"
if [[ ! -s "${OUTPUT_FILE}" ]]; then
    echo "Expected output file missing or empty: ${OUTPUT_FILE}" >&2
    exit 1
fi

COMPLETE_HASH="$(shasum -a 256 "${OUTPUT_FILE}" | awk '{print $1}')"

echo "Running skip-existing smoke check for complete output"
"${EXECUTABLE}" "${TMP_FIXTURE}" -b 50 -o "${OUTPUT_DIR}" --skip-existing

AFTER_COMPLETE_SKIP_HASH="$(shasum -a 256 "${OUTPUT_FILE}" | awk '{print $1}')"
if [[ "${AFTER_COMPLETE_SKIP_HASH}" != "${COMPLETE_HASH}" ]]; then
    echo "Expected output to remain unchanged with --skip-existing: ${OUTPUT_FILE}" >&2
    exit 1
fi

printf "skip-existing-sentinel" > "${OUTPUT_FILE}"
SENTINEL_HASH="$(shasum -a 256 "${OUTPUT_FILE}" | awk '{print $1}')"

echo "Running skip-existing smoke check for incomplete output"
"${EXECUTABLE}" "${TMP_FIXTURE}" -b 50 -o "${OUTPUT_DIR}" --skip-existing

AFTER_RECONVERT_HASH="$(shasum -a 256 "${OUTPUT_FILE}" | awk '{print $1}')"
if [[ "${AFTER_RECONVERT_HASH}" == "${SENTINEL_HASH}" ]]; then
    echo "Expected incomplete output to be reconverted with --skip-existing: ${OUTPUT_FILE}" >&2
    exit 1
fi

echo "Smoke test passed"
