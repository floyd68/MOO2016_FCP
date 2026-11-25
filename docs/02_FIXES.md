# 02. Fixes and why they give identical results

All C# fixes live in `src/MooFix/MooFix.cs`; the `hooks` table in `tools/FixPatcher/Program.cs` decides which method gets which prefix.
Prefix convention: `static bool Pre_X(Self self, args..., out R result)` — `true` = skip the original, `false` = run it.
To call the original from MooFix: `bypass = true; self.Method(...)` (the called prefix consumes the flag and falls through to the original).

## A. Simulation (server and client, always active)

| Hook | Change | Why the result is identical |
|---|---|---|
| `Colony.GetStructuresMoraleBoost` | iterate only the planets of the same star instead of the whole empire | same set of colonies; with ≤2 non-zero system-morale terms the summation order does not matter (IEEE addition is commutative); with ≥3 the original runs |
| `Awareness.IsProductionMature/IsResearchMature/IsLifeMature` | check only buildings with the matching bonus type, stop at the first available one | original = `GetAvailableStructures(ignoreQueue:true)` filtered, then count == 0; the checks are side-effect free, so existence is enough |
| `Colony.GetStructureSecurityBonus` | no LINQ, no list allocation | same values added in the same order (bit-identical) |
| `MajorCivilization.CheckDeficit` | skipped once `advice_deficit` has been shown (showAgainDictionary = false) | its only effect, `ShowAdvice`, is a no-op in that state (`isUnique` advice) |
| `MajorCivilization.UpdateNotifications` | cache scope | no buildings/queues/revolt state/colony lists change inside |
| `NavigationNode.ReportChange`, `Civilization.RefreshVisibleKnownNodes`, `MajorCivilization.ScanNavigationNodesInRange` | cache scope → `GetEmpireSecurity` once per refresh | these only refresh known data. Virtual methods are hooked only on the final override (`MajorCivilization`); hooking base and override would recurse forever |

### Cache scope
Only while `[ThreadStatic] scopeDepth > 0`; cleared when the scope ends **and before/after every action** (wrapped `Universe.DispatchAction`):
- `MajorCivilization.GetEmpireSecurity`, `Civilization.CalculateResearchPoints` → per civ value
- `Civilization.HasBuiltStructure/HasBuiltUpgrade/HasBuildableInQueue` → per civ index (built set, upgrade-chain set, queue key → colony count / first id)
- `Colony.GetAvailableBuildables` → per colony result (a copy is returned each time; `ShipRaceProductionItem` is recreated on every call, so verification compares type + identifier)

## B. UI (main thread)

| Hook | Change |
|---|---|
| `SWFPlanet.OnMessage`, `SWFEmpireTopBar.OnMessage`, `SWFEmpireBottomBar.OnMessage`, `SWFEmpireManagement.OnMessage` | refresh requests are collected and processed **once per frame** in `UIManager.LateUpdate` (flags OR-ed, `All` → Refresh). The planet screen is refreshed only while its movie belongs to the current state; removed movies are dropped via `OnRemoved` hooks |
| `SWFEmpireManagement.LoadColoniesData/UpdateSpecificColony`, `SWFPlanet.Refresh`, `SWFEmpireTopBar.SendEmpireData` | cache scope |
| `Scaleform.Movie.DoHitTest` (firstpass) | real cost measured per movie; only for movies ≥20 ms: reuse at the same position, reuse up to 2 s while the mouse moves (Repaint), compute once when the mouse rests ≥0.12 s. **For the Empire Management movie (`SWFEmpireManagement`), after 3 real `true` results it returns true without calling native code** (any `false` disables this and is logged) |
| `SWFMovie.PlaySound` | intercepts `MOOPERF:` diagnostics (only used by the profiling SWF) |

## C. Flash (EmpireManagement.swf, method body replacement)

| Class.method (body index) | Change |
|---|---|
| `ngd.ColonyWindow` constructor (712) | appends `addEventListener(ENTER_FRAME, adjustScrollbar)` |
| `ngd.ColonyWindow.FillList` (718) | sets only `colonyIndex` and marks rows as pending instead of `SetData`/`SetSelected` for every row |
| `ngd.ColonyWindow.adjustScrollbar` (1236) | the empty method UCP added becomes a per-frame handler: show only rows within ±400 px of the displayed and the target scroll position; pending rows are filled from the latest `_data` immediately if on screen, else up to 10 per frame; rows not yet filled stay hidden. Off-screen rows: tooltips unregistered, `mouseEnabled/mouseChildren=false`. First/last/selected rows always visible (keeps the scroll height). State is stored on the dynamic MovieClip `mc` (no new fields) |
| `ngd.ColonyWindow.setBuyBtns` (1237) | remembers the last credit value so rows that become visible get their buy button updated |
| `ngd.ASRoot.CheckScrollableObjects` (979) | returns immediately when `currentScrollableObject != null` (only one scrollable object exists in this SWF, so further iterations cannot change anything) |
| `fl.controls.ScrollBar.thumbPressHandler/thumbReleaseHandler` (818/820) | drag events are taken from `stage` instead of `focusManager.form` |

