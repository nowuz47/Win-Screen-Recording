@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_EVIDENCE=%GLIDE_ROOT%\.artifacts\app-ui\run-%RANDOM%-%RANDOM%"
if exist "%GLIDE_EVIDENCE%" exit /b 2
mkdir "%GLIDE_EVIDENCE%"
if not "%ERRORLEVEL%"=="0" exit /b 1
if exist "%LOCALAPPDATA%\Glide\projects" robocopy "%LOCALAPPDATA%\Glide\projects" "%GLIDE_EVIDENCE%\projects" /E /NFL /NDL /NJH /NJS >nul
if errorlevel 8 exit /b 1
if exist "%LOCALAPPDATA%\Glide\diagnostics\app-errors.log" copy /Y "%LOCALAPPDATA%\Glide\diagnostics\app-errors.log" "%GLIDE_EVIDENCE%\app-errors.log" >nul
if exist "%LOCALAPPDATA%\Glide\diagnostics\toolbar-actions.log" copy /Y "%LOCALAPPDATA%\Glide\diagnostics\toolbar-actions.log" "%GLIDE_EVIDENCE%\toolbar-actions.log" >nul
if exist "%LOCALAPPDATA%\Glide\preferences.json" copy /Y "%LOCALAPPDATA%\Glide\preferences.json" "%GLIDE_EVIDENCE%\preferences.json" >nul
set /p GLIDE_APP=<"%LOCALAPPDATA%\GlideDev\app-current.txt"
powershell -NoProfile -Command "Get-FileHash -Algorithm SHA256 -Path '%GLIDE_APP%\Glide.App.exe','%GLIDE_APP%\Glide.App.dll','%GLIDE_APP%\Glide.Core.dll','%GLIDE_APP%\Glide.Media.dll','%GLIDE_APP%\Glide.Capture.dll' | Select-Object Path,Hash | ConvertTo-Json" > "%GLIDE_EVIDENCE%\build-manifest.json"
exit /b %ERRORLEVEL%
