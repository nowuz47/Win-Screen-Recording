@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_BUILD=%LOCALAPPDATA%\GlideDev\native-x64"
set "GLIDE_DEV_DIAGNOSTICS=1"
set "GLIDE_AUDIO_TRACE_PCM=1"
set "GLIDE_RUN_ID=run-%RANDOM%-%RANDOM%"
set "GLIDE_RUN=%GLIDE_BUILD%\audio-tests\%GLIDE_RUN_ID%"
if exist "%GLIDE_RUN%" exit /b 2
"%GLIDE_BUILD%\glide-capture-probe.exe" "%GLIDE_RUN%" --system-audio > "%GLIDE_BUILD%\audio-probe.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
if exist "%GLIDE_RUN%" copy /Y "%GLIDE_BUILD%\build-manifest.json" "%GLIDE_RUN%\build-manifest.json" >nul
echo Exit code: %GLIDE_RESULT% >> "%GLIDE_BUILD%\audio-probe.log"
echo Evidence folder: windows-audio\%GLIDE_RUN_ID% >> "%GLIDE_BUILD%\audio-probe.log"
copy /Y "%GLIDE_BUILD%\audio-probe.log" "%GLIDE_ROOT%\.artifacts\windows-audio-probe.log" >nul
if exist "%GLIDE_RUN%" robocopy "%GLIDE_RUN%" "%GLIDE_ROOT%\.artifacts\windows-audio\%GLIDE_RUN_ID%" /E /NFL /NDL /NJH /NJS >nul
exit /b %GLIDE_RESULT%
