@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_WORK=%LOCALAPPDATA%\GlideDev\workspace"
set "GLIDE_OUT=%LOCALAPPDATA%\GlideDev\app-x64-build-%RANDOM%-%RANDOM%"
if exist "%GLIDE_OUT%" exit /b 2
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
if not exist "%GLIDE_ROOT%\.artifacts" mkdir "%GLIDE_ROOT%\.artifacts"
robocopy "%GLIDE_ROOT%" "%GLIDE_WORK%" /E /XD .git .tools .artifacts bin obj .vs /NFL /NDL /NJH /NJS >nul
if errorlevel 8 exit /b 1
pushd "%GLIDE_WORK%"
"%LOCALAPPDATA%\GlideDev\dotnet\dotnet.exe" publish src\Glide.App -c Release -r win-x64 -p:Platform=x64 -o "%GLIDE_OUT%" > "%LOCALAPPDATA%\GlideDev\app-build.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\app-build.log" "%GLIDE_ROOT%\.artifacts\windows-app-build.log" >nul
if not "%GLIDE_RESULT%"=="0" goto done
copy /Y "%LOCALAPPDATA%\GlideDev\native-x64\Glide.Capture.dll" "%GLIDE_OUT%\Glide.Capture.dll" >nul
if not "%ERRORLEVEL%"=="0" goto copyfailed
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Write-App-Manifest.ps1" -AppDirectory "%GLIDE_OUT%"
if not "%ERRORLEVEL%"=="0" goto copyfailed
> "%LOCALAPPDATA%\GlideDev\app-current.txt" echo %GLIDE_OUT%
> "%GLIDE_ROOT%\.artifacts\windows-app-location.txt" echo %GLIDE_OUT%
goto done
:copyfailed
set "GLIDE_RESULT=1"
:done
popd
exit /b %GLIDE_RESULT%
