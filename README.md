# Windows Codex Usage Bar

Windows 작업표시줄에서 Codex와 Claude의 남은 사용량을 표시합니다. 배터리마다 `5h`·`W` 같은 한도 이름을 붙입니다. Claude나 Codex Plus처럼 한도가 여러 개면 한도마다 나란히 그리고, 주간 한도만 있으면 `W` 배터리 하나만 그립니다. 상세 창의 **표시** 버튼으로 앞에 띄운 앱만 보여주는 "자동 전환"과 Codex·Claude를 나란히 보여주는 "둘 다"를 고를 수 있고, 선택은 저장됩니다. 앱 아이콘과 배터리를 누르면 두 계정의 한도 및 초기화 시각을 함께 볼 수 있습니다.

## 실행과 빌드

다른 PC에 설치할 때는 저장소 루트에서 다음 한 줄을 실행하면 빌드·설치·실행을 처리합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\install.ps1
```

기본 설치 경로는 `%LOCALAPPDATA%\Programs\CodexUsageBar`입니다. `-AutoStart`를 추가하면 사용자 로그인 시 자동 실행하며, `-NoStart`는 설치 후 실행을 생략합니다. `-InstallDirectory 'C:\원하는\폴더'`로 경로를 지정할 수 있습니다. 원래 계정 정보와 사용량 캐시를 보존하고, 같은 설치 경로에서 실행 중인 위젯만 교체합니다. 다른 경로의 위젯이 실행 중이면 먼저 그 위젯의 상세 창에서 종료한 다음 새 실행 파일을 실행합니다.

개발용으로 실행 파일만 빌드하려면 아래를 사용합니다.

Windows 10/11 x64 및 .NET Framework 4.8 환경에서 별도 SDK 설치 없이 빌드합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
Start-Process .\windows\dist\CodexUsageBar.exe
```

이 폴더에서 빌드할 때는 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`을 사용합니다. 실행 파일과 같은 폴더에 `CodexUsageBar.exe.config`를 함께 둡니다.

두 앱 중 앞에 있는 앱의 사용량을 작업표시줄에 표시하고, 다른 앱으로 전환하면 마지막 표시를 유지합니다. 둘 다 종료되면 위젯은 숨겨지고 앱 실행 감지는 유지됩니다. 상세 창의 **종료**로 완전히 종료할 수 있습니다. **자동 시작** 버튼은 현재 사용자 로그인 시 실행 여부를 설정합니다. 빌드나 첫 실행만으로 자동 시작을 등록하지 않습니다.

## Claude: 로그인 토큰으로 직접 조회

- `https://api.anthropic.com/api/oauth/usage`에 GET 요청을 보내 5시간/주간 사용률과 초기화 시각을 읽습니다. 위젯이 Claude를 표시 중이면(둘 다 표시 포함) **1분마다**, Codex만 표시 중이면 **3분마다** 조회하고, Claude로 전환하거나 상세 창을 열 때는 바로 한 번 조회합니다(20초 이내 재조회는 생략). 모델 추론을 요청하지 않습니다.
- 이 API는 토큰마다 조회 제한(HTTP 429)이 있고 Claude 앱도 같은 토큰으로 조회하므로 간격을 넉넉히 둡니다.
- 기본적으로 Claude 데스크톱 앱의 `oauth:tokenCacheV2`를 읽습니다. 일반 설치와 Microsoft Store 설치의 `LocalCache\Roaming\Claude` 경로를 지원하며 Windows DPAPI/CNG로 현재 사용자에게 저장된 토큰을 읽습니다.
- 데스크톱 프로필이 없으면 Claude Code의 `%USERPROFILE%\.claude\.credentials.json`을 사용합니다. `CLAUDE_CONFIG_DIR`이 있으면 그 경로를 따릅니다. 데스크톱 토큰이 만료되거나 거부됐을 때 다른 계정으로 자동 전환하지 않습니다.
- 고급 설정으로 `CLAUDE_USAGE_ACCESS_TOKEN` 환경 변수를 지정하면 해당 토큰을 우선 사용합니다. 토큰을 명령행 인수나 로그에 넣지 마세요.
- 로그인 정보와 refresh token을 수정하지 않으며, 토큰을 별도 파일에 복사하지 않습니다. 각 조회마다 원본에서 다시 읽으므로 Claude가 갱신한 토큰을 반영합니다.
- HTTP 429에서는 `Retry-After`를 존중하며, 헤더가 없으면 2분부터 최대 15분까지 재시도 간격을 늘립니다. 수동 새로고침도 제한을 우회하지 않습니다.
- API 조회가 실패하면 마지막으로 받은 값이나 로컬 사용량 기록을 유지하고, 상세 창에 인증/네트워크 오류와 데이터 시각을 표시합니다. 오래된 로컬 기록을 사용량 0%로 바꾸지 않습니다.
- 작업표시줄 배터리는 표시 중인 값이 **10분보다 오래됐을 때만** 흐리게 표시합니다. 조회가 실패해도 Claude 앱의 로컬 기록이 최근이면 선명하게 유지합니다.
- 사용량 API는 공개적으로 안정성을 보장하는 API가 아니므로 응답/인증 형식이 바뀔 수 있습니다. 관련 자료: [Claude Code 인증 정보 저장 위치](https://code.claude.com/docs/en/authentication#credential-management), [Anthropic 저장소의 사용량 API 429 보고](https://github.com/anthropics/claude-code/issues/31021).

## Codex

- 로컬 세션의 사용량 기록을 **20초마다** 확인합니다.
- 앱 시작, **새로고침**, Codex로 전환할 때(마지막 조회 후 1분이 지났을 때만)는 Codex App Server의 `account/rateLimits/read`로 실시간 계정 한도를 조회하고 조회용 프로세스를 종료합니다.
- 현재 Store 버전의 `OpenAI.Codex_*\app\ChatGPT.exe`와 이전 `Codex.exe`를 감지합니다. 일반 ChatGPT 앱이나 CLI만 실행된 경우는 제외합니다.
- 새 버전의 `%LOCALAPPDATA%\OpenAI\Codex\bin\<version>\codex.exe` 조회 경로도 지원합니다.
- 서버가 제공한 한도만 표시합니다. 주간 한도만 반환되는 계정에 5시간 한도를 임의로 추가하지 않습니다.

## 검증과 진단

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\test.ps1
```

테스트는 합성 토큰을 사용하며 네트워크를 호출하지 않습니다. 응답 해석, 토큰 만료, 조회 간격, 흐림 표시 기준, HTTP 오류/재시도 제한, 실패 후 상태 표시, 앱 감지, UI 렌더링을 확인합니다. 렌더 결과는 `windows\test-output`에 저장됩니다.

실제 사용량 진단은 다음 옵션을 사용합니다. GUI 실행 파일이므로 PowerShell에서는 출력을 리디렉션하고 종료를 기다립니다.

```powershell
$exe = '.\windows\dist\CodexUsageBar.exe'
Start-Process $exe -ArgumentList '--print-claude-live-usage' -Wait -WindowStyle Hidden -RedirectStandardOutput '.\claude-usage.json' -RedirectStandardError '.\claude-error.txt'
Get-Content '.\claude-usage.json' -Encoding UTF8
```

옵션: `--print-claude-live-usage` (Claude 토큰 API), `--print-claude-usage` (Claude 로컬 기록), `--print-live-usage` (Codex 계정 API), `--print-usage` (Codex 로컬 기록). 출력에는 사용량과 시각만 포함하고 로그인 토큰은 포함하지 않습니다.
