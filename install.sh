#!/usr/bin/env bash
# Install the patch into the game folder.
# Usage: ./install.sh [source_dir]   (default: build/release, falls back to _local_only/release)
# Before copying, checks that the game files are the UCP 6.4b originals or already patched by this project
# (tools/BuildHelper prepare-target) and keeps UCP 6.4b *.orig backups of the replaced files.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
# game folder: GAME=<.../MasterOfOrion_Data>, else MOO_DIR=<game folder>, else auto-detected
if [ -z "${GAME:-}" ]; then
  source "$ROOT/tools/find_moo/find_moo.sh" || exit 1
  GAME="$MOO_DIR/MasterOfOrion_Data"; echo "Game folder: $MOO_DIR   [found via $MOO_HOW]"
fi
SRC="${1:-$ROOT/build/release}"
# an explicitly given folder must contain a build; only the default falls back to the archived release
if [ ! -f "$SRC/MooFix.dll" ] && [ -n "${1:-}" ]; then echo "$SRC does not contain a build (MooFix.dll missing)."; exit 1; fi
if [ ! -f "$SRC/MooFix.dll" ]; then SRC="$ROOT/_local_only/release"; fi
pick() { if [ -f "$SRC/$1" ]; then echo "$SRC/$1"; else echo "$SRC/$2"; fi; }
[ -f "$(pick MooFix.dll Managed/MooFix.dll)" ] || { echo "No build found. Run ./build.sh first."; exit 1; }
if tasklist 2>/dev/null | grep -qi MasterOfOrion; then echo "Close the game first."; exit 1; fi

# the target check needs the BuildHelper built by build.sh and the .NET SDK
HELP="$ROOT/build/tools/BuildHelper/BuildHelper.dll"
[ -f "$HELP" ] || { echo "$HELP not found. Run ./build.sh first."; exit 1; }
if ! command -v dotnet >/dev/null 2>&1 && [ -x "/c/Program Files/dotnet/dotnet.exe" ]; then PATH="/c/Program Files/dotnet:$PATH"; fi
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found. Run ./setup.sh."; exit 1; }

echo "== checking the game files"
dotnet "$HELP" prepare-target "$GAME" "$(pick Assembly-CSharp.dll Managed/Assembly-CSharp.dll)" \
  "$(pick Assembly-CSharp-firstpass.dll Managed/Assembly-CSharp-firstpass.dll)" \
  "$(pick EmpireManagement.swf StreamingAssets/EmpireManagement.swf)" || { echo "Installation cancelled."; exit 1; }

echo "== installing"
for f in Assembly-CSharp.dll Assembly-CSharp-firstpass.dll MooFix.dll; do cp "$(pick "$f" "Managed/$f")" "$GAME/Managed/$f"; done
cp "$(pick EmpireManagement.swf StreamingAssets/EmpireManagement.swf)" "$GAME/StreamingAssets/EmpireManagement.swf"
# profiling builds (build/perf) also need MooPerf.dll; release builds must not leave it behind
if [ -f "$(pick MooPerf.dll Managed/MooPerf.dll)" ]; then cp "$(pick MooPerf.dll Managed/MooPerf.dll)" "$GAME/Managed/"; else rm -f "$GAME/Managed/MooPerf.dll"; fi
echo "Installed from $SRC"
