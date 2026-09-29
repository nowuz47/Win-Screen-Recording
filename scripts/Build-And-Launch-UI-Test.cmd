@echo off
call "%~dp0Build-App.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Launch-UI-Test.cmd"
exit /b %ERRORLEVEL%
