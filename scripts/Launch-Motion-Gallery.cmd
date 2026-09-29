@echo off
setlocal
set "GLIDE_DEV_DIAGNOSTICS=1"
set /p GLIDE_APP=<"%LOCALAPPDATA%\GlideDev\app-current.txt"
start "" "%GLIDE_APP%\Glide.App.exe" --motion-gallery
