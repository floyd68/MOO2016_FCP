# 01. Analysis: what was slow and why

Code locations refer to the ILSpy decompilation in `_local_only/decompiled/Assembly-CSharp/...`
(UCP 6.4b `Assembly-CSharp.dll`, sha256 `915c5eac…`, decompiled with ilspycmd 11.2).

## 0. Game architecture

- Unity 5.2.1p3, Mono (.NET 3.5 profile), DirectX 9. Game logic lives in `MasterOfOrion_Data/Managed/Assembly-CSharp.dll`.
- UI is Scaleform (Flash/AS3). SWFs are in `MasterOfOrion_Data/StreamingAssets/*.swf`; the C# wrapper is `Assembly-CSharp-firstpass.dll` (`Scaleform.*`).
- **Single-player is client/server too**: a server thread (`ServerThread.ServerLoop`, 33 ms loop) computes the turn, then the client
  (main thread) **recomputes the same turn** in `Client.AdvanceSimulation` and compares checksums. On mismatch the client becomes
  `OutOfSync` and stops simulating → every simulation change must produce identical results.
- AI runs on the server thread: `Universe.RunAI` → `AIPlayer.Play` → `Planner.RunTasks` (T_* tasks). AI orders go
  `AIPlayerBase.Send` → `Server.RunAndBroadcast` → `Universe.DispatchAction`.
- Each turn a timeline snapshot is appended to `Documents/Master of Orion/Saves/default.data` (setting `timeline_save_mode_1`).

## 1. O(N²) patterns found by static analysis (N = number of colonies)

| Location | What happens |
|---|---|
| `Colony.OnTurn` → `Awareness.CalculateColonyMature` (`AI/Awareness.cs:544`) | every colony of every civ builds `GetAvailableStructures` up to 3× per turn; for each empire-unique building `HasBuiltStructure`/`HasBuiltUpgrade`/`HasBuildableInQueue` (`Civilization.cs:4354,4367,4413`) scan all colonies |
| `Colony.Morale` → `GetStructuresMoraleBoost` (`Colony.cs:376`) | filters all `Owner.Colonies` to find colonies in the same system → one Morale read = O(N) |
| `Colony.GetSecurityRating` → `MajorCivilization.GetEmpireSecurity` → `GetAverageColonialSecurity` (`MajorCivilization.cs:2076`) | empire security = all colonies × `GetStructureSecurityBonus` (LINQ + list allocation) |
| `KnownColonyData.Refresh` (`KnownColonyData.cs:70`) | whenever a colony changes (`NavigationNode.ReportChange`) every observing civ recomputes the owner's `GetEmpireSecurity` |
| `Colony.Govern` → `EvaluateColonyNeeds` (`Colony.cs:5120`) | per auto-build colony: empire income/upkeep/fleet support recomputed |
| `MajorCivilization.UpdateNotifications` (`:2888`) | per auto-build colony: buildable list built |
| `Colony.RefreshGrid` → `CheckDeficit` | during client turn processing, empire income/upkeep recomputed per colony |
| `AchievementManager.OnEvent` (`:619`) | `Colonies.Max(...)` on each food/research change event (until the achievement is earned) |

## 2. UI: messages are never coalesced

- `MessageSink.Dispatch` is synchronous with no coalescing.
- `SetFoodDelta`/`RefreshGrid` send, per colony, `MessageRefreshEmpire` (→ `SWFEmpireTopBar.SendEmpireData(ALL)`: whole-empire
  income, upkeep, research ×3, tradeable resources, security) and `MessageRefreshPlanet(All)` (→ `SWFPlanet.Refresh()`: full screen rebuild, `GetAvailableBuildables` ×2).
- One citizen drag & drop = `Remove` + `Move` actions, each with 2 full screen refreshes + 3–4 top bar recomputes → measured 260–690 ms per action.

## 3. Empire Management — causes revealed step by step

Measuring and removing one cause at a time revealed the following, in order:

1. **C# list construction O(N²)**: per row `GetSecurityRating` (O(N)) and `Morale` ×2 (O(N)). 1.3–1.7 s.
2. **Flash `ASRoot.CheckTooltips`**: while the mouse is over the movie, every frame hit-tests every registered `TooltipArea` (9 per row).
   After the fix it measured 0.2 ms: **not the main cause**.
