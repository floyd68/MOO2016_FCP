# 03. Measurements

Environment: Windows 11, 3840x2160, Massive galaxy, very high planet density, saves at turns 225–227, ~345 player colonies, 12 mods (incl. UCP).
Raw logs: `_local_only/logs/` (`perf_log_*.txt` = PerfPatcher profiling, `moofix_log_*.txt` = MooFix logs).
Numbers from profiling builds include the profiling overhead (especially AI path finding).

## Turn processing

| Stage | Server `Universe.AdvanceSimulation` | Client `Client.AdvanceSimulation` |
|---|---|---|
| Before (run3) | 19.3 s / 25.3 s (wider profiling) | 17.7–17.9 s |
| + security caches / `GetStructureSecurityBonus` (run5) | 14.0 s | 17.7 s |
| + `CheckDeficit` skip (run8) | **8.4 s** | **9.2 s** |

One turn before the fix: `KnownColonyData.Refresh` 17.7 s → `GetEmpireSecurity` 16.5 s → `GetStructureSecurityBonus` 1.4 M calls 13.3 s.

## AI stage (build without profiling code, MooFix timers, 2 turns)

| | Path searches computed | of which unreachable | `RunAI` total | excluding turn simulation |
|---|---|---|---|---|
| cache only (ai2) | 16,869 / 36.2 s | 8,192 / 21.1 s | 70.1 s | ~49.2 s |
| + FastPlotPath (ai3, incl. verification mode) | 16,869 / **8.6 s** | 8,192 / **5.3 s** | 44.5 s | **~22.1 s** |

Cache hit rate ~11% (few repeated queries). 1,059 verified comparisons, 0 mismatches.

## Planet screen citizen drag & drop

| | One action | Note |
|---|---|---|
| Before | 260–690 ms (Remove and Move each) | 2 full screen refreshes + 3–4 top bar recomputes |
| After | <15 ms + one batched refresh per frame 115–280 ms (verification mode on) | — |

## Empire Management screen

