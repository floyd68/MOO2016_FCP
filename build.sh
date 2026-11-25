#!/usr/bin/env bash
# Rebuild the MOO2016_FCP performance patch from the original (UCP 6.4b) game files.
#
# Usage (Git Bash):  ./build.sh            -> release build into build/release
#                    PERF=1 ./build.sh     -> also build a profiling build into build/perf
#                    VERIFY=1 ./build.sh   -> also JIT-verify the patched DLLs against the originals
#
# Inputs (override with environment variables):
#   MOO_DIR       game folder (contains MasterOfOrion_Data); default: auto-detected (tools/find_moo/find_moo.sh)
#   GAME_MANAGED  the game's MasterOfOrion_Data/Managed folder (for UnityEngine.dll etc.); default: from MOO_DIR
#   ORIG_MANAGED  folder with the ORIGINAL (UCP 6.4b) Assembly-CSharp.dll / Assembly-CSharp-firstpass.dll; default: GAME_MANAGED
#   ORIG_SWF      the ORIGINAL (UCP 6.4b) EmpireManagement.swf; default: the game's StreamingAssets
#                 (for each file, "<file>.orig" - the backup made by install - is used when the file itself is already patched)
#   FFDEC_JAR     JPEXS FFDec jar; default: tools/ffdec/ffdec.jar, then the FFDec installer folders
# Requirements: .NET SDK 9+, Java 8+, JPEXS FFDec, Master of Orion with UCP 6.4b. Missing tools: run ./setup.sh first.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
B="$ROOT/build"

# run a step with its output in a log; on failure show the end of the log and stop
step() { # $1 = log file, rest = command
  local log="$1"; shift
  if ! "$@" > "$log" 2>&1; then
    echo; echo "BUILD FAILED: $*"; echo "---- last lines of $log ----"; tail -n 30 "$log"; echo "----"
    echo "If the log mentions NuGet or a network error, check the internet connection and run ./build.sh again."
    exit 1
  fi
}

echo "== checking tools"
source "$ROOT/tools/check_tools.sh"
if [ "$TOOLS_MISSING" != 0 ]; then
  echo "ERROR: required tools are missing (see above). Run  ./setup.sh  - it explains and can install them." >&2; exit 1
fi

if [ -z "${GAME_MANAGED:-}" ]; then
  source "$ROOT/tools/find_moo/find_moo.sh" || exit 1
  GAME_MANAGED="$MOO_DIR/MasterOfOrion_Data/Managed"; echo "Game folder: $MOO_DIR   [found via $MOO_HOW]"
fi
[ -f "$GAME_MANAGED/UnityEngine.dll" ] || { echo "ERROR: $GAME_MANAGED/UnityEngine.dll not found - GAME_MANAGED/MOO_DIR does not point to the game." >&2; exit 1; }
ORIG_MANAGED="${ORIG_MANAGED:-$GAME_MANAGED}"
ORIG_SWF="${ORIG_SWF:-$GAME_MANAGED/../StreamingAssets/EmpireManagement.swf}"
mkdir -p "$B/pub" "$B/release" "$B/perf"

echo "== tools (the first build downloads NuGet packages)"
for p in BuildHelper FixPatcher PerfPatcher Verify; do step "$B/log_build_$p.txt" dotnet build -c Release "$ROOT/tools/$p" -o "$B/tools/$p"; done
HELP="$B/tools/BuildHelper/BuildHelper.dll"
FIX="$B/tools/FixPatcher/FixPatcher.dll"; PERFP="$B/tools/PerfPatcher/Patcher.dll"; [ -f "$PERFP" ] || PERFP="$B/tools/PerfPatcher/PerfPatcher.dll"

echo "== original files (UCP 6.4b, checked by SHA-256)"
rm -rf "$B/orig"
dotnet "$HELP" originals "$B/orig" "$ORIG_MANAGED/Assembly-CSharp.dll" "$ORIG_MANAGED/Assembly-CSharp-firstpass.dll" "$ORIG_SWF"
ORIG_MANAGED="$B/orig/Managed"
ORIG_SWF="$B/orig/StreamingAssets/EmpireManagement.swf"

echo "== publicized reference assembly (compile-time only)"
dotnet "$FIX" publicize "$ORIG_MANAGED/Assembly-CSharp.dll" "$B/pub/Assembly-CSharp.dll" "$GAME_MANAGED"

echo "== MooFix / MooPerf"
step "$B/log_build_MooFix.txt" dotnet build -c Release "$ROOT/src/MooFix" -p:GameManaged="$(cygpath -w "$GAME_MANAGED")" -p:PubDir="$(cygpath -w "$B/pub")" -o "$B/moofix"
step "$B/log_build_MooPerf.txt" dotnet build -c Release "$ROOT/src/MooPerf" -o "$B/mooperf"
cp "$B/moofix/MooFix.dll" "$B/release/"

