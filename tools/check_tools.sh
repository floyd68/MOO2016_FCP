#!/usr/bin/env bash
# check_tools.sh - checks the build tools (Git Bash) and prints one status line per tool.
# Usage: source tools/check_tools.sh   -> sets DOTNET_OK/DOTNET_VER, JAVA_OK/JAVA_VER, FFDEC_JAR (empty if missing),
#                                          HAVE_WINGET, TOOLS_MISSING
# Tools installed a moment ago (PATH not refreshed yet) are found in their default folders and added to PATH.
CT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS_MISSING=0

# ---- .NET SDK 9+ ----
DOTNET_OK=; DOTNET_VER=
if ! command -v dotnet >/dev/null 2>&1 && [ -x "/c/Program Files/dotnet/dotnet.exe" ]; then PATH="/c/Program Files/dotnet:$PATH"; fi
if command -v dotnet >/dev/null 2>&1; then
  DOTNET_VER=$(dotnet --list-sdks 2>/dev/null | tr -d '\r' | awk '{ split($1, v, "."); if (v[1] + 0 >= 9) last = $1 } END { print last }' || true)
fi
if [ -n "$DOTNET_VER" ]; then DOTNET_OK=1; echo "  [ok]      .NET SDK $DOTNET_VER"
else
  echo "  [missing] .NET SDK 9 or newer"
  if command -v dotnet >/dev/null 2>&1; then
    echo "            installed SDKs (a .NET runtime alone is not enough): $(dotnet --list-sdks 2>/dev/null | tr -d '\r' | awk '{print $1}' | xargs echo)"
  fi
  TOOLS_MISSING=$((TOOLS_MISSING + 1))
fi

# ---- Java 8+ ----
JAVA_OK=; JAVA_VER=
if ! command -v java >/dev/null 2>&1; then
  for d in "/c/Program Files/Eclipse Adoptium/"* "/c/Program Files/Microsoft/jdk-"* "/c/Program Files/Java/"*; do
    if [ -x "$d/bin/java.exe" ]; then PATH="$d/bin:$PATH"; break; fi
  done
fi
if command -v java >/dev/null 2>&1; then
  JAVA_VER=$(java -version 2>&1 | tr -d '\r' | grep -i version | head -1 | sed -n 's/.*version "\([^"]*\)".*/\1/p' || true)
fi
jm=$(echo "$JAVA_VER" | awk -F'[._-]' '{ print ($1 == "1") ? $2 : $1 }' || true)
if [ -n "$jm" ] && [ "$jm" -ge 8 ] 2>/dev/null; then JAVA_OK=1; echo "  [ok]      Java $JAVA_VER"
else
  echo "  [missing] Java 8 or newer${JAVA_VER:+ (found $JAVA_VER)}"
  TOOLS_MISSING=$((TOOLS_MISSING + 1))
fi

# ---- JPEXS FFDec ----
if [ -n "${FFDEC_JAR:-}" ] && [ ! -f "$FFDEC_JAR" ]; then echo "  FFDEC_JAR=$FFDEC_JAR does not exist - ignored"; FFDEC_JAR=; fi
if [ -z "${FFDEC_JAR:-}" ]; then
  for j in "$CT_ROOT/tools/ffdec/ffdec.jar" "$CT_ROOT/_local_only/external/ffdec/ffdec.jar" "/c/Program Files/FFDec/ffdec.jar" "/c/Program Files (x86)/FFDec/ffdec.jar"; do
    if [ -f "$j" ]; then FFDEC_JAR="$j"; break; fi
  done
fi
if [ -n "${FFDEC_JAR:-}" ]; then echo "  [ok]      JPEXS FFDec  $FFDEC_JAR"
else echo "  [missing] JPEXS FFDec  (tools/ffdec/ffdec.jar)"; TOOLS_MISSING=$((TOOLS_MISSING + 1)); fi

# ---- winget (only needed by setup.sh to install .NET / Java) ----
HAVE_WINGET=; command -v winget >/dev/null 2>&1 && HAVE_WINGET=1
true
