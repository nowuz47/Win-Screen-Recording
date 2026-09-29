@echo off
call "%~dp0Test-Instance.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Build-App.cmd"
exit /b %ERRORLEVEL%
