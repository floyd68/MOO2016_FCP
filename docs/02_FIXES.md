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

**Shared A\* core.** `FastPlotPath` and the star separation matrix (section G) use one faithful copy of `PathFinder.FindPath`
(`FaithfulAStar<G>`): the game's `PriorityList<Node>`, the same push/pop sequence and f/g values, the "closest cell" rule when the
target is unreachable. Graph differences are injected through a small interface implemented by structs (`GalaxyGraph`,
`StarSepGraph`), so Mono compiles a specialised copy per graph. Open/closed sets are mark arrays (membership tests only).
Re-verified after the refactoring: 500 path searches and 3,841 star pairs compared, 0 mismatches.

## F. Turn results and screens (main thread)

| Hook | Problem | Change |
|---|---|---|
| `VisualActionsSequencer.ProcessQueueableMessages` + `SWFEmpireBottomBar.RefreshNotifications` | one chunk of turn-result messages called `RefreshNotifications` per notification; each call rebuilds the whole list in Flash (0.26–0.6 s) → 105 calls = 19.7 s after a battle, 99 = 16.6 s after an event | refreshes requested inside one chunk are merged into one at the end of the chunk (same final list). Measured afterwards: 714 → 1 refresh in 12 ms |
| `RadarZone.RefreshAll` | the radar outline (Clipper union of ~350 colony/fleet circles, 0.55–0.7 s) was rebuilt after each of the player's fleet animations, i.e. in consecutive frames during the AI stage | debounced: rebuilt once requests stop for 0.5 s, at the latest 2 s after the first request |
| `Civilization.GetTradeableResourceCount` / `GetRawTradeableResourceProduction` | asked per resource-requiring ship module by ship cost calculations (planet screen ETAs); each call scans all colonies, developments and deals | cache scope, per (civilization, resource) |
| `Colony.RunUpdateProductionWorkers` + `Universe.DispatchMessage` | a citizen drag & drop sent `MessageRefreshPlanet(All)` (full planet screen rebuild) | messages sent while that action runs are downgraded to the game's own partial refresh `ProductionGrid` (job grid, production values/ETAs, current item) |
| `SWFTechTree.SendTechTree`, `SWFResearchManagement.SendResearchData` | every technology node recomputed the empire research output (238 × = 4.5 s when opening the tech tree) | cache scope |
| `Colony.UpdateObsoleteBlueprintsInProductionQueue` | runs for every colony when a blueprint becomes obsolete; recomputed `GetAvailableBuildables(ignoreQueue: true)` for every queue item | the list cannot change during the loop (it ignores this colony's queue; the empire-wide checks exclude this colony). No ship item in the queue → the result is irrelevant and not computed; otherwise computed once |

## G. Load and turn simulation

| Hook | Problem | Change / why identical |
|---|---|---|
| `Civilization.ScanNavigationNodesInRange` | every scanner (~1,800 colonies/developments/fleets) evaluated every star and every discovered node of every explored star: 12.6 M visibility tests at load, and per espionage "reveal colonies" | `EvaluateScanDetection` only acts when "visible" ≠ "already detected by this scanner", and "visible" always requires `scanner.DetectPosition` (pure distance test). Nodes clearly out of range and not detected by the scanner are skipped (borderline distances never). Same iteration order, without per-child LINQ enumerators. 92% skipped at load |
| `SpatialData.BuildStarsTravelDistance` | all-pairs A* (177² pairs) per universe on every load; the heuristic (squared distance) is not admissible, so BFS would give different values | faithful A* (shared core) with neighbours precomputed per star; fills Universe's separation cache exactly like the original — **including hash collisions**: the cache key's `Equals` compares hash codes only, and the original then reuses the value of the earlier colliding pair. 2 s → 0.45 s per universe |
| `Colony.GetPopulationGrowthETA` | refreshed into every observer's known colony data (once per observing civilization) | cache scope |
| `SpatialData.BuildTerritory` + `NavigationNode.GetConnectedStars` | ~260,000 calls per turn, each enumerating connections and allocating a list | the star topology does not change during `BuildTerritory` and its call sites only iterate → computed once per node for one `BuildTerritory` |
| `Universe.AdvanceSimulation` (client) + `MajorCivilization.CheckDeficit` | `Colony.RefreshGrid` (client only) recomputed the empire income for the deficit advice after every colony update: 323 × per turn, 1.7 s | while the client simulates, checks are recorded and run once per civilization at the end (advice only, no game state). The advice now reflects the end-of-turn balance |
| `TimelineManager.GetTurnData` | per-turn timeline snapshot: serialization 0.44 s + LZ4 **HC** 0.47 s on the main thread | same code with `LZ4Codec.Encode32` (same block format, decoded by the unchanged `Decode32`); stored turn data becomes somewhat larger |

## H. Overlapped turn simulation (experimental, `moofix_overlap.txt`)

Single player simulates every turn twice, one after the other: the server thread, then the client (main thread), compared by checksum.
The server broadcasts `AdvanceUniverseTurn` right before simulating, when the client already has every action of the turn
(verified: no action arrives in between). With `Documents\Master of Orion\moofix_overlap.txt` present the client starts its
simulation on that message, so both run at the same time; on `ProcessingComplete` only the checksums are compared.
Shared static state the two simulations would race on is serialized with locks (`CheckSum.CalculateCheckSum`, `RandomEvent.Create`).
A mismatch or an exception goes through the game's own single-player recovery. Measured: a turn with 6.9 s + 7.6 s sequential
simulations froze the screen for ~8 s instead of ~15 s; checksums matched in every test turn. If the file contains `alternate`,
only even turns overlap (for comparisons). Delete the file to turn it off (no restart needed).

## I. Diagnostics (log only)

`FRAME stall` (gaps ≥ 250 ms between frames, with the server state), `SERVER state`, `PLANET refresh`, `NOTIF`, `RADAR`, `STARDIST`,
`DEFICIT`, `TIMELINE`, `OVERLAP` lines in `moofix_log.txt`.

## J. How it was verified

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

**공통 A\* 뼈대.** `FastPlotPath`와 별 사이 거리 행렬(G)은 `PathFinder.FindPath`를 충실히 옮긴 `FaithfulAStar<G>` 하나를 씁니다(게임의 `PriorityList<Node>`, 같은 넣기·꺼내기 순서와 f·g 값, 도달 불가 시 "가장 가까운 칸" 규칙). 그래프별 차이는 구조체(`GalaxyGraph`, `StarSepGraph`)가 구현하는 작은 인터페이스로 주입합니다. 공통화 후 다시 검증: 경로 500건, 별 쌍 3,841건 불일치 0.

#### F. 턴 결과 연출과 화면 (메인 스레드)

| 훅 | 문제 | 수정 |
|---|---|---|
| `VisualActionsSequencer.ProcessQueueableMessages` + `SWFEmpireBottomBar.RefreshNotifications` | 턴 결과 메시지 묶음 처리 중 알림마다 목록 전체를 Flash로 재구성(1회 0.26~0.6초) → 전투 후 105회 19.7초, 이벤트 후 99회 16.6초 | 한 묶음 동안의 갱신 요청을 끝에 한 번만(최종 목록 동일). 적용 후 714회 → 1회, 12ms |
| `RadarZone.RefreshAll` | 플레이어 함대 연출이 끝날 때마다 레이더 테두리(약 350개 원의 합집합, 0.55~0.7초) 재계산 | 요청이 0.5초 멈추면 한 번, 늦어도 2초 안에 다시 그림 |
| `GetTradeableResourceCount` / `GetRawTradeableResourceProduction` | 우주선 비용 계산이 모듈마다 호출, 매번 식민지·거점·거래 전체 순회 | 캐시 범위, (문명, 자원)별 |
| `Colony.RunUpdateProductionWorkers` + `Universe.DispatchMessage` | 시민 드래그가 행성 화면 전체 재구성(`All`) 요청 | 그 행동 중의 요청을 게임의 부분 갱신 `ProductionGrid`로 |
| `SWFTechTree.SendTechTree`, `SWFResearchManagement.SendResearchData` | 기술마다 제국 연구량 재계산(238회, 4.5초) | 캐시 범위 |
| `Colony.UpdateObsoleteBlueprintsInProductionQueue` | 블루프린트가 구식이 되면 모든 식민지에서 대기열 항목마다 건설 가능 목록 재계산 | 루프 중 목록 불변. 우주선 항목이 없으면 계산 생략, 있으면 1회 |

#### G. 로딩과 턴 시뮬레이션

| 훅 | 문제 | 수정 / 같은 결과인 이유 |
|---|---|---|
| `Civilization.ScanNavigationNodesInRange` | 스캐너(약 1,800개)마다 모든 별·탐사한 노드 전부 평가: 로딩 때 가시성 판정 1,260만 번, 첩보 "식민지 공개" 때도 | 평가 함수는 "보임"과 "이미 탐지 중"이 다를 때만 동작하고, "보임"은 항상 거리 검사가 참이어야 함 → 확실히 범위 밖이고 탐지 중이 아닌 노드만 건너뜀(경계 근처는 제외). 같은 순회 순서, LINQ 열거자 할당 제거. 로딩 때 92% 건너뜀 |
| `SpatialData.BuildStarsTravelDistance` | 로딩마다 universe별 모든 별 쌍(177²) A*. 휴리스틱(거리 제곱)이 허용 가능하지 않아 BFS로 바꾸면 값이 달라짐 | 공통 A* + 별마다 이웃 목록 미리 계산. 거리 캐시도 원본과 똑같이 채움 — **해시 충돌 포함**(키의 `Equals`가 해시값만 비교하므로 원본은 충돌한 쌍에 앞 쌍의 값을 재사용). universe당 2초 → 0.45초 |
| `Colony.GetPopulationGrowthETA` | 관찰 문명마다 같은 식민지를 반복 계산 | 캐시 범위 |
| `SpatialData.BuildTerritory` + `NavigationNode.GetConnectedStars` | 턴당 약 26만 회 호출, 매번 리스트 할당 | 영토 계산 중 연결 구조 불변, 호출부는 순회만 → 노드별 1회 |
| `Universe.AdvanceSimulation`(클라이언트) + `MajorCivilization.CheckDeficit` | 식민지 갱신마다(클라이언트 전용) 적자 경고용 제국 수입 재계산: 턴당 323회, 1.7초 | 클라이언트 시뮬레이션 중에는 모아 두었다가 끝에 문명별 1회(안내 메시지만, 게임 상태 무관). 경고는 턴 종료 시점의 수지를 기준으로 표시 |
| `TimelineManager.GetTurnData` | 턴마다 타임라인 저장: 직렬화 0.44초 + LZ4 **HC** 0.47초(메인 스레드) | 같은 코드에 `LZ4Codec.Encode32`(같은 블록 형식, 해제는 원래 `Decode32` 그대로). 턴당 저장 데이터가 다소 커짐 |

#### H. 턴 시뮬레이션 겹치기 (실험 기능, `moofix_overlap.txt`)

싱글플레이는 매 턴을 서버 스레드에서 한 번, 이어서 클라이언트(메인 스레드)에서 한 번 시뮬레이션하고 체크섬을 비교합니다. 서버는 시뮬레이션 직전에 `AdvanceUniverseTurn`을 보내는데, 이때 클라이언트는 이미 그 턴의 행동을 모두 받은 상태입니다(그 사이에 끼어드는 행동 없음을 확인). `문서\Master of Orion\moofix_overlap.txt`가 있으면 클라이언트가 이 메시지에서 바로 시뮬레이션을 시작해 두 시뮬레이션이 동시에 돌고, `ProcessingComplete`에서는 체크섬만 비교합니다. 두 시뮬레이션이 공유하는 정적 상태(`CheckSum.CalculateCheckSum`, `RandomEvent.Create`)는 잠금으로 직렬화했습니다. 불일치나 예외는 게임 자체의 싱글플레이 복구로 넘어갑니다. 측정: 6.9초 + 7.6초가 순서대로 걸리던 턴의 화면 멈춤이 약 15초 → 약 8초. 시험한 모든 턴에서 체크섬 일치. 파일 내용이 `alternate`면 짝수 턴만 겹침(비교용). 파일을 지우면 꺼집니다(재시작 불필요).

#### I. 진단 (로그만)

`moofix_log.txt`의 `FRAME stall`(250ms 이상 프레임 간격과 서버 상태), `SERVER state`, `PLANET refresh`, `NOTIF`, `RADAR`, `STARDIST`, `DEFICIT`, `TIMELINE`, `OVERLAP` 줄.

#### J. 검증 방법

- **JIT 검사**(`build.cmd verify` / `VERIFY=1 ./build.sh`): 모든 메서드를 CoreCLR JIT로 컴파일해 오류 목록이 원본과 같은지 확인(원본에도 CoreCLR에서만 걸리는 5개 존재).
- **동치 검증 모드**(`moofix_verify.txt`): 16번 중 1번 원본과 비교, 다르면 `MISMATCH` 로그 후 원본 값 사용.
- **SWF 검사**: 전후 모든 스크립트를 P-code로 내보내 비교 → 의도한 클래스만 달라야 함. 원본 본문 재삽입 시 바이트 동일 확인.
