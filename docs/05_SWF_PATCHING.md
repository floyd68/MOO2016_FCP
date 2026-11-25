# 05. Patching the SWF (Scaleform / AS3)

## Key rule: never recompile a whole class

The first attempt replaced the whole `ngd.ColonyWindow` class with FFDec's AS3 compiler (`-replace <swf> <out> ngd.ColonyWindow ColonyWindow.as`).
The decompile→recompile output looked identical as source, but in game **all Empire Management rows piled up at the top and sorting stopped**
(Unity log: `### SF Exception prevented in FillColoniesData / updateSpecificColony / SetColonyFocusOptions`).
Comparing P-code showed a newly generated private namespace (`PrivateNamespace(null,"12")` → `PrivateNamespace("ngd:ColonyWindow")`),
`initproperty`→`setproperty`, `increment+convert_u`→`inclocal`, restructured conditions — existing code had changed too.

→ Switched to **keeping the original bytecode and replacing only method bodies with P-code**; no problems since.

## Procedure

1. **Find method body indices**
   ```
   java -jar ffdec.jar -swf2xml EmpireManagement.swf em.xml
   python -I tools/scripts/abcidx3.py em.xml ColonyWindow
   ```
   For the UCP 6.4b `EmpireManagement.swf`: ColonyWindow constructor 712, ResetFilters 713, FillList 718, SelectColony 719,
   updateSpecificColony 720, positionItems 721, ClearWidgets 722, adjustScrollbar 1236, setBuyBtns 1237,
   ASRoot.OnEnterFrame 973, ASRoot.CheckScrollableObjects 979, ScrollBar.thumbPressHandler 818, thumbReleaseHandler 820.

2. **Export the original P-code**
   ```
   java -jar ffdec.jar -format script:pcode -selectclass ngd.ColonyWindow -export script out EmpireManagement.swf
   ```
   The replacement unit is the `method … end ; method` block following `trait method QName(...,"name")`.

