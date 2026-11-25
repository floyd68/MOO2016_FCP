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
| `ngd.ColonyWindow.FillList` (718) | sets only `colonyIndex` and marks rows as pending instead of `SetData`/`SetSelected` for every row; publishes the current sort (section K); reports the first construction of the row widgets (`FLASH FillList ...` log line) |
| `ngd.ColonyWindow.adjustScrollbar` (1236) | the empty method UCP added becomes a per-frame handler: show only rows within ±400 px of the displayed and the target scroll position; pending rows are filled from the latest `_data` immediately if on screen, else up to 10 per frame; rows not yet filled stay hidden. Off-screen rows: tooltips unregistered, `mouseEnabled/mouseChildren=false`. First/last/selected rows always visible (keeps the scroll height). State is stored on the dynamic MovieClip `mc` (no new fields) |
| `ngd.ColonyWindow.setBuyBtns` (1237) | remembers the last credit value so rows that become visible get their buy button updated; skips non-row children |
| `ngd.ColonyWindow.FillList/SelectColony/updateSpecificColony/positionItems/ClearWidgets/ResetFilters/adjustScrollbar` (718/719/720/721/722/713/1236) | **list virtualization** (section L): row widgets exist only for the visible rows ± 400 px and are reused while scrolling; this replaces the "all rows, filled lazily" scheme of the FillList/adjustScrollbar rows above |
| `ngd.ASRoot.CheckScrollableObjects` (979) | returns immediately when `currentScrollableObject != null` (only one scrollable object exists in this SWF, so further iterations cannot change anything) |
| `fl.controls.ScrollBar.thumbPressHandler/thumbReleaseHandler` (818/820) | drag events are taken from `stage` instead of `focusManager.form` |

## D. AI path finding (server thread, inside `Universe.RunAI`)

| Hook | Change | Why the result is identical |
|---|---|---|
| `Universe.RunAI` | marks the AI scope, logs a per-pass summary | — |
| `Universe.DispatchAction` | clears the path cache and scope caches before and after each action; nothing is cached while an action runs | all state changes during the AI stage (AI orders, fleet movement) go through here |
| `Universe.PlotPath` | (1) reuse results keyed by from/to/civ/3 flags, (2) on a miss use `FastPlotPath` | (2) is a faithful re-implementation of `PathFinder.FindPath` + `NavigationNode.GetNeighbors`: the same heap operations as the game's `PriorityList<Node>` (identical tie-breaking, see below), same neighbor order, rules, cost (1) and partial-path rule for unreachable targets; `CanMoveAcross`/`WasDiscovered`/alliance checks call the originals. Only values that cannot change during one search are precomputed (jump gate presence/owner, owning star, achievement, AI flag) |

Verification: 1,059 comparisons in verification mode, 0 mismatches. Both runs issued exactly 16,869 path searches (same AI decision flow).

**Shared A\* core.** `FastPlotPath` and the star separation matrix (section G) use one faithful copy of `PathFinder.FindPath`
(`FaithfulAStar<G>`): the same push/pop sequence and f/g values, the "closest cell" rule when the target is unreachable.
Graph differences are injected through a small interface implemented by structs (`GalaxyGraph`, `StarSepGraph`), so Mono
compiles a specialised copy per graph. Open/closed sets are mark arrays (membership tests only).
The open list is an int-array binary heap that performs **exactly the swaps of the game's `PriorityList<Node>`**: the game pushes
the same `Node` object again when a cheaper path is found and changes its `f` while older entries of it are still in the heap,
so the heap stores cell numbers and always compares the cells' *current* f values with `Node.CompareTo`'s rule
(no object comparer, no interface calls, no `Node` objects). Node data (jump gate, owning star) and cell numbers are kept for
the whole AI stage and dropped with the path cache. Verified: 509 path searches and 3,844 star pairs compared, 0 mismatches;
star separation matrix 0.55 s → 0.14 s per universe.

## E. AI stage caches (server thread, same validity rules as the path cache)

