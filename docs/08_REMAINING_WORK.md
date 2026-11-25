# 08. Remaining optimization candidates

State after the third round (see [03](03_MEASUREMENTS.md)): loading ~9.1 s, screen freeze per turn ~4.7–5.3 s, AI stage ~6.5 s.

| Item | Current cost | Approach | Risk |
|---|---|---|---|
| Client turn re-simulation (screen freeze) | ~3.6 s per turn (main thread) | only shorter simulation helps: the client universe is read by the galaxy view every frame, so it cannot run in the background. Overlapping it with the server simulation is already available (experimental, [02 H](02_FIXES.md#h-overlapped-turn-simulation-experimental-moofix_overlaptxt)) | — |
| Scan after the turn / loading scan | ~0.7–0.8 s per turn, ~4.2 s while loading | most of it is walking interstellar end points; a per-star end point list would need invalidation at every connection change (wormholes, Antaran portals, anomalies, warp gates, node deletion, loading) | medium |
| Timeline turn data | serialization ~0.65 s + LZ4 ~0.06 s per turn | the game's own serializer; moving it to a background thread risks losing the turn's save when quitting right after a turn | data loss |
| AI path finding | ~1.5 s per turn for all AIs, mostly unreachable targets | a failed search returns a partial path that the AI uses for ETAs, so it cannot be short-circuited; only the per-search cost can shrink further | — |
| AI stage, other | ~5 s per turn | profile again (`build.cmd perf`) | not analyzed |
| Loading: client and server universes are initialized one after the other | ~2–3 s possible | the server universe is cloned from the finished client universe; would need a different load order and shares `ObjectBase.bSerializingGame` | high |
| Loading: garbage collection | ~25 collections while loading | most remaining garbage is deserialization (reading the save) | — |
| First opening of the Empire Management screen after starting the game | 4.6–5.5 s frame, of which 1.6 s builds the 356 rows | virtualize the Flash list (sorting, row expansion, scroll height and buy buttons are all tied to it) | high for ≤1 s |
| Opening screens (Flash movie load) | ~2.2 s each first time | Scaleform movie loading; not analyzed | — |
| Auto-build `EvaluateColonyNeeds` | auto-build colonies × O(N) | no exact cache possible (results of earlier colonies must be reflected) | changes results |

Not possible: upgrading the .NET runtime (Mono is embedded in the Unity 5.2 player) and tuning the Boehm GC (no settings exposed).

Recommended way to measure: the `FRAME stall` lines of the regular build for what the player sees (they include the
garbage collection count), a profiling build (`build.cmd perf`) for the breakdown and per-method allocations, MooFix timers
for exact numbers (profiling distorts very hot methods). Measure allocations with the overlap off.

---

## 한국어 (Korean)

### 08. 남은 최적화 후보

3차 작업 후 상태([03](03_MEASUREMENTS.md)): 로딩 약 9.1초, 턴당 화면 멈춤 약 4.7~5.3초, AI 단계 약 6.5초.

| 항목 | 현재 비용 | 방법 | 위험 |
|---|---|---|---|
| 클라이언트 턴 재시뮬레이션(화면 멈춤) | 턴당 약 3.6초(메인 스레드) | 시뮬레이션을 짧게 하는 것만 효과. 클라이언트 universe를 은하 화면이 매 프레임 읽으므로 백그라운드 불가. 서버 시뮬레이션과 겹치기는 이미 제공(실험, [02 H](02_FIXES.md)) | — |
| 턴 후 스캔 / 로딩 스캔 | 턴당 약 0.7~0.8초, 로딩 중 약 4.2초 | 대부분 성간 연결점 순회. 별마다 연결점 목록을 두려면 연결이 바뀔 때마다 무효화 필요(웜홀, 안타란 포털, 이상현상, 워프게이트, 노드 삭제, 로딩) | 중간 |
| 타임라인 턴 데이터 | 턴당 직렬화 약 0.65초 + LZ4 약 0.06초 | 게임 자체 직렬화. 백그라운드로 옮기면 턴 직후 종료 시 그 턴의 저장이 사라질 위험 | 데이터 손실 |
| AI 경로 탐색 | 모든 AI 합쳐 턴당 약 1.5초, 대부분 도달 불가 목표 | 실패한 탐색도 AI가 도착 시간 계산에 쓰는 부분 경로를 돌려주므로 건너뛸 수 없음. 탐색 한 번의 비용만 더 줄일 수 있음 | — |
| AI 단계 나머지 | 턴당 약 5초 | 다시 측정(`build.cmd perf`) | 미분석 |
| 로딩: 클라이언트·서버 universe 순차 초기화 | 2~3초 가능 | 서버 universe는 완성된 클라이언트를 복제해 만듦 → 로딩 순서 변경 필요, `ObjectBase.bSerializingGame` 공유 | 높음 |
| 로딩: GC | 로딩 중 약 25회 | 남은 쓰레기는 대부분 세이브 읽기(역직렬화) | — |
| 게임 시작 후 첫 제국 관리 진입 | 4.6~5.5초 프레임, 그중 356행 생성 1.6초 | Flash 목록 가상화(정렬, 행 펼침, 스크롤 높이, 구매 버튼이 모두 얽힘) | 1초 이하 이득에 비해 높음 |
| 화면 처음 열기(Flash 무비 불러오기) | 화면마다 약 2.2초 | Scaleform 무비 로딩, 미분석 | — |
| 자동 건설 `EvaluateColonyNeeds` | 자동 건설 식민지 수 × O(N) | 정확한 캐시 불가 | 결과 변경 |

불가능: .NET 런타임 업그레이드(Mono가 Unity 5.2 플레이어에 내장), Boehm GC 조정(설정이 노출되지 않음).

측정 권장: 플레이어가 겪는 멈춤은 일반 빌드의 `FRAME stall` 로그로(GC 횟수 포함), 내부 구성과 메서드별 할당량은 측정용 빌드(`build.cmd perf`)로, 정확한 수치는 MooFix 타이머로(측정 코드는 아주 자주 불리는 함수를 왜곡함). 할당량은 겹치기를 끄고 측정.