| Item | Before | Final |
|---|---|---|
| `LoadColoniesData` (C#) | 1,300–1,700 ms | ~100 ms |
| `UpdateSpecificColony` | up to 234 ms | 5 ms |
| Frame (mouse over the list) | 300–400 ms | `Movie.Advance@SWFEmpireManagement` ~6 ms on average |
| `CheckTooltips` / `CheckScrollableObjects` (per Flash frame) | — / 21 ms | 0.2 ms / 0 |
| `Movie.DoHitTest@SWFEmpireManagement` | 200–300 ms every frame | first 3 calls of the session |
| Click `SFManager.HandleMouseEvent` | ~250 ms per down and per up | 8–10 ms on average |
| First-open Flash `Advance` | 4.2–7.2 s | (unsolved: widget construction) |
| After a long scrollbar drag | up to 12.9 s freeze (intermediate-version regression) | fixed by a per-frame budget |

## Other observations

- Timeline: `default.data` 320 MB, +~2 MB per turn, `TimelineManager.PushTurn` ~1.2 s per turn.
- Bottom bar `RefreshNotifications` ~220 ms per call (mostly Flash), territory rebuild `BuildTerritoryAndDispatchChanges` ~2.8 s per turn.
- Loading a save: the `ScanNavigationNodesInRange` path also had the O(N²) `GetEmpireSecurity` (15 s during loading) → fixed.

## Screen freezes, loading and turn processing (second round)

Measured with the `FRAME stall` monitor (gaps ≥ 250 ms between frames) in the regular build unless noted; same save (turn 227 → 228).

| Item | Before | After | Fix |
|---|---|---|---|
| Freeze after a battle / an event (turn results) | 24–33 s, 17 s | ~0 (714 refreshes → 1, 12 ms) | merged notification refreshes |
| AI stage frames | 0.55–0.7 s per frame for several seconds | one ~0.55 s rebuild per burst | debounced radar outline |
| Blueprint screen "confirm" (obsolete blueprints) | several seconds | not measurable any more (< 2.7 s screen-load stalls only) | availability once per colony |
| Tech tree opening | 5.0 s (238 research recomputations) | screen load only (~2.2 s) | cache scope |
| Planet refresh after a citizen drag | 92–370 ms (profiling build) | ~63 ms | partial refresh + tradeable cache |
| **Loading the save** (one frame) | 17.1–17.3 s | **10.6–11.1 s** (11.5 s with verification mode) | scan filter, star separation A*, growth ETA cache |
| `Universe.PostInit` (client + server) | 4.7 s | 0.93 s (profiling build) | star separation matrix |
| **Screen freeze per turn** (simulation + new turn) | 10.1–10.3 s | **7.15 s** (7.4 s with verification mode) | below |
| Client simulation | 8.4 s | 5.96 s | merged deficit checks, territory memo, growth ETA |
| Server simulation | 7.1 s | 5.93 s | territory memo, growth ETA |
| Timeline turn data | serialization 0.44–0.6 s + LZ4 HC 0.47 s, 2.9 MB per turn | LZ4 fast 56–63 ms, 3.9 MB per turn; saves written this way load normally | fast compression |

Overlapped turn simulation (experimental): same turn sequential 6.9 s + 7.6 s, overlapped 6.7 s ‖ 8.1 s → freeze ~8 s instead of ~15 s.

## Third round (AI caches, allocations, A\* heap)

Same save (turn 227 → 228), regular build, overlap on, verification off, `FRAME stall` and MooFix timers.

| Item | After the second round | After the third round | Main fixes |
|---|---|---|---|
| **Loading the save** (one frame) | 10.6–11.1 s | **9.1–9.2 s** | system-level scan skip, allocation-free tradeable resources, array heap |
| Star separation matrix (per universe) | 0.55 s | **0.14 s** | array heap |
| **Screen freeze per turn** | 7.15 s | **4.7–5.3 s** | below |
| Client / server simulation | 5.96 s / 5.93 s | **3.5–3.6 s / 3.5–3.6 s** | security generation cache, settlement value memo, allocation-free replacements |
| Garbage collections in the turn frame | 8 (~150 ms each) | **3** | allocation-free replacements (~600 → ~150 MB per side, profiling build) |
| Empire security recomputed during colony processing | ~1,040 per side | 52 | generation cache |
| AI stage, 8 AI empires (excluding the simulation) | 7.75 s (with the planet rating cache) | **6.47 s** | closest-location and pact memos, array heap |

Verification-mode runs (same turn): 0 mismatches for paths (509), star pairs (3,844), planet ratings, empire security,
settlement values and the allocation-free replacements; checksums matched in every run.

Empire Management first opening, measured inside Flash: building the 356 rows takes 1.55–1.6 s (row widgets 0.83–0.86 s,
dropdown data 0.21 s) of a 4.6–5.5 s frame; the rest is loading the movie. Not changed (see [08](08_REMAINING_WORK.md)).


## Fourth round (first opening of screens)

Same save, regular build. First opening of each screen after loading (`FRAME stall`, `MOVIE`, `SWFCALL`, `STAGE` lines).

| Screen | Before | After | Fix |
|---|---|---|---|
| Empire Management | 4.7–6.9 s (list fill 1.66 s + first frame of 356 row widgets ~3 s) | **< 1 s** (list fill 0.09 s, 15 widgets) | list virtualization |
| Espionage | ~2.2 s (outside C#) | no stall | shader warm-up |
| Races / diplomacy | 1.3–2.5 s (stage switch + rendering) | no stall | shader warm-up |
| Known Planets | 1.35 s ETA computation + ~0.4 s | ETA 5 ms | Dijkstra |
| Loading | ~9.1 s | ~14.3 s (+5.2 s warm-up) | `moofix_warmup_off.txt` restores the old loading time |
| Tech tree, blueprints (3D stage switch) | 0.06–1.8 s, varies | unchanged | Unity object activation |

SWF read and native movie creation: 1–6 ms per screen (main menu 0.65 s), i.e. not the cause.

---

## 한국어 (Korean)

### 03. 측정 기록

환경: Windows 11, 3840x2160, Massive 은하·매우 높은 행성 밀도, 225~227턴 세이브, 플레이어 식민지 약 345개, 모드 12개(UCP 포함).
원본 로그: `_local_only/logs/` (`perf_log_*.txt` = PerfPatcher 측정, `moofix_log_*.txt` = MooFix 로그).
측정 코드가 들어간 빌드의 수치는 측정 부담을 포함합니다(특히 AI 경로 탐색).

#### 턴 처리

| 단계 | 서버 `Universe.AdvanceSimulation` | 클라이언트 `Client.AdvanceSimulation` |
|---|---|---|
| 패치 전 (run3) | 19.3초 / 25.3초(측정 확장 후) | 17.7~17.9초 |
| + 보안 캐시/`GetStructureSecurityBonus` (run5) | 14.0초 | 17.7초 |
| + `CheckDeficit` 생략 (run8) | **8.4초** | **9.2초** |

#### AI 단계 (측정 코드 없는 빌드, MooFix 내부 타이머, 2턴 합계)

| | 경로 탐색 계산 | 그중 도달 불가 | `RunAI` 합계 | 턴 시뮬레이션 제외 |
|---|---|---|---|---|
| 캐시만 (ai2) | 16,869회 / 36.2초 | 8,192회 / 21.1초 | 70.1초 | 약 49.2초 |
| + FastPlotPath (ai3, 검증 모드 포함) | 16,869회 / **8.6초** | 8,192회 / **5.3초** | 44.5초 | **약 22.1초** |

캐시 적중률 약 11%. 검증 1,059건 불일치 0.

#### 행성 화면 인구 드래그&드롭
패치 전 액션당 260~690ms → 패치 후 15ms 미만 + 프레임당 일괄 갱신 115~280ms(검증 모드).

#### 제국 관리 화면

| 항목 | 패치 전 | 최종 |
|---|---|---|
| `LoadColoniesData` (C#) | 1,300~1,700ms | 약 100ms |
| `UpdateSpecificColony` | 최대 234ms | 5ms |
| 프레임 (마우스가 목록 위) | 300~400ms | 평균 약 6ms |
| `CheckTooltips` / `CheckScrollableObjects` | — / 21ms | 0.2ms / 0 |
| `DoHitTest@SWFEmpireManagement` | 매 프레임 200~300ms | 세션 처음 3회 |
| 클릭 처리 | 누름·뗌 각각 약 250ms | 평균 8~10ms |
| 첫 진입 Flash `Advance` | 4.2~7.2초 | (미해결) |
| 긴 스크롤 드래그 후 | 최대 12.9초 멈춤(중간 버전 회귀) | 프레임당 처리량 제한으로 해소 |

#### 기타 관찰
- 타임라인 `default.data` 320MB, 턴당 약 2MB 증가, `PushTurn` 약 1.2초/턴.
- 하단 바 `RefreshNotifications` 1회 약 220ms, 영역 재계산 약 2.8초/턴.
- 세이브 로딩 중 `ScanNavigationNodesInRange` 경로의 O(N²)(15초) → 수정됨.

#### 화면 멈춤, 로딩, 턴 처리 (2차)

`FRAME stall` 기록(프레임 간격 250ms 이상) 기준, 따로 표시하지 않으면 일반 빌드. 같은 세이브(227 → 228턴).

| 항목 | 이전 | 이후 | 수정 |
|---|---|---|---|
| 전투·이벤트 후 멈춤(턴 결과 연출) | 24~33초, 17초 | 거의 0 (714회 → 1회, 12ms) | 알림 갱신 합치기 |
| AI 단계 프레임 | 프레임당 0.55~0.7초가 수 초간 | 묶음당 약 0.55초 1회 | 레이더 다시 그리기 미루기 |
| 블루프린트 확인(구식 블루프린트) | 수 초 | 화면 불러오기 멈춤(2.7초 미만)만 남음 | 식민지당 건설 가능 목록 1회 |
| 기술 트리 열기 | 5.0초(연구량 238회 재계산) | 화면 불러오기만(약 2.2초) | 캐시 범위 |
| 시민 드래그 후 행성 화면 갱신 | 92~370ms(측정 빌드) | 약 63ms | 부분 갱신 + 교역 자원 캐시 |
| **세이브 로딩**(한 프레임) | 17.1~17.3초 | **10.6~11.1초**(검증 모드 켜면 11.5초) | 스캔 걸러내기, 별 거리 A*, 성장 예상 턴 캐시 |
| `Universe.PostInit`(클라이언트+서버) | 4.7초 | 0.93초(측정 빌드) | 별 거리 행렬 |
| **턴당 화면 멈춤**(시뮬레이션 + 새 턴) | 10.1~10.3초 | **7.15초**(검증 모드 켜면 7.4초) | 아래 |
| 클라이언트 시뮬레이션 | 8.4초 | 5.96초 | 적자 검사 합치기, 영토 계산 재사용, 성장 예상 턴 |
| 서버 시뮬레이션 | 7.1초 | 5.93초 | 영토 계산 재사용, 성장 예상 턴 |
| 타임라인 턴 데이터 | 직렬화 0.44~0.6초 + LZ4 HC 0.47초, 턴당 2.9MB | LZ4 빠른 압축 56~63ms, 턴당 3.9MB. 이렇게 저장한 세이브도 정상 로딩 | 빠른 압축 |

턴 시뮬레이션 겹치기(실험): 같은 턴 순차 6.9초 + 7.6초, 겹치면 6.7초 ‖ 8.1초 → 멈춤 약 15초 → 약 8초.

#### 3차 (AI 캐시, 할당, A\* 힙)

같은 세이브(227 → 228턴), 일반 빌드, 겹치기 켬, 검증 모드 끔. `FRAME stall`과 MooFix 타이머 기준.

| 항목 | 2차 후 | 3차 후 | 주요 수정 |
|---|---|---|---|
| **세이브 로딩**(한 프레임) | 10.6~11.1초 | **9.1~9.2초** | 항성계 단위 스캔 건너뛰기, 교역 자원 할당 제거, 배열 힙 |
| 별 거리 행렬(universe당) | 0.55초 | **0.14초** | 배열 힙 |
| **턴당 화면 멈춤** | 7.15초 | **4.7~5.3초** | 아래 |
| 클라이언트 / 서버 시뮬레이션 | 5.96초 / 5.93초 | **3.5~3.6초 / 3.5~3.6초** | 보안 세대 캐시, 정착지 값 메모, 할당 없는 대체 구현 |
| 턴 프레임 안의 GC | 8회(회당 약 150ms) | **3회** | 할당 없는 대체 구현(한쪽 약 600 → 약 150MB, 측정 빌드) |
| 식민지 처리 중 제국 보안 재계산 | 한쪽 약 1,040회 | 52회 | 세대 캐시 |
| AI 단계, AI 제국 8개(시뮬레이션 제외) | 7.75초(행성 평가 캐시 적용 후) | **6.47초** | 가장 가까운 위치·협정 메모, 배열 힙 |

검증 모드 실행(같은 턴): 경로(509건), 별 쌍(3,844건), 행성 평가, 제국 보안, 정착지 값, 할당 없는 대체 구현 모두 불일치 0. 모든 실행에서 체크섬 일치.

제국 관리 첫 진입을 Flash 안에서 측정: 4.6~5.5초 프레임 중 356행 생성이 1.55~1.6초(행 위젯 0.83~0.86초, 드롭다운 데이터 0.21초), 나머지는 무비 불러오기. 변경하지 않음([08](08_REMAINING_WORK.md) 참고).

#### 4차 (화면 첫 전환)

같은 세이브, 일반 빌드. 로딩 후 각 화면을 처음 열 때(`FRAME stall`, `MOVIE`, `SWFCALL`, `STAGE` 줄 기준).

| 화면 | 이전 | 이후 | 수정 |
|---|---|---|---|
| 제국 관리 | 4.7~6.9초(목록 채우기 1.66초 + 행 위젯 356개 첫 프레임 약 3초) | **1초 미만**(목록 채우기 0.09초, 위젯 15개) | 목록 가상화 |
| 첩보 | 약 2.2초(C# 밖) | 멈춤 없음 | 셰이더 미리 컴파일 |
| 종족/외교 | 1.3~2.5초(스테이지 전환 + 렌더링) | 멈춤 없음 | 셰이더 미리 컴파일 |
| 알려진 행성 | 도착 시간 계산 1.35초 + 약 0.4초 | 도착 시간 5ms | 다익스트라 |
| 로딩 | 약 9.1초 | 약 14.3초(+5.2초 미리 컴파일) | `moofix_warmup_off.txt`로 이전 로딩 시간 |
| 기술 트리, 블루프린트(3D 스테이지 전환) | 0.06~1.8초, 매번 다름 | 변화 없음 | Unity 오브젝트 활성화 |

SWF 읽기와 네이티브 무비 생성은 화면당 1~6ms(메인 메뉴만 0.65초)로 원인이 아님.
