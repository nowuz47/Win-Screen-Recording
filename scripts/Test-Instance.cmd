@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_WORK=%LOCALAPPDATA%\GlideDev\workspace"
set "GLIDE_OUT=%LOCALAPPDATA%\GlideDev\instance-x64"
set "GLIDE_RUN_ID=run-%RANDOM%-%RANDOM%"
set "GLIDE_EVIDENCE=%LOCALAPPDATA%\GlideDev\instance-results\%GLIDE_RUN_ID%"
if exist "%GLIDE_EVIDENCE%" exit /b 2
robocopy "%GLIDE_ROOT%" "%GLIDE_WORK%" /E /XD .git .tools .artifacts bin obj .vs /NFL /NDL /NJH /NJS >nul
if errorlevel 8 exit /b 1
pushd "%GLIDE_WORK%"
"%LOCALAPPDATA%\GlideDev\dotnet\dotnet.exe" publish tests\Glide.Instance.Tests -c Release -r win-x64 -o "%GLIDE_OUT%" > "%LOCALAPPDATA%\GlideDev\instance-build.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\instance-build.log" "%GLIDE_ROOT%\.artifacts\windows-instance-build.log" >nul
if not "%GLIDE_RESULT%"=="0" goto done
"%GLIDE_OUT%\Glide.Instance.Tests.exe" "%GLIDE_EVIDENCE%" > "%LOCALAPPDATA%\GlideDev\instance-tests.log" 2>&1
set "GLIDE_RESULT=%ERRORLEVEL%"
copy /Y "%LOCALAPPDATA%\GlideDev\instance-tests.log" "%GLIDE_ROOT%\.artifacts\windows-instance-tests.log" >nul
if exist "%GLIDE_EVIDENCE%" powershell -NoProfile -Command "Get-FileHash -Algorithm SHA256 -Path '%GLIDE_OUT%\Glide.Instance.Tests.exe','%GLIDE_OUT%\Glide.Instance.Tests.dll','%GLIDE_WORK%\src\Glide.App\SingleInstanceGate.cs','%GLIDE_WORK%\tests\Glide.Instance.Tests\Program.cs' | Select-Object Path,Hash | ConvertTo-Json" > "%GLIDE_EVIDENCE%\build-manifest.json"
if exist "%GLIDE_EVIDENCE%" robocopy "%GLIDE_EVIDENCE%" "%GLIDE_ROOT%\.artifacts\windows-instance\%GLIDE_RUN_ID%" /E /NFL /NDL /NJH /NJS >nul
:done
popd
exit /b %GLIDE_RESULT%