## D. AI path finding (server thread, inside `Universe.RunAI`)

| Hook | Change | Why the result is identical |
|---|---|---|
| `Universe.RunAI` | marks the AI scope, logs a per-pass summary | — |
| `Universe.DispatchAction` | clears the path cache and scope caches before and after each action; nothing is cached while an action runs | all state changes during the AI stage (AI orders, fleet movement) go through here |
| `Universe.PlotPath` | (1) reuse results keyed by from/to/civ/3 flags, (2) on a miss use `FastPlotPath` | (2) is a faithful re-implementation of `PathFinder.FindPath` + `NavigationNode.GetNeighbors`: the game's own `PriorityList`/`Node` (identical tie-breaking), same neighbor order, rules, cost (1) and partial-path rule for unreachable targets; `CanMoveAcross`/`WasDiscovered`/alliance checks call the originals. Only values that cannot change during one search are precomputed (jump gate presence/owner, owning star, achievement, AI flag) |

Verification: 1,059 comparisons in verification mode, 0 mismatches. Both runs issued exactly 16,869 path searches (same AI decision flow).

## E. How it was verified

- **JIT check** (`tools/Verify`, `build.cmd verify` / `VERIFY=1 ./build.sh`): every method of the patched DLL is JIT-compiled by CoreCLR;
  the list of `InvalidProgramException`s must equal the original's (the original has 5 methods that only CoreCLR rejects; they run fine on Mono).
- **Equivalence verification mode** (`moofix_verify.txt`): cached/replacement results compared with the original call (1 in 16 samples); differences are logged as `MISMATCH` and the original value is used.
- **SWF check**: export every script of the SWF before/after as P-code and `diff -r` → only the intended classes may differ.
  Tool sanity check: re-inserting an unmodified body produced a byte-identical SWF.

---

## 한국어 (Korean)

### 02. 수정 내역과 "원본과 같다"는 근거

모든 C# 수정은 `src/MooFix/MooFix.cs`에 있고, `tools/FixPatcher/Program.cs`의 `hooks` 표가 어느 메서드에 prefix를 넣는지 정합니다.
Prefix 규약: `static bool Pre_X(Self self, args..., out R result)` — `true` = 원본 건너뜀, `false` = 원본 실행.
MooFix에서 원본을 호출할 때: `bypass = true; self.Method(...)` (호출된 prefix가 플래그를 소비하고 원본으로 진행).

#### A. 시뮬레이션 (서버·클라이언트 모두, 항상 동작)

| 훅 | 수정 | 같은 결과인 이유 |
|---|---|---|
| `Colony.GetStructuresMoraleBoost` | 제국 전체 대신 같은 항성계의 행성들만 순회 | 같은 식민지 집합. 0이 아닌 시스템 사기 보너스 항이 2개 이하면 덧셈 순서와 무관(IEEE 교환법칙), 3개 이상이면 원본 실행 |
| `Awareness.IsProductionMature/IsResearchMature/IsLifeMature` | 조건(보너스 종류)에 맞는 건물만, 찾는 즉시 종료 | 원본 = `GetAvailableStructures(ignoreQueue:true)` 필터 후 개수==0. 판정 함수들이 상태를 바꾸지 않으므로 존재 여부만 보면 같음 |
| `Colony.GetStructureSecurityBonus` | LINQ·리스트 할당 제거 | 같은 순서로 같은 값을 더함(비트 단위 동일) |
| `MajorCivilization.CheckDeficit` | `advice_deficit`가 이미 표시됨(showAgainDictionary=false)이면 생략 | 이 함수의 유일한 효과인 `ShowAdvice`가 그 경우 아무것도 하지 않음(`isUnique` 조언) |
| `MajorCivilization.UpdateNotifications` | 캐시 범위(scope) | 범위 안에서 건물/대기열/반란/식민지 목록을 바꾸지 않음 |
| `NavigationNode.ReportChange`, `Civilization.RefreshVisibleKnownNodes`, `MajorCivilization.ScanNavigationNodesInRange` | 캐시 범위 → 갱신 한 번 동안 `GetEmpireSecurity` 1회 | 알려진 정보 갱신만 함. 가상 메서드는 최종 override에만 훅(양쪽에 걸면 무한 재귀) |

