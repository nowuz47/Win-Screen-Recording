@echo off
call "%~dp0Verify-Render.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Core-Windows.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Build-App.cmd"
exit /b %ERRORLEVEL%
