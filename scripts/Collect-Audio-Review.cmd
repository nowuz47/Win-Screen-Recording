@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-Audio-UI-Evidence.ps1"
if not "%ERRORLEVEL%"=="0" exit /b %errorlevel%
call "%~dp0Launch-App.cmd"
