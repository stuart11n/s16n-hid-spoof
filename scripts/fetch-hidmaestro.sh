#!/usr/bin/env bash
set -euo pipefail
VERSION="${1:-v2.1.0}"
# SHA-256 of HIDMaestro.Core.dll inside the v2.1.0 release zip.
EXPECTED_SHA256="${EXPECTED_SHA256:-F13E32591404DB0F418AF9265B8E240F71257297CFAE5A41DCD696FD87E9B4D5}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${ROOT}/lib"
mkdir -p "$OUT"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

URL="https://github.com/hifihedgehog/HIDMaestro/releases/download/${VERSION}/HIDMaestro-${VERSION}.zip"
echo "Downloading ${URL}"
curl -fsSL -o "${TMP}/hm.zip" "$URL"
unzip -jo "${TMP}/hm.zip" "HIDMaestro.Core.dll" -d "$OUT"

DLL="${OUT}/HIDMaestro.Core.dll"
if command -v sha256sum >/dev/null 2>&1; then
  ACTUAL="$(sha256sum "$DLL" | awk '{print toupper($1)}')"
elif command -v shasum >/dev/null 2>&1; then
  ACTUAL="$(shasum -a 256 "$DLL" | awk '{print toupper($1)}')"
else
  echo "warning: no sha256 tool; skipping integrity check" >&2
  ACTUAL=""
fi

if [[ "$VERSION" == "v2.1.0" && -n "$ACTUAL" ]]; then
  EXPECTED="$(echo "$EXPECTED_SHA256" | tr '[:lower:]' '[:upper:]')"
  if [[ "$ACTUAL" != "$EXPECTED" ]]; then
    rm -f "$DLL"
    echo "HIDMaestro.Core.dll SHA-256 mismatch." >&2
    echo "Expected: $EXPECTED" >&2
    echo "Actual:   $ACTUAL" >&2
    exit 1
  fi
fi

echo "Wrote ${DLL}"
[[ -n "$ACTUAL" ]] && echo "SHA-256 ${ACTUAL}"
