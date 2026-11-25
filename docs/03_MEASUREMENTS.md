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