3. **Write the new body** — two ways
   - Small edits: edit the original P-code directly (e.g. `scroll_fixed.pcode` adds 4 lines at the top; `sb_*.fixed.pcode` replaces `focusManager.form` with `getlocal0; getproperty stage`).
   - Larger logic: write AS3 in a **scratch copy** only, let FFDec recompile that class, extract just that method's P-code, then normalize namespaces to the original notation:
     ```bash
     sed -e 's/PrivateNamespace("ngd:ColonyWindow")/PrivateNamespace(null,"12")/g' \
         -e 's/,PrivateNamespace("ColonyWindow\.as\$91")//g' -e 's/PrivateNamespace("ColonyWindow\.as\$91"),//g' \
         -e '/^debug /d' -e 's/^name "[^"]*"/name null/'
     ```
     (`"12"` is how the original P-code names this class's private namespace; check the original export for other classes.)
     The scratch source used for this patch is kept locally (`_local_only/swf_work_as/ColonyWindow.patched.as`, not published).
   - Avoid `try/catch`: the compiler then creates an activation object (NEED_ACTIVATION) and turns locals into slots.
   - New fields cannot be added; store state on a dynamic object (this patch uses `ColonyWindow.mc`, a dynamic MovieClip: `mfState`, `mfPending`, …).

4. **Insert**
   ```
   java -jar ffdec.jar -replace in.swf out.swf <class> <body.pcode> <body index> [<class> <body> <index> ...]
   ```

5. **Verify**
   - Tool sanity: inserting an unmodified original body must give a byte-identical SWF (`fc /b` or `cmp`) — confirmed.
   - Scope of change: export all scripts of both SWFs (`-format script:pcode -export script`) and compare directories → only the intended classes may differ.
   - Behavior: after playing, `MasterOfOrion_Data/output_log.txt` must contain no `SF Exception`.

## Distributing the changes as diffs

The repository does not contain complete method bodies (they would include the original bytecode). It contains only
`swf/patches/<name>.diff`: unified diffs (1 line of context) between the original and the patched body, with every line trimmed.
At build time:

```
java -jar ffdec.jar -format script:pcode -selectclass ngd.ColonyWindow,ngd.ASRoot,fl.controls.ScrollBar -export script build/pcode_export <original swf>
dotnet BuildHelper.dll apply build/pcode_export swf/patches build/pcode      # -> build/pcode/<name>.pcode
java -jar ffdec.jar -replace <original swf> <out swf> <class> build/pcode/<name>.pcode <index> ...
```

`BuildHelper` finds each method in the export (`trait method QName(...,"<name>")`, or `public function ColonyWindow()` for the
constructor), takes the `method … end ; method` block, and applies the diff. Every context and removed line must match the
original exactly at the stated position, otherwise the build stops ("Different SWF version?").

| Diff | Class | Method | Body index | Release | Perf |
|---|---|---|---|---|---|
| `ctor` | `ngd.ColonyWindow` | constructor | 712 | ✓ | ✓ |
| `fill` | `ngd.ColonyWindow` | `FillList` | 718 | ✓ | ✓ |
| `adj` | `ngd.ColonyWindow` | `adjustScrollbar` (empty in UCP; new logic) | 1236 | ✓ | ✓ |
| `buy` | `ngd.ColonyWindow` | `setBuyBtns` | 1237 | ✓ | ✓ |
| `reset` | `ngd.ColonyWindow` | `ResetFilters` | 713 | ✓ | ✓ |
| `select` | `ngd.ColonyWindow` | `SelectColony` | 719 | ✓ | ✓ |
| `update` | `ngd.ColonyWindow` | `updateSpecificColony` | 720 | ✓ | ✓ |
| `position` | `ngd.ColonyWindow` | `positionItems` | 721 | ✓ | ✓ |
| `clear` | `ngd.ColonyWindow` | `ClearWidgets` | 722 | ✓ | ✓ |
| `scroll` | `ngd.ASRoot` | `CheckScrollableObjects` | 979 | ✓ | ✓ |
| `sb_press` | `fl.controls.ScrollBar` | `thumbPressHandler` | 818 | ✓ | ✓ |
| `sb_release` | `fl.controls.ScrollBar` | `thumbReleaseHandler` | 820 | ✓ | ✓ |
| `enterframe` | `ngd.ASRoot` | `OnEnterFrame` (Flash timing diagnostic) | 973 | | ✓ |

Verified: the bodies produced this way are identical to the previously used full bodies, and the resulting SWFs are byte-identical.

### Maintaining the diffs

```
dotnet BuildHelper.dll extract build/pcode_export work/orig          # original bodies -> work/orig/<name>.orig.pcode
(edit a copy, e.g. work/new/fill.pcode, as described in "Procedure")
dotnet BuildHelper.dll mkdiff work/orig/fill.orig.pcode work/new/fill.pcode swf/patches/fill.diff
```
A new body also needs a row in `Bodies` in `tools/BuildHelper/Program.cs` and an entry in the `-replace` lists of `build.cmd`/`build.sh`.

## Diagnostic channel Flash → C#

`ExternalInterface.call("PlaySound", "MOOPERF:…", "")` reaches C# `SWFMovie.PlaySound(string,string)`.
MooFix's `Pre_SWFMovie_PlaySound` intercepts keys starting with `MOOPERF` and writes `FLASH …` lines to `moofix_log.txt` (`swf/patches/enterframe.diff`, perf build only).

## Notes on this SWF

- `ngd.ColonyWindow`: one `ngd.ColonyInfoWidget` row per colony inside an `ngd.components.ScrollPane` (no virtualization).
- `ngd.ASRoot`: runs `CheckTooltips` and `CheckScrollableObjects` on ENTER_FRAME only while the mouse is over the movie.
- Scroll height is computed in `Fill()` by `sp.update()` from `widgetContainer.height` (all used rows are visible at that moment).
- Thumb drag moves `contentScrollRect.y` to the target with a 0.2 s Actuate tween.

---

## 한국어 (Korean)

### 05. SWF(Scaleform / AS3) 패치 방법

#### 핵심 원칙: 클래스 전체를 재컴파일하지 말 것
FFDec AS3 컴파일러로 `ngd.ColonyWindow` 클래스 전체를 교체했더니, 소스는 같아 보였는데 게임에서 **제국 관리 행이 전부 맨 위에 겹치고 정렬이 멈췄습니다**
(Unity 로그 `### SF Exception prevented in …`). P-code 비교 결과 private 네임스페이스 재생성, `initproperty`→`setproperty`, `inclocal`, 조건식 구조 변경 등 기존 코드까지 바뀌어 있었습니다.
→ **원본 바이트코드는 그대로 두고 메서드 본문만 P-code로 교체**하는 방식으로 바꿨고, 이후 문제가 없었습니다.

#### 절차
1. **메서드 본문 인덱스 찾기**: `-swf2xml` 후 `python -I tools/scripts/abcidx3.py em.xml ColonyWindow`.
   UCP 6.4b 기준: 생성자 712, ResetFilters 713, FillList 718, SelectColony 719, updateSpecificColony 720, positionItems 721, ClearWidgets 722, adjustScrollbar 1236, setBuyBtns 1237, OnEnterFrame 973, CheckScrollableObjects 979, thumbPressHandler 818, thumbReleaseHandler 820.
2. **원본 P-code 내보내기**: `-format script:pcode -selectclass … -export script`. 교체 단위는 `method … end ; method` 블록.
3. **새 본문 만들기**: 짧은 수정은 P-code 직접 편집, 긴 로직은 임시 사본에서 AS3로 작성 → 재컴파일 → 해당 메서드 P-code만 추출 → 네임스페이스를 원본 표기로 정규화(위 sed). 작업용 소스는 `_local_only/swf_work_as/ColonyWindow.patched.as`.
   `try/catch` 금지(activation 객체 생성), 새 필드 불가 → 동적 객체(`mc`)에 상태 저장.
4. **삽입**: `-replace in.swf out.swf <클래스> <본문.pcode> <인덱스> …`
5. **검증**: 원본 본문 재삽입 시 바이트 동일 확인(완료), 전후 전체 P-code 비교로 의도한 클래스만 바뀌었는지, 플레이 후 `output_log.txt`에 `SF Exception` 없음.

#### diff로 배포
저장소에는 완전한 메서드 본문(원본 바이트코드 포함)이 없고, 원본 본문과 패치된 본문의 차이인 `swf/patches/<이름>.diff`(앞뒤 공백 제거, 문맥 1줄)만 있습니다.
빌드할 때 원본 SWF에서 세 클래스의 P-code를 내보내고 → `BuildHelper apply`가 각 메서드 블록을 찾아 diff 적용(문맥·삭제 줄이 정확히 일치해야 하며, 다르면 빌드 중단) → FFDec `-replace`로 삽입합니다.
대상: `ctor`(생성자, 712), `fill`(`FillList`, 718), `adj`(`adjustScrollbar`, 1236), `buy`(`setBuyBtns`, 1237), `reset`(`ResetFilters`, 713), `select`(`SelectColony`, 719), `update`(`updateSpecificColony`, 720), `position`(`positionItems`, 721), `clear`(`ClearWidgets`, 722), `scroll`(`CheckScrollableObjects`, 979), `sb_press`(818), `sb_release`(820), `enterframe`(`OnEnterFrame`, 973, 측정 빌드 전용).
이 방식으로 만든 본문은 이전의 전체 본문과 같고, 결과 SWF도 바이트 단위로 같음을 확인했습니다.

diff 관리: `BuildHelper extract <export> <폴더>`로 원본 본문을 꺼내고, 사본을 수정한 뒤 `BuildHelper mkdiff <원본> <수정본> swf/patches/<이름>.diff`.
새 본문을 추가하면 `tools/BuildHelper/Program.cs`의 `Bodies`와 `build.cmd`/`build.sh`의 `-replace` 목록에도 추가합니다.

#### Flash → C# 진단 통로
`ExternalInterface.call("PlaySound","MOOPERF:…","")` → `SWFMovie.PlaySound` → MooFix가 가로채 `moofix_log.txt`에 `FLASH …` 기록.

#### 이 SWF의 구조
- `ColonyWindow`: 식민지 수만큼 행 위젯을 모두 생성(가상화 없음). `ASRoot`: 마우스가 화면 위일 때만 ENTER_FRAME 검사.
- 스크롤 높이는 `Fill()`의 `sp.update()`에서 계산(이때 사용 중인 행은 모두 visible). 썸 드래그는 0.2초 트윈.