3. **Flash `ASRoot.CheckScrollableObjects`**: ~21 ms per Flash frame, and usually 3 Flash frames ran per Unity frame
   (when Unity frames are slow, Scaleform catches up on missed SWF frames).
4. **`Movie.DoHitTest` in the Scaleform wrapper** (`Scaleform/SFCamera.cs:183`, `SFManager.HandleMouseEvent`):
   - `SFCamera.OnGUI` calls `DoHitTest(x,y,HitTest_ShapesNoInvisible)` on the focused movie **on every Repaint**.
   - `SFManager.HandleMouseEvent` calls `DoHitTest` per movie **on every mouse down/up**.
   - For the Empire Management movie each call has a **fixed cost of ~200–300 ms**. The bounds test is just as slow, and the result was always true regardless of position (diagnostic 10/10).
   - **This was the real cause of the scroll lag and the Quick Buy delay.**
5. **`FillList` did `SetData` + `SetSelected` for every row**: per row it creates Buildable/ColonyEvent objects, loads images and starts 4 Actuate tweens → 260–860 ms on sort/select/open, 4–7 s on first open.
6. **Scrollbar drag broke when the mouse left the bar horizontally**: `fl.controls.ScrollBar.thumbPressHandler` listens on `focusManager.form`;
   in Flash MOUSE_MOVE only bubbles to ancestors of the object under the cursor, so moving over empty space stops the drag.

## 4. Turn processing (measured)

| Item (one server turn of 25.3 s) | Time |
|---|---|
| `KnownColonyData.Refresh` (4,400 calls) | 17.7 s |
| └ `MajorCivilization.GetEmpireSecurity` (4,287 calls) | 16.5 s |
| └ `Colony.GetStructureSecurityBonus` (1.4 M calls) | 13.3 s |
| (client) `MajorCivilization.CheckDeficit` (332 calls) | 3.1 s |

## 5. AI stage

- `Universe.PlotPath` (`Universe.cs:3330`) → `GalaxyMap.Prepare(civ, …)` → `AStar.PathFinder.FindPath` (`AStar/PathFinder.cs`).
- Each AI civ rates hundreds of known planets once per turn (`Awareness.GetPlanetRating`, and in `T_ColonizeWithinVicinity.HandleColonyShips`
  `GetKnownPlanets().Average(GetPlanetRating)`); every rating plots a path "closest own colony → planet".
- 2 turns without profiling code: 16,869 path searches = 36.2 s, of which **unreachable** 8,192 = 21.1 s.
  When the target is unreachable A* expands every reachable node and returns a partial path to the closest node — and the AI uses that value.
- Only ~11% of path queries repeat → caching alone helps little.
- Every node expansion runs `NavigationNode.GetNeighbors` (`NavigationNode.cs:874`): achievement lookup (`HasAchievement`), AI check,
  2× `HasJumpGate` per neighbor (LINQ `Any` over colony buildings), and an iterator allocation.

## 6. Tools and methods

- **Decompiling**: `dotnet tool install ilspycmd`, `ilspycmd -p -o <out> -r <Managed> Assembly-CSharp.dll`.
- **SWF**: JPEXS FFDec to export AS3/P-code (`-export script`, `-format script:pcode`) and SWF→XML (`-swf2xml`) for the ABC structure.
- **Profiling**: `tools/PerfPatcher` wraps methods with Stopwatch code → `perf_log.txt` (calls ≥15 ms + per-method totals every 20 s and after each turn/AI pass).
  Caution: wrapping very hot methods (e.g. `CanMoveAcross`, 9.35 M calls) inflates the measured time a lot → final conclusions came from builds without profiling code plus MooFix's own timers.
- **Inside Flash**: `ASRoot.OnEnterFrame` body replaced with `getTimer()` measurements sent to C# via `ExternalInterface.call("PlaySound","MOOPERF:…")`.
- **Scaleform**: firstpass `Movie.Advance/DoHitTest/HandleMouseEvent` measured per movie (`MooPerfLog.EndObj`).

---

## 한국어 (Korean)

### 01. 분석: 무엇이 왜 느렸나

