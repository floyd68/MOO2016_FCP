#!/usr/bin/env bash
# find_moo.sh - locate the Master of Orion (2016) installation folder (Git Bash).
# Usage:  source tools/find_moo/find_moo.sh   -> sets MOO_DIR (POSIX path of the folder that contains
#         MasterOfOrion_Data) and MOO_HOW; returns 1 with instructions on failure.
# Search order: MOO_DIR env var, Steam (MOO_STEAM_ROOT env var, else registry -> libraryfolders.vdf -> appmanifest_298050.acf),
#               GOG registry, default path.
moo__check() { [ -f "$1/MasterOfOrion_Data/Managed/Assembly-CSharp.dll" ]; }
moo__regval() { # $1=key $2=value name -> prints data
  reg query "$1" //v "$2" 2>/dev/null | tr -d '\r' | awk -v n="$2" '$1==n { $1=""; $2=""; sub(/^ +/, ""); print; exit }'
}
moo__steamlib() { # $1 = library root (POSIX)
  local acf="$1/steamapps/appmanifest_298050.acf" idir
  [ -f "$acf" ] || return 1
  idir=$(tr -d '\r' < "$acf" | sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' | head -1)
  [ -n "$idir" ] && moo__check "$1/steamapps/common/$idir" && { MOO_DIR="$1/steamapps/common/$idir"; MOO_HOW="Steam library $1"; return 0; }
  return 1
}
moo_find() {
  if [ -n "${MOO_DIR:-}" ]; then
    MOO_DIR="$(cygpath -u "$MOO_DIR")"
    if moo__check "$MOO_DIR"; then MOO_HOW="MOO_DIR environment variable"; return 0; fi
    echo "MOO_DIR=\"$MOO_DIR\" does not contain MasterOfOrion_Data/Managed/Assembly-CSharp.dll" >&2; return 1
  fi
  local steam lib k gn gp
  steam="${MOO_STEAM_ROOT:-}"
  [ -n "$steam" ] || steam=$(moo__regval 'HKCU\Software\Valve\Steam' SteamPath)
  [ -n "$steam" ] || steam=$(moo__regval 'HKLM\SOFTWARE\WOW6432Node\Valve\Steam' InstallPath)
  [ -n "$steam" ] || steam=$(moo__regval 'HKLM\SOFTWARE\Valve\Steam' InstallPath)
  if [ -n "$steam" ]; then
    steam="$(cygpath -u "$steam")"
    moo__steamlib "$steam" && return 0
    if [ -f "$steam/steamapps/libraryfolders.vdf" ]; then
      while IFS= read -r lib; do
        lib="$(cygpath -u "${lib//\\\\/\\}")"
        moo__steamlib "$lib" && return 0
      done < <(tr -d '\r' < "$steam/steamapps/libraryfolders.vdf" | sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p')
    fi
  fi
  for k in 'HKLM\SOFTWARE\WOW6432Node\GOG.com\Games' 'HKLM\SOFTWARE\GOG.com\Games'; do
    while IFS= read -r sub; do
      [ -n "$sub" ] || continue
      gn=$(moo__regval "$sub" gameName)
      case "$gn" in "Master of Orion"*)
        gp=$(moo__regval "$sub" path)
        if [ -n "$gp" ] && moo__check "$(cygpath -u "$gp")"; then MOO_DIR="$(cygpath -u "$gp")"; MOO_HOW="GOG registry"; return 0; fi;;
      esac
    done < <(reg query "$k" 2>/dev/null | tr -d '\r' | grep -i '^HKEY')
  done
  if moo__check "/c/Program Files (x86)/Steam/steamapps/common/Master of Orion"; then
    MOO_DIR="/c/Program Files (x86)/Steam/steamapps/common/Master of Orion"; MOO_HOW="default path"; return 0
  fi
  echo "Could not find the Master of Orion installation." >&2
  echo "Set it explicitly, e.g.:  MOO_DIR='D:/Games/Steam/steamapps/common/Master of Orion' ./build.sh" >&2
  echo "(the folder that contains MasterOfOrion_Data), then run the script again." >&2
  return 1
}
moo_find