Active only inside `Universe.RunAI` and outside actions; cleared before/after every action, when the AI rebuilds its own
data (`Awareness.UpdateEmpire`, `CalculateStarCentrality`, `SP_GeneralStrategy.RefreshIndependentCivilizations/RefreshTargetVictoryCondition`)
and at the end of the AI stage. The server turn simulation also runs inside `RunAI`; it is treated like an action (no AI cache
is used or filled while it runs).

| Hook | Problem | Change / why identical |
|---|---|---|
| `Awareness.GetPlanetRating` | every known planet rated several times per pass (`HandleColonyShips`: GetPlanetsToColonize, then the average; strategy code again); each rating plots a path | result reused per (awareness, planet, colony ship blueprint, shared-system flag). ~1,300 reuses per turn |
| `Awareness.GetClosestLocationInEmpireTo` | per rating: separation levels from every star of the AI's empire | depends only on the target's star (and the empire set, rebuilt by `UpdateEmpire`) → per (awareness, star) |
| `MajorCivilization.CheckIfPlanetWouldBeUnderNonSettlementPact` | per planet: LINQ over all colonies to build "undominated neighbours of dominated stars" | that set does not depend on the planet → built once per civilization; the planet-specific tests run unchanged |
| `T_Engage.BoostPriorityOfBlockingSettlements` | for every path node, the whole engage target list was scanned again (O(T² × L)) | same loop with a node → targets index built once (list order kept; the loop only changes priorities, never the list or the nodes) |
| `NavigationNode.CacheCanTravelRegions` | profiling showed 2.2 s | timing only: real cost ~1 ms per AI (the profiler wrapped every recursive call) |

## G2. Turn simulation and loading (third round)

| Hook | Problem | Change / why identical |
|---|---|---|
| `Civilization.ProcessSettlementsOnTurn` + `MajorCivilization.GetEmpireSecurity` | ~1,040 empire security recomputations (O(colonies) each) per side and turn, because every refresh is its own cache scope | inside colony processing a per-civilization value is reused until a generation counter changes. The counter is bumped before and after everything that can change security: building/demolishing structures (all public overloads + the protected virtual cores), owner changes, colonies gained/lost, bombardment. 1,994 reused, 52 computed |
| `Colony.GetCreditOutput/GetUpkeepValue/GetSecurityRating`, `Settlement.GetOrbitalDefenseRating` | the same colony's values are copied into the known data of every observing civilization (~5 per change) | memoized per settlement inside cache scopes (~38,000 reuses per turn) |
| `Civilization.ScanNavigationNodesInRange` (system level) | after the node filter, walking the nodes was most of the scan | `NavigationNode.DetectionBeganBy/DetectionEndedBy` are wrapped to count, per scanner, the detected nodes per star system (exact: before/after comparison of `IsAlreadyDetectedBy`). A whole explored system is skipped when it is out of range by more than its radius (+ the same margins) and the scanner detects nothing in it; interstellar end points are still evaluated node by node |
| Allocation-free replacements | ~600 MB allocated per side and turn (and ~360 MB while loading) → 8 garbage collections per turn of ~150 ms each (old Boehm GC, full heap) | same results without the garbage: `Civilization.GetRawTradeableResourceProduction` (built `Planet.TradeableResources`, a new filtered Dictionary, twice per colony), `Star.HasAllConnectionsDiscoveredBy` (iterators + HashSets), `ColonyStructureType.IsCompatible` (closure per call, 130,000 calls), `MajorCivilization.GetEmpireStructureSecurityBonus`, `Civilization.GetStarSystemLeaderBonusValue` (LINQ). Same iteration order where it matters (float sums). Allocation per side ~600 → ~150 MB, GCs per turn 8 → 3 |

## K. Empire Management list state (convenience)

