#!/usr/bin/env bash
#
# build-app.sh
#
# Builds an InterlinedSync.app bundle from the SwiftPM Release build and
# conditionally signs + notarizes + staples it.
#
# Run from the repository root:
#
#   bash macos/scripts/build-app.sh
#
# Output: macos/build/InterlinedSync.app
#
# Optional environment variables (signing — all-or-nothing):
#   APPLE_DEVELOPER_ID_APPLICATION   e.g. "Developer ID Application: NAME (TEAMID)"
#
# Optional environment variables (notarization — all-or-nothing, only used if
# the signing variable above is set):
#   APPLE_ID                         Apple ID email for notarytool
#   APPLE_APP_SPECIFIC_PASSWORD      App-specific password from appleid.apple.com
#   APPLE_TEAM_ID                    Apple Developer 10-character Team ID
#
# Optional flags:
#   BUILD_ARCHS="arm64"              Space-separated; defaults to "arm64".
#                                    Use "arm64 x86_64" for a universal build.
#   SKIP_BUILD=1                     Reuse the existing .build/release output.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

APP_NAME="InterlinedSync"
BUILD_DIR="${MACOS_DIR}/build"
APP_BUNDLE="${BUILD_DIR}/${APP_NAME}.app"
CONTENTS_DIR="${APP_BUNDLE}/Contents"
MACOS_BIN_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

INFO_PLIST_SRC="${MACOS_DIR}/Info.plist"
ENTITLEMENTS_SRC="${MACOS_DIR}/${APP_NAME}.entitlements"

BUILD_ARCHS="${BUILD_ARCHS:-arm64}"
SKIP_BUILD="${SKIP_BUILD:-0}"

echo "==> Verifying inputs..."
if [[ ! -f "${INFO_PLIST_SRC}" ]]; then
    echo "Error: Info.plist not found at ${INFO_PLIST_SRC}" >&2
    exit 1
fi
if [[ ! -f "${ENTITLEMENTS_SRC}" ]]; then
    echo "Error: entitlements not found at ${ENTITLEMENTS_SRC}" >&2
    exit 1
fi

mkdir -p "${BUILD_DIR}"

build_arch() {
    local arch="$1"
    echo "==> Building (swift build -c release --arch ${arch})..."
    ( cd "${MACOS_DIR}" && swift build -c release --arch "${arch}" )
}

declare -a ARCH_BINS=()
if [[ "${SKIP_BUILD}" != "1" ]]; then
    for arch in ${BUILD_ARCHS}; do
        build_arch "${arch}"
        ARCH_BINS+=("${MACOS_DIR}/.build/${arch}-apple-macosx/release/${APP_NAME}")
    done
else
    echo "==> SKIP_BUILD=1 set; reusing existing release output."
    for arch in ${BUILD_ARCHS}; do
        ARCH_BINS+=("${MACOS_DIR}/.build/${arch}-apple-macosx/release/${APP_NAME}")
    done
fi

FINAL_BIN_SRC=""
if [[ "${#ARCH_BINS[@]}" -eq 1 ]]; then
    FINAL_BIN_SRC="${ARCH_BINS[0]}"
    if [[ ! -f "${FINAL_BIN_SRC}" ]]; then
        # Fall back to the default single-arch build path used when --arch is
        # omitted (e.g. SKIP_BUILD=1 against a prior `swift build -c release`).
        FALLBACK="${MACOS_DIR}/.build/release/${APP_NAME}"
        if [[ -f "${FALLBACK}" ]]; then
            FINAL_BIN_SRC="${FALLBACK}"
        fi
    fi
else
    FINAL_BIN_SRC="${BUILD_DIR}/${APP_NAME}.universal"
    echo "==> lipo'ing ${#ARCH_BINS[@]} architectures into ${FINAL_BIN_SRC}..."
    lipo -create -output "${FINAL_BIN_SRC}" "${ARCH_BINS[@]}"
fi

if [[ ! -f "${FINAL_BIN_SRC}" ]]; then
    echo "Error: release binary not found at ${FINAL_BIN_SRC}" >&2
    exit 1
fi

echo "==> Cleaning previous .app bundle (if any)..."
rm -rf "${APP_BUNDLE}"

