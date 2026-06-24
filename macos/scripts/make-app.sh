#!/usr/bin/env bash
#
# make-app.sh
#
# Back-compat shim. New code should call build-app.sh directly; this wrapper
# exists so existing callers (and the old release.yml step name) keep working.
#
#   bash macos/scripts/make-app.sh
#
# Output: macos/build/InterlinedSync.app

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec bash "${SCRIPT_DIR}/build-app.sh" "$@"
</content>
