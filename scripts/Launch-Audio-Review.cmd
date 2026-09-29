@echo off
call "%~dp0Verify-AudioUX.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Stage-Audio-UI-Test.ps1"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Launch-App.cmd"
exit /b %ERRORLEVEL%
