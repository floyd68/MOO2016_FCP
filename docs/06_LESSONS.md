# 06. Lessons and pitfalls

## Measuring
- **O(N²) found by static analysis may not be what the user feels.** The Empire Management lag moved from C# O(N²) → Flash tooltips → Flash scroll check → Scaleform `DoHitTest`, a different cause after each measurement. Confirm every step by measuring.
- **Profiling code distorts results.** Wrapping very hot methods (e.g. `CanMoveAcross`, 9.35 M calls) makes path finding look far more expensive than it is. Draw conclusions from builds without profiling code plus timers around the target only.
- Methods with a prefix are **double counted** by the profiling wrapper (it wraps both prefix and original).
- When Unity frames are slow, Scaleform **runs missed SWF frames several times** (typically 3 observed). Flash cost = per-frame cost × repetitions.
- Scaleform's `DoHitTest` had a large fixed cost independent of the test type (shape/bounds), presumably whole-movie processing. Only after diagnostics showed "always true" could it be skipped safely.
- **Old runtime = expensive garbage.** Unity 5.2's Mono uses the Boehm GC (full, non-generational, not tunable from outside:
  `mono.dll` only reads `GC_DONT_GC`). One collection of a ~600 MB heap took ~150 ms, and the same ~148 ms "max" showed up
  in many unrelated methods of the profile. Counting collections in the frame-stall line, then attributing allocations per
  method (heap-in-use delta between entry and exit, calls with a collection inside skipped) found five methods responsible for
  ~3/4 of all garbage. Replacing them cut turn simulation more than any cache of that round.
- The .NET version cannot be upgraded: the runtime is embedded in the Unity player, so `Span`, SIMD, TPL or a modern GC are
  out of reach; hand-written loops and plain threads are the tools.

## Correctness
- Single-player still runs server and client simulations and compares checksums → simulation code must produce **bit-identical values**. Mind floating-point summation order (swapping two terms is safe, regrouping three or more is not).
- Restrict caches to windows where state cannot change, clear them before and after every action, and never fill them while an action runs.
- Caches/re-implementations were compared with the originals in real play via a **sampled verification mode**. Reference comparison first produced false mismatches (objects recreated on every call) → compare by meaning.
- `[ThreadStatic]` caches keep the server thread and the main thread apart.
- Hooking both a virtual base method and its override, then re-calling through bypass, **recurses forever**. Hook only the final override.
- Set the bypass flag immediately before the call (so argument evaluation cannot trigger another hook first).
- Making a private field of a MonoBehaviour public changes Unity serialization → FixPatcher refuses.
- **Read the key type's `Equals`/`GetHashCode` before re-creating code that fills a game dictionary.** The star separation
  cache key compares hash codes only, so different pairs collide and the original silently reuses the earlier pair's value.
  A "compute everything, then add" version threw a duplicate-key exception that the loading code swallowed — the load never
  finished. Mirror the original's sequential check-then-add on a private copy, then write.
- **Verification has blind spots:** it compared newly computed values with the original A* (0 mismatches) but not the
  surrounding effects (cache insertion). Check every side effect of the replaced code, not only its return value.
- A heuristic that is not admissible (here: squared distance) means A* does not return shortest paths; a "better" algorithm
  (BFS) would change AI behaviour. Faithful re-implementation, not replacement.
