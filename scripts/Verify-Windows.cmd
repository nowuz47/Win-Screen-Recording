@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
if not exist "%GLIDE_ROOT%\.artifacts" mkdir "%GLIDE_ROOT%\.artifacts"
call "%~dp0Test-Core-Windows.cmd"
set "GLIDE_CORE=%ERRORLEVEL%"
call "%~dp0Build-Native.cmd"
set "GLIDE_NATIVE=%ERRORLEVEL%"
call "%~dp0Build-App.cmd"
set "GLIDE_APP=%ERRORLEVEL%"
set "GLIDE_PROBE=NOT_RUN"
set "GLIDE_INTEGRATION=NOT_RUN"
if not "%GLIDE_NATIVE%"=="0" goto report
call "%~dp0Test-Native.cmd"
set "GLIDE_PROBE=%ERRORLEVEL%"
call "%~dp0Test-Windows-Integration.cmd"
set "GLIDE_INTEGRATION=%ERRORLEVEL%"
:report
> "%GLIDE_ROOT%\.artifacts\windows-verification.txt" echo CoreTests=%GLIDE_CORE%
>> "%GLIDE_ROOT%\.artifacts\windows-verification.txt" echo NativeBuild=%GLIDE_NATIVE%
>> "%GLIDE_ROOT%\.artifacts\windows-verification.txt" echo WinUIBuild=%GLIDE_APP%
>> "%GLIDE_ROOT%\.artifacts\windows-verification.txt" echo InteractiveCaptureProbe=%GLIDE_PROBE%
>> "%GLIDE_ROOT%\.artifacts\windows-verification.txt" echo WindowsIntegration=%GLIDE_INTEGRATION%
type "%GLIDE_ROOT%\.artifacts\windows-verification.txt"
if not "%GLIDE_CORE%"=="0" exit /b 1
if not "%GLIDE_NATIVE%"=="0" exit /b 1
if not "%GLIDE_APP%"=="0" exit /b 1
if not "%GLIDE_PROBE%"=="0" exit /b 1
if not "%GLIDE_INTEGRATION%"=="0" exit /b 1
exit /b 0
