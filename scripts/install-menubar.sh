#!/bin/sh
# Build and install the local macOS controller. The backend remains in its repo.
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
build_app="$project_root/.local/menubar/AgentDesk.app"
installed_app="$HOME/Applications/AgentDesk.app"
if [ ! -f "$project_root/.local/runtime/AgentDesk.dll" ]; then
  echo 'Build AgentDesk first with scripts/build.sh.' >&2
  exit 1
fi
mkdir -p "$build_app/Contents/MacOS" "$HOME/Applications"
xcrun swiftc -swift-version 6 -parse-as-library -O -target "$(uname -m)-apple-macos15.0" \
  "$project_root/macos/AgentDeskMenu.swift" \
  -o "$build_app/Contents/MacOS/AgentDesk"
python3 - "$build_app/Contents/Info.plist" "$project_root" <<'PY'
import plistlib
import sys

with open(sys.argv[1], 'wb') as destination:
    plistlib.dump({
        'CFBundleIdentifier': 'local.agentdesk.menubar',
        'CFBundleName': 'AgentDesk',
        'CFBundleDisplayName': 'AgentDesk',
        'CFBundleExecutable': 'AgentDesk',
        'CFBundlePackageType': 'APPL',
        'CFBundleShortVersionString': '0.3.0',
        'CFBundleVersion': '1',
        'LSMinimumSystemVersion': '15.0',
        'LSUIElement': True,
        'NSHighResolutionCapable': True,
        'AgentDeskProjectRoot': sys.argv[2],
    }, destination)
PY
codesign --force --sign - "$build_app"
# Stop only this installed controller when replacing it, never the backend.
pkill -f "^$installed_app/Contents/MacOS/AgentDesk$" 2>/dev/null || true
ditto "$build_app" "$installed_app"
open "$installed_app"
echo "AgentDesk menu bar installed: $installed_app"
