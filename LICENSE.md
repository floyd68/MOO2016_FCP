# License

## 1. This project — MIT License

The source code, scripts, patch files and documentation **written for this project** (everything in this repository
except the third-party material listed in sections 2 and 3) are released under the MIT License:

```
MIT License

Copyright (c) 2026 floyd (Lee, Eun Suk - 이은석)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

This covers in particular: `src/MooFix`, `src/MooPerf`, `tools/BuildHelper`, `tools/FixPatcher`, `tools/PerfPatcher`,
`tools/Verify`, `tools/find_moo`, `tools/scripts`, `build.*`, `install.*`, `uninstall.*`, `swf/patches` (our changes, see below)
and `docs/`, `README.md`.

## 2. The game and UCP — not included, not licensed by this project

This repository **does not contain** any file of the game or of the Unofficial Code Patch. The build reads them from the
user's own installation and writes the patched result only to the user's own computer. You need a legitimately owned copy of the game.

| Work | Owner | Notes |
|---|---|---|
| *Master of Orion: Conquer the Stars* (2016) — game program, `Assembly-CSharp*.dll`, `EmpireManagement.swf`, data and assets | © Wargaming Group Limited and its licensors; developed by NGD Studios | "Master of Orion" is a trademark of its owner. |
| Unity engine (`UnityEngine*.dll`, Mono runtime) shipped with the game | © Unity Technologies | referenced at build time from the game folder only |
| Autodesk Scaleform (Flash UI runtime) used by the game | © Autodesk, Inc. | |
| Unofficial Code Patch (UCP) 6.4b | © WhatIsSol and contributors ([Steam Workshop 1109736522](https://steamcommunity.com/sharedfiles/filedetails/?id=1109736522)) | No license is stated by UCP. This patch requires UCP 6.4b and modifies the UCP-modified files on the user's machine. |

This project is **unofficial**. It is not affiliated with, endorsed or supported by Wargaming, NGD Studios, Unity Technologies,
Autodesk or the UCP author. All trademarks belong to their respective owners.

**Fragments of original code.** To change the game's behaviour, some files necessarily reference or reproduce small parts of the
original work; these parts remain the property of their owners and are *not* placed under the MIT License:
- `swf/patches/*.diff` contain, besides the new instructions, one line of original P-code context around each change and the
  original instructions being replaced. The full original method bodies are taken from the user's own SWF at build time.
- `src/MooFix/MooFix.cs` re-implements a few game methods so that they return identical results faster
  (path finding, structure morale/security bonuses, awareness checks); it follows the logic of the original methods and uses their
  type and member names.
- The documentation cites type/method names and line numbers of a decompiled `Assembly-CSharp.dll`.

These are provided for interoperability with a game you own. If a rights holder objects, please open an issue.

## 3. Third-party tools and libraries

None of these is stored in the repository; they are downloaded by NuGet or installed by the user.

| Component | Used for | License |
|---|---|---|
| [Mono.Cecil](https://github.com/jbevain/cecil) 0.11.6 (NuGet) | IL reading/writing in FixPatcher, PerfPatcher | MIT — © 2008–2015 Jb Evain, © 2008–2011 Novell, Inc. |
| [Microsoft.NETFramework.ReferenceAssemblies](https://github.com/microsoft/dotnet/tree/main/releases/reference-assemblies) 1.0.3 (NuGet) | compiling MooFix/MooPerf for .NET Framework 3.5 (compile time only) | MIT — © Microsoft Corporation |
| [.NET SDK](https://dotnet.microsoft.com/) 9+ | building | MIT — © .NET Foundation and Contributors |
| [JPEXS Free Flash Decompiler (FFDec)](https://github.com/jindrapetrik/jpexs-decompiler) 26.x | exporting/replacing SWF P-code; run as an external program, not distributed (`setup` downloads the official release into `tools/ffdec/` on the user's request) | application: GPL-3.0; libraries: LGPL-3.0 — © Jindřich Petřík and contributors |
| Java runtime 8+ (e.g. [Eclipse Temurin](https://adoptium.net/)) | running FFDec | GPL-2.0 with Classpath Exception (Temurin/OpenJDK) |
| [ILSpy / ilspycmd](https://github.com/icsharpcode/ILSpy) (optional) | decompiling for analysis | MIT — © ICSharpCode |
| Git for Windows (optional, for `*.sh`) / Python 3 (optional, `tools/scripts`) | | GPL-2.0 / PSF License |

The patched `Assembly-CSharp.dll` / `Assembly-CSharp-firstpass.dll` produced by the build contain no third-party library code
besides the game's own code; they load `MooFix.dll` (MIT, this project).

## 4. Distribution note

Do **not** redistribute build output (`build/`) or patched game files: they contain the game's and UCP's code.
Distribute this source repository; every user builds and installs the patch from their own game files.

The **binary release** (`MOO2016_FCP-<version>.zip`, made by `tools/make_release.sh`) follows the same rule: it contains only this
project's code (MooFix.dll, the installer, FixPatcher), Mono.Cecil (MIT, notice in `THIRD-PARTY-NOTICES.txt`), a delta of the changed
SWF P-code and the SHA-256 of the expected results. The patched game files are produced on the user's PC from the user's own files.

---

## 한국어 (Korean)

### 라이선스 요약 (법적 효력은 위 영어 원문 기준)

1. **이 프로젝트에서 작성한 코드·스크립트·패치 파일·문서는 MIT License**입니다(저작권자: floyd (Lee, Eun Suk - 이은석)) (`src/`, `tools/`, `build.*`, `install.*`, `uninstall.*`, `swf/patches`의 변경분, `docs/`, `README.md`).
2. **게임과 UCP 파일은 이 저장소에 포함되어 있지 않으며 이 프로젝트의 라이선스 대상이 아닙니다.**
   - *Master of Orion: Conquer the Stars*(2016): © Wargaming Group Limited 및 라이선스 제공자, 개발 NGD Studios. "Master of Orion"은 해당 소유자의 상표입니다.
   - 게임에 포함된 Unity 엔진(© Unity Technologies), Scaleform(© Autodesk).
   - Unofficial Code Patch 6.4b: © WhatIsSol 및 기여자. 별도 라이선스 명시 없음. 이 패치는 UCP 6.4b가 설치된 사용자의 파일을 그 사용자의 PC에서 수정합니다.
   - 비공식 프로젝트이며 위 권리자들과 관계가 없고 승인·지원을 받지 않았습니다.
   - **원본 코드 일부**: `swf/patches/*.diff`에는 변경 위치 주변 1줄의 원본 P-code와 교체되는 원본 명령이, `MooFix.cs`에는 원본과 같은 결과를 내도록 다시 구현한 일부 게임 로직(경로 탐색, 건물 사기/보안 보너스 등)과 원본 타입·멤버 이름이, 문서에는 디컴파일 소스의 이름·줄 번호가 들어 있습니다. 이 부분은 원 권리자의 것이며 MIT 대상이 아닙니다. 권리자가 이의를 제기하면 이슈로 알려 주세요.
3. **제3자 도구/라이브러리**(저장소에 포함하지 않음, NuGet이 내려받거나 사용자가 설치): Mono.Cecil(MIT), Microsoft.NETFramework.ReferenceAssemblies(MIT), .NET SDK(MIT), JPEXS FFDec(GPL-3.0 / 라이브러리 LGPL-3.0, 외부 프로그램으로 실행), Java 런타임(Temurin 등, GPL-2.0 + Classpath Exception), 선택: ILSpy(MIT), Git for Windows(GPL-2.0), Python(PSF).
4. **배포 주의**: 빌드 결과(`build/`)나 패치된 게임 파일은 게임·UCP 코드를 포함하므로 재배포하지 마세요. 소스 저장소만 배포하고 각 사용자가 자기 게임 파일로 빌드·설치합니다.
   **바이너리 릴리스**(`tools/make_release.sh`가 만드는 `MOO2016_FCP-<버전>.zip`)도 같은 원칙입니다: 이 프로젝트 코드(MooFix.dll, 설치 프로그램, FixPatcher), Mono.Cecil(MIT, `THIRD-PARTY-NOTICES.txt`에 고지), 바뀐 SWF P-code의 차이 파일, 결과 SHA-256만 들어 있고, 패치된 게임 파일은 사용자 PC에서 사용자의 파일로 만들어집니다.