echo "==> Assembling .app bundle structure..."
mkdir -p "${MACOS_BIN_DIR}"
mkdir -p "${RESOURCES_DIR}"

echo "==> Copying executable into Contents/MacOS/..."
cp "${FINAL_BIN_SRC}" "${MACOS_BIN_DIR}/${APP_NAME}"
chmod +x "${MACOS_BIN_DIR}/${APP_NAME}"

echo "==> Stripping binary symbols (strip -x)..."
strip -x "${MACOS_BIN_DIR}/${APP_NAME}"

echo "==> Copying Info.plist into Contents/..."
cp "${INFO_PLIST_SRC}" "${CONTENTS_DIR}/Info.plist"

BUNDLE_RES_DIR=""
for candidate in \
    "${MACOS_DIR}/.build/release/${APP_NAME}_${APP_NAME}.bundle" \
    "${MACOS_DIR}/.build/arm64-apple-macosx/release/${APP_NAME}_${APP_NAME}.bundle" \
    "${MACOS_DIR}/.build/x86_64-apple-macosx/release/${APP_NAME}_${APP_NAME}.bundle"; do
    if [[ -d "${candidate}" ]]; then
        BUNDLE_RES_DIR="${candidate}"
        break
    fi
done
if [[ -n "${BUNDLE_RES_DIR}" ]]; then
    echo "==> Copying SwiftPM resource bundle from ${BUNDLE_RES_DIR}..."
    cp -R "${BUNDLE_RES_DIR}" "${RESOURCES_DIR}/"
fi

APPLE_DEVELOPER_ID_APPLICATION="${APPLE_DEVELOPER_ID_APPLICATION:-}"
APPLE_ID="${APPLE_ID:-}"
APPLE_APP_SPECIFIC_PASSWORD="${APPLE_APP_SPECIFIC_PASSWORD:-}"
APPLE_TEAM_ID="${APPLE_TEAM_ID:-}"

if [[ -z "${APPLE_DEVELOPER_ID_APPLICATION}" ]]; then
    echo "==> APPLE_DEVELOPER_ID_APPLICATION not set; producing UNSIGNED bundle."
    echo "==> Done."
    echo "    .app bundle (unsigned): ${APP_BUNDLE}"
    exit 0
fi

echo "==> Signing .app with Hardened Runtime + entitlements..."
codesign \
    --force \
    --options runtime \
    --entitlements "${ENTITLEMENTS_SRC}" \
    --sign "${APPLE_DEVELOPER_ID_APPLICATION}" \
    --timestamp \
    "${APP_BUNDLE}"

echo "==> Verifying signature..."
codesign --verify --deep --strict --verbose=2 "${APP_BUNDLE}"

if [[ -z "${APPLE_ID}" || -z "${APPLE_APP_SPECIFIC_PASSWORD}" || -z "${APPLE_TEAM_ID}" ]]; then
    echo "==> Notarization credentials not all set; skipping notarization."
    echo "    Set APPLE_ID, APPLE_APP_SPECIFIC_PASSWORD, APPLE_TEAM_ID to notarize."
    echo "==> Done (signed, not notarized)."
    echo "    .app bundle: ${APP_BUNDLE}"
    exit 0
fi

NOTARY_ZIP="${BUILD_DIR}/${APP_NAME}-notarize.zip"
echo "==> Zipping for notarization submission..."
rm -f "${NOTARY_ZIP}"
( cd "${BUILD_DIR}" && ditto -c -k --sequesterRsrc --keepParent "${APP_NAME}.app" "${NOTARY_ZIP}" )

echo "==> Submitting to Apple notary service (xcrun notarytool)..."
xcrun notarytool submit "${NOTARY_ZIP}" \
    --apple-id "${APPLE_ID}" \
    --team-id "${APPLE_TEAM_ID}" \
    --password "${APPLE_APP_SPECIFIC_PASSWORD}" \
    --wait

echo "==> Stapling notarization ticket..."
xcrun stapler staple "${APP_BUNDLE}"

echo "==> Verifying staple with spctl..."
spctl --assess --type exec --verbose "${APP_BUNDLE}" || {
    echo "Warning: spctl assessment failed." >&2
}

rm -f "${NOTARY_ZIP}"

echo "==> Done."
echo "    .app bundle (signed + notarized + stapled): ${APP_BUNDLE}"