코드 위치는 ILSpy로 디컴파일한 소스 기준입니다(`_local_only/decompiled/Assembly-CSharp/...`).
줄 번호는 UCP 6.4b `Assembly-CSharp.dll`(sha256 `915c5eac…`)을 ilspycmd 11.2로 디컴파일한 결과 기준입니다.

#### 0. 게임 구조

- Unity 5.2.1p3, Mono(.NET 3.5 프로필), DirectX 9. 게임 로직은 `MasterOfOrion_Data/Managed/Assembly-CSharp.dll`.
- UI는 Scaleform(Flash/AS3). SWF 파일은 `MasterOfOrion_Data/StreamingAssets/*.swf`, C# 래퍼는 `Assembly-CSharp-firstpass.dll`(`Scaleform.*`).
- **싱글플레이도 서버/클라이언트 구조**: 서버 스레드(`ServerThread.ServerLoop`, 33ms 주기)가 턴을 계산하고,
  클라이언트(메인 스레드)가 `Client.AdvanceSimulation`으로 **같은 턴을 다시 계산**한 뒤 체크섬을 비교합니다.
  불일치 시 `OutOfSync`가 되어 클라이언트 시뮬레이션이 멈춥니다 → 시뮬레이션 수정은 반드시 결과가 같아야 함.
- AI는 서버 스레드에서 `Universe.RunAI` → `AIPlayer.Play` → `Planner.RunTasks`(T_* 태스크). 명령은 `AIPlayerBase.Send` → `Server.RunAndBroadcast` → `Universe.DispatchAction`.
- 매 턴 타임라인 스냅샷을 `Documents/Master of Orion/Saves/default.data`에 추가(설정 `timeline_save_mode_1`).

#### 1. 정적 분석으로 찾은 O(N²) 패턴 (N = 식민지 수)

| 위치 | 내용 |
|---|---|
| `Colony.OnTurn` → `Awareness.CalculateColonyMature` (`AI/Awareness.cs:544`) | 매 턴 모든 문명의 모든 식민지가 `GetAvailableStructures`를 최대 3번 생성. 제국 유일 건물마다 `HasBuiltStructure`/`HasBuiltUpgrade`/`HasBuildableInQueue`(`Civilization.cs:4354,4367,4413`)가 전 식민지를 훑음 |
| `Colony.Morale` → `GetStructuresMoraleBoost` (`Colony.cs:376`) | 같은 항성계 식민지를 찾으려고 `Owner.Colonies` 전체를 필터링 → Morale 1회 = O(N) |
| `Colony.GetSecurityRating` → `MajorCivilization.GetEmpireSecurity` → `GetAverageColonialSecurity` (`MajorCivilization.cs:2076`) | 제국 보안 등급이 전 식민지 × `GetStructureSecurityBonus`(LINQ + 리스트 할당) |
| `KnownColonyData.Refresh` (`KnownColonyData.cs:70`) | 식민지 하나가 바뀔 때마다(`NavigationNode.ReportChange`) 관찰하는 모든 문명이 그 식민지 주인의 `GetEmpireSecurity`를 재계산 |
| `Colony.Govern` → `EvaluateColonyNeeds` (`Colony.cs:5120`) | 자동 건설 식민지마다 제국 전체 수입/유지비/함대지원 재계산 |
| `MajorCivilization.UpdateNotifications` (`:2888`) | 자동 건설 식민지마다 건설 가능 목록 생성 |
| `Colony.RefreshGrid` → `CheckDeficit` | 클라이언트 턴 처리 중 식민지마다 제국 수입/유지비 재계산 |
| `AchievementManager.OnEvent` (`:619`) | 식량/연구 변화 이벤트마다 `Colonies.Max(...)` (업적 미달성 시) |

#### 2. UI: 메시지가 병합되지 않음

- `MessageSink.Dispatch`는 동기식이며 병합이 없습니다.
- `SetFoodDelta`/`RefreshGrid`가 식민지마다 `MessageRefreshEmpire`(→ `SWFEmpireTopBar.SendEmpireData(ALL)`: 제국 전체 수입·유지비·연구(×3)·교역자원·보안 재계산)와
  `MessageRefreshPlanet(All)`(→ `SWFPlanet.Refresh()`: 화면 전체 재구성, `GetAvailableBuildables` 2회)를 보냅니다.
