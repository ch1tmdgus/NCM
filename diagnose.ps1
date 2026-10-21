$ErrorActionPreference = "Stop"
$LogDirectory = Join-Path $env:LOCALAPPDATA "NCM_X\Logs"
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$Report = Join-Path $LogDirectory "diagnostic.txt"
$HostTrace = Join-Path $LogDirectory "dotnet-host.txt"
$Exe = Join-Path $PSScriptRoot "NCM_X.exe"
$StartedAt = Get-Date
@("NCM X 실행 진단", "시간: $StartedAt", "위치: $PSScriptRoot",
  "Windows: $([Environment]::OSVersion.VersionString)", "64비트 OS: $([Environment]::Is64BitOperatingSystem)") |
    Set-Content -Path $Report -Encoding UTF8
function Record([string]$Message) { Add-Content -Path $Report -Value $Message -Encoding UTF8 }
try {
    if (-not (Test-Path $Exe)) { throw "NCM_X.exe와 같은 폴더에서 실행하세요." }
    foreach ($Name in @("resources.pri", "NCM_X.dll", "NCM_X.deps.json", "NCM_X.runtimeconfig.json", "Microsoft.ui.xaml.dll", "WinRT.Runtime.dll", "hostfxr.dll", "Engine\NcmEngine.exe")) {
        Record "$Name : $(Test-Path (Join-Path $PSScriptRoot $Name))"
    }
    if (-not (Test-Path (Join-Path $PSScriptRoot "resources.pri"))) {
        Record "확인된 배포 누락: resources.pri가 없습니다. 1.0.2 이상 소스로 다시 빌드하세요."
    }
    $VcRuntime = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
    Record "Visual C++ x64 runtime registry: $($VcRuntime.Installed); version=$($VcRuntime.Version)"
    $Info = New-Object System.Diagnostics.ProcessStartInfo
    $Info.FileName = $Exe
    $Info.WorkingDirectory = $PSScriptRoot
    $Info.UseShellExecute = $false
    $Info.EnvironmentVariables["COREHOST_TRACE"] = "1"
    $Info.EnvironmentVariables["COREHOST_TRACEFILE"] = $HostTrace
    $Process = [System.Diagnostics.Process]::Start($Info)
    Record "PID: $($Process.Id)"
    if ($Process.WaitForExit(8000)) {
        Record "종료 코드: $($Process.ExitCode)"
    } else {
        $Process.Refresh()
        Record "8초 후 실행 중. 창 핸들: $($Process.MainWindowHandle)"
    }
} catch {
    Record "진단 예외: $($_.Exception.ToString())"
}
Record "`nWindows 응용 프로그램 이벤트:"
try {
    Get-WinEvent -FilterHashtable @{LogName="Application"; StartTime=$StartedAt.AddSeconds(-2)} -MaxEvents 40 -ErrorAction Stop |
        Where-Object { $_.Message -match "NCM_X|Microsoft.ui.xaml|WindowsAppRuntime|NcmX" } |
        ForEach-Object { Record "$($_.TimeCreated) [$($_.ProviderName)] $($_.Message)" }
} catch { Record "새 응용 프로그램 이벤트 없음 또는 읽기 불가" }
$StartupLog = Join-Path $LogDirectory "startup.log"
if (Test-Path $StartupLog) {
    Record "`n앱 시작 로그 (최근 100줄):"
    Get-Content $StartupLog -Tail 100 -Encoding UTF8 | ForEach-Object { Record $_ }
} else {
    Record "앱 시작 로그 없음: 관리 코드 진입 전에 종료되었을 수 있습니다."
}
Record "`n.NET 호스트 추적: $HostTrace"
if (Test-Path $HostTrace) {
    Get-Content $HostTrace -Tail 80 -Encoding UTF8 | ForEach-Object { Record $_ }
}
Start-Process notepad.exe -ArgumentList ('"' + $Report + '"')
Write-Host "진단 파일: $Report"
