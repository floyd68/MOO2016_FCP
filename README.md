# MOO2016_FCP — Late-game performance patch for Master of Orion (2016)

> **Playing Master of Orion, don't be afraid to press a button anymore — almost all of the annoying lags are drastically reduced!**  
> **Master of Orion을 플레이 하다 더 이상 버튼 누를때 겁먹지 마세요. 거의 모든 짜증나는 랙들을 획기적으로 줄였습니다!**

**FCP** = Fast Code Patch. This project reverse-engineers *Master of Orion: Conquer the Stars* (2016; Unity 5.2 / Mono / Scaleform)
and fixes the **slowdown that grows with the number of colonies**.
It is built on top of the game files as patched by **UCP 6.4b (Unofficial Code Patch, by WhatIsSol)**.

> Design rule: the patch must not change game results. Even single-player runs a server and a client simulation and compares
> checksums, so every simulation-side change was designed to produce **identical values** and was verified in play.

This repository contains **no game or UCP files**. You build the patch from your own installed game; see [LICENSE.md](LICENSE.md).
Unofficial — not affiliated with Wargaming, NGD Studios or the UCP author.

## Results (Massive galaxy, very high planet density, turn 225, ~345 player colonies)

| Item | Before | After |
|---|---|---|
| Turn simulation (server / client) | 25.3 s / 17.7 s | ~3.6 s / ~3.6 s |
| AI stage excluding turn simulation (2 turns) | ~49 s | ~22 s (first round); turn 227→228 now ~6.5 s for all 8 AI empires |
| AI path finding (2 turns) | 36.2 s | 8.6 s |
| Planet screen citizen drag & drop | 0.7–1.4 s per drag | <15 ms per action + one screen refresh |
| Empire Management frame, mouse over the list | 300–400 ms | ~6 ms on average |
| Empire Management click handling (Quick Buy, row select) | ~0.5 s per click | 8–10 ms on average |
| Empire Management list construction (C#) | 1.3–1.7 s | ~0.1 s |
| Empire Management first opening | 4.7–6.9 s | <1 s (rows virtualized) |
| First opening of espionage / races, Known Planets | ~2.2 s / 1.3–2.5 s, 1.35 s | no stall (shader warm-up), 5 ms |
| Freeze after a battle / an event (turn results) | 17–33 s | ~0 |
| Loading the save | 17.1–17.3 s | ~9.1–9.2 s (+~5 s shader warm-up, optional) |
| Screen freeze per turn (simulation + new turn) | 10.1–10.3 s | ~4.7–5.3 s (both measured with the experimental overlap on) |
| Tech tree opening, blueprint "confirm" | 5 s, several s | screen load only (~2 s) |
| Empire Management after visiting a planet | list reset (sorted by name, top, first colony) | sort, selected colony and scroll position kept |

Experimental: overlapping the server and client turn simulations (`moofix_overlap.txt`, see [docs/02 H](docs/02_FIXES.md)).

Details: [docs/03_MEASUREMENTS.md](docs/03_MEASUREMENTS.md)

## Install without building (binary release)

1. Requirements: Windows 10/11, *Master of Orion: Conquer the Stars* (Steam) with **UCP 6.4b** installed.
2. Download `MOO2016_FCP-<version>.zip` from [Releases](https://github.com/floyd68/MOO2016_FCP/releases) and extract it anywhere,
   **or** subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3845815644) (files arrive in `Steam\steamapps\workshop\content\298050\3845815644\`).
   Close the game and run **`install.cmd`**
   (or `install.cmd "D:\SteamLibrary\steamapps\common\Master of Orion"` if the folder is not found automatically).
3. `uninstall.cmd` restores the original files.

Nothing else is needed (.NET Framework 4.8 is part of Windows). The package contains **no game or UCP code**: the two game DLLs
are patched on your PC by the same Mono.Cecil patcher the build uses, the SWF gets a 2 KB delta of the changed P-code, and every
result is checked against the SHA-256 recorded for the release. The same safety check as the build's `install` applies
(UCP 6.4b originals, or this patch with UCP 6.4b `*.orig` backups; otherwise nothing is changed).
The main menu shows `**** FCP <version> ****` next to the UCP version when the patch is active.

## Prerequisites

To build it yourself, install these first:

| # | What | Version / notes | Where |
|---|---|---|---|
| 1 | **Windows** | 10 or 11 (tested on Windows 11). macOS/Linux are not supported. | |
| 2 | **Master of Orion: Conquer the Stars** | **Steam, Windows** version (app 298050). Start it once and reach the main menu before installing UCP. GOG/Wargaming copies have different UCP builds and are not supported by the hash check. | Steam |
| 3 | **Unofficial Code Patch (UCP) 6.4b** | Required: the patch is built from the UCP 6.4b `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll` and `EmpireManagement.swf` (checked by SHA-256). Subscribe on the Workshop, then run `javaInstaller.bat` (or `java -jar MOOCts-Installer-UCP.jar`) in `steamapps\workshop\content\298050\1109736522`, and keep the "Unofficial Code Patch" mod active in the game's mod menu. Follow the UCP ReadMe. | [Steam Workshop 1109736522](https://steamcommunity.com/sharedfiles/filedetails/?id=1109736522) |
| 4 | **.NET SDK** | 9.0 or newer, `dotnet` on `PATH` (builds the tools; MooFix itself targets .NET Framework 3.5 through a NuGet reference package — no .NET 3.5 install needed) | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| 5 | **Java runtime** | 8 or newer, `java` on `PATH` (for FFDec; UCP's installer needs Java as well) | e.g. [Adoptium Temurin](https://adoptium.net/) |
| 6 | **JPEXS FFDec** | tested with 26.5.0. Download `ffdec_<version>.zip` and extract it to **`tools\ffdec\`** so that `tools\ffdec\ffdec.jar` exists (or install it with the FFDec installer, or set `FFDEC_JAR`). | [GitHub releases](https://github.com/jindrapetrik/jpexs-decompiler/releases) |
| 7 | Internet access on the first build | NuGet downloads Mono.Cecil and the .NET Framework reference assemblies | |
| — | *Optional*: Git for Windows | to clone the repository and to run the `*.sh` scripts; the `*.cmd` scripts need only the Windows Command Prompt | [git-scm.com](https://git-scm.com/) |
| — | *Optional*: Python 3, ilspycmd | only for analysis (`tools/scripts`, decompiling) | |

**`setup.cmd`** (or `./setup.sh`) checks all of this and installs what it can **after asking** for each item:
JPEXS FFDec 26.5.0 is downloaded into `tools\ffdec\` (SHA-256 checked), the .NET SDK 9 and Java (Eclipse Temurin 21 JRE) are installed
with `winget`. The game and UCP cannot be installed automatically; setup reports whether they are found and how to install them.
`setup.cmd yes` installs without asking, `setup.cmd no` only reports. After winget installed something, open a new Command Prompt.
The build and install scripts run the same checks and stop with a message naming what is missing.

## Quick start

Windows Command Prompt (in the folder where you cloned/extracted this repository):
```bat
setup.cmd                  & rem check prerequisites, install missing tools after asking
build.cmd                  & rem build\release from your game's original (UCP 6.4b) files
build.cmd verify           & rem + compare JIT results of the patched DLLs with the originals
build.cmd perf             & rem + profiling build (build\perf)
install.cmd                & rem install into the game folder (creates *.orig backups)
uninstall.cmd              & rem restore from *.orig
```

Git Bash:
```bash
./setup.sh          # ./setup.sh yes | no
./build.sh          # VERIFY=1 ./build.sh, PERF=1 ./build.sh
./install.sh
./uninstall.sh
```

Close the game before `install`/`uninstall`.

- **Install safety check**: before copying, `install` verifies that each game file is either the UCP 6.4b original or already
  patched by this project with a UCP 6.4b `*.orig` backup. Otherwise (UCP not installed, another UCP version, game updated or
  verified by Steam) it explains the reason and changes nothing.
- **Build errors** show the end of the failing step's log (`build\log_*.txt`, `build\*.log`).

- **Game folder**: the scripts find it automatically (Steam registry → every Steam library in `libraryfolders.vdf` →
  `appmanifest_298050.acf`; then GOG; then the default `C:\Program Files (x86)\Steam\steamapps\common\Master of Orion`) and print
  `Game folder: … [found via …]`. If it is not found, or the wrong copy is found, set it yourself before running:
  `set "MOO_DIR=D:\Games\Steam\steamapps\common\Master of Orion"` (CMD) or `MOO_DIR='D:/Games/…/Master of Orion' ./build.sh` (bash) —
  the folder that contains `MasterOfOrion_Data`. Details: [docs/04](docs/04_BUILD_AND_INSTALL.md#game-folder).
- **Original files**: taken from the game folder. Once the patch is installed, the game files are patched, so the build uses the
  `*.orig` backups made by `install`. A file is accepted only if its SHA-256 matches UCP 6.4b.
- The build is reproducible: building on another PC from the same UCP 6.4b files gives byte-identical game files
  (`MooFix.dll` differs only in its per-build module id (MVID)).
- **After a Steam update or "Verify integrity of game files"** the game files are replaced: reinstall UCP 6.4b if needed, then run
  `install.cmd` (or `./install.sh`) again.
- **If UCP is updated**: the build stops (hash mismatch). The patch has to be ported to the new files
  ([docs/04](docs/04_BUILD_AND_INSTALL.md#when-ucp-or-the-game-is-updated), [docs/05](docs/05_SWF_PATCHING.md)).

## Layout

| Path | Contents |
|---|---|
| `README.md`, `LICENSE.md`, `docs/` | overview, license, analysis, fix list, measurements, build/SWF procedures, lessons, UCP research |
| `src/MooFix/` | the fix logic (C#, .NET 3.5), called by prefix hooks injected into the game DLLs |
| `src/MooPerf/` | profiling library (profiling builds only) |
| `tools/BuildHelper/` | picks and hash-checks the original files; extracts original P-code method bodies and applies `swf/patches` |
| `tools/FixPatcher/` | Mono.Cecil patcher: makes the publicized reference DLL, injects prefix hooks |
| `tools/PerfPatcher/` | Mono.Cecil patcher: wraps methods with timing code |
| `tools/Verify/` | JIT-compiles every method of a DLL to compare IL errors with the original |
| `tools/find_moo/` | finds the game installation folder (Steam libraries / GOG / `MOO_DIR`) |
| `tools/FcpPatch/`, `tools/FixPatcher48/`, `tools/release/`, `tools/make_release.sh` | binary release: installer (.NET Framework 4.8), FixPatcher built for .NET Framework 4.8, package templates, packaging script |
| `tools/check_tools.*` | checks .NET SDK 9+, Java 8+, FFDec (used by setup and build) |
| `tools/scripts/` | SWF string extraction, finding method body indices from SWF XML (analysis only) |
| `tools/ffdec/` | *(not in the repository)* put JPEXS FFDec here |
| `swf/patches/` | changes to 13 EmpireManagement.swf method bodies as diffs against the original P-code |
| `setup.*`, `build.*`, `install.*`, `uninstall.*` | `.cmd` for the Windows Command Prompt, `.sh` for Git Bash |

`.gitignore` excludes `build/`, `tools/ffdec/`, game binaries and `_local_only/` (the author's local working material).

## How it works

1. **C# (Assembly-CSharp.dll)**: a Harmony-style *prefix* is injected (as IL) at the start of selected methods.
   The prefix calls a static method in `MooFix.dll`; `true` skips the original (result via an out parameter), `false` runs it.
   To call an original from MooFix, a thread-local `bypass` flag is set right before the call.
2. **Scaleform wrapper (Assembly-CSharp-firstpass.dll)**: one prefix of the same kind on `Movie.DoHitTest`.
3. **Flash UI (EmpireManagement.swf)**: instead of recompiling classes, **only method bodies** are replaced with P-code (12 bodies; the Empire Management list is virtualized).
   The original bodies are exported from your SWF and the diffs in `swf/patches` are applied to them.
4. **MooFix log**: `Documents\Master of Orion\moofix_log.txt` — exceptions and one summary line per AI pass.
   Creating `moofix_verify.txt` in the same folder enables **verification mode** (1 in 16 results compared with the original; differences logged as `MISMATCH`).

## Documents

1. [Analysis: what was slow and why](docs/01_ANALYSIS.md)
2. [Fixes and why they give identical results](docs/02_FIXES.md)
3. [Measurements](docs/03_MEASUREMENTS.md)
4. [Build, install, verify, profile](docs/04_BUILD_AND_INSTALL.md)
5. [Patching the SWF (Scaleform/AS3)](docs/05_SWF_PATCHING.md)
6. [Lessons and pitfalls](docs/06_LESSONS.md)
7. [UCP dependency, distribution, contributing](docs/07_UCP_AND_DISTRIBUTION.md)
8. [Remaining optimization candidates](docs/08_REMAINING_WORK.md)

## License

Code and documentation of this project: MIT. The game, UCP and third-party tools belong to their owners and are not included.
See [LICENSE.md](LICENSE.md).

---

## 한국어 (Korean)

### MOO2016_FCP — Master of Orion (2016) 후반부 성능 패치

> **Master of Orion을 플레이 하다 더 이상 버튼 누를때 겁먹지 마세요. 거의 모든 짜증나는 랙들을 획기적으로 줄였습니다!**

**FCP** = Fast Code Patch. *Master of Orion: Conquer the Stars*(2016, Unity 5.2 / Mono / Scaleform)에서
**식민지가 많아질수록 게임이 느려지는 문제**를 리버스 엔지니어링으로 분석하고 고친 패치입니다.
**UCP 6.4b(Unofficial Code Patch, WhatIsSol)가 적용된 게임 파일**을 기반으로 만들었습니다.

> 이 패치는 게임 결과를 바꾸지 않는 것을 원칙으로 했습니다. 싱글플레이에서도 서버·클라이언트 시뮬레이션이
> 체크섬으로 비교되므로, 모든 시뮬레이션 쪽 수정은 원본과 **동일한 값**을 내도록 설계하고 실제 플레이로 검증했습니다.

이 저장소에는 **게임이나 UCP 파일이 들어 있지 않습니다.** 각자 설치한 게임 파일로 직접 빌드합니다([LICENSE.md](LICENSE.md) 참고).
비공식 프로젝트이며 Wargaming, NGD Studios, UCP 제작자와 관계가 없습니다.

### 결과 요약 (Massive 은하, 매우 높은 행성 밀도, 225턴, 식민지 약 345개)

| 항목 | 패치 전 | 패치 후 |
|---|---|---|
| 턴 시뮬레이션 (서버 / 클라이언트) | 25.3초 / 17.7초 | 약 3.6초 / 약 3.6초 |
| AI 단계 (턴 시뮬레이션 제외, 2턴) | 약 49초 | 약 22초(1차). 현재 227→228턴은 AI 제국 8개 합계 약 6.5초 |
| AI 경로 탐색 (2턴) | 36.2초 | 8.6초 |
| 행성 화면 인구 드래그&드롭 | 1회 0.7~1.4초 | 액션 15ms 미만 + 화면 갱신 1회 |
| 제국 관리 프레임, 마우스가 목록 위 | 300~400ms | 평균 약 6ms |
| 제국 관리 클릭 처리(Quick Buy, 행 선택) | 클릭당 약 0.5초 | 평균 8~10ms |
| 제국 관리 목록 생성(C#) | 1.3~1.7초 | 약 0.1초 |
| 제국 관리 첫 진입 | 4.7~6.9초 | 1초 미만(목록 가상화) |
| 첩보 / 종족 첫 진입, 알려진 행성 | 약 2.2초 / 1.3~2.5초, 1.35초 | 멈춤 없음(셰이더 미리 컴파일), 5ms |
| 전투·이벤트 후 멈춤(턴 결과 연출) | 17~33초 | 거의 0 |
| 세이브 로딩 | 17.1~17.3초 | 약 9.1~9.2초(+셰이더 미리 컴파일 약 5초, 끌 수 있음) |
| 턴당 화면 멈춤(시뮬레이션 + 새 턴) | 10.1~10.3초 | 약 4.7~5.3초(둘 다 실험 기능인 겹치기를 켠 상태로 측정) |
| 기술 트리 열기, 블루프린트 확인 | 5초, 수 초 | 화면 불러오기만(약 2초) |
| 행성 화면에 다녀온 뒤 제국 관리 화면 | 목록 초기화(이름순, 맨 위, 첫 식민지) | 정렬·선택 식민지·스크롤 위치 유지 |

실험 기능: 서버·클라이언트 턴 시뮬레이션 겹치기(`moofix_overlap.txt`, [docs/02 H](docs/02_FIXES.md)).

### 빌드 없이 설치 (바이너리 릴리스)

1. 필요 조건: Windows 10/11, **UCP 6.4b**가 설치된 *Master of Orion: Conquer the Stars*(Steam판).
2. [Releases](https://github.com/floyd68/MOO2016_FCP/releases)에서 `MOO2016_FCP-<버전>.zip`을 받아 아무 곳에나 풀거나,
   [Steam 창작마당](https://steamcommunity.com/sharedfiles/filedetails/?id=3845815644)에서 구독(파일은 `Steam\steamapps\workshop\content\298050\3845815644\`에 내려옴).
   게임을 종료한 뒤 **`install.cmd`** 실행
   (게임 폴더를 자동으로 못 찾으면 `install.cmd "D:\SteamLibrary\steamapps\common\Master of Orion"`).
3. `uninstall.cmd`로 원래 파일 복원.

그 밖에 설치할 것은 없습니다(.NET Framework 4.8은 Windows에 포함). 패키지에는 **게임·UCP 코드가 없습니다**: 게임 DLL 두 개는 빌드와 같은 Mono.Cecil 패처가 사용자 PC에서 패치하고, SWF는 바뀐 P-code만 담은 2KB 차이 파일로 만들며, 모든 결과를 릴리스에 기록된 SHA-256과 대조합니다. 빌드판 `install`과 같은 안전 검사를 합니다(UCP 6.4b 원본, 또는 UCP 6.4b `*.orig` 백업이 있는 이 패치가 아니면 아무것도 바꾸지 않음).
패치가 적용되면 메인 메뉴의 UCP 버전 옆에 `**** FCP <버전> ****`이 표시됩니다.

### 사전 준비 (Prerequisites)

직접 빌드하려면 먼저 설치해야 하는 것들입니다.

| # | 항목 | 버전 / 설명 | 받는 곳 |
|---|---|---|---|
| 1 | **Windows** | 10 또는 11 (Windows 11에서 시험). macOS/Linux 미지원 | |
| 2 | **Master of Orion: Conquer the Stars** | **Steam판, Windows**(앱 298050). UCP 설치 전에 한 번 실행해 메인 메뉴까지 진입. GOG/Wargaming판은 UCP 빌드가 달라 해시 검사를 통과하지 못함(미지원) | Steam |
| 3 | **Unofficial Code Patch(UCP) 6.4b** | 필수. UCP 6.4b의 `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`, `EmpireManagement.swf`(SHA-256 확인)로 빌드합니다. 창작마당에서 구독한 뒤 `steamapps\workshop\content\298050\1109736522`의 `javaInstaller.bat`(또는 `java -jar MOOCts-Installer-UCP.jar`)를 실행하고, 게임의 모드 메뉴에서 "Unofficial Code Patch" 모드를 켜 둡니다. 자세한 내용은 UCP ReadMe 참고 | [Steam 창작마당 1109736522](https://steamcommunity.com/sharedfiles/filedetails/?id=1109736522) |
| 4 | **.NET SDK** | 9.0 이상, `PATH`에 `dotnet`. MooFix는 NuGet 참조 패키지로 .NET Framework 3.5용으로 빌드되므로 .NET 3.5 설치는 필요 없음 | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| 5 | **Java 런타임** | 8 이상, `PATH`에 `java` (FFDec용, UCP 설치 프로그램도 Java 필요) | 예: [Adoptium Temurin](https://adoptium.net/) |
| 6 | **JPEXS FFDec** | 26.5.0에서 시험. `ffdec_<버전>.zip`을 받아 **`tools\ffdec\`**에 풀어 `tools\ffdec\ffdec.jar`가 있게 합니다(FFDec 설치 프로그램으로 설치하거나 `FFDEC_JAR` 지정도 가능) | [GitHub releases](https://github.com/jindrapetrik/jpexs-decompiler/releases) |
| 7 | 첫 빌드 시 인터넷 연결 | NuGet이 Mono.Cecil과 .NET Framework 참조 어셈블리를 내려받음 | |
| — | *선택*: Git for Windows | 저장소 복제, `*.sh` 스크립트 실행용. `*.cmd`는 명령 프롬프트만으로 충분 | [git-scm.com](https://git-scm.com/) |
| — | *선택*: Python 3, ilspycmd | 분석용(`tools/scripts`, 디컴파일)만 | |

**`setup.cmd`**(또는 `./setup.sh`)가 위 항목을 모두 확인하고, 설치할 수 있는 것은 **항목마다 물어본 뒤** 설치합니다.
JPEXS FFDec 26.5.0은 `tools\ffdec\`에 내려받아 풀고(SHA-256 확인), .NET SDK 9와 Java(Eclipse Temurin 21 JRE)는 `winget`으로 설치합니다.
게임과 UCP는 자동 설치할 수 없으므로 찾았는지 여부와 설치 방법만 알려 줍니다.
`setup.cmd yes`는 묻지 않고 설치, `setup.cmd no`는 확인만 합니다. winget으로 설치한 뒤에는 새 명령 프롬프트를 여세요.
빌드·설치 스크립트도 같은 검사를 하고, 빠진 것이 있으면 이유를 표시하고 멈춥니다.

### 빠른 사용법

명령 프롬프트(CMD), 이 저장소를 받은 폴더에서:
```bat
setup.cmd            & rem 사전 준비 확인, 빠진 도구는 물어본 뒤 설치
build.cmd            & rem 게임의 원본(UCP 6.4b) 파일로부터 build\release 생성
build.cmd verify     & rem + 패치된 DLL의 JIT 검사를 원본과 비교
build.cmd perf       & rem + 측정용 빌드(build\perf)
install.cmd          & rem 게임 폴더에 설치 (*.orig 백업 생성)
uninstall.cmd        & rem *.orig 로 복원
```
Git Bash: `./setup.sh`, `./build.sh` (`VERIFY=1`, `PERF=1`), `./install.sh`, `./uninstall.sh`. 설치·제거 전에는 게임을 종료하세요.

- **설치 안전 검사**: `install`은 복사하기 전에 게임 파일이 UCP 6.4b 원본이거나, 이미 이 패치가 설치되어 있고 UCP 6.4b `*.orig` 백업이
  있는 상태인지 확인합니다. 아니면(UCP 미설치, 다른 UCP 버전, Steam 업데이트/무결성 검사로 파일이 바뀜) 이유를 설명하고 아무것도 바꾸지 않습니다.
- **빌드 오류**가 나면 실패한 단계의 로그 끝부분(`build\log_*.txt`, `build\*.log`)을 보여 줍니다.

- **게임 폴더**: 스크립트가 자동으로 찾습니다(Steam 레지스트리 → `libraryfolders.vdf`의 모든 Steam 라이브러리 →
  `appmanifest_298050.acf`, 다음 GOG, 마지막으로 기본 경로 `C:\Program Files (x86)\Steam\steamapps\common\Master of Orion`).
  실행하면 `Game folder: … [found via …]`가 표시됩니다. 찾지 못하거나 다른 사본이 잡히면 실행 전에 직접 지정하세요:
  `set "MOO_DIR=D:\Games\Steam\steamapps\common\Master of Orion"`(CMD) 또는 `MOO_DIR='D:/Games/…/Master of Orion' ./build.sh`(bash).
  `MasterOfOrion_Data`가 들어 있는 폴더를 지정합니다. 자세한 내용은 [docs/04](docs/04_BUILD_AND_INSTALL.md#게임-폴더).
- **원본 파일**: 게임 폴더에서 가져옵니다. 패치를 설치한 뒤에는 게임 파일이 패치된 상태이므로 `install`이 만든 `*.orig` 백업을 씁니다.
  SHA-256이 UCP 6.4b와 같은 파일만 사용합니다.
- 빌드는 재현 가능합니다. 같은 UCP 6.4b 파일이면 다른 PC에서도 바이트 단위로 같은 게임 파일이 나옵니다(`MooFix.dll`은 MVID만 다름).
- **Steam 업데이트/파일 무결성 검사 후**: 게임 파일이 바뀌므로 필요하면 UCP 6.4b를 다시 설치하고 `install.cmd`(또는 `./install.sh`)를 다시 실행합니다.
- **UCP가 새 버전으로 바뀌면**: 해시가 달라 빌드가 멈춥니다. 패치를 새 파일에 맞게 옮겨야 합니다([docs/04](docs/04_BUILD_AND_INSTALL.md), [docs/05](docs/05_SWF_PATCHING.md)).

### 폴더 구성

| 경로 | 내용 |
|---|---|
| `README.md`, `LICENSE.md`, `docs/` | 개요, 라이선스, 분석, 수정 내역, 측정, 빌드/SWF 방법, 교훈, UCP 조사 |
| `src/MooFix/` | 수정 로직 (C#, .NET 3.5). 게임 DLL에 삽입된 prefix 훅이 호출 |
| `src/MooPerf/` | 측정용 라이브러리 (측정 빌드 전용) |
| `tools/BuildHelper/` | 원본 파일 선택과 해시 확인, 원본 P-code 메서드 본문 추출과 `swf/patches` 적용 |
| `tools/FixPatcher/` | Mono.Cecil 패처: 참조용 publicize, prefix 훅 삽입 |
| `tools/PerfPatcher/` | Mono.Cecil 패처: 메서드에 시간 측정 래퍼 삽입 |
| `tools/Verify/` | 패치된 DLL을 .NET JIT로 컴파일해 IL 오류를 원본과 비교 |
| `tools/find_moo/` | 게임 설치 폴더 찾기(Steam 라이브러리 / GOG / `MOO_DIR`) |
| `tools/FcpPatch/`, `tools/FixPatcher48/`, `tools/release/`, `tools/make_release.sh` | 바이너리 릴리스: 설치 프로그램(.NET Framework 4.8), .NET Framework 4.8용 FixPatcher, 패키지 템플릿, 패키지 스크립트 |
| `tools/check_tools.*` | .NET SDK 9+, Java 8+, FFDec 확인(setup과 build가 사용) |
| `tools/scripts/` | SWF 문자열 추출, SWF→XML에서 메서드 본문 인덱스 찾기(분석용) |
| `tools/ffdec/` | *(저장소에 없음)* JPEXS FFDec를 여기에 풀어 둠 |
| `swf/patches/` | EmpireManagement.swf 메서드 본문 13개의 변경 내용(원본 P-code 대비 diff) |
| `setup.*`, `build.*`, `install.*`, `uninstall.*` | `.cmd`는 명령 프롬프트용, `.sh`는 Git Bash용 |

`.gitignore`는 `build/`, `tools/ffdec/`, 게임 바이너리, `_local_only/`(작성자의 로컬 작업 자료)를 제외합니다.

### 동작 방식

1. **C# (Assembly-CSharp.dll)**: 원본 메서드 앞에 Harmony 방식 *prefix*를 IL로 삽입. prefix가 `MooFix.dll`을 호출하고,
   `true`면 원본을 건너뛰고(결과는 out 인자), `false`면 원본 실행. MooFix에서 원본을 다시 부를 때는 스레드별 `bypass` 플래그 사용.
2. **Scaleform 래퍼 (Assembly-CSharp-firstpass.dll)**: `Movie.DoHitTest`에 같은 방식의 prefix 1개.
3. **Flash UI (EmpireManagement.swf)**: 클래스 재컴파일 대신 **메서드 본문만** P-code로 교체(12개, 제국 관리 목록 가상화 포함).
   원본 본문은 사용자의 SWF에서 내보내고 `swf/patches`의 diff를 적용합니다.
4. **MooFix 로그**: `문서\Master of Orion\moofix_log.txt`. 같은 폴더에 `moofix_verify.txt`를 만들면 검증 모드
   (16번 중 1번 원본과 결과 비교, 불일치 시 `MISMATCH` 기록).

### 문서 목록
위 영어 목록과 같습니다(각 문서 아래쪽에 한국어 부분이 있습니다).

### 라이선스
이 프로젝트의 코드와 문서는 MIT. 게임, UCP, 제3자 도구는 각 소유자의 것이며 포함되어 있지 않습니다. [LICENSE.md](LICENSE.md) 참고.