- 인구 드래그&드롭 1회 = `Remove`+`Move` 액션 2개, 액션마다 화면 전체 갱신 2회 + 상단 바 재계산 3~4회 → 측정 260~690ms/액션.

#### 3. 제국 관리 화면 — 단계적으로 드러난 원인

1. **C# 목록 생성 O(N²)**: 행마다 `GetSecurityRating`(O(N)), `Morale`×2(O(N)). 1.3~1.7초.
2. **Flash `ASRoot.CheckTooltips`**: 마우스가 화면 위에 있으면 매 프레임 모든 `TooltipArea`(행당 9개)에 `hitTestPoint`. 수정 후 0.2ms: **주원인이 아니었음**.
3. **Flash `ASRoot.CheckScrollableObjects`**: Flash 프레임당 약 21ms, Unity 프레임당 Flash 프레임이 보통 3번 실행.
4. **Scaleform 래퍼의 `Movie.DoHitTest`** (`Scaleform/SFCamera.cs:183`, `SFManager.HandleMouseEvent`):
   매 Repaint와 클릭(누름/뗌)마다 호출되며, 제국 관리 무비는 **호출 1회당 약 200~300ms의 고정 비용**. Bounds 방식도 똑같이 느리고 결과는 항상 true(진단 10/10).
   **스크롤 렉과 Quick Buy 지연의 진짜 원인**.
5. **`FillList`가 모든 행에 `SetData` + `SetSelected`**: 행마다 객체 생성, 이미지 로딩, 트윈 4개 → 정렬·선택·진입 시 260~860ms, 첫 진입 4~7초.
6. **스크롤바 드래그가 X 좌표를 벗어나면 끊김**: `fl.controls.ScrollBar.thumbPressHandler`가 이벤트를 `focusManager.form`에서 받음.

#### 4. 턴 처리 (측정 기반)

| 항목 (서버 턴 1회 25.3초 중) | 시간 |
|---|---|
| `KnownColonyData.Refresh` (4,400회) | 17.7초 |
| └ `MajorCivilization.GetEmpireSecurity` (4,287회) | 16.5초 |
| └ `Colony.GetStructureSecurityBonus` (140만 회) | 13.3초 |
| (클라이언트) `MajorCivilization.CheckDeficit` (332회) | 3.1초 |

#### 5. AI 단계

- `Universe.PlotPath`(`Universe.cs:3330`) → `GalaxyMap.Prepare(civ,…)` → `AStar.PathFinder.FindPath`.
- AI는 문명마다 턴당 한 번 아는 행성 수백 개를 평가하고, 평가마다 "가장 가까운 자기 식민지 → 행성" 경로 탐색.
- 2턴 측정(측정 코드 없음): 경로 탐색 16,869회 36.2초, 그중 **도달 불가** 8,192회 21.1초. 도달 불가 시 도달 가능한 노드를 전부 탐색한 뒤 부분 경로를 돌려주고, AI가 그 값을 평가에 씀.
- 같은 경로를 반복 질의하는 비율은 약 11%뿐.
- 노드 확장마다 `NavigationNode.GetNeighbors`(`NavigationNode.cs:874`)가 업적 조회, AI 여부, 이웃마다 `HasJumpGate` 2회(건물 LINQ), 이터레이터 할당을 반복.

#### 6. 분석 도구와 방법

- **디컴파일**: `ilspycmd -p -o <out> -r <Managed> Assembly-CSharp.dll`.
- **SWF**: JPEXS FFDec로 AS3/P-code 추출, SWF→XML로 ABC 구조 분석.
- **측정**: `tools/PerfPatcher`(15ms 이상 호출 + 메서드별 누적 통계). 초고빈도 메서드 래핑은 측정을 크게 왜곡 → 최종 판단은 측정 코드 없는 빌드 + MooFix 내부 타이머로.
- **Flash 내부 측정**: `ASRoot.OnEnterFrame`에 `getTimer()` + `ExternalInterface.call("PlaySound","MOOPERF:…")`.
- **Scaleform 측정**: firstpass의 `Movie.Advance/DoHitTest/HandleMouseEvent`를 무비별로 측정.
