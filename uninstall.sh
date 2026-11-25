#!/usr/bin/env bash
# Restore the original (UCP) files from the *.orig backups created by install.sh.
set -euo pipefail
# game folder: GAME=<.../MasterOfOrion_Data>, else MOO_DIR=<game folder>, else auto-detected
if [ -z "${GAME:-}" ]; then
  source "$(cd "$(dirname "$0")" && pwd)/tools/find_moo/find_moo.sh" || exit 1
  GAME="$MOO_DIR/MasterOfOrion_Data"; echo "Game folder: $MOO_DIR   [found via $MOO_HOW]"
fi
if tasklist 2>/dev/null | grep -qi MasterOfOrion; then echo "Close the game first."; exit 1; fi
for f in Managed/Assembly-CSharp.dll Managed/Assembly-CSharp-firstpass.dll StreamingAssets/EmpireManagement.swf; do
  if [ -f "$GAME/$f.orig" ]; then cp -p "$GAME/$f.orig" "$GAME/$f"; echo "restored $f"; else echo "no backup for $f (skipped)"; fi
done
rm -f "$GAME/Managed/MooFix.dll" "$GAME/Managed/MooPerf.dll"
echo "Done. (Steam 'Verify integrity of game files' also restores the files, but then UCP must be reinstalled.)"
