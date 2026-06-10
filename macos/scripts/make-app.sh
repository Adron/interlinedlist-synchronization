#!/usr/bin/env bash
#
# make-app.sh
#
# Assembles a proper macOS .app bundle from the SPM release build for
# InterlinedSync. Run from the repository root:
#
#   bash macos/scripts/make-app.sh
#
# Output: macos/build/InterlinedSync.app

set -euo pipefail

# Resolve the macos/ directory regardless of where the script is invoked from.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

APP_NAME="InterlinedSync"
BUILD_DIR="${MACOS_DIR}/build"
APP_BUNDLE="${BUILD_DIR}/${APP_NAME}.app"
CONTENTS_DIR="${APP_BUNDLE}/Contents"
MACOS_BIN_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

RELEASE_BIN="${MACOS_DIR}/.build/release/${APP_NAME}"
INFO_PLIST_SRC="${MACOS_DIR}/Info.plist"

echo "==> Verifying inputs..."
if [[ ! -f "${INFO_PLIST_SRC}" ]]; then
    echo "Error: Info.plist not found at ${INFO_PLIST_SRC}" >&2
    exit 1
fi

echo "==> Building (swift build -c release)..."
( cd "${MACOS_DIR}" && swift build -c release )

if [[ ! -f "${RELEASE_BIN}" ]]; then
    echo "Error: release binary not found at ${RELEASE_BIN}" >&2
    exit 1
fi

echo "==> Cleaning previous .app bundle (if any)..."
rm -rf "${APP_BUNDLE}"

echo "==> Assembling .app bundle structure..."
mkdir -p "${MACOS_BIN_DIR}"
mkdir -p "${RESOURCES_DIR}"

echo "==> Copying executable into Contents/MacOS/..."
cp "${RELEASE_BIN}" "${MACOS_BIN_DIR}/${APP_NAME}"
chmod +x "${MACOS_BIN_DIR}/${APP_NAME}"

echo "==> Stripping binary symbols (strip -x)..."
strip -x "${MACOS_BIN_DIR}/${APP_NAME}"

echo "==> Copying Info.plist into Contents/..."
cp "${INFO_PLIST_SRC}" "${CONTENTS_DIR}/Info.plist"

echo "==> Done."
echo "    .app bundle: ${APP_BUNDLE}"
