@echo off
call "%~dp0Build-Native.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Audio.cmd"
exit /b %ERRORLEVEL%
