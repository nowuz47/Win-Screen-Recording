@echo off
setlocal
set "GLIDE_EVIDENCE=%~dp0..\.artifacts\ui-motion-app\run-%RANDOM%-%RANDOM%"
mkdir "%GLIDE_EVIDENCE%"
if exist "%LOCALAPPDATA%\Glide\diagnostics" robocopy "%LOCALAPPDATA%\Glide\diagnostics" "%GLIDE_EVIDENCE%\diagnostics" /E /NFL /NDL /NJH /NJS >nul
if exist "%LOCALAPPDATA%\Glide\preferences.json" copy /Y "%LOCALAPPDATA%\Glide\preferences.json" "%GLIDE_EVIDENCE%\preferences.json" >nul
set /p GLIDE_APP=<"%LOCALAPPDATA%\GlideDev\app-current.txt"
powershell -NoProfile -Command "Get-FileHash -Algorithm SHA256 -Path '%GLIDE_APP%\Glide.App.exe','%GLIDE_APP%\Glide.App.dll','%GLIDE_APP%\Glide.Capture.dll' | Select-Object Path,Hash | ConvertTo-Json" > "%GLIDE_EVIDENCE%\build-manifest.json"
exit /b %ERRORLEVEL%
