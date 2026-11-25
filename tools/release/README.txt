MOO2016_FCP {VERSION} - late-game performance patch for Master of Orion: Conquer the Stars (2016)
https://github.com/floyd68/MOO2016_FCP
Steam Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3845815644

Playing Master of Orion, don't be afraid to press a button anymore - almost all of the annoying lags are drastically reduced!

REQUIREMENTS
  - Windows 10 or 11 (.NET Framework 4.8 is part of Windows; nothing else to install)
  - Master of Orion: Conquer the Stars, Steam version
  - Unofficial Code Patch (UCP) 6.4b installed (Steam Workshop 1109736522)

INSTALL
  1. Close the game.
  2. Extract this zip anywhere and run install.cmd.
     The game folder is found automatically (Steam libraries, GOG). If not, run:
       install.cmd "D:\SteamLibrary\steamapps\common\Master of Orion"
  3. Start the game as usual.
  The installer first checks that the game files are the UCP 6.4b originals (or this patch with UCP 6.4b *.orig backups).
  If not, it explains why and changes nothing. It creates *.orig backups of the three files it replaces.
  The patched files are produced on your PC from your own game files; this package contains no game code.

UNINSTALL
  Run uninstall.cmd (restores the *.orig backups and removes MooFix.dll).
  Steam "Verify integrity of game files" also restores the game, but then UCP has to be installed again.

OPTIONS (files in Documents\Master of Orion)
  moofix_warmup_off.txt   do not compile shaders while loading (loading ~5 s shorter, first opening of some 3D screens slower)
  moofix_overlap.txt      experimental: run the server and client turn simulations at the same time
  moofix_log.txt          the patch's log (timings, diagnostics)

Details, measurements and source: see the GitHub page.
License: MIT for this project's code (LICENSE.md). Mono.Cecil (MIT) is included for patching the game DLLs.
Unofficial - not affiliated with Wargaming, NGD Studios or the UCP author.

-----------------------------------------------------------------------------------------------------------------------
MOO2016_FCP {VERSION} - Master of Orion: Conquer the Stars (2016) 후반부 성능 패치

Master of Orion을 플레이 하다 더 이상 버튼 누를때 겁먹지 마세요. 거의 모든 짜증나는 랙들을 획기적으로 줄였습니다!

필요 조건
  - Windows 10 또는 11 (.NET Framework 4.8은 Windows에 포함, 따로 설치할 것 없음)
  - Master of Orion: Conquer the Stars Steam판
  - Unofficial Code Patch(UCP) 6.4b 설치 (Steam 창작마당 1109736522)

설치
  1. 게임을 종료합니다.
  2. 압축을 아무 곳에나 풀고 install.cmd를 실행합니다.
     게임 폴더는 자동으로 찾습니다(Steam 라이브러리, GOG). 못 찾으면:
       install.cmd "D:\SteamLibrary\steamapps\common\Master of Orion"
  3. 평소처럼 게임을 실행합니다.
  설치 프로그램은 먼저 게임 파일이 UCP 6.4b 원본인지(또는 UCP 6.4b *.orig 백업이 있는 이 패치인지) 확인하고,
  아니면 이유를 알려 주고 아무것도 바꾸지 않습니다. 교체하는 파일 3개는 *.orig로 백업합니다.
  패치된 파일은 사용자 PC에서 사용자의 게임 파일로 만들어지며, 이 패키지에는 게임 코드가 들어 있지 않습니다.

제거
  uninstall.cmd 실행(*.orig 백업 복원, MooFix.dll 삭제).
  Steam "게임 파일 무결성 확인"으로도 복원되지만, 그 경우 UCP를 다시 설치해야 합니다.

옵션 (문서\Master of Orion 폴더의 파일)
  moofix_warmup_off.txt   로딩 중 셰이더 미리 컴파일 안 함(로딩 약 5초 단축, 일부 3D 화면 첫 진입이 느려짐)
  moofix_overlap.txt      실험 기능: 서버·클라이언트 턴 시뮬레이션을 동시에 실행
  moofix_log.txt          패치 로그(시간 측정, 진단)

자세한 내용, 측정 결과, 소스: GitHub 페이지 참고.
라이선스: 이 프로젝트 코드는 MIT(LICENSE.md). 게임 DLL 패치용으로 Mono.Cecil(MIT)을 포함합니다.
비공식 패치이며 Wargaming, NGD Studios, UCP 제작자와 무관합니다.
