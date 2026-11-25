# 08. Remaining optimization candidates

| Item | Current cost | Approach | Risk |
|---|---|---|---|
| First opening of the Empire Management screen after starting the game | 4–7 s (later opens ~1 s) | virtualize the Flash list: create only the row widgets needed on screen and reuse them. Sorting, selection (row expands by 62 px), scroll height and buy-button updates are intertwined | medium–high |
| AI: `T_Engage.BoostPriorityOfBlockingSettlements` | ~6 s per turn (measured with profiling code) | re-measure after the path-finding fix | not analyzed |
| AI: `SP_GeneralStrategy.RefreshTargetLocalWars` | ~3.7 s per turn (with profiling) | re-measure | not analyzed |
| AI: `Awareness.CalculateStarCentrality` | ~2.2 s per turn (with profiling) | re-measure | not analyzed |
| Client turn re-simulation | ~9 s | skip the client's duplicate simulation | high (sync design) |
| `Universe.BuildTerritoryAndDispatchChanges` | ~2.8 s per turn | analyze (`NavigationNode.GetConnectedStars`, 262 k calls) | not analyzed |
| Bottom bar `RefreshNotifications` | ~220 ms per call (Flash) | analyze the notification list SWF | not analyzed |
| Auto-build `EvaluateColonyNeeds` | auto-build colonies × O(N) | no exact cache possible (results of earlier colonies must be reflected) | changes results |
| Per-turn timeline save | ~1.2 s per turn | lower the save frequency in new-game settings (no code change) | none |

Recommended way to measure next: instead of `build.cmd perf`, add timers for the target section to MooFix in a build without profiling code (profiling distortion was confirmed with AI path finding).

---

## 한국어 (Korean)

### 08. 남은 최적화 후보

| 항목 | 현재 비용 | 방법 | 위험 |
|---|---|---|---|
| 게임 시작 후 첫 제국 관리 진입 | 4~7초 (이후 약 1초) | Flash 목록 가상화(필요한 행 위젯만 만들어 재사용). 정렬·선택(+62px)·스크롤 높이·구매 버튼 갱신이 엮여 있음 | 중~높음 |
| AI: `T_Engage.BoostPriorityOfBlockingSettlements` | 턴당 약 6초(측정 포함) | 경로 탐색 최적화 후 재측정 | 미분석 |
| AI: `SP_GeneralStrategy.RefreshTargetLocalWars` | 턴당 약 3.7초 | 재측정 | 미분석 |
| AI: `Awareness.CalculateStarCentrality` | 턴당 약 2.2초 | 재측정 | 미분석 |
| 클라이언트 턴 재계산 | 약 9초 | 클라이언트 중복 시뮬레이션 생략 | 높음 |
| `BuildTerritoryAndDispatchChanges` | 턴당 약 2.8초 | 분석 필요 | 미분석 |
| 하단 바 `RefreshNotifications` | 1회 약 220ms | 알림 SWF 분석 | 미분석 |
| 자동 건설 `EvaluateColonyNeeds` | 자동 건설 식민지 수 × O(N) | 정확한 캐시 불가 | 결과 변경 |
| 매 턴 타임라인 저장 | 턴당 약 1.2초 | 새 게임 설정에서 저장 빈도 낮춤 | 없음 |

다음 측정은 `build.cmd perf` 대신 측정 코드 없는 빌드에 대상 구간 타이머를 추가하는 방식을 권장.
