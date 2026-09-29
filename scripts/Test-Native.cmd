@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_BUILD=%LOCALAPPDATA%\GlideDev\native-x64"
set "GLIDE_DEV_DIAGNOSTICS=1"
set "GLIDE_RUN_ID=run-%RANDOM%-%RANDOM%"
set "GLIDE_RUN=%GLIDE_BUILD%\captures\%GLIDE_RUN_ID%"
if exist "%GLIDE_RUN%" exit /b 2
if not exist "%GLIDE_ROOT%\.artifacts" mkdir "%GLIDE_ROOT%\.artifacts"
"%GLIDE_BUILD%\glide-capture-probe.exe" "%GLIDE_RUN%" > "%GLIDE_BUILD%\probe.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
if exist "%GLIDE_RUN%" copy /Y "%GLIDE_BUILD%\build-manifest.json" "%GLIDE_RUN%\build-manifest.json" >nul
echo Exit code: %GLIDE_RESULT% >> "%GLIDE_BUILD%\probe.log"
echo Evidence folder: native-captures\%GLIDE_RUN_ID% >> "%GLIDE_BUILD%\probe.log"
copy /Y "%GLIDE_BUILD%\probe.log" "%GLIDE_ROOT%\.artifacts\native-probe.log" >nul
if exist "%GLIDE_RUN%" robocopy "%GLIDE_RUN%" "%GLIDE_ROOT%\.artifacts\native-captures\%GLIDE_RUN_ID%" /E /NFL /NDL /NJH /NJS >nul
exit /b %GLIDE_RESULT%
