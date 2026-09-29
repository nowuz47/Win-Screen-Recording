@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_WORK=%LOCALAPPDATA%\GlideDev\workspace"
set "GLIDE_OUT=%LOCALAPPDATA%\GlideDev\integration-x64"
set "GLIDE_RUN_ID=run-%RANDOM%-%RANDOM%"
set "GLIDE_EVIDENCE=%LOCALAPPDATA%\GlideDev\integration-results\%GLIDE_RUN_ID%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
if exist "%GLIDE_EVIDENCE%" exit /b 2
robocopy "%GLIDE_ROOT%" "%GLIDE_WORK%" /E /XD .git .tools .artifacts bin obj .vs /NFL /NDL /NJH /NJS >nul
if errorlevel 8 exit /b 1
pushd "%GLIDE_WORK%"
"%LOCALAPPDATA%\GlideDev\dotnet\dotnet.exe" publish tests\Glide.Windows.Tests -c Release -r win-x64 -o "%GLIDE_OUT%" > "%LOCALAPPDATA%\GlideDev\integration-build.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\integration-build.log" "%GLIDE_ROOT%\.artifacts\windows-integration-build.log" >nul
if not "%GLIDE_RESULT%"=="0" goto done
copy /Y "%LOCALAPPDATA%\GlideDev\native-x64\Glide.Capture.dll" "%GLIDE_OUT%\Glide.Capture.dll" >nul
if not "%ERRORLEVEL%"=="0" goto failed
"%GLIDE_OUT%\Glide.Windows.Tests.exe" "%LOCALAPPDATA%\GlideDev\native-x64\glide-capture-probe.exe" "%GLIDE_EVIDENCE%" > "%LOCALAPPDATA%\GlideDev\integration-tests.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\integration-tests.log" "%GLIDE_ROOT%\.artifacts\windows-integration-tests.log" >nul
if exist "%GLIDE_EVIDENCE%" copy /Y "%LOCALAPPDATA%\GlideDev\native-x64\build-manifest.json" "%GLIDE_EVIDENCE%\build-manifest.json" >nul
if exist "%GLIDE_EVIDENCE%" robocopy "%GLIDE_EVIDENCE%" "%GLIDE_ROOT%\.artifacts\windows-integration\%GLIDE_RUN_ID%" /E /NFL /NDL /NJH /NJS >nul
goto done
:failed
set "GLIDE_RESULT=1"
:done
popd
exit /b %GLIDE_RESULT%
