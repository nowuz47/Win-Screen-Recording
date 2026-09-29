@echo off
setlocal
call "%~dp0Build-App.cmd"
if not "%ERRORLEVEL%"=="0" exit /b 1
pushd "%LOCALAPPDATA%\GlideDev\workspace"
"%LOCALAPPDATA%\GlideDev\dotnet\dotnet.exe" run --project tests\Glide.UI.Tests -c Release > "%~dp0..\.artifacts\windows-ui-settings-tests.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
if exist .artifacts\ui-settings-tests.json copy /Y .artifacts\ui-settings-tests.json "%~dp0..\.artifacts\windows-ui-settings-tests.json" >nul
popd
if not "%GLIDE_RESULT%"=="0" exit /b %GLIDE_RESULT%
call "%~dp0Test-UI-Motion.cmd"
exit /b %ERRORLEVEL%
