@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_DEV_DIAGNOSTICS=1"
set "GLIDE_MOTION_EVIDENCE=%GLIDE_ROOT%\.artifacts\windows-ui-motion\run-%RANDOM%-%RANDOM%"
set /p GLIDE_APP=<"%LOCALAPPDATA%\GlideDev\app-current.txt"
if not exist "%GLIDE_APP%\Glide.App.exe" exit /b 2
mkdir "%GLIDE_MOTION_EVIDENCE%"
start "" /wait "%GLIDE_APP%\Glide.App.exe" --motion-test
set "GLIDE_MOTION_EXIT=%ERRORLEVEL%"
> "%GLIDE_MOTION_EVIDENCE%\process-exit.txt" echo %GLIDE_MOTION_EXIT%
if not "%GLIDE_MOTION_EXIT%"=="0" exit /b %GLIDE_MOTION_EXIT%
if not exist "%GLIDE_MOTION_EVIDENCE%\result.json" exit /b 1
powershell -NoProfile -Command "$ErrorActionPreference='Stop'; $r=Get-Content -Raw -LiteralPath '%GLIDE_MOTION_EVIDENCE%\result.json' | ConvertFrom-Json; if($r.fallbackCount -ne 0 -or @($r.results).Count -ne 6 -or @($r.results | Where-Object {-not $_.passed}).Count -ne 0){exit 1}; $r.results | Format-Table name,passed"
exit /b %ERRORLEVEL%
