@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_WORK=%LOCALAPPDATA%\GlideDev\workspace"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
robocopy "%GLIDE_ROOT%" "%GLIDE_WORK%" /E /XD .git .tools .artifacts bin obj .vs /NFL /NDL /NJH /NJS >nul
if errorlevel 8 exit /b 1
pushd "%GLIDE_WORK%"
"%LOCALAPPDATA%\GlideDev\dotnet\dotnet.exe" run --project tests\Glide.Core.Tests -c Release -- all > "%LOCALAPPDATA%\GlideDev\core-tests.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\core-tests.log" "%GLIDE_ROOT%\.artifacts\windows-core-tests.log" >nul
if exist .artifacts\core-tests-all.json copy /Y .artifacts\core-tests-all.json "%GLIDE_ROOT%\.artifacts\windows-core-tests.json" >nul
popd
exit /b %GLIDE_RESULT%
