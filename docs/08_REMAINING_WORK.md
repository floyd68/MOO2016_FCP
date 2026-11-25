# 08. Remaining optimization candidates

State after the second round (see [03](03_MEASUREMENTS.md)): loading ~11.5 s, screen freeze per turn ~7.4 s.

| Item | Current cost | Approach | Risk |
|---|---|---|---|
| Client turn re-simulation (screen freeze) | ~6 s per turn (main thread) | only shorter simulation helps: the client universe is read by the galaxy view every frame, so it cannot run in the background. Overlapping it with the server simulation is already available (experimental, [02 H](02_FIXES.md#h-overlapped-turn-simulation-experimental-moofix_overlaptxt)) | — |
| Simulation: `KnownColonyData.Refresh` / `NavigationNode.ReportChange` | ~1.2–1.6 s per side (profiling build) | further per-colony values reused inside cache scopes (credit output, upkeep, security rating) | low–medium |
| Simulation: `MajorCivilization.GetEmpireSecurity` outside cache scopes | ~0.4–0.6 s per side | needs invalidation when structures are built during the turn | medium (changes results if wrong) |
| Espionage "reveal colonies" (`UpdateOverallVisibility`) | was 5.3 s per mission; now much less with the scan filter, not re-measured | re-measure on a turn with successful missions | — |
| Loading: scan loop | ~3 s (profiling build) of which the detection work itself is ~half | reuse the per-civilization node list across scanners (discovery may change during a scan) | medium |
| Loading: client and server universes are initialized one after the other | ~2–3 s possible | the server universe is cloned from the finished client universe; would need a different load order and shares `ObjectBase.bSerializingGame` | high |
| First opening of the Empire Management screen after starting the game | 4–7 s (later opens ~1 s) | virtualize the Flash list: create only the row widgets needed on screen and reuse them | medium–high |
| Opening screens (Flash movie load) | ~2.2 s each first time | Scaleform movie loading; not analyzed | — |
| AI: `T_Engage.BoostPriorityOfBlockingSettlements`, `SP_GeneralStrategy.RefreshTargetLocalWars`, `Awareness.CalculateStarCentrality` | 2–6 s per turn (profiling build) | re-measure without profiling code | not analyzed |
| Auto-build `EvaluateColonyNeeds` | auto-build colonies × O(N) | no exact cache possible (results of earlier colonies must be reflected) | changes results |
| Timeline save | ~0.45 s per turn after fast compression | moving it to a background thread risks losing the turn's save when quitting right after a turn | data loss |

Recommended way to measure: the `FRAME stall` lines of the regular build for what the player sees, a profiling build
(`build.cmd perf`) for the breakdown, MooFix timers for exact numbers (profiling distorts very hot methods).

---

## 한국어 (Korean)

### 08. 남은 최적화 후보

2차 작업 후 상태([03](03_MEASUREMENTS.md)): 로딩 약 11.5초, 턴당 화면 멈춤 약 7.4초.

| 항목 | 현재 비용 | 방법 | 위험 |
|---|---|---|---|
| 클라이언트 턴 재시뮬레이션(화면 멈춤) | 턴당 약 6초(메인 스레드) | 시뮬레이션을 짧게 하는 것만 효과. 클라이언트 universe를 은하 화면이 매 프레임 읽으므로 백그라운드 불가. 서버 시뮬레이션과 겹치기는 이미 제공(실험, [02 H](02_FIXES.md)) | — |
| 시뮬레이션: `KnownColonyData.Refresh` / `ReportChange` | 양쪽 각 약 1.2~1.6초(측정 빌드) | 캐시 범위 안에서 식민지별 값(수입, 유지비, 보안 등급) 추가 재사용 | 낮음~중간 |
| 시뮬레이션: 캐시 범위 밖 `GetEmpireSecurity` | 양쪽 각 약 0.4~0.6초 | 턴 중 건물 완성 시 무효화 필요 | 중간(틀리면 결과 변경) |
| 첩보 "식민지 공개"(`UpdateOverallVisibility`) | 건당 5.3초였음. 스캔 걸러내기로 크게 줄었을 것, 재측정 필요 | 첩보 성공 턴에서 재측정 | — |
| 로딩: 스캔 순회 | 약 3초(측정 빌드), 그중 절반은 실제 탐지 처리 | 문명별 노드 목록을 스캐너 간 재사용(스캔 중 발견 상태가 바뀔 수 있음) | 중간 |
| 로딩: 클라이언트·서버 universe 순차 초기화 | 2~3초 가능 | 서버 universe는 완성된 클라이언트를 복제해 만듦 → 로딩 순서 변경 필요, `ObjectBase.bSerializingGame` 공유 | 높음 |
| 게임 시작 후 첫 제국 관리 진입 | 4~7초(이후 약 1초) | Flash 목록 가상화 | 중~높음 |
| 화면 처음 열기(Flash 무비 불러오기) | 화면마다 약 2.2초 | Scaleform 무비 로딩, 미분석 | — |
| AI: `BoostPriorityOfBlockingSettlements`, `RefreshTargetLocalWars`, `CalculateStarCentrality` | 턴당 2~6초(측정 빌드) | 측정 코드 없이 재측정 | 미분석 |
| 자동 건설 `EvaluateColonyNeeds` | 자동 건설 식민지 수 × O(N) | 정확한 캐시 불가 | 결과 변경 |
| 타임라인 저장 | 빠른 압축 후 턴당 약 0.45초 | 백그라운드로 옮기면 턴 직후 종료 시 그 턴의 저장이 사라질 위험 | 데이터 손실 |

측정 권장: 플레이어가 겪는 멈춤은 일반 빌드의 `FRAME stall` 로그로, 내부 구성은 측정용 빌드(`build.cmd perf`)로, 정확한 수치는 MooFix 타이머로(측정 코드는 아주 자주 불리는 함수를 왜곡함).