echo "== inject prefix hooks"
step "$B/patch_acs.log" dotnet "$FIX" patch "$ORIG_MANAGED/Assembly-CSharp.dll"           "$B/release/MooFix.dll" "$B/release/Assembly-CSharp.dll"           "$GAME_MANAGED"
step "$B/patch_fp.log"  dotnet "$FIX" patch "$ORIG_MANAGED/Assembly-CSharp-firstpass.dll" "$B/release/MooFix.dll" "$B/release/Assembly-CSharp-firstpass.dll" "$GAME_MANAGED"
grep -h "injected" "$B/patch_acs.log" "$B/patch_fp.log" || true

echo "== SWF: replace method bodies (P-code) in EmpireManagement.swf"
# Method body indices are specific to the UCP 6.4b EmpireManagement.swf (see docs/05_SWF_PATCHING.md).
# Only diffs are distributed (swf/patches); the original bodies come from the user's own SWF.
S="$B/pcode"
rm -rf "$B/pcode_export" "$S" "$B/release/EmpireManagement.swf"
step "$B/ffdec_export.log" java -jar "$FFDEC_JAR" -format script:pcode -selectclass ngd.ColonyWindow,ngd.ASRoot,fl.controls.ScrollBar -export script "$B/pcode_export" "$ORIG_SWF"
[ -f "$B/pcode_export/scripts/ngd/ColonyWindow.pcode" ] || { echo "ERROR: FFDec P-code export failed:"; tail -n 30 "$B/ffdec_export.log"; exit 1; }
dotnet "$HELP" apply "$B/pcode_export" "$ROOT/swf/patches" "$S"
step "$B/ffdec_release.log" java -jar "$FFDEC_JAR" -replace "$ORIG_SWF" "$B/release/EmpireManagement.swf" \
  ngd.ColonyWindow      "$S/ctor.pcode"       712 \
  ngd.ColonyWindow      "$S/fill.pcode"       718 \
  ngd.ColonyWindow      "$S/adj.pcode"        1236 \
  ngd.ColonyWindow      "$S/buy.pcode"        1237 \
  ngd.ASRoot            "$S/scroll.pcode"     979 \
  fl.controls.ScrollBar "$S/sb_press.pcode"   818 \
  fl.controls.ScrollBar "$S/sb_release.pcode" 820
NREP=$(grep -c "Replace AS3 PCode" "$B/ffdec_release.log" || true)
echo "  method bodies replaced: $NREP (expected 7)"
if [ "$NREP" != 7 ] || [ ! -f "$B/release/EmpireManagement.swf" ]; then
  echo "ERROR: SWF patch incomplete:"; tail -n 30 "$B/ffdec_release.log"; exit 1
fi

if [ "${PERF:-0}" = "1" ]; then
  echo "== profiling build (MooPerf wrappers on ~1760 methods)"
  cp "$B/mooperf/MooPerf.dll" "$B/release/MooFix.dll" "$B/perf/"
  dotnet "$PERFP" "$B/release/Assembly-CSharp.dll"           "$B/perf/MooPerf.dll" "$B/perf/Assembly-CSharp.dll"           "$GAME_MANAGED"
  dotnet "$PERFP" "$B/release/Assembly-CSharp-firstpass.dll" "$B/perf/MooPerf.dll" "$B/perf/Assembly-CSharp-firstpass.dll" "$GAME_MANAGED"
  # optional: Flash-side timing of ASRoot.OnEnterFrame (body 973) -> MooFix logs "FLASH ..." lines
  rm -f "$B/perf/EmpireManagement.swf"
  step "$B/ffdec_perf.log" java -jar "$FFDEC_JAR" -replace "$B/release/EmpireManagement.swf" "$B/perf/EmpireManagement.swf" ngd.ASRoot "$S/enterframe.pcode" 973
  [ -f "$B/perf/EmpireManagement.swf" ] || { echo "ERROR: FFDec did not write the profiling SWF:"; tail -n 30 "$B/ffdec_perf.log"; exit 1; }
fi

if [ "${VERIFY:-0}" = "1" ]; then
  echo "== JIT verification (patched vs original; lists must be identical)"
  V="$B/tools/Verify/Verify.dll"
  for f in Assembly-CSharp.dll Assembly-CSharp-firstpass.dll; do
    a=$(ALLM=1 dotnet "$V" "$ORIG_MANAGED/$f" "$B/release" "$GAME_MANAGED" 2>&1 | grep "INVALID\|jitted" || true)
    b=$(ALLM=1 dotnet "$V" "$B/release/$f"     "$B/release" "$GAME_MANAGED" 2>&1 | grep "INVALID\|jitted" || true)
    if [ "$a" == "$b" ]; then echo "  $f: identical to original ($b)"; else echo "  $f: DIFFERS"; diff <(echo "$a") <(echo "$b") || true; fi
  done
fi
echo "== done: $B/release"
ls -la "$B/release"
echo "Next: ./install.sh  (close the game first)"
