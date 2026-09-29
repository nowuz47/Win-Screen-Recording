@echo off
setlocal
call "%~dp0Launch-Fixture.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Launch-App.cmd"
exit /b %ERRORLEVEL%
