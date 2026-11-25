#!/usr/bin/env bash
# Maintainer: build the binary release zip.   ./tools/make_release.sh <version>   (e.g. v1.0.0)
# Output: build/dist/MOO2016_FCP-<version>.zip
#   install.cmd, uninstall.cmd, README.txt, LICENSE.md
#   files/FcpPatch.exe          installer (.NET Framework 4.8)
#   files/FixPatcher.exe + Mono.Cecil*.dll   patches the two game DLLs on the user's PC (same patcher as the build)
#   files/MooFix.dll            this project's code
#   files/EmpireManagement.swf.fcpd   delta UCP 6.4b SWF -> patched SWF (only the changed P-code)
#   files/manifest.txt          SHA-256 of the expected results (the installer refuses anything else)
#   files/find_moo.cmd          game folder detection
# No game or UCP code is included.
set -euo pipefail
VER="${1:?usage: tools/make_release.sh <version>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"; B="$ROOT/build"
cd "$ROOT"
SRCVER=$(grep -o 'FcpVersion = "[^"]*"' src/MooFix/MooFix.cs | cut -d'"' -f2)
[ "$SRCVER" = "$VER" ] || { echo "ERROR: MooFix.FcpVersion is \"$SRCVER\" but the release is \"$VER\" (update src/MooFix/MooFix.cs)"; exit 1; }
./build.sh
dotnet build -c Release tools/FcpPatch -o "$B/tools/FcpPatch" > "$B/log_build_FcpPatch.txt" 2>&1 || { tail -n 20 "$B/log_build_FcpPatch.txt"; exit 1; }
dotnet build -c Release tools/FixPatcher48 -o "$B/tools/FixPatcher48" > "$B/log_build_FixPatcher48.txt" 2>&1 || { tail -n 20 "$B/log_build_FixPatcher48.txt"; exit 1; }
FP="$B/tools/FcpPatch/FcpPatch.exe"

D="$B/dist/MOO2016_FCP-$VER"; rm -rf "$D" "$D.zip"; mkdir -p "$D/files"
cp "$B/tools/FcpPatch/FcpPatch.exe" "$B/tools/FcpPatch/FcpPatch.exe.config" "$D/files/"
cp "$B/tools/FixPatcher48/FixPatcher.exe" "$B/tools/FixPatcher48/FixPatcher.exe.config" "$B/tools/FixPatcher48"/Mono.Cecil*.dll "$D/files/"
cp "$B/release/MooFix.dll" "$D/files/"
cp tools/find_moo/find_moo.cmd "$D/files/"
"$FP" make "$B/orig/StreamingAssets/EmpireManagement.swf" "$B/release/EmpireManagement.swf" "$D/files/EmpireManagement.swf.fcpd"
{
  echo "# SHA-256 of the files this release produces (SWF: decompressed content)"
  for f in Assembly-CSharp.dll Assembly-CSharp-firstpass.dll EmpireManagement.swf; do
    echo "$f $("$FP" hash "$(cygpath -w "$B/release/$f" 2>/dev/null || echo "$B/release/$f")" | tr -d '\r')"
  done
} > "$D/files/manifest.txt"
for f in install.cmd uninstall.cmd THIRD-PARTY-NOTICES.txt; do sed 's/\r$//; s/$/\r/' "tools/release/$f" > "$D/$f"; done
sed "s/{VERSION}/$VER/g; s/\r$//; s/$/\r/" tools/release/README.txt > "$D/README.txt"
cp LICENSE.md "$D/"
( cd "$B/dist" && powershell.exe -NoProfile -Command "Compress-Archive -Path 'MOO2016_FCP-$VER' -DestinationPath 'MOO2016_FCP-$VER.zip' -Force" )
echo "== $D.zip"
ls -la "$D/files"
