@echo off
call "%~dp0Build-Native.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Core-Windows.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Instance.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Windows-Integration.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Render.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-AudioPlayback.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Test-Presentation.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
call "%~dp0Build-App.cmd"
exit /b %ERRORLEVEL%
