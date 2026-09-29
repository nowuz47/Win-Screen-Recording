@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-Verification-ExitCodes.ps1"
exit /b %ERRORLEVEL%
