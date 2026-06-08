#!/usr/bin/env bash
#
# make-pkg.sh
#
# Signs, packages, notarizes, and staples the InterlinedSync .app bundle
# into a distributable .pkg for Developer ID (direct distribution).
#
# Run from the repository root:
#
#   bash macos/scripts/make-pkg.sh
#
# Required environment variables:
#   DEVELOPER_ID_APP        e.g. "Developer ID Application: NAME (TEAMID)"
#   DEVELOPER_ID_INSTALLER  e.g. "Developer ID Installer: NAME (TEAMID)"
#   APPLE_ID                Apple ID email for notarytool
#   TEAM_ID                 Apple Developer Team ID
#   APP_SPECIFIC_PASSWORD   App-specific password for notarytool
#
# Optional:
#   SKIP_NOTARIZE=1         Skip notarization + stapling (local testing)

set -euo pipefail

# Resolve the macos/ directory regardless of where the script is invoked from.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

APP_NAME="InterlinedSync"
VERSION="0.1.0"
BUNDLE_ID="com.interlinedlist.sync"

BUILD_DIR="${MACOS_DIR}/build"
APP_BUNDLE="${BUILD_DIR}/${APP_NAME}.app"
ENTITLEMENTS="${MACOS_DIR}/${APP_NAME}.entitlements"
COMPONENT_PKG="${BUILD_DIR}/${APP_NAME}-component.pkg"
DIST_PKG="${BUILD_DIR}/${APP_NAME}-${VERSION}.pkg"
DISTRIBUTION_XML="${MACOS_DIR}/scripts/distribution.xml"

SKIP_NOTARIZE="${SKIP_NOTARIZE:-0}"

echo "==> Checking required environment variables..."
missing=0
require_var() {
    local name="$1"
    if [[ -z "${!name:-}" ]]; then
        echo "Error: ${name} is not set." >&2
        missing=1
    fi
}

require_var "DEVELOPER_ID_APP"
require_var "DEVELOPER_ID_INSTALLER"

if [[ "${SKIP_NOTARIZE}" != "1" ]]; then
    require_var "APPLE_ID"
    require_var "TEAM_ID"
    require_var "APP_SPECIFIC_PASSWORD"
fi

if [[ "${missing}" -ne 0 ]]; then
    echo "" >&2
    echo "One or more required environment variables are missing. Aborting." >&2
    echo "Set SKIP_NOTARIZE=1 to skip notarization and stapling for local testing." >&2
    exit 1
fi

echo "==> Verifying inputs..."
if [[ ! -d "${APP_BUNDLE}" ]]; then
    echo "Error: .app bundle not found at ${APP_BUNDLE}" >&2
    echo "       Run 'bash macos/scripts/make-app.sh' first." >&2
    exit 1
fi
if [[ ! -f "${ENTITLEMENTS}" ]]; then
    echo "Error: entitlements not found at ${ENTITLEMENTS}" >&2
    exit 1
fi
if [[ ! -f "${DISTRIBUTION_XML}" ]]; then
    echo "Error: distribution.xml not found at ${DISTRIBUTION_XML}" >&2
    exit 1
fi

echo "==> Step 1: Signing .app bundle with Developer ID Application..."
codesign \
    --deep \
    --force \
    --options runtime \
    --entitlements "${ENTITLEMENTS}" \
    --sign "${DEVELOPER_ID_APP}" \
    "${APP_BUNDLE}"

echo "==> Step 2: Verifying signature..."
codesign --verify --deep --strict --verbose=2 "${APP_BUNDLE}"
spctl --assess --type exec --verbose "${APP_BUNDLE}" || {
    echo "Warning: spctl assessment failed (expected before notarization)." >&2
}

echo "==> Step 3: Building component .pkg with pkgbuild..."
rm -f "${COMPONENT_PKG}"
pkgbuild \
    --root "${APP_BUNDLE}" \
    --install-location "/Applications/${APP_NAME}.app" \
    --identifier "${BUNDLE_ID}" \
    --version "${VERSION}" \
    --sign "${DEVELOPER_ID_INSTALLER}" \
    "${COMPONENT_PKG}"

echo "==> Step 4: Building distribution .pkg with productbuild..."
rm -f "${DIST_PKG}"
productbuild \
    --distribution "${DISTRIBUTION_XML}" \
    --package-path "${BUILD_DIR}" \
    --sign "${DEVELOPER_ID_INSTALLER}" \
    "${DIST_PKG}"

if [[ "${SKIP_NOTARIZE}" == "1" ]]; then
    echo "==> SKIP_NOTARIZE=1 set; skipping notarization and stapling."
    echo "==> Done (unsigned-for-Gatekeeper)."
    echo "    .pkg: ${DIST_PKG}"
    exit 0
fi

echo "==> Step 5: Submitting to Apple notary service (xcrun notarytool)..."
xcrun notarytool submit "${DIST_PKG}" \
    --apple-id "${APPLE_ID}" \
    --team-id "${TEAM_ID}" \
    --password "${APP_SPECIFIC_PASSWORD}" \
    --wait

echo "==> Step 6: Stapling notarization ticket..."
xcrun stapler staple "${DIST_PKG}"

echo "==> Step 7: Verifying final package with spctl..."
spctl --assess --type install --verbose "${DIST_PKG}"

echo "==> Done."
echo "    Signed + notarized + stapled .pkg: ${DIST_PKG}"
