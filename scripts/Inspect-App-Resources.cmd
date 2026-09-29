@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set /p GLIDE_APP=<"%LOCALAPPDATA%\GlideDev\app-current.txt"
dir /s /b "%GLIDE_APP%\*.pri" > "%GLIDE_ROOT%\.artifacts\windows-app-resources.txt" 2>&1
dir /s /b "%GLIDE_APP%\*.xbf" >> "%GLIDE_ROOT%\.artifacts\windows-app-resources.txt" 2>&1
dir /b "%LOCALAPPDATA%\GlideDev\workspace\src\Glide.App\obj\x64\Release\net10.0-windows10.0.26100.0\win-x64" >> "%GLIDE_ROOT%\.artifacts\windows-app-resources.txt" 2>&1
type "%GLIDE_ROOT%\.artifacts\windows-app-resources.txt"
