@echo off
if "%~1"=="" exit /b 2
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-Capture-Review.ps1" -SinceUtc "%~1"
exit /b %ERRORLEVEL%
