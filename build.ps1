[CmdletBinding()]
param([string]$Python = "python3")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if ($env:OS -ne "Windows_NT") { throw "Windows x64에서 실행하세요." }
if (-not (Get-Command $Python -ErrorAction SilentlyContinue)) { throw "Python을 찾을 수 없습니다. -Python python 또는 Python 실행 파일 경로를 지정하세요." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET 8 SDK를 설치한 뒤 다시 실행하세요." }

function Invoke-Checked {
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "명령 실행 실패: $Program (종료 코드 $LASTEXITCODE)" }
}

Invoke-Checked $Python @("-c", "import sys,struct; assert sys.version_info >= (3,11), 'Python 3.11 이상 필요'; assert struct.calcsize('P') == 8, '64비트 Python 필요'")
Invoke-Checked $Python @("-m", "venv", ".venv")
$BridgePath = Join-Path $PSScriptRoot "Engine\bridge.py"
$VenvPython = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
Invoke-Checked $VenvPython @("-m", "pip", "install", "--upgrade", "pip")
Invoke-Checked $VenvPython @("-m", "pip", "install", "-r", "Engine\requirements.txt")

# The helper keeps redirected console pipes but never shows a console window (CreateNoWindow in the UI).
Invoke-Checked $VenvPython @("-m", "PyInstaller", "--noconfirm", "--clean", "--onedir", "--console",
    "--name", "NcmEngine", "--collect-all", "pyautogui", "--collect-all", "mss", "--collect-all", "numpy", "--hidden-import", "numpy.version",
    "--distpath", "artifacts\engine",
    "--workpath", "artifacts\pyinstaller", "--specpath", "artifacts", $BridgePath)
   

# Rebuild generated XAML/PRI instead of reusing the previous incomplete resource output.
foreach ($GeneratedDirectory in @("NcmX\bin", "NcmX\obj")) {
    if (Test-Path $GeneratedDirectory) { Remove-Item -Recurse -Force $GeneratedDirectory }
}
# Keep publish output separate and clean so old DLLs are never mixed into this build.
if (Test-Path "artifacts\publish") { Remove-Item -Recurse -Force "artifacts\publish" }
Invoke-Checked "dotnet" @("publish", "NcmX\NcmX.csproj", "-c", "Release", "-p:Platform=x64", "-r", "win-x64", "--self-contained", "true", "-o", "artifacts\publish")
$EngineDestination = Join-Path $PSScriptRoot "artifacts\publish\Engine"
if (Test-Path $EngineDestination) { Remove-Item -Recurse -Force $EngineDestination }
Copy-Item "artifacts\engine\NcmEngine" $EngineDestination -Recurse
Invoke-Checked (Join-Path $EngineDestination "NcmEngine.exe") @("--self-test")
Copy-Item "diagnose.ps1" "artifacts\publish\diagnose.ps1" -Force
Copy-Item "진단 실행.cmd" "artifacts\publish\진단 실행.cmd" -Force
$Archive = Join-Path $PSScriptRoot "artifacts\NCM_X_Windows_x64.zip"
Compress-Archive -Path "artifacts\publish\*" -DestinationPath $Archive -Force
Write-Host ""
Write-Host "완료: artifacts\publish\NCM_X.exe" -ForegroundColor Green
Write-Host "배포 파일: artifacts\NCM_X_Windows_x64.zip" -ForegroundColor Green

& (Join-Path $PSScriptRoot "SingleFile\build-singlefile.ps1") -PublishDirectory (Join-Path $PSScriptRoot "artifacts\publish") -Output (Join-Path $PSScriptRoot "artifacts\NCM_X.exe")
Write-Host "단일 실행 파일: artifacts\NCM_X.exe" -ForegroundColor Green
