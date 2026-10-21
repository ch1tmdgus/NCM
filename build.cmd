@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 (
    echo Build failed. See the message above.
    pause
    exit /b 1
)
pause
