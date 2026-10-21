# NCM X · WinUI 3

Windows용 네이티브 WinUI 3 화면과 Python 기반 감지·클릭 엔진입니다. 이 저장소는 1.1.0 오프라인 버전의 전체 소스와 단일 EXE 빌드 도구를 포함합니다.

Telegram 알림, Firebase 계정 조회·인증, 등록 ID 입력, 관련 API 호출 및 자격 증명은 모두 제거했습니다. API 키·서버 주소·계정 정보나 설정 파일 없이 실행합니다.

## 화면과 기능

- 왼쪽 위 햄버거 메뉴의 홈 / 설정
- 홈 왼쪽: 영역·색상·최종 클릭 위치 지정과 미리보기
- 홈 오른쪽: 상태·작업 횟수·실행 로그
- 설정: 감지 엔진, 새로고침 방식·속도, 추가 좌표, 시작 대기
- 토글 스위치, 창 크기에 맞춰 조정되는 네이티브 Grid, Mica 제목 표시줄
- 색상 선택 중 십자 커서, ESC 취소·중지, 성공 후 A로 다시 시작
- 두 감지 엔진, 중앙 재조준·재시도, 복수 색상, YES24 방향키 모드, 성공·퍼즐 경고음

## Windows 빌드

Windows 10 2004 이상 또는 Windows 11 x64가 필요합니다. 64비트 Python 3.11 이상(검증 환경: 3.12.10), .NET 8 SDK, Windows SDK 및 WinUI 빌드 도구를 준비하세요. Visual Studio 2022의 .NET 데스크톱 및 Windows 앱 개발 도구를 사용할 수 있습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Python python
```

빌드 결과:

- `artifacts/publish/NCM_X.exe`: 폴더형 배포의 본체. 해당 폴더 전체가 필요합니다.
- `artifacts/NCM_X.exe`: 단독으로 전달할 수 있는 단일 EXE
- `artifacts/NCM_X_Windows_x64.zip`: 폴더형 배포 ZIP

빌드는 새 엔진을 생성하고 NumPy/Pillow 자체 검사 및 WinUI 리소스 누락 검사를 수행합니다. 이전 버전의 엔진이나 EXE를 이 소스와 섞지 마세요. 단일 EXE의 최초 실행은 구성요소를 앱 전용 저장소에 준비하고 이후 재사용합니다. 세부 사항은 [SingleFile/README.md](SingleFile/README.md)를 참고하세요.

## 사용

1. 홈에서 영역 시작점 A와 끝점 B를 지정합니다.
2. 색상 추가를 누르고 원하는 위치에서 A를 누릅니다. 색상 칩을 클릭하면 삭제합니다.
3. 최종 클릭 위치를 지정합니다. 필요한 모드의 추가 좌표는 설정에서 지정합니다.
4. 설정에서 감지 엔진과 속도·새로고침 방식을 고릅니다.
5. 시작 버튼을 누릅니다. ESC나 중지 버튼으로 취소합니다.
6. 감지 후 클릭 결과는 대상 화면에서 확인합니다. 성공 대화상자에서 A로 다시 시작합니다.

## 소스 구성

| 경로 | 내용 |
| --- | --- |
| `NcmX/` | WinUI 3 화면, 십자 커서, Python 엔진 통신, 시작 진단 |
| `Engine/` | 감지·클릭 알고리즘 및 로컬 JSON IPC |
| `SingleFile/` | 단일 EXE 실행 래퍼와 패키징 도구 |
| `tests/` | 실제 마우스·키보드 조작 없이 실행하는 엔진 회귀 검사 |
| `build.ps1`, `build.cmd` | Windows 빌드 |
| `diagnose.ps1`, `진단 실행.cmd` | 폴더형 배포 진단 |

## 검사

NumPy와 Pillow를 설치한 환경에서:

```text
python -m unittest discover -s tests -v
```

두 감지 엔진, 새로고침 우선순위, 중지·중복 시작, 좌표 보정, 설정 파일과 네트워크 없이 시작하는 동작을 검사합니다. 이 테스트는 외부 서비스에 연결하거나 실제 클릭을 하지 않습니다.

시작 로그는 `%LOCALAPPDATA%\NCM_X\Logs\startup.log`, 단일 EXE 준비 로그는 같은 위치의 `launcher.log`에 저장됩니다.
