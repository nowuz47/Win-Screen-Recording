@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Inspect-Glide-Processes.ps1"
exit /b %ERRORLEVEL%