캐시 범위: `[ThreadStatic] scopeDepth > 0`일 때만 캐시, 범위를 나가면 비움, **모든 액션 실행 전후에도 비움**.
대상: `GetEmpireSecurity`, `CalculateResearchPoints`(문명별 값), `HasBuiltStructure/HasBuiltUpgrade/HasBuildableInQueue`(문명별 색인), `GetAvailableBuildables`(식민지별, 복사본 반환).

#### B. UI (메인 스레드)

| 훅 | 수정 |
|---|---|
| `SWFPlanet/SWFEmpireTopBar/SWFEmpireBottomBar/SWFEmpireManagement.OnMessage` | 갱신 요청을 모아 `UIManager.LateUpdate`에서 **프레임당 1회** 처리 |
| `LoadColoniesData/UpdateSpecificColony`, `SWFPlanet.Refresh`, `SendEmpireData` | 캐시 범위 |
| `Scaleform.Movie.DoHitTest` (firstpass) | 20ms 이상인 무비만 결과 재사용(같은 위치 / 이동 중 최대 2초 / 0.12초 멈추면 미리 계산). **제국 관리 무비는 실제 결과 true가 3번 확인되면 이후 true 반환**(false가 나오면 즉시 해제 + 로그) |
| `SWFMovie.PlaySound` | `MOOPERF:` 진단 메시지 가로채기(측정용 SWF 전용) |

#### C. Flash (EmpireManagement.swf, 메서드 본문 교체)

| 클래스.메서드 (본문 인덱스) | 수정 |
|---|---|
| `ColonyWindow` 생성자 (712) | 끝에 `addEventListener(ENTER_FRAME, adjustScrollbar)` 추가 |
| `ColonyWindow.FillList` (718) | 행마다 `SetData`/`SetSelected` 대신 `colonyIndex`만 설정하고 "대기" 표시 |
| `ColonyWindow.adjustScrollbar` (1236) | UCP가 만든 빈 메서드를 프레임 처리기로: 실제/목표 위치 주변 ±400px 행만 표시, 대기 행은 화면 안이면 즉시·여유 구간이면 프레임당 10개까지 최신 `_data`로 채움, 처리 전 행은 숨김. 화면 밖 행은 툴팁 해제 + 마우스 판정 제외. 첫/마지막/선택 행은 항상 표시. 상태는 동적 MovieClip `mc`에 저장 |
| `ColonyWindow.setBuyBtns` (1237) | 마지막 크레딧 값 저장 → 다시 보이는 행의 구매 버튼 갱신 |
| `ASRoot.CheckScrollableObjects` (979) | `currentScrollableObject != null`이면 즉시 반환(스크롤 대상이 하나뿐) |
| `ScrollBar.thumbPressHandler/thumbReleaseHandler` (818/820) | 드래그 이벤트를 `stage`에서 수신 |

#### D. AI 경로 탐색 (서버 스레드, `Universe.RunAI` 범위 안)

| 훅 | 수정 | 같은 결과인 이유 |
|---|---|---|
| `Universe.RunAI` | AI 범위 표시, 단계별 요약 로그 | — |
| `Universe.DispatchAction` | 실행 전후로 캐시 비우기, 실행 중에는 캐시 채우지 않음 | AI 단계의 상태 변화는 모두 여기를 거침 |
| `Universe.PlotPath` | (1) 출발·목적지·문명·옵션 3개 키로 결과 재사용 (2) 미스 시 `FastPlotPath` | (2)는 `PathFinder.FindPath` + `GetNeighbors`의 충실한 재구현: 게임의 `PriorityList`/`Node`로 동점 처리까지 동일, 이웃 순서·조건·비용·부분 경로 규칙 동일, 봉쇄·발견·동맹 판정은 원본 호출. 탐색 중 변하지 않는 값만 미리 계산 |

검증 모드 1,059건 비교 불일치 0. 두 실행에서 AI의 경로 요청 수가 16,869회로 동일.

#### E. 검증 방법

- **JIT 검사**(`build.cmd verify` / `VERIFY=1 ./build.sh`): 모든 메서드를 CoreCLR JIT로 컴파일해 오류 목록이 원본과 같은지 확인(원본에도 CoreCLR에서만 걸리는 5개 존재).
- **동치 검증 모드**(`moofix_verify.txt`): 16번 중 1번 원본과 비교, 다르면 `MISMATCH` 로그 후 원본 값 사용.
- **SWF 검사**: 전후 모든 스크립트를 P-code로 내보내 비교 → 의도한 클래스만 달라야 함. 원본 본문 재삽입 시 바이트 동일 확인.
