# 07. UCP dependency, distribution, contributing

## Dependency on UCP
- The originals used for the patch, `Assembly-CSharp.dll` (dated 2024-01) and `EmpireManagement.swf` (2023-11), are **files replaced by UCP 6.4b**.
  The patched DLL therefore contains UCP code (`UCPStartTimer`, `UCPLog`, UCP features of the Empire Management screen, …).
- The SWF patch uses the empty method `ColonyWindow.adjustScrollbar` that UCP added, and its method body indices refer to this SWF.
- `Assembly-CSharp-firstpass.dll` seems untouched by UCP (same date as the other game files).
- Consequences: removing UCP removes the patch; a new UCP version requires a rebuild from the new files; a vanilla build would need vanilla originals plus SWF rework (no vanilla originals on this PC).
- Local mod folder `Documents\Master of Orion\Mods\Unofficial Code Patch\description.txt`: "This mod contains the Yaml updates used by UCP. This mod MUST be active if you are using UCP."

## Steam Workshop
- The game's mod system only loads **`*.yaml` (data) and `*.csv` (localization)** from active mod folders
  (`Universe.cs:587` `GetActiveModPaths("yaml")`, `Localization.cs:240` `GetActiveModPaths("csv")`). DLLs/SWFs in a mod are not loaded.
- Upload UI: `config.cfg` `[Content] UploadModTool = True`. The upload code has a UCP thumbnail check (`allowUCPUpload`, around `UIStateMods.cs:228`).
- The patched DLL/SWF contain original game code and UCP code → redistributing them means redistributing others' work.
- Recommended ways to distribute: ① contribute to UCP; ② publish only what you wrote (MooFix source/binary, patchers, P-code scripts) and apply the patch on the user's PC (with an original-hash check), via GitHub, Nexus, or a Steam guide.
  - **Implemented (option ②):** the repository holds no game/UCP files. The build takes the originals from the user's game folder
    (SHA-256 checked), and the SWF changes are shipped as diffs (`swf/patches`) applied to P-code exported from the user's SWF
    ([04](04_BUILD_AND_INSTALL.md#original-files), [05](05_SWF_PATCHING.md#distributing-the-changes-as-diffs)).
    The diffs still contain one line of original context per change and the replaced instructions; see [LICENSE.md](../LICENSE.md).
  - Do not publish `build/` output or the patched game files.

## Contributing to UCP (researched 2026-11)
- One-person project by WhatIsSol. No formal contribution process, repository or team page found.
- Outside contributions have been merged before: the 6.4a description says "Drunked Rompa supplied the needed P-Code to correct" (2 fixes); the change log thanks Spud Dastardly for bug fixes (seen only via a summary).
- Workflow: "I do all my edits of the DLL in CIL using the tools ildasm (decompile) and ilasm (compile)". Only the Java installer is published with source; the DLL is binary.
  → This patch's prefix + external DLL design does not merge directly; a **per-method change report** (what, why, equivalence argument, measurements) is the realistic way to contribute.
- Latest release 6.4b (2024-01-05). Author reply on the Workshop comments dated "27 Aug" (year not shown; probably 2026).
- Contact: pinned Workshop discussion "UCP 6.4a/b Bugs" and the author's Steam profile. No e-mail/Discord.
- To check: a logged-out fetch of the Workshop page showed a "removed for violating Community Guidelines" banner → verify yourself.

Sources:
- https://steamcommunity.com/sharedfiles/filedetails/?id=1109736522
- https://steamcommunity.com/sharedfiles/filedetails/changelog/1109736522?p=1
- https://steamcommunity.com/workshop/filedetails/discussion/1109736522/3873718561390759260
- https://steamcommunity.com/workshop/filedetails/discussion/1109736522/1483235412198485312
- https://www.moddb.com/mods/ucp (not readable: HTTP 403)
- https://www.gog.com/forum/master_of_orion_conquer_the_stars/unofficial_code_patch_ucp_62_released

---

## 한국어 (Korean)

### 07. UCP 의존성, 배포, 기여

#### UCP 의존성
- 원본으로 쓴 `Assembly-CSharp.dll`(2024-01)과 `EmpireManagement.swf`(2023-11)는 **UCP 6.4b가 교체한 파일**. 패치된 DLL에 UCP 코드가 들어 있다.
- SWF 패치는 UCP가 추가한 빈 메서드 `adjustScrollbar`를 이용하고, 메서드 본문 인덱스도 이 SWF 기준.
- `Assembly-CSharp-firstpass.dll`은 UCP가 건드리지 않은 것으로 보임.
- 결과: UCP를 빼면 패치도 사라지고, UCP 업데이트 시 재빌드 필요. 바닐라 대응은 바닐라 원본으로 재빌드 + SWF 재작업(이 PC에는 바닐라 원본 없음).

#### Steam 창작마당
- 모드 시스템은 활성 모드 폴더에서 **`*.yaml`과 `*.csv`만** 읽음. DLL/SWF는 로드되지 않음.
- 업로드 UI: `config.cfg`의 `[Content] UploadModTool = True`.
- 패치된 DLL/SWF에는 게임 원본 코드와 UCP 코드가 포함 → 그대로 배포하면 타인 저작물 재배포.
- 권장: ① UCP에 기여, ② 직접 작성한 부분(MooFix, 패처, P-code 스크립트)만 배포하고 사용자 PC에서 적용(원본 해시 확인). 
  - **②로 구현 완료**: 저장소에는 게임/UCP 파일이 없고, 빌드가 사용자 게임 폴더에서 원본을 가져오며(SHA-256 확인), SWF 변경은 사용자 SWF에서 내보낸 P-code에 적용하는 diff(`swf/patches`)로 배포합니다. diff에는 변경마다 원본 문맥 1줄과 교체되는 원본 명령이 남습니다([LICENSE.md](../LICENSE.md)).
  - `build/` 결과물이나 패치된 게임 파일은 공개하지 않습니다.

#### UCP 기여 가능성 (2026-11 조사)
- WhatIsSol 1인 프로젝트. 공식 기여 절차·저장소 없음. 외부 기여 반영 사례 있음(Drunked Rompa의 P-Code 수정, Spud Dastardly의 버그 수정).
- 제작자는 ildasm/ilasm으로 CIL을 직접 편집 → **메서드별 변경 내역서**로 전달하는 것이 현실적.
- 최신 6.4b(2024-01-05), 제작자 답변 "27 Aug"(2026년 추정). 문의는 창작마당 토론 "UCP 6.4a/b Bugs"와 Steam 프로필.
- 확인 필요: 로그인 없이 읽은 창작마당 페이지에 "가이드라인 위반으로 삭제" 배너가 보였다는 보고.

출처는 위 영어 부분 참고.
