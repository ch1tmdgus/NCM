# 단일 EXE 빌드

배포할 파일은 `NCM_X.exe` 하나입니다. 첫 실행 시 필요한 파일을 `%LOCALAPPDATA%\NCM_X\Runtime\1.1.0-<패키지 해시>`에 준비하고 이후 재사용합니다. 실행 래퍼는 본체를 시작한 뒤 종료합니다.

## 빌드

루트 `build.ps1`은 폴더형 배포와 단일 EXE를 함께 생성합니다. 이미 이 버전의 소스로 빌드한 폴더를 따로 패키징할 경우:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\SingleFile\build-singlefile.ps1 -PublishDirectory .\artifacts\publish -Output .\artifacts\NCM_X.exe
```

Windows에 포함된 .NET Framework 4.x의 C# 컴파일러를 사용합니다. 별도 런타임 설치나 관리자 권한 없이 실행할 수 있도록 본체의 .NET·WinUI·Python 런타임을 포함합니다.

이전 버전의 배포 폴더에는 제거된 연동 코드와 자격 증명이 포함되어 있을 수 있으므로, 반드시 이 저장소의 소스에서 새로 빌드한 폴더를 사용하세요.

## 패키징 방식

- 테스트 데이터, 디버그 심벌 등 개발 자료를 제외합니다.
- 첫 준비 시 포함 파일의 SHA-256을 검증합니다.
- 임시 폴더에 추출을 완료한 뒤 버전·내용 해시별 저장소로 이동합니다.
- 사용자 세션 뮤텍스로 동시에 시작하는 두 실행의 추출 충돌을 방지합니다.
- 핵심 파일이 없거나 준비가 완료되지 않았으면 다시 준비합니다.
- EXE 옆에 파일을 만들지 않습니다.

기록은 `%LOCALAPPDATA%\NCM_X\Logs\launcher.log`에 저장됩니다. 최초 실행 시간은 PC의 저장장치와 보안 프로그램에 따라 달라집니다.