- Two simulations running at the same time also share **static** state (checksum helpers, debug lists): audit statics and
  serialize the ones that are written, and keep a fallback (the game's own out-of-sync recovery).
- Hook a method only once: a duplicate entry in the hooks table injects two prefixes.
- **A failed A\* search still has a result.** The game returns the path to the closest reached node and the AI computes ETAs
  from it, so "unreachable" cannot be short-circuited by a reachability check; only the cost per search can be reduced.
- **Copying a heap means copying its quirks.** The game pushes the same mutable `Node` again and changes its `f` while it is
  still in the heap; a faithful replacement must compare *current* values of the referenced cell, not snapshot keys.
- A cache scope that is "per refresh" can still be O(N²) overall (security recomputed for every refresh). A generation counter
  bumped around every state-changing method gives a longer validity window without guessing.
- The server turn simulation runs *inside* `Universe.RunAI`: any "AI stage" cache must treat it like an action.

## UI
- When messages are never coalesced, simply processing refreshes once per frame gives a big win.
- Lazily filling hidden rows can create multi-second freezes **when many rows become visible at once** (long drag) → per-frame budget and keep unfilled rows hidden.
- Flash drags must listen to MOUSE_MOVE/UP on `stage`, otherwise moving over empty space ends the drag.
- When hiding rows, check when the scroll height is measured (here: in `Fill()`, while all rows are visible).
- Scaleform `Value` numbers come as `VT_Int`, `VT_UInt` or `VT_Number`; the implicit `double` conversion of an int value
  returned 0 (`numChildren` = 0, so nothing was found). Read each type with its own getter.
- Opening a planet from Empire Management does not close it: the planet screen is pushed on top and the list is refilled when
  the screen is presented again. Restore UI state in `OnPresented`, after the refill — not when the list data is loaded.

## Tools
- FFDec's AS3 recompilation can change runtime behavior even when the decompiled source looks identical → only replace method bodies with P-code.
- First check the tool: re-inserting an unmodified body must give a byte-identical file.
- CoreCLR JIT rejects a few methods that run fine on Mono → judge by "same list as the original".
- A shell command that waits on stdin (e.g. a stray `python -`) hangs the session (happened once).
- When `cmd.exe` is launched from Git Bash it inherits Git's `PATH`, so `find`/`sort` may resolve to GNU tools (GNU `find /c` scanned the whole drive) → batch files call `%SystemRoot%\System32\…` explicitly.
- `certutil -hashfile` output has localized header/status lines → take the second line instead of pattern matching.
- Batch files need CRLF line endings; editing them with `sed` and backslash escapes (`\f`, `\t`) can silently insert control characters.
- In batch files, a `%var%`/`%~1` holding a path such as `C:\Program Files (x86)` inside a `( … )` block closes the block early → use `!var!` (delayed expansion) there.
- `exit /b 1` inside a `( … )` block after `call` sets `%errorlevel%` but can be lost as the process exit code of `cmd /c script.cmd` → do the `call … || exit /b 1` outside blocks.
- In `for /f ('""tool.exe" … "arg""')` the quote state alternates, so a `^` meant to be quoted (e.g. `findstr /n "^"`) can end up unquoted and act as an escape → use `find /n /v ""` instead.
- Git Bash passes both `PROGRAMFILES` and `ProgramFiles` to child processes and restores them; to simulate a missing tool, set the variables inside cmd (a wrapper .cmd), not with `env`.

---

## 한국어 (Korean)

### 06. 교훈과 함정

#### 측정
- **정적 분석의 O(N²)가 체감 원인과 일치하지 않을 수 있다.** 제국 관리 렉은 C# O(N²) → Flash 툴팁 → Flash 스크롤 검사 → Scaleform `DoHitTest` 순으로, 측정할 때마다 원인이 바뀌었다.
- **측정 코드가 결과를 왜곡한다.** 초고빈도 메서드(예: `CanMoveAcross` 935만 회)까지 감싸면 경로 탐색이 실제보다 훨씬 비싸 보인다.
- prefix가 있는 메서드는 측정 래퍼에 **이중 집계**된다.
- Unity 프레임이 느리면 Scaleform이 **밀린 SWF 프레임을 여러 번 실행**한다(관측: 보통 3번).
- Scaleform의 `DoHitTest`는 검사 종류와 무관하게 큰 고정 비용이 있었다. "항상 true"를 진단으로 확인한 뒤에야 안전하게 생략할 수 있었다.
- **오래된 런타임 = 비싼 쓰레기.** Unity 5.2의 Mono는 Boehm GC(전체 수집, 세대 구분 없음, 외부 설정 불가: `mono.dll`은 `GC_DONT_GC`만 읽음)를 씀. 약 600MB 힙의 수집 1회가 약 150ms였고, 측정 결과에서 서로 관계없는 여러 메서드의 최대값이 똑같이 약 148ms로 나타남. 화면 멈춤 로그에 GC 횟수를 넣고, 메서드별 할당량(진입·종료 시 힙 사용량 차이, 그 사이 GC가 있던 호출은 제외)을 집계하자 쓰레기의 약 3/4이 메서드 5개에서 나옴. 이를 바꾼 것이 그 회차의 어떤 캐시보다 턴 시뮬레이션을 많이 줄였음.
- .NET 버전은 올릴 수 없음: 런타임이 Unity 플레이어에 내장되어 있어 `Span`, SIMD, TPL, 최신 GC를 쓸 수 없음. 손으로 쓴 루프와 직접 만든 스레드가 도구.

#### 정확성
- 싱글플레이도 서버·클라이언트 이중 시뮬레이션 + 체크섬 비교 → 시뮬레이션 코드는 **비트 단위로 같은 값**이어야 함(교환은 안전, 결합 순서는 위험).
- 캐시는 상태가 바뀌지 않는 구간으로 한정, 모든 액션 전후에 비우고, 실행 중에는 채우지 않는다.
- **표본 검증 모드**로 실제 플레이에서 원본과 비교. 참조 비교로 인한 거짓 불일치 → 의미 단위로 비교.
- `[ThreadStatic]` 캐시로 서버/메인 스레드 분리.
- 가상 메서드에 기반+파생 양쪽 prefix → **무한 재귀**. 최종 override에만.
- bypass 플래그는 호출 직전에 세운다.
- MonoBehaviour private 필드를 public으로 바꾸면 Unity 직렬화가 바뀜 → FixPatcher가 거부.
- **게임 사전을 채우는 코드를 다시 만들 때는 키 형식의 `Equals`/`GetHashCode`부터 볼 것.** 별 거리 캐시 키는 해시값만 비교해서 서로 다른 쌍이 충돌하고, 원본은 앞 쌍의 값을 조용히 재사용함. "모두 계산 후 한꺼번에 추가"한 버전은 중복 키 예외가 로딩 코드에 삼켜져 로딩이 끝나지 않았음 → 원본처럼 "확인 후 추가"를 순서대로 복사본에서 재현한 뒤 기록.
- **검증의 사각지대:** 새로 계산한 값은 원본 A*와 비교했지만(불일치 0) 주변 효과(캐시 추가)는 비교하지 않았음. 반환값뿐 아니라 교체한 코드의 모든 부수 효과를 확인할 것.
- 허용 가능하지 않은 휴리스틱(거리 제곱)이면 A*가 최단 경로를 내지 않음. "더 나은" 알고리즘(BFS)으로 바꾸면 AI 행동이 달라짐 → 교체가 아니라 충실한 재구현.
- 두 시뮬레이션을 동시에 돌리면 **정적(static) 상태**도 공유함(체크섬 도우미, 디버그 목록). 정적 필드를 조사해 쓰기가 있는 것은 직렬화하고, 대비책(게임 자체의 동기화 오류 복구)을 남길 것.
- 같은 메서드는 한 번만 훅: 훅 표에 중복 항목이 있으면 prefix가 두 번 삽입됨.
- **실패한 A\* 탐색에도 결과가 있다.** 게임은 가장 가까이 도달한 노드까지의 경로를 돌려주고 AI는 그것으로 도착 시간을 계산함 → 도달 가능성 검사로 "도달 불가"를 건너뛸 수 없고, 탐색 한 번의 비용만 줄일 수 있음.
- **힙을 옮기면 그 특이 동작까지 옮겨야 한다.** 게임은 같은 가변 `Node`를 다시 넣고, 힙에 남아 있는 동안 `f`를 바꿈 → 충실한 대체는 키 사본이 아니라 칸의 *현재* 값을 비교해야 함.
- "갱신마다"인 캐시 범위도 전체로는 O(N²)일 수 있음(갱신마다 보안 재계산). 상태를 바꾸는 모든 메서드 전후에 올리는 세대 번호로 추측 없이 유효 구간을 넓힘.
- 서버 턴 시뮬레이션은 `Universe.RunAI` *안에서* 실행됨: "AI 단계" 캐시는 이것을 행동과 똑같이 취급해야 함.

#### UI
- 병합 없는 메시지 구조에서는 "프레임당 1회 처리"만으로 큰 효과.
- 지연 SetData는 한 번에 많은 행이 보일 때(긴 드래그) 오히려 긴 멈춤 → 프레임당 처리량 제한 + 처리 전 행 숨김.
- Flash 드래그는 `stage`에서 MOUSE_MOVE/UP을 받아야 한다.
- 행을 숨길 때 스크롤 높이 계산 시점 확인(`Fill()`, 모든 행이 visible일 때).
- Scaleform `Value`의 숫자는 `VT_Int`, `VT_UInt`, `VT_Number`로 옴. int 값을 암묵적 `double` 변환으로 읽으면 0이 됨(`numChildren` = 0 → 아무것도 못 찾음). 형식마다 맞는 getter로 읽을 것.
- 제국 관리에서 행성을 열어도 화면은 닫히지 않음: 행성 화면이 위에 쌓이고, 다시 표시될 때 목록을 새로 채움 → UI 상태 복원은 목록 데이터를 불러올 때가 아니라 다시 채운 뒤 `OnPresented`에서.

#### 도구
- FFDec AS3 재컴파일은 소스가 같아 보여도 동작이 달라질 수 있음 → 메서드 본문 P-code 교체만.
- 도구 먼저 검증: 원본 본문 재삽입 시 바이트 동일.
- CoreCLR JIT는 Mono에서 정상인 메서드 몇 개를 거부 → "원본과 같은 목록"으로 판단.
- 표준입력을 기다리는 명령(`python -`)이 섞이면 멈춤.
- Git Bash에서 `cmd.exe`를 실행하면 Git의 `PATH`를 물려받아 `find`가 GNU find로 실행될 수 있음(드라이브 전체 탐색) → 배치 파일은 `%SystemRoot%\System32\…`를 명시.
- `certutil -hashfile` 출력의 머리줄/상태줄은 언어별로 다름 → 패턴 대신 둘째 줄을 읽는다.
- 배치 파일은 CRLF 필요. `sed`의 백슬래시 이스케이프(`\f`, `\t`)가 제어 문자를 몰래 넣을 수 있음.
- 배치 파일의 `( … )` 블록 안에서 `C:\Program Files (x86)` 같은 경로를 `%변수%`/`%~1`로 쓰면 괄호 때문에 블록이 일찍 닫힘 → 블록 안에서는 `!변수!` 사용.
- `call` 뒤 `( … )` 블록 안의 `exit /b 1`은 `%errorlevel%`은 1이지만 `cmd /c 스크립트.cmd`의 프로세스 종료 코드로는 사라질 수 있음 → `call … || exit /b 1`을 블록 밖에서.
- `for /f ('""도구.exe" … "인자""')`에서는 따옴표 상태가 번갈아 바뀌어, 따옴표 안에 있어야 할 `^`(예: `findstr /n "^"`)가 밖으로 나와 이스케이프로 동작할 수 있음 → `find /n /v ""` 사용.
- Git Bash는 자식 프로세스에 `PROGRAMFILES`와 `ProgramFiles`를 모두 넘기고 되돌려 놓음 → 도구가 없는 상황을 흉내 내려면 `env`가 아니라 cmd 안(래퍼 .cmd)에서 변수를 바꿀 것.
