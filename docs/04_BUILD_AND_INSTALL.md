# 04. Build, install, verify, profile

## Requirements

See also the Prerequisites table in the [README](../README.md#prerequisites).

- Master of Orion (Steam, Windows) with **UCP 6.4b** installed
- .NET SDK 9+ (tools target net9.0; MooFix/MooPerf target net35 via `Microsoft.NETFramework.ReferenceAssemblies`)
- Java 8+ on `PATH` and JPEXS FFDec (tested 26.5.0) for the SWF patch. The build looks for `ffdec.jar` in this order:
  `FFDEC_JAR`, `tools\ffdec\ffdec.jar`, `_local_only\external\ffdec\ffdec.jar`, `%ProgramFiles%\FFDec\`, `%ProgramFiles(x86)%\FFDec\`
- Either the **Windows Command Prompt** (`*.cmd`, uses only built-in `where`, `findstr`, `fc`, `tasklist`, `reg`) or **Git Bash** (`*.sh`)
- Python 3 (optional, `tools/scripts`); decompiler (optional): `dotnet tool install ilspycmd --tool-path <dir>`

### Original files

The build needs the UCP 6.4b versions of three game files. `tools/BuildHelper originals` takes, for each file, the file in the game
folder or its `*.orig` backup (made by `install`), whichever has the expected SHA-256, and copies it to `build\orig\`.
If neither matches, the build stops and lists the hashes it found.

| File | SHA-256 (UCP 6.4b, Steam Windows) |
|---|---|
| `Managed\Assembly-CSharp.dll` | `915c5eacf2314a64a7f39ac243c45333f07e96b70e5c1ec26b90888d64b5895a` |
| `Managed\Assembly-CSharp-firstpass.dll` | `ab936ff9ea0bd8157e5a5d503dc4def9ea13c731aa737f82c006bd5d38d039e6` |
| `StreamingAssets\EmpireManagement.swf` | `55d6a8489a816ae9c794affacedf6dfeb8603cfd92b782842d19f4cfb5932e51` |

`ORIG_MANAGED` (folder) and `ORIG_SWF` (file) point the build at other copies; the same hash check applies.

## Setup (checking and installing the tools)

`setup.cmd` / `./setup.sh` (argument `yes` = install without asking, `no` = report only; default = ask for each item):

| Item | Check | If missing |
|---|---|---|
| .NET SDK 9+ | `dotnet --list-sdks` (a runtime alone is not enough) | `winget install --id Microsoft.DotNet.SDK.9` after confirmation; without winget: link to dotnet.microsoft.com |
| Java 8+ | `java -version` | `winget install --id EclipseAdoptium.Temurin.21.JRE` after confirmation; without winget: link to adoptium.net |
| JPEXS FFDec | `FFDEC_JAR`, `tools\ffdec\`, FFDec installer folders | after confirmation: `curl` download of `ffdec_26.5.0.zip` from GitHub, SHA-256 check (`8d00c271…`), extract with `tar` into `tools\ffdec\` |
| Master of Orion | `tools\find_moo` | instructions (`MOO_DIR`) |
| UCP 6.4b | SHA-256 of the three game files or their `*.orig` | installation instructions (Workshop 1109736522, `javaInstaller.bat`) |

Tools installed a moment ago are also found in their default folders (`%ProgramFiles%\dotnet`, `%ProgramFiles%\Eclipse Adoptium\*`, …)
and added to `PATH` for that window; if one is still not found, open a new Command Prompt.
`build` runs the same tool check (`tools\check_tools.*`) first and points to `setup` when something is missing.
Tested: all tools present; FFDec missing (`no` → manual instructions, `yes` → downloaded and verified); .NET and Java hidden with and
without winget (prompts answered N → manual links); build refusing to start without tools. An actual winget installation was not run.

## Game folder

All scripts (`build`, `install`, `uninstall`, both CMD and bash) locate the game with `tools\find_moo\find_moo.cmd` / `tools/find_moo/find_moo.sh`
and print `Game folder: <path>   [found via <how>]`. Search order:

1. `MOO_DIR` environment variable — the folder that contains `MasterOfOrion_Data` (an invalid `MOO_DIR` is an error, no fallback).
2. Steam: install folder from the registry (`HKCU\Software\Valve\Steam\SteamPath`, else `HKLM\SOFTWARE\(WOW6432Node\)Valve\Steam\InstallPath`;
   `MOO_STEAM_ROOT` overrides it) → every library in `steamapps\libraryfolders.vdf` → `steamapps\appmanifest_298050.acf` → `installdir`.
   This covers games moved to another drive/library and renamed install folders.
3. GOG Galaxy: `HKLM\SOFTWARE\(WOW6432Node\)GOG.com\Games\*` whose `gameName` starts with "Master of Orion" (untested — no GOG copy here; the patch targets the UCP 6.4b files anyway).
4. The default path `C:\Program Files (x86)\Steam\steamapps\common\Master of Orion`.

A candidate is accepted only if `MasterOfOrion_Data\Managed\Assembly-CSharp.dll` exists. If nothing is found the script stops and tells you to set `MOO_DIR`:

```bat
set "MOO_DIR=D:\Games\Steam\steamapps\common\Master of Orion"
build.cmd
```
```bash
MOO_DIR='D:/Games/Steam/steamapps/common/Master of Orion' ./build.sh
```

The older, more specific overrides still win over detection: `GAME_MANAGED` (build, the `...\MasterOfOrion_Data\Managed` folder)
and `GAME` (install/uninstall, the `...\MasterOfOrion_Data` folder).
Building `src\MooFix` directly with `dotnet build` (without the scripts) needs `-p:GameManaged=<...\MasterOfOrion_Data\Managed>`
unless the game is at the default path.

Tested: real Steam install (registry), a second fake Steam library under a path containing spaces and `(x86)` with a renamed install folder,
explicit `MOO_DIR`, invalid `MOO_DIR` (exit code 1), and a Steam install without the game (falls back to the default path).

## Build

| Command Prompt | Git Bash | Result |
|---|---|---|
| `build.cmd` | `./build.sh` | `build\release` |
| `build.cmd verify` | `VERIFY=1 ./build.sh` | + JIT comparison with the originals |
| `build.cmd perf` | `PERF=1 ./build.sh` | + profiling build in `build\perf` |
| `build.cmd verify perf` | `VERIFY=1 PERF=1 ./build.sh` | both |

Paths can be overridden with environment variables in both versions: `MOO_DIR` (see [Game folder](#game-folder)), `ORIG_MANAGED`, `ORIG_SWF`,
`GAME_MANAGED`, `FFDEC_JAR` (CMD: `set "NAME=value"` before `build.cmd`).

If a step fails, the build prints the last 30 lines of that step's log (`build\log_*.txt`, `build\*.log`).

Steps performed:
1. Check prerequisites (game folder, `dotnet`, `java`, FFDec), build the tools (BuildHelper, FixPatcher, PerfPatcher, Verify),
   and select/hash-check the original files into `build\orig`.
2. `FixPatcher publicize` — a **compile-time only** reference DLL with every member public (`build\pub`), so MooFix can use private members.
3. Build MooFix (`-p:GameManaged=… -p:PubDir=…`).
4. `FixPatcher patch` — (a) make public only the private members MooFix actually references (refuses MonoBehaviour fields: Unity serialization would change),
   (b) inject the prefixes from the `hooks` table. The same command is run on firstpass (types not present in that module are skipped).
5. FFDec exports the P-code of `ngd.ColonyWindow`, `ngd.ASRoot`, `fl.controls.ScrollBar` from the original SWF, `BuildHelper apply`
   applies `swf\patches\*.diff` to the original method bodies (`build\pcode`), and FFDec `-replace` swaps 12 method bodies
   ([05](05_SWF_PATCHING.md)); the script reports "method bodies replaced: 12".
6. `perf`: PerfPatcher profiling build / `verify`: JIT comparison (`identical to original` expected for both DLLs).

Tested: a clean copy containing only the repository files (no `_local_only`, FFDec in `tools\ffdec`, located in a folder whose path
contains spaces and `(x86)`), built with `build.cmd verify perf` from a plain Windows PATH (System32 + dotnet + Java only) and with
`build.sh` from Git Bash, produces byte-identical `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll` and `EmpireManagement.swf`
(equal to the installed files; the perf build equals the earlier perf build). Missing FFDec, missing Java and a non-original SWF
stop the build with an error.

## Install / uninstall

| Command Prompt | Git Bash | Action |
|---|---|---|
| `install.cmd [dir]` | `./install.sh [dir]` | check the game files, keep UCP 6.4b `*.orig` backups, then install `build\release` (falls back to `_local_only\release`) |
| `uninstall.cmd` | `./uninstall.sh` | restore `*.orig`, delete `MooFix.dll`/`MooPerf.dll` |

**Target check** (`BuildHelper prepare-target`, needs the build's `build\tools\BuildHelper` and `dotnet`). For each of the three files:

| Current file | `*.orig` | Result |
|---|---|---|
| UCP 6.4b original | anything | OK; `*.orig` is created or refreshed from the current file |
| this build, or an earlier build of this patch (the DLL references MooFix) | UCP 6.4b | OK |
| anything else | UCP 6.4b | stop: "game updated or verified by Steam — reinstall UCP 6.4b" |
| anything else | missing / other | stop: "UCP 6.4b not installed or another UCP version" |

All files are checked before anything is written; on a stop nothing is changed.
Tested on a fake game folder: fresh UCP 6.4b, reinstall of the same build, an earlier (profiling) build installed,
files replaced as by a Steam verify, no UCP at all, and uninstall followed by install.

Both refuse to run while the game is running. The game folder is detected automatically ([Game folder](#game-folder));
override it with `MOO_DIR` (game folder) or `GAME` (e.g. `set "GAME=D:\...\MasterOfOrion_Data"`).
Installed files: `Managed\Assembly-CSharp.dll`, `Managed\Assembly-CSharp-firstpass.dll`, `Managed\MooFix.dll`, `StreamingAssets\EmpireManagement.swf`.

## Runtime log and verification mode

- `Documents\Master of Orion\moofix_log.txt`: load message, `EXCEPTION`, `MISMATCH`, and one line per AI pass:
  `AI RunAI <ms> | verified fast paths=… mismatches=… | PlotPath hits=… computed=… (ms) of which unreachable=… (ms)`.
- If `Documents\Master of Orion\moofix_verify.txt` exists, verification mode is on (1 in 16 results compared with the original; slightly slower).
- If `Documents\Master of Orion\moofix_overlap.txt` exists, the experimental overlapped turn simulation is on ([02 H](02_FIXES.md)).
  Content `alternate` = only even turns (for comparisons). Read every turn; delete the file to turn it off.
- Diagnostic lines: `FRAME stall <ms> (server <state>) | GC <n>x since last frame, heap <MB>` for every frame gap ≥ 250 ms
  (what the player sees as a freeze; during loading and turn processing also scan statistics), `SERVER state`, `OVERLAP`,
  `NOTIF`, `RADAR`, `PLANET refresh`, `STARDIST`, `DEFICIT`, `TIMELINE`, `SECURITY` (security / settlement value reuse per
  simulation), `FLASH` (first construction of the Empire Management rows), `EMLIST` (Empire Management list state saved/restored).
- Shaders are compiled at the end of loading (~5 s longer loading, `WARMUP all shaders` line); create
  `Documents\Master of Orion\moofix_warmup_off.txt` to turn this off.
- Screen-opening diagnostics (only slow cases are logged): `MOVIE read/create/advance`, `STAGE switch`, `SWFCALL` (C# → Flash calls
  queued until a movie was ready), and `FRAME stall … | before update …, update …` (frame split).
- Profiling builds additionally print `ALLOC` blocks (allocated MB per method, self, approximate, and the collections inside);
  turn off the overlap while measuring allocations.

## Profiling build (`perf`)

- `build\perf`: ~1,760 methods of Assembly-CSharp and firstpass wrapped with `MooPerfLog.Begin/End`, plus `MooPerf.dll`;
  the SWF variant has the `ASRoot.OnEnterFrame` diagnostic. Install with `install.cmd build\perf` (or `./install.sh build/perf`); `MooPerf.dll` is copied automatically, and installing a release build removes it again.
- Output `Documents\Master of Orion\perf_log.txt`:
  - calls ≥15 ms with nesting depth: `HH:mm:ss.fff T<thread> d<depth> <method> <ms>`
  - `---- STATS ... ----`: every 20 s and after `Universe.AdvanceSimulation` / `Universe.RunAI`, per-method totals (ms, calls, avg, max)
  - Scaleform movie methods carry a `@MovieClass` suffix
- Methods with a prefix are **counted twice** (the wrapper wraps both prefix and original).
- Wrapping very hot methods distorts results badly (seen with AI path finding). Use a build without profiling code for final numbers.

Useful aggregations (Git Bash):
```bash
grep -v calls perf_log.txt | grep -v "STATS\|^ " | awk '{print $(NF-2)}' | sort | uniq -c | sort -rn | head
awk '/STATS T3 \(after Universe.RunAI/{f=1;next} /end STATS/{f=0} f{print}' perf_log.txt \
 | awk '{s[$NF]+=$1; c[$NF]+=$3} END{for(k in s) printf "%9.0f ms %9d calls %s\n", s[k], c[k], k}' | sort -rn | head -30
```
Command Prompt: `findstr /c:"STATS" perf_log.txt`, `findstr /c:"Universe.RunAI" perf_log.txt`.

## When UCP or the game is updated

1. The build stops at the hash check. Update the hashes in `tools/BuildHelper/Program.cs` (`Originals`) after confirming the new files.
2. If FixPatcher cannot find a hook target signature it stops with an exception → update the `hooks` table and the MooFix signature.
3. Decompile and confirm that the hooked methods did not change behavior (especially the ones MooFix re-implements:
   `PathFinder.FindPath`/`GetNeighbors`, `GetStructureSecurityBonus`, `GetStructuresMoraleBoost`, `Awareness.Is*Mature`).
4. Find the SWF method body indices again (`tools/scripts/abcidx3.py`); if `BuildHelper apply` reports that a diff no longer
   matches, port the change and regenerate the diff ([05](05_SWF_PATCHING.md#maintaining-the-diffs)).
5. Build with `verify`, play one session in verification mode (Empire Management screen, planet screen, 2 turns) and confirm 0 `MISMATCH`/`EXCEPTION` and no `SF Exception` in `MasterOfOrion_Data\output_log.txt`.

---

## 한국어 (Korean)

### 04. 빌드 · 설치 · 검증 · 측정

#### 도구 확인과 설치 (setup)
`setup.cmd` / `./setup.sh`(인자 `yes` = 묻지 않고 설치, `no` = 확인만, 기본 = 항목마다 물어봄):
- .NET SDK 9+(`dotnet --list-sdks`, 런타임만으로는 부족): 동의하면 `winget install --id Microsoft.DotNet.SDK.9`, winget이 없으면 다운로드 주소 안내.
- Java 8+(`java -version`): 동의하면 `winget install --id EclipseAdoptium.Temurin.21.JRE`, winget이 없으면 adoptium.net 안내.
- JPEXS FFDec: 동의하면 GitHub에서 `ffdec_26.5.0.zip`을 `curl`로 받아 SHA-256 확인 후 `tar`로 `tools\ffdec\`에 풀기.
- 게임, UCP 6.4b: 자동 설치 불가 → 찾았는지 여부와 설치 방법 안내.

방금 설치한 도구는 기본 설치 폴더에서도 찾아 그 창의 `PATH`에 추가합니다. 그래도 못 찾으면 새 명령 프롬프트를 여세요.
`build`도 먼저 같은 도구 검사(`tools\check_tools.*`)를 하고, 빠진 것이 있으면 `setup` 실행을 안내합니다.
시험: 모두 있음, FFDec 없음(`no` → 수동 안내, `yes` → 다운로드·검증), .NET·Java를 숨긴 상태(winget 있음/없음, 질문에 N → 수동 안내), 도구 없이 빌드 거부. 실제 winget 설치는 실행하지 않았습니다.

빌드 중 단계가 실패하면 그 단계 로그(`build\log_*.txt`, `build\*.log`)의 마지막 30줄을 보여 줍니다.

#### 준비물
[README의 사전 준비 표](../README.md#사전-준비-prerequisites)도 참고하세요.
- UCP 6.4b가 설치된 Master of Orion(Steam, Windows)
- .NET SDK 9 이상, `PATH`에 Java 8 이상, JPEXS FFDec(26.5.0에서 시험). `ffdec.jar`를 찾는 순서:
  `FFDEC_JAR` → `tools\ffdec\ffdec.jar` → `_local_only\external\ffdec\ffdec.jar` → `%ProgramFiles%\FFDec\` → `%ProgramFiles(x86)%\FFDec\`
- **명령 프롬프트**(`*.cmd`, Windows 기본 명령만 사용) 또는 **Git Bash**(`*.sh`)

#### 원본 파일
UCP 6.4b 버전의 게임 파일 3개가 필요합니다. `BuildHelper originals`가 파일마다 게임 폴더의 파일과 `*.orig` 백업(`install`이 만든 것) 중
SHA-256이 맞는 쪽을 골라 `build\orig\`에 복사합니다. 둘 다 맞지 않으면 찾은 해시를 표시하고 빌드를 멈춥니다.
해시: `Assembly-CSharp.dll` `915c5eac…`, `Assembly-CSharp-firstpass.dll` `ab936ff9…`, `EmpireManagement.swf` `55d6a848…`(전체 값은 위 영어 표).
`ORIG_MANAGED`(폴더), `ORIG_SWF`(파일)로 다른 사본을 지정할 수 있으며 같은 해시 검사를 거칩니다.

#### 게임 폴더

모든 스크립트(빌드·설치·제거, CMD와 bash 모두)가 `tools\find_moo\find_moo.cmd` / `tools/find_moo/find_moo.sh`로 게임 폴더를 찾고
`Game folder: <경로>   [found via <방법>]`을 표시합니다. 찾는 순서:

1. `MOO_DIR` 환경 변수 — `MasterOfOrion_Data`가 들어 있는 폴더(잘못된 값이면 다른 곳을 찾지 않고 오류).
2. Steam: 레지스트리의 Steam 설치 폴더(`MOO_STEAM_ROOT`로 바꿀 수 있음) → `steamapps\libraryfolders.vdf`의 모든 라이브러리 →
   `appmanifest_298050.acf`의 `installdir`. 다른 드라이브/라이브러리로 옮긴 경우와 설치 폴더 이름이 다른 경우도 찾습니다.
3. GOG Galaxy 레지스트리(`gameName`이 "Master of Orion"으로 시작). GOG판이 없어 시험하지 못함.
4. 기본 경로 `C:\Program Files (x86)\Steam\steamapps\common\Master of Orion`.

`MasterOfOrion_Data\Managed\Assembly-CSharp.dll`이 있는 폴더만 인정합니다. 찾지 못하면 `MOO_DIR`을 지정하라는 안내와 함께 멈춥니다:

```bat
set "MOO_DIR=D:\Games\Steam\steamapps\common\Master of Orion"
build.cmd
```
```bash
MOO_DIR='D:/Games/Steam/steamapps/common/Master of Orion' ./build.sh
```

기존의 `GAME_MANAGED`(빌드, `...\MasterOfOrion_Data\Managed`)와 `GAME`(설치·제거, `...\MasterOfOrion_Data`)을 지정하면 자동 탐지보다 우선합니다.
스크립트 없이 `src\MooFix`를 `dotnet build`로 직접 빌드할 때는 게임이 기본 경로가 아니면 `-p:GameManaged=<...\Managed>`가 필요합니다.

시험: 실제 Steam 설치(레지스트리), 공백과 `(x86)`이 들어간 경로의 두 번째 가짜 Steam 라이브러리 + 다른 설치 폴더 이름, `MOO_DIR` 지정,
잘못된 `MOO_DIR`(종료 코드 1), 게임이 없는 Steam(기본 경로로 대체).

#### 빌드

| 명령 프롬프트 | Git Bash | 결과 |
|---|---|---|
| `build.cmd` | `./build.sh` | `build\release` |
| `build.cmd verify` | `VERIFY=1 ./build.sh` | + 원본과 JIT 비교 |
| `build.cmd perf` | `PERF=1 ./build.sh` | + 측정용 빌드 `build\perf` |

경로 변경: `MOO_DIR`(위 게임 폴더 참고), `ORIG_MANAGED`, `ORIG_SWF`, `GAME_MANAGED`, `FFDEC_JAR` (CMD는 `set "변수=값"` 후 실행).
순서: 준비물 확인 → 도구 빌드 → 원본 선택·해시 확인(`build\orig`) → `publicize`(컴파일 전용 참조 DLL) → MooFix 빌드 → `patch`(MooFix가 쓰는 private 멤버만 public, prefix 삽입; MonoBehaviour 필드는 거부) → 원본 SWF에서 P-code 내보내기 + `swf\patches\*.diff` 적용 → FFDec로 SWF 본문 12개 교체 → (perf) 측정 빌드 / (verify) JIT 비교.
시험 결과: 저장소 파일만 있는 깨끗한 사본(`_local_only` 없음, FFDec는 `tools\ffdec`, 경로에 공백과 `(x86)` 포함)을 일반 Windows PATH(System32+dotnet+Java)에서 `build.cmd verify perf`로, Git Bash에서 `build.sh`로 빌드한 결과가 설치본과 바이트 단위로 같았습니다(perf 빌드도 이전 perf 빌드와 동일). FFDec 없음, Java 없음, 원본이 아닌 SWF는 오류로 멈춥니다.

#### 설치 / 제거

| 명령 프롬프트 | Git Bash | 동작 |
|---|---|---|
| `install.cmd [폴더]` | `./install.sh [폴더]` | 게임 파일 확인, UCP 6.4b `*.orig` 백업 유지 후 `build\release`(없으면 `_local_only\release`) 설치 |
| `uninstall.cmd` | `./uninstall.sh` | `*.orig` 복원, `MooFix.dll`/`MooPerf.dll` 삭제 |

**설치 대상 검사**(`BuildHelper prepare-target`, 빌드가 만든 `build\tools\BuildHelper`와 `dotnet` 필요). 파일마다:
현재 파일이 UCP 6.4b 원본이면 통과(`*.orig`를 만들거나 갱신) / 이 빌드 또는 이전 버전의 이 패치가 설치되어 있고 `*.orig`가 UCP 6.4b면 통과 /
그 밖에 `*.orig`가 UCP 6.4b면 "Steam 업데이트·무결성 검사로 바뀜 → UCP 6.4b 재설치" 안내 후 중지 / 둘 다 아니면 "UCP 6.4b 미설치 또는 다른 버전" 안내 후 중지.
모든 파일을 먼저 검사하고, 중지할 때는 아무것도 바꾸지 않습니다. 가짜 게임 폴더에서 위 경우들과 제거 후 재설치를 시험했습니다.

게임 실행 중에는 동작하지 않습니다. 게임 폴더는 자동으로 찾으며, `MOO_DIR`(게임 폴더) 또는 `GAME`(`...\MasterOfOrion_Data`)으로 바꿀 수 있습니다.

#### 런타임 로그와 검증 모드
- `문서\Master of Orion\moofix_log.txt`: 예외, 불일치, AI 단계마다 요약 한 줄.
- `moofix_verify.txt`가 있으면 검증 모드(16번 중 1번 원본과 비교).
- `moofix_overlap.txt`가 있으면 턴 시뮬레이션 겹치기(실험, [02 H](02_FIXES.md)). 내용이 `alternate`면 짝수 턴만(비교용). 매 턴 다시 읽으므로 파일을 지우면 바로 꺼짐.
- 진단 줄: `FRAME stall <ms> (server <상태>) | GC <n>x since last frame, heap <MB>`(250ms 이상 프레임 간격 = 플레이어가 느끼는 멈춤, 로딩·턴 처리 때는 스캔 통계 포함), `SERVER state`, `OVERLAP`, `NOTIF`, `RADAR`, `PLANET refresh`, `STARDIST`, `DEFICIT`, `TIMELINE`, `SECURITY`(시뮬레이션별 보안·정착지 값 재사용), `FLASH`(제국 관리 행 첫 생성), `EMLIST`(제국 관리 목록 상태 저장·복원).
- 로딩 끝에 셰이더를 미리 컴파일(로딩 약 5초 증가, `WARMUP all shaders` 줄). 끄려면 `문서\Master of Orion\moofix_warmup_off.txt`를 만들 것.
- 화면 열기 진단(느린 경우만 기록): `MOVIE read/create/advance`, `STAGE switch`, `SWFCALL`(무비 준비 전까지 대기한 C# → Flash 호출), `FRAME stall … | before update …, update …`(프레임 구간 분리).
- 측정용 빌드는 `ALLOC` 블록(메서드별 할당 MB, 자기 몫, 근사값, 그 안의 GC 횟수)도 출력. 할당량을 잴 때는 겹치기를 끌 것.

#### 측정용 빌드
- `build\perf`: 약 1,760개 메서드에 시간 측정 래퍼 + `MooPerf.dll`, SWF는 Flash 진단 버전. `install.cmd build\perf`(또는 `./install.sh build/perf`)로 설치하면 `MooPerf.dll`도 자동 복사되고, 릴리스 빌드를 설치하면 다시 삭제됩니다.
- 출력 `perf_log.txt`: 15ms 이상 호출, 20초/턴/AI 단계별 메서드 누적 통계, 무비 메서드는 `@무비클래스` 접미사.
- prefix가 있는 메서드는 이중 집계, 초고빈도 메서드 래핑은 측정 왜곡 → 최종 수치는 측정 코드 없는 빌드로.

#### UCP/게임이 업데이트되었을 때
1. 해시 검사에서 빌드가 멈춤 → 새 파일을 확인한 뒤 `tools/BuildHelper/Program.cs`의 `Originals` 해시를 갱신.
2. 훅 대상 시그니처를 못 찾으면 예외로 중단 → `hooks` 표와 MooFix 시그니처 수정.
3. 훅 대상 메서드의 동작 변경 여부 확인(특히 MooFix가 재구현한 메서드들).
4. SWF 메서드 본문 인덱스를 다시 찾고, `BuildHelper apply`가 diff 불일치를 보고하면 변경을 옮긴 뒤 diff를 다시 만듦([05](05_SWF_PATCHING.md)).
5. `verify` 빌드 + 검증 모드로 한 세션 플레이 후 `MISMATCH`/`EXCEPTION`/`SF Exception` 0건 확인.