Opening a planet from the Empire Management list used to bring the list back sorted by name, scrolled to the top, first colony
selected (the planet screen is pushed on top and the list is refilled; with Ctrl+click the screen is closed and rebuilt).
`SWFEmpireManagement.OnColonyClicked` now reads the selected colony, the scroll position and the sort from the movie
(the patched `FillList` publishes the sort as `widgetContainer.name = "mfsort|<filter>|<direction>"`), and
`UIStateEmpireManagement.OnPresented` re-applies them once the list has been filled, through the screen's own paths:
header button clicks, the selection through the list's `selectedColonyID` setter (plus the C# `OnColonySelected`), then the scroll position. Applied once per visit; game state is not involved.

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

## L. First opening of screens (fourth round)

Measured per step (file read, native movie creation, first frame, C# → Flash calls, 3D stage switch, frame split):
reading and creating the SWF movies takes a few ms for every screen, so pre-caching more movies would not help.

| Change | Problem | Why identical / effect |
|---|---|---|
| Empire Management list virtualization (Flash, section C) | the first opening built 356 row widgets (1.5 s) and the next frame processed all of them for the first time (3 s; hidden widgets too) | widgets only for the rows within ±400 px of the view (15 at opening, ~20–40 while scrolling); a widget leaving the range is hidden and reused for the next row (`SetData` refills it completely, as the lazy filling did before). Row positions come from the row number and the selected row (94 px, selected 156 px). Selection is kept as a colony index, so it survives scrolling; `updateSpecificColony` updates the row data and the bound widget. The scroll height comes from a 1×1 px object at the bottom (a transparent rectangle of the full height was measured as 2^20 twips by Scaleform). First opening 4.7–6.9 s → < 1 s; list fill 1.66 s → 0.09 s |
| `FleetHelper.GetETAToAll` (Known Planets) | recursive depth-first label correcting: re-expands a node whenever a cheaper cost is found, calling `Fleet.CalcJumpETA` again for the same edges (1.35 s) | Dijkstra with the same expansion rule (start or not a planet, discovered, not an anomaly); the fixed point is the same shortest cost per node, and edge costs are non-negative integers, so the float sums are exact in any order. Compared with the original in verification mode: 0 mismatches. 5 ms |
| `HerefordGame.OnLoadingComplete` | the first display of a 3D view stalled for 1–2.5 s outside any C# code (espionage, races) | `Shader.WarmupAllShaders()` at the end of loading (~5.2 s more loading). Those first-opening stalls disappeared. `moofix_warmup_off.txt` disables it |
| `SWFEmpireManagement.OnColonyClicked` restore (section K) | the selected row may have no widget now | restores the selection through the `selectedColonyID` setter and calls the C# `OnColonySelected`, then the scroll position |

Diagnostics added (log only, slow cases): `Movie.MovieInit`, `Movie.Advance`, `AssetManager.LoadExternalFileToMemory`,
`GameStage.SetCurrent`, `SWFMovie.PumpDelayedCalls` (same loop, timed), `HerefordGame.Update` (frame split).

Not changed: switching the 3D stage (`GameStage.SetCurrent`: Unity activating/deactivating object trees, 0.06–1.8 s, varies
between switches of the same screen) and the two ~2 s frames right after loading (galaxy view construction and rendering).

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

`FRAME stall` (gaps ≥ 250 ms between frames, with the server state, garbage collections and heap size), `SERVER state`, `PLANET refresh`,
`NOTIF`, `RADAR`, `STARDIST`, `DEFICIT`, `TIMELINE`, `OVERLAP`, `SECURITY`, `FLASH`, `EMLIST`, `AI RunAI`, `MOVIE`, `STAGE`, `SWFCALL`, `WARMUP` lines in `moofix_log.txt`.
`UIVersion.GetBuildString` appends `**** FCP <version> ****` after the UCP version (main menu watermark, crash report).
The profiling build (`MooPerf`) also prints, per method, the allocated MB (self, approximate) and the collections that happened inside.

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
| `ColonyWindow.FillList` (718) | 행마다 `SetData`/`SetSelected` 대신 `colonyIndex`만 설정하고 "대기" 표시. 현재 정렬을 알려 줌(K). 행 위젯 첫 생성 시간 보고(`FLASH FillList ...` 로그) |
| `ColonyWindow.adjustScrollbar` (1236) | UCP가 만든 빈 메서드를 프레임 처리기로: 실제/목표 위치 주변 ±400px 행만 표시, 대기 행은 화면 안이면 즉시·여유 구간이면 프레임당 10개까지 최신 `_data`로 채움, 처리 전 행은 숨김. 화면 밖 행은 툴팁 해제 + 마우스 판정 제외. 첫/마지막/선택 행은 항상 표시. 상태는 동적 MovieClip `mc`에 저장 |
| `ColonyWindow.setBuyBtns` (1237) | 마지막 크레딧 값 저장 → 다시 보이는 행의 구매 버튼 갱신. 행이 아닌 자식은 건너뜀 |
| `ColonyWindow.FillList/SelectColony/updateSpecificColony/positionItems/ClearWidgets/ResetFilters/adjustScrollbar` (718/719/720/721/722/713/1236) | **목록 가상화**(L): 보이는 행 ±400px에만 행 위젯을 두고 스크롤 중 재사용. 위의 "모든 행 + 지연 채우기" 방식을 대체 |
| `ASRoot.CheckScrollableObjects` (979) | `currentScrollableObject != null`이면 즉시 반환(스크롤 대상이 하나뿐) |
| `ScrollBar.thumbPressHandler/thumbReleaseHandler` (818/820) | 드래그 이벤트를 `stage`에서 수신 |

#### D. AI 경로 탐색 (서버 스레드, `Universe.RunAI` 범위 안)

| 훅 | 수정 | 같은 결과인 이유 |
|---|---|---|
| `Universe.RunAI` | AI 범위 표시, 단계별 요약 로그 | — |
| `Universe.DispatchAction` | 실행 전후로 캐시 비우기, 실행 중에는 캐시 채우지 않음 | AI 단계의 상태 변화는 모두 여기를 거침 |
| `Universe.PlotPath` | (1) 출발·목적지·문명·옵션 3개 키로 결과 재사용 (2) 미스 시 `FastPlotPath` | (2)는 `PathFinder.FindPath` + `GetNeighbors`의 충실한 재구현: 게임의 `PriorityList<Node>`와 같은 힙 연산으로 동점 처리까지 동일(아래 참고), 이웃 순서·조건·비용·부분 경로 규칙 동일, 봉쇄·발견·동맹 판정은 원본 호출. 탐색 중 변하지 않는 값만 미리 계산 |

검증 모드 1,059건 비교 불일치 0. 두 실행에서 AI의 경로 요청 수가 16,869회로 동일.

**공통 A\* 뼈대.** `FastPlotPath`와 별 사이 거리 행렬(G)은 `PathFinder.FindPath`를 충실히 옮긴 `FaithfulAStar<G>` 하나를 씁니다(같은 넣기·꺼내기 순서와 f·g 값, 도달 불가 시 "가장 가까운 칸" 규칙). 그래프별 차이는 구조체(`GalaxyGraph`, `StarSepGraph`)가 구현하는 작은 인터페이스로 주입합니다.
열린 목록은 **게임의 `PriorityList<Node>`와 똑같이 교환하는** 정수 배열 이진 힙입니다. 게임은 더 싼 경로를 찾으면 같은 `Node` 객체를 다시 넣고, 이전 항목이 힙에 남아 있는 상태에서 그 `f`를 바꿉니다. 그래서 힙에는 칸 번호를 넣고 항상 칸의 *현재* f 값을 `Node.CompareTo`와 같은 규칙으로 비교합니다(객체 비교자·인터페이스 호출·`Node` 객체 없음). 노드 정보(점프 게이트, 소속 별)와 칸 번호는 AI 단계 동안 유지하고 경로 캐시와 함께 버립니다. 검증: 경로 509건, 별 쌍 3,844건 불일치 0. 별 거리 행렬 universe당 0.55초 → 0.14초.

#### E. AI 단계 캐시 (서버 스레드, 경로 캐시와 같은 유효 조건)

`Universe.RunAI` 안, 행동 실행 밖에서만 동작합니다. 행동 실행 전후, AI가 자기 데이터를 다시 만들 때(`Awareness.UpdateEmpire`, `CalculateStarCentrality`, `SP_GeneralStrategy.RefreshIndependentCivilizations/RefreshTargetVictoryCondition`), AI 단계 끝에서 비웁니다. 서버 턴 시뮬레이션도 `RunAI` 안에서 실행되므로 행동과 똑같이 취급합니다(실행 중 AI 캐시를 쓰지도 채우지도 않음).

| 훅 | 문제 | 수정 / 같은 결과인 이유 |
|---|---|---|
| `Awareness.GetPlanetRating` | 알려진 행성마다 한 단계에 여러 번 평가(`HandleColonyShips`의 후보 목록과 평균, 전략 코드), 평가마다 경로 탐색 | (awareness, 행성, 식민선 설계, 공유 항성계 여부)별 재사용. 턴당 약 1,300회 재사용 |
| `Awareness.GetClosestLocationInEmpireTo` | 평가마다 AI 제국의 모든 별에서 별 거리 조회 | 목표의 별(과 `UpdateEmpire`가 만드는 제국 목록)에만 의존 → (awareness, 별)별 1회 |
| `MajorCivilization.CheckIfPlanetWouldBeUnderNonSettlementPact` | 행성마다 모든 식민지를 LINQ로 돌며 "지배 중인 별의 주인 없는 이웃" 집합 생성 | 그 집합은 행성과 무관 → 문명별 1회. 행성별 검사는 그대로 |
| `T_Engage.BoostPriorityOfBlockingSettlements` | 경로 노드마다 교전 대상 목록 전체를 다시 훑음(O(T² × L)) | 노드 → 교전 대상 색인을 한 번 만들어 같은 루프 실행(목록 순서 유지, 루프는 우선순위만 바꾸고 목록·노드는 그대로) |
| `NavigationNode.CacheCanTravelRegions` | 측정 빌드에서 2.2초 | 시간 측정만: 실제 AI당 약 1ms(측정 코드가 재귀 호출마다 붙었던 것) |

#### G2. 턴 시뮬레이션과 로딩 (3차)

| 훅 | 문제 | 수정 / 같은 결과인 이유 |
|---|---|---|
| `Civilization.ProcessSettlementsOnTurn` + `MajorCivilization.GetEmpireSecurity` | 갱신마다 캐시 범위가 따로라 제국 보안(O(식민지))을 턴·한쪽당 약 1,040회 재계산 | 식민지 처리 중에는 세대 번호가 바뀌기 전까지 문명별 값을 재사용. 보안을 바꿀 수 있는 모든 동작 전후에 번호 증가: 건물 건설/철거(모든 공개 오버로드 + protected virtual 본체), 소유자 변경, 식민지 획득/상실, 폭격. 재사용 1,994회, 계산 52회 |
| `Colony.GetCreditOutput/GetUpkeepValue/GetSecurityRating`, `Settlement.GetOrbitalDefenseRating` | 같은 식민지 값을 관찰 문명마다(변경당 약 5개) 알려진 정보에 복사 | 캐시 범위 안에서 정착지별 메모(턴당 약 38,000회 재사용) |
| `Civilization.ScanNavigationNodesInRange` (항성계 단위) | 노드 걸러내기 후에도 노드 순회가 스캔 대부분 | `NavigationNode.DetectionBeganBy/DetectionEndedBy`를 감싸 스캐너별·항성계별 탐지 노드 수를 정확히 기록(`IsAlreadyDetectedBy` 전후 비교). 항성계 반지름(+같은 여유)만큼 더 멀고 그 스캐너가 계 안에서 아무것도 탐지하지 않으면 계 전체를 건너뜀. 성간 연결점은 노드 단위로 그대로 평가 |
| 할당 없는 대체 구현 | 턴·한쪽당 약 600MB 할당(로딩 중 약 360MB) → 턴당 GC 8회, 회당 약 150ms(오래된 Boehm GC, 힙 전체) | 같은 결과를 쓰레기 없이: `Civilization.GetRawTradeableResourceProduction`(식민지마다 두 번 `Planet.TradeableResources` = 새 Dictionary), `Star.HasAllConnectionsDiscoveredBy`(반복자 + HashSet), `ColonyStructureType.IsCompatible`(호출마다 클로저, 13만 회), `MajorCivilization.GetEmpireStructureSecurityBonus`, `Civilization.GetStarSystemLeaderBonusValue`(LINQ). 순서가 중요한 곳(실수 합)은 같은 순서. 한쪽 할당 약 600 → 약 150MB, 턴당 GC 8 → 3회 |

#### K. 제국 관리 목록 상태 유지 (편의 기능)

제국 관리 목록에서 행성을 열었다가 돌아오면 목록이 이름순, 맨 위, 첫 식민지 선택으로 초기화됐습니다(행성 화면이 위에 쌓이고 목록을 다시 채움, Ctrl+클릭이면 화면을 닫았다 새로 만듦). 이제 `SWFEmpireManagement.OnColonyClicked`에서 선택 식민지, 스크롤 위치, 정렬(패치된 `FillList`가 `widgetContainer.name = "mfsort|<기준>|<방향>"`으로 알려 줌)을 무비에서 읽어 두고, `UIStateEmpireManagement.OnPresented`에서 목록이 채워진 뒤 화면 자체 경로로 되돌립니다: 헤더 버튼 클릭, 목록의 `selectedColonyID` setter로 선택(C# `OnColonySelected`도 호출), 마지막으로 스크롤 위치. 방문당 한 번만 적용하며 게임 상태와는 무관합니다.

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

#### L. 화면 첫 전환 (4차)

단계별 측정(파일 읽기, 네이티브 무비 생성, 첫 프레임, C# → Flash 호출, 3D 스테이지 전환, 프레임 구간 분리): SWF 무비 읽기·생성은 모든 화면에서 수 ms라 무비를 더 미리 불러와도 효과 없음.

| 변경 | 문제 | 같은 결과인 이유 / 효과 |
|---|---|---|
| 제국 관리 목록 가상화(Flash, C) | 첫 진입 때 행 위젯 356개 생성(1.5초) + 다음 프레임에 숨긴 것까지 모두 첫 처리(3초) | 화면 ±400px 행에만 위젯(진입 시 15개, 스크롤 중 약 20~40개). 범위를 벗어난 위젯은 숨겼다가 다음 행에 재사용(`SetData`가 위젯을 완전히 다시 채움, 이전 지연 채우기와 같음). 행 위치는 행 번호와 선택 행으로 계산(94px, 선택 156px). 선택은 식민지 번호로 기억해 스크롤해도 유지, `updateSpecificColony`는 행 데이터와 연결된 위젯을 갱신. 스크롤 높이는 맨 아래 1×1px 객체로 맞춤(전체 높이의 투명 사각형은 Scaleform이 2^20 트윕으로 잘못 계산). 첫 진입 4.7~6.9초 → 1초 미만, 목록 채우기 1.66초 → 0.09초 |
| `FleetHelper.GetETAToAll`(알려진 행성) | 재귀 깊이 우선 label correcting: 더 싼 비용을 찾을 때마다 노드를 다시 펼쳐 같은 간선의 `Fleet.CalcJumpETA` 반복(1.35초) | 같은 펼침 조건(출발점이거나 행성이 아님, 발견함, 이상현상 아님)의 다익스트라. 고정점은 노드별 같은 최단 비용이고, 간선 비용이 0 이상의 정수라 실수 합이 순서와 무관하게 정확. 검증 모드로 원본과 비교 불일치 0. 5ms |
| `HerefordGame.OnLoadingComplete` | 3D 화면 첫 표시 때 C# 밖에서 1~2.5초 멈춤(첩보, 종족) | 로딩 끝에 `Shader.WarmupAllShaders()`(로딩 약 5.2초 증가). 해당 첫 진입 멈춤이 사라짐. `moofix_warmup_off.txt`로 끔 |
| `SWFEmpireManagement.OnColonyClicked` 복원(K) | 선택 행에 위젯이 없을 수 있음 | `selectedColonyID` setter로 선택을 넣고 C# `OnColonySelected` 호출, 그다음 스크롤 위치 |

추가한 진단(로그만, 느린 경우): `Movie.MovieInit`, `Movie.Advance`, `AssetManager.LoadExternalFileToMemory`, `GameStage.SetCurrent`, `SWFMovie.PumpDelayedCalls`(같은 루프에 시간 측정), `HerefordGame.Update`(프레임 구간 분리).

변경하지 않음: 3D 스테이지 전환(`GameStage.SetCurrent`, Unity 오브젝트 트리 활성화·비활성화, 같은 화면도 0.06~1.8초로 매번 다름), 로딩 직후 약 2초 프레임 2개(은하 화면 구성과 렌더링).

#### H. 턴 시뮬레이션 겹치기 (실험 기능, `moofix_overlap.txt`)

싱글플레이는 매 턴을 서버 스레드에서 한 번, 이어서 클라이언트(메인 스레드)에서 한 번 시뮬레이션하고 체크섬을 비교합니다. 서버는 시뮬레이션 직전에 `AdvanceUniverseTurn`을 보내는데, 이때 클라이언트는 이미 그 턴의 행동을 모두 받은 상태입니다(그 사이에 끼어드는 행동 없음을 확인). `문서\Master of Orion\moofix_overlap.txt`가 있으면 클라이언트가 이 메시지에서 바로 시뮬레이션을 시작해 두 시뮬레이션이 동시에 돌고, `ProcessingComplete`에서는 체크섬만 비교합니다. 두 시뮬레이션이 공유하는 정적 상태(`CheckSum.CalculateCheckSum`, `RandomEvent.Create`)는 잠금으로 직렬화했습니다. 불일치나 예외는 게임 자체의 싱글플레이 복구로 넘어갑니다. 측정: 6.9초 + 7.6초가 순서대로 걸리던 턴의 화면 멈춤이 약 15초 → 약 8초. 시험한 모든 턴에서 체크섬 일치. 파일 내용이 `alternate`면 짝수 턴만 겹침(비교용). 파일을 지우면 꺼집니다(재시작 불필요).

#### I. 진단 (로그만)

`moofix_log.txt`의 `FRAME stall`(250ms 이상 프레임 간격과 서버 상태, GC 횟수, 힙 크기), `SERVER state`, `PLANET refresh`, `NOTIF`, `RADAR`, `STARDIST`, `DEFICIT`, `TIMELINE`, `OVERLAP`, `SECURITY`, `FLASH`, `EMLIST`, `AI RunAI`, `MOVIE`, `STAGE`, `SWFCALL`, `WARMUP` 줄. `UIVersion.GetBuildString`이 UCP 버전 뒤에 `**** FCP <버전> ****`을 덧붙임(메인 메뉴 표시, 오류 보고). 측정 빌드(`MooPerf`)는 메서드별 할당량(MB, 자기 몫, 근사)과 그 안에서 일어난 GC 횟수도 출력합니다.

#### J. 검증 방법

- **JIT 검사**(`build.cmd verify` / `VERIFY=1 ./build.sh`): 모든 메서드를 CoreCLR JIT로 컴파일해 오류 목록이 원본과 같은지 확인(원본에도 CoreCLR에서만 걸리는 5개 존재).
- **동치 검증 모드**(`moofix_verify.txt`): 16번 중 1번 원본과 비교, 다르면 `MISMATCH` 로그 후 원본 값 사용.
- **SWF 검사**: 전후 모든 스크립트를 P-code로 내보내 비교 → 의도한 클래스만 달라야 함. 원본 본문 재삽입 시 바이트 동일 확인.
