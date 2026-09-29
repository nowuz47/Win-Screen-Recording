@echo off
setlocal
call "%~dp0Build-Native.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Windows-Integration.cmd"
exit /b %ERRORLEVEL%
