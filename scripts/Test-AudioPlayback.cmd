@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_WORK=%LOCALAPPDATA%\GlideDev\workspace"
set "GLIDE_OUT=%LOCALAPPDATA%\GlideDev\audio-playback-tests-x64"
set "GLIDE_RUN_ID=run-%RANDOM%-%RANDOM%"
set "GLIDE_EVIDENCE=%LOCALAPPDATA%\GlideDev\audio-playback-results\%GLIDE_RUN_ID%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
robocopy "%GLIDE_ROOT%" "%GLIDE_WORK%" /E /XD .git .tools .artifacts bin obj .vs /NFL /NDL /NJH /NJS >nul
if errorlevel 8 exit /b 1
pushd "%GLIDE_WORK%"
"%LOCALAPPDATA%\GlideDev\dotnet\dotnet.exe" publish tests\Glide.AudioPlayback.Tests -c Release -r win-x64 -o "%GLIDE_OUT%" > "%LOCALAPPDATA%\GlideDev\audio-playback-build.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\audio-playback-build.log" "%GLIDE_ROOT%\.artifacts\windows-audio-playback-build.log" >nul
if not "%GLIDE_RESULT%"=="0" goto done
copy /Y "%LOCALAPPDATA%\GlideDev\native-x64\Glide.Capture.dll" "%GLIDE_OUT%\Glide.Capture.dll" >nul
if not "%ERRORLEVEL%"=="0" goto failed
"%GLIDE_OUT%\Glide.AudioPlayback.Tests.exe" "%LOCALAPPDATA%\GlideDev\render-results" "%GLIDE_EVIDENCE%" > "%LOCALAPPDATA%\GlideDev\audio-playback-tests.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\audio-playback-tests.log" "%GLIDE_ROOT%\.artifacts\windows-audio-playback-tests.log" >nul
if exist "%GLIDE_EVIDENCE%" robocopy "%GLIDE_EVIDENCE%" "%GLIDE_ROOT%\.artifacts\windows-audio-playback\%GLIDE_RUN_ID%" /E /NFL /NDL /NJH /NJS >nul
goto done
:failed
set "GLIDE_RESULT=1"
:done
popd
exit /b %GLIDE_RESULT%
