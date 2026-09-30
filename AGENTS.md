# CCusagebar (Windows) 작업 안내

이 저장소는 Codex·Claude의 남은 계정 사용량을 Windows 작업표시줄에 보여주는 작은 도구입니다. macOS 버전은 별도 저장소 [share-seoi/CCusagebar-mac](https://github.com/share-seoi/CCusagebar-mac)에 있습니다.

## 사용자가 다른 PC에 설치해 달라고 할 때

1. 현재 OS를 확인합니다. macOS라면 이 저장소가 아니라 [CCusagebar-mac](https://github.com/share-seoi/CCusagebar-mac)의 안내를 따릅니다.
2. Windows 10/11 x64라면 저장소 루트에서 아래 명령을 실행합니다. 관리자 권한이나 .NET SDK, Node, Python 설치는 필요하지 않습니다. Windows에 .NET Framework 4.8 이상이 있어야 하며 설치 스크립트가 확인합니다.

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
   ```

3. 기본 설치 위치는 `%LOCALAPPDATA%\Programs\CCusagebar\CCusagebar.exe`입니다. 설치 스크립트가 소스를 빌드하고 실행 파일·설정 파일만 복사한 뒤 실행합니다. 설치 후에는 저장소를 옮겨도 됩니다.
4. 사용자가 로그인 시 자동 실행도 요청했다면 같은 명령에 `-AutoStart`를 붙입니다. 바탕화면 켜기/끄기 바로가기를 원하면 `-DesktopShortcut`을 붙입니다. 단순 설치만으로 자동 시작을 켜지 않습니다. 실행하지 않고 설치하려면 `-NoStart`, 경로를 지정하려면 `-InstallDirectory 'C:\원하는\폴더'`를 사용합니다.
5. Codex 또는 Claude 데스크톱 앱이 실행 중이면 작업표시줄에 위젯이 표시됩니다. 둘 다 꺼져 있으면 위젯을 숨기고 앱 실행을 기다리므로, 프로세스가 살아 있다는 이유만으로 표시가 확인됐다고 말하지 않습니다. 상세 창에서 연결 상태와 데이터 시각을 확인합니다.
6. 다른 폴더의 위젯이 이미 실행 중이면 설치 스크립트는 그 프로세스를 종료하지 않습니다. 해당 위젯의 상세 창에서 종료한 뒤 설치된 실행 파일을 실행합니다. Codex나 Claude 프로세스를 종료하지 않습니다.

## 계정 연결과 검증

- 다른 PC의 Claude/Codex 앱에 사용자 본인이 로그인해야 합니다. 현재 PC의 토큰이나 설정 파일을 복사하거나 Git에 넣지 않습니다.
- Claude는 데스크톱 로그인 토큰을 읽어 사용량 API를 조회합니다(위젯이 Claude 표시 중 1분, 아니면 3분, 전환·상세 창 열기 때 즉시). 토큰별 HTTP 429 제한이 있으므로 간격을 줄이지 않습니다. Store 설치도 지원합니다. 데스크톱 프로필이 없을 때만 Claude Code 로그인 파일을 사용합니다. 토큰 갱신은 Claude에 맡깁니다.
- Codex는 20초마다 로컬 기록을 읽고, 시작·수동 새로고침·Codex로 전환할 때(1분에 한 번까지) App Server에서 계정 한도를 조회합니다. 조회마다 프로세스를 띄우므로 주기 조회는 넣지 않습니다.
- 토큰이 없거나 만료됐다면 UI에 오류를 표시합니다. 로그인을 우회하거나 토큰을 새로 만들지 말고 해당 PC에서 로그인 상태를 확인합니다.
- 개인 로그인 정보 없이 코드만 검증할 때:

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File .\test.ps1
  ```

- 실제 계정 사용량을 확인하는 진단 옵션과 stdout 리디렉션 예시는 `docs/details.md`를 봅니다. 모델 추론 요청 없이 사용량만 조회합니다. 실행 파일 생성 성공과 실제 계정 조회 성공을 구분해 보고합니다.

## 수정 시 규칙

- 사용자의 실행 중인 다른 프로그램, 인증 정보, 자동 시작 설정, 관계없는 변경을 보존합니다.
- C# 코드는 Windows 기본 .NET Framework 컴파일러와 호환되게 작성합니다. 변경 후 `test.ps1` 및 `build.ps1`을 실행합니다. 실행 중인 위젯 파일을 덮어쓰지 않도록 검증 빌드는 `-OutputDirectory`로 별도 위치를 지정할 수 있습니다.
- UI 수정은 밝은/어두운 테마와 100/125/150/200% 배율의 `test-output` 렌더를 확인합니다.
- `dist`와 `test-output`은 빌드 산출물이며 Git에 추가하지 않습니다. 계정 진단 결과, 토큰, 로컬 사용량 캐시도 커밋하지 않습니다.
