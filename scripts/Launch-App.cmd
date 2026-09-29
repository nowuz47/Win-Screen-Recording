@echo off
setlocal
set "GLIDE_DEV_DIAGNOSTICS=1"
if not exist "%LOCALAPPDATA%\GlideDev\app-current.txt" exit /b 2
set /p GLIDE_APP=<"%LOCALAPPDATA%\GlideDev\app-current.txt"
start "" "%GLIDE_APP%\Glide.App.exe"
