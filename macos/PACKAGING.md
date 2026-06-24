# Packaging InterlinedList Sync (macOS)

This document covers how to produce signed + notarized distribution artifacts
for the macOS client, and how the CI release pipeline switches between unsigned
and fully-signed builds based on which repository secrets are configured.

## Scripts

All scripts live in `macos/scripts/` and are POSIX-friendly bash with
`set -euo pipefail`. Run them from the repository root.

| Script | Purpose |
|--------|---------|
| `build-app.sh` | Builds the `.app` bundle from SwiftPM. Conditionally signs (Hardened Runtime + entitlements) and notarizes if the relevant env vars are set. |
| `build-pkg.sh` | Wraps a built `.app` into an `InterlinedSync-<version>.pkg` via `productbuild`. Signs it when `APPLE_DEVELOPER_ID_INSTALLER` is set. |
| `make-app.sh` | Back-compat shim that delegates to `build-app.sh`. |
| `make-pkg.sh` | Legacy strict packaging script (requires all signing+notarization vars). Kept for parity with the earlier release tooling. |

### Local unsigned build

No secrets required. From the repo root:

```sh
bash macos/scripts/build-app.sh
bash macos/scripts/build-pkg.sh
```

Outputs:

- `macos/build/InterlinedSync.app`
- `macos/build/InterlinedSync-<version>.pkg`

### Local signed build (for testing on your own machine)

You need a Developer ID Application certificate installed in your login
keychain. Then:

```sh
export APPLE_DEVELOPER_ID_APPLICATION="Developer ID Application: Your Name (ABCDE12345)"
export APPLE_DEVELOPER_ID_INSTALLER="Developer ID Installer: Your Name (ABCDE12345)"

bash macos/scripts/build-app.sh
bash macos/scripts/build-pkg.sh
```

To also notarize locally:

```sh
export APPLE_ID="you@example.com"
export APPLE_APP_SPECIFIC_PASSWORD="abcd-efgh-ijkl-mnop"
export APPLE_TEAM_ID="ABCDE12345"

bash macos/scripts/build-app.sh
```

## GitHub Actions secrets

The release workflow (`.github/workflows/release.yml`) reads the following
repository secrets. Until they are set, the macOS job produces unsigned
artifacts (current behavior); once they are set, signing and notarization
turn on automatically.

| Secret | How to obtain |
|--------|---------------|
| `APPLE_DEVELOPER_ID_APPLICATION` | Common name of the Developer ID Application certificate in Keychain Access. Example: `Developer ID Application: Your Name (ABCDE12345)`. Create via Apple Developer portal → Certificates, Identifiers & Profiles → Certificates → +. |
| `APPLE_DEVELOPER_ID_INSTALLER` | Common name of the Developer ID Installer certificate. Created the same way (separate certificate type). |
| `APPLE_CERTIFICATES_P12_BASE64` | Both certificates plus their private keys exported as a single `.p12` from Keychain Access, then base64-encoded: `base64 -i certs.p12 \| pbcopy`. The `apple-actions/import-codesign-certs` action loads this into the runner's temporary keychain. |
| `APPLE_CERTIFICATES_P12_PASSWORD` | The password you set when exporting the `.p12`. |
| `APPLE_ID` | Apple ID email associated with the Developer Program account. |
| `APPLE_APP_SPECIFIC_PASSWORD` | Generate at https://appleid.apple.com → Sign-In and Security → App-Specific Passwords. Label it `notarytool` so it is easy to revoke. |
| `APPLE_TEAM_ID` | 10-character identifier from Apple Developer → Membership. |

Add each via **Settings → Secrets and variables → Actions → New repository secret**.

### How the workflow decides what to do

1. If `APPLE_CERTIFICATES_P12_BASE64` is empty, no certificates are imported
   and `build-app.sh` produces an unsigned `.app`.
2. If `APPLE_DEVELOPER_ID_APPLICATION` is empty inside `build-app.sh`, signing
   is skipped (even if certs happen to be installed).
3. If any of `APPLE_ID`, `APPLE_APP_SPECIFIC_PASSWORD`, or `APPLE_TEAM_ID` is
   empty, notarization is skipped.
4. `build-pkg.sh` signs the `.pkg` only when `APPLE_DEVELOPER_ID_INSTALLER`
   is set; otherwise it produces an unsigned `.pkg` (still useful for
   distribution if the inner `.app` is signed + notarized + stapled).

## Entitlements

`macos/InterlinedSync.entitlements` declares the sandboxed entitlement set:

- `com.apple.security.app-sandbox` — sandboxed app
- `com.apple.security.network.client` — HTTPS to `interlinedlist.com`
- `com.apple.security.files.user-selected.read-write` — user-chosen sync folder
- `com.apple.security.files.bookmarks.app-scope` — persisted security-scoped
  bookmarks so the sync folder survives restarts
- `com.apple.keychain-access-groups` — Keychain item sharing scope

No `network.server`, no temporary exceptions, no privileged entitlements.

## Path to App Store distribution

If we later want a Mac App Store build:

- Switch to **Apple Distribution** + **Mac App Distribution** certificates.
- Drop Developer ID notarization (App Store ingest handles equivalent checks).
- Add `PrivacyInfo.xcprivacy` declaring each accessed API category
  (`NSPrivacyAccessedAPICategoryFileTimestamp`, etc.).
- Audit entitlements against App Store-permitted entitlements; remove anything
  flagged by `productbuild --check-signature`.
- Use `xcrun altool --upload-app` or Xcode Organizer to submit the `.pkg`.

The current sandboxed entitlement set was deliberately chosen to make this
transition straightforward.

