@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-AppActivation.ps1" -Phase "%~1"
exit /b %ERRORLEVEL%
