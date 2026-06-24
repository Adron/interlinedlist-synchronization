#!/usr/bin/env bash
#
# build-pkg.sh
#
# Produces an InterlinedSync.pkg installer from a pre-built .app bundle. The
# .pkg is signed when APPLE_DEVELOPER_ID_INSTALLER is provided; otherwise an
# unsigned .pkg is emitted (useful for local smoke tests).
#
# Run from the repository root, AFTER build-app.sh has produced the .app:
#
#   bash macos/scripts/build-pkg.sh
#
# Output: macos/build/InterlinedSync-<version>.pkg
#
# Optional environment variables:
#   APPLE_DEVELOPER_ID_INSTALLER   e.g. "Developer ID Installer: NAME (TEAMID)"

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

APP_NAME="InterlinedSync"
BUNDLE_ID="com.interlinedlist.sync"
BUILD_DIR="${MACOS_DIR}/build"
APP_BUNDLE="${BUILD_DIR}/${APP_NAME}.app"
INFO_PLIST="${APP_BUNDLE}/Contents/Info.plist"

if [[ ! -d "${APP_BUNDLE}" ]]; then
    echo "Error: .app bundle not found at ${APP_BUNDLE}" >&2
    echo "       Run 'bash macos/scripts/build-app.sh' first." >&2
    exit 1
fi

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${INFO_PLIST}")"
OUT_PKG="${BUILD_DIR}/${APP_NAME}-${VERSION}.pkg"

APPLE_DEVELOPER_ID_INSTALLER="${APPLE_DEVELOPER_ID_INSTALLER:-}"

echo "==> Building .pkg via productbuild (version ${VERSION})..."
rm -f "${OUT_PKG}"

if [[ -n "${APPLE_DEVELOPER_ID_INSTALLER}" ]]; then
    productbuild \
        --component "${APP_BUNDLE}" /Applications \
        --identifier "${BUNDLE_ID}" \
        --version "${VERSION}" \
        --sign "${APPLE_DEVELOPER_ID_INSTALLER}" \
        "${OUT_PKG}"
    echo "==> Done."
    echo "    Signed .pkg: ${OUT_PKG}"
else
    productbuild \
        --component "${APP_BUNDLE}" /Applications \
        --identifier "${BUNDLE_ID}" \
        --version "${VERSION}" \
        "${OUT_PKG}"
    echo "==> APPLE_DEVELOPER_ID_INSTALLER not set; .pkg is UNSIGNED."
    echo "==> Done."
    echo "    Unsigned .pkg: ${OUT_PKG}"
fi
