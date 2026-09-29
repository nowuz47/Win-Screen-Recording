@echo off
setlocal
set "GLIDE_PROBE=%LOCALAPPDATA%\GlideDev\native-x64\glide-capture-probe.exe"
if not exist "%GLIDE_PROBE%" exit /b 2
start "" "%GLIDE_PROBE%" --fixture-only
