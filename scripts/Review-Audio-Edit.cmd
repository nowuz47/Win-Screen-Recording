@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-Audio-UI-Evidence.ps1"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Build-App.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Launch-App.cmd"
exit /b %ERRORLEVEL%
