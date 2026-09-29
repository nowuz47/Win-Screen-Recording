@echo off
call "%~dp0Test-AudioPlayback.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Build-App.cmd"
exit /b %ERRORLEVEL%
