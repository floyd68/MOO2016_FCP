# 06. Lessons and pitfalls

## Measuring
- **O(N²) found by static analysis may not be what the user feels.** The Empire Management lag moved from C# O(N²) → Flash tooltips → Flash scroll check → Scaleform `DoHitTest`, a different cause after each measurement. Confirm every step by measuring.
- **Profiling code distorts results.** Wrapping very hot methods (e.g. `CanMoveAcross`, 9.35 M calls) makes path finding look far more expensive than it is. Draw conclusions from builds without profiling code plus timers around the target only.
- Methods with a prefix are **double counted** by the profiling wrapper (it wraps both prefix and original).
- When Unity frames are slow, Scaleform **runs missed SWF frames several times** (typically 3 observed). Flash cost = per-frame cost × repetitions.
- Scaleform's `DoHitTest` had a large fixed cost independent of the test type (shape/bounds), presumably whole-movie processing. Only after diagnostics showed "always true" could it be skipped safely.

## Correctness
- Single-player still runs server and client simulations and compares checksums → simulation code must produce **bit-identical values**. Mind floating-point summation order (swapping two terms is safe, regrouping three or more is not).
- Restrict caches to windows where state cannot change, clear them before and after every action, and never fill them while an action runs.
- Caches/re-implementations were compared with the originals in real play via a **sampled verification mode**. Reference comparison first produced false mismatches (objects recreated on every call) → compare by meaning.
- `[ThreadStatic]` caches keep the server thread and the main thread apart.
- Hooking both a virtual base method and its override, then re-calling through bypass, **recurses forever**. Hook only the final override.
- Set the bypass flag immediately before the call (so argument evaluation cannot trigger another hook first).
- Making a private field of a MonoBehaviour public changes Unity serialization → FixPatcher refuses.

## UI
- When messages are never coalesced, simply processing refreshes once per frame gives a big win.
- Lazily filling hidden rows can create multi-second freezes **when many rows become visible at once** (long drag) → per-frame budget and keep unfilled rows hidden.
- Flash drags must listen to MOUSE_MOVE/UP on `stage`, otherwise moving over empty space ends the drag.
- When hiding rows, check when the scroll height is measured (here: in `Fill()`, while all rows are visible).

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

#### 정확성
- 싱글플레이도 서버·클라이언트 이중 시뮬레이션 + 체크섬 비교 → 시뮬레이션 코드는 **비트 단위로 같은 값**이어야 함(교환은 안전, 결합 순서는 위험).
- 캐시는 상태가 바뀌지 않는 구간으로 한정, 모든 액션 전후에 비우고, 실행 중에는 채우지 않는다.
- **표본 검증 모드**로 실제 플레이에서 원본과 비교. 참조 비교로 인한 거짓 불일치 → 의미 단위로 비교.
- `[ThreadStatic]` 캐시로 서버/메인 스레드 분리.
- 가상 메서드에 기반+파생 양쪽 prefix → **무한 재귀**. 최종 override에만.
- bypass 플래그는 호출 직전에 세운다.
- MonoBehaviour private 필드를 public으로 바꾸면 Unity 직렬화가 바뀜 → FixPatcher가 거부.

#### UI
- 병합 없는 메시지 구조에서는 "프레임당 1회 처리"만으로 큰 효과.
- 지연 SetData는 한 번에 많은 행이 보일 때(긴 드래그) 오히려 긴 멈춤 → 프레임당 처리량 제한 + 처리 전 행 숨김.
- Flash 드래그는 `stage`에서 MOUSE_MOVE/UP을 받아야 한다.
- 행을 숨길 때 스크롤 높이 계산 시점 확인(`Fill()`, 모든 행이 visible일 때).

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
