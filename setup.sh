#!/usr/bin/env bash
# setup.sh - check everything the build needs and install missing tools after asking (Git Bash).
#
# Usage:  ./setup.sh         ask before each installation
#         ./setup.sh yes     install missing tools without asking
#         ./setup.sh no      only report, install nothing
#
# Installs (only after confirmation):
#   JPEXS FFDec 26.5.0   downloaded from GitHub into tools/ffdec/ (SHA-256 checked; nothing installed system-wide)
#   .NET SDK 9           winget install Microsoft.DotNet.SDK.9
#   Java (Temurin 21)    winget install EclipseAdoptium.Temurin.21.JRE
# Cannot install (reported with instructions): Master of Orion, UCP 6.4b.
set -uo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
MODE="${1:-}"
FFDEC_VER=26.5.0
FFDEC_URL="https://github.com/jindrapetrik/jpexs-decompiler/releases/download/version26.5.0/ffdec_26.5.0.zip"
FFDEC_SHA=8d00c2713a724bb3f7e6c9a64994836ce3a953b5ac264f405d98f92e7c266d33
INSTALLED_SOMETHING=

ask() { # $1 = question -> returns 0 for yes
  case "$MODE" in yes) return 0;; no) return 1;; esac
  local a; read -r -p "$1 [Y/N]? " a || return 1
  case "$a" in [Yy]*) return 0;; *) return 1;; esac
}
ffdec_manual() {
  echo "  Manual: download ffdec_$FFDEC_VER.zip from https://github.com/jindrapetrik/jpexs-decompiler/releases"
  echo "          and extract it to $ROOT/tools/ffdec/ (tools/ffdec/ffdec.jar must exist), or set FFDEC_JAR."
}
get_ffdec() {
  echo "JPEXS FFDec $FFDEC_VER is needed to patch the Flash UI. It is downloaded into tools/ffdec/ only."
  echo "  source: $FFDEC_URL"
  ask "Download and extract JPEXS FFDec $FFDEC_VER now" || { ffdec_manual; return; }
  local zip="$ROOT/build/ffdec_$FFDEC_VER.zip" tar="/c/Windows/System32/tar.exe"
  mkdir -p "$ROOT/build" "$ROOT/tools/ffdec"
  curl -fL --retry 2 -o "$zip" "$FFDEC_URL" || { echo "  download failed."; ffdec_manual; return; }
  if [ "$(sha256sum "$zip" | cut -c1-64)" != "$FFDEC_SHA" ]; then echo "  SHA-256 mismatch - file deleted."; rm -f "$zip"; ffdec_manual; return; fi
  if command -v unzip >/dev/null 2>&1; then unzip -q -o "$zip" -d "$ROOT/tools/ffdec"
  elif [ -x "$tar" ]; then "$tar" -xf "$(cygpath -w "$zip")" -C "$(cygpath -w "$ROOT/tools/ffdec")"
  else echo "  no unzip/tar to extract."; ffdec_manual; return; fi || { echo "  extraction failed."; ffdec_manual; return; }
  rm -f "$zip"; echo "  JPEXS FFDec extracted to tools/ffdec/"; INSTALLED_SOMETHING=1
}
winget_install() { # $1 = id, $2 = name, $3 = manual page
  echo "$2 is needed."
  if [ -z "$HAVE_WINGET" ]; then echo "  winget is not available. Install it manually from $3 (or get \"App Installer\" from the Microsoft Store)."; return; fi
  ask "Install $2 now with winget (may show a Windows administrator prompt)" || { echo "  Skipped. Install it manually from $3"; return; }
  if winget install --id "$1" --exact --accept-source-agreements --accept-package-agreements; then INSTALLED_SOMETHING=1
  else echo "  winget reported an error. Install it manually from $3"; fi
}
ucp_file() { # $1 = game file, $2 = expected sha256; file or its .orig must match
  local c
  for c in "$1" "$1.orig"; do
    if [ -f "$c" ] && [ "$(sha256sum "$c" | cut -c1-64)" = "$2" ]; then echo "            $(basename "$c")"; return 0; fi
  done
  echo "            $(basename "$1"): not UCP 6.4b"; return 1
}

echo "== Build tools"
source "$ROOT/tools/check_tools.sh"
if [ "$TOOLS_MISSING" != 0 ]; then
  echo
  [ -n "$FFDEC_JAR" ] || get_ffdec
  [ -n "$DOTNET_OK" ] || winget_install Microsoft.DotNet.SDK.9 ".NET SDK 9" "https://dotnet.microsoft.com/download"
  [ -n "$JAVA_OK" ] || winget_install EclipseAdoptium.Temurin.21.JRE "Java runtime (Eclipse Temurin 21 JRE)" "https://adoptium.net/"
  echo; echo "== Build tools (after setup)"
  source "$ROOT/tools/check_tools.sh"
fi

echo; echo "== Game and UCP"
GAME_OK=; UCP_OK=
if source "$ROOT/tools/find_moo/find_moo.sh"; then
  GAME_OK=1; echo "  [ok]      Master of Orion  $MOO_DIR   [found via $MOO_HOW]"
  D="$MOO_DIR/MasterOfOrion_Data"; bad=0
  ucp_file "$D/Managed/Assembly-CSharp.dll" 915c5eacf2314a64a7f39ac243c45333f07e96b70e5c1ec26b90888d64b5895a || bad=1
  ucp_file "$D/Managed/Assembly-CSharp-firstpass.dll" ab936ff9ea0bd8157e5a5d503dc4def9ea13c731aa737f82c006bd5d38d039e6 || bad=1
  ucp_file "$D/StreamingAssets/EmpireManagement.swf" 55d6a8489a816ae9c794affacedf6dfeb8603cfd92b782842d19f4cfb5932e51 || bad=1
  if [ $bad = 0 ]; then UCP_OK=1; echo "  [ok]      UCP 6.4b original files"
  else
    echo "  [missing] UCP 6.4b original files"
    echo "            Install UCP 6.4b: run the game once to the main menu and quit, subscribe to"
    echo "            https://steamcommunity.com/sharedfiles/filedetails/?id=1109736522"
    echo "            then run javaInstaller.bat in .../steamapps/workshop/content/298050/1109736522"
    echo "            and keep the \"Unofficial Code Patch\" mod active in the game's mod menu."
  fi
fi

echo
if [ "$TOOLS_MISSING" = 0 ] && [ -n "$GAME_OK" ] && [ -n "$UCP_OK" ]; then
  echo "Everything is ready. Next:  ./build.sh   then   ./install.sh"; exit 0
fi
echo "Not ready yet - see the [missing] lines above."
[ -z "$INSTALLED_SOMETHING" ] || echo "If a tool was just installed but is still reported missing, open a NEW terminal and run ./setup.sh again."
exit 1
