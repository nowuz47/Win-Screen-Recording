@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_SDK=%LOCALAPPDATA%\GlideDev\dotnet"
if not exist "%GLIDE_ROOT%\.artifacts" mkdir "%GLIDE_ROOT%\.artifacts"
powershell.exe -NoProfile -Command "$ErrorActionPreference='Stop'; $zip=Join-Path $env:GLIDE_ROOT '.tools\dotnet-sdk-win-arm64.zip'; $expected='8272eaab6f06ad658b1976e19d88beed287a601f968b71d5c26b75d10587cf087665c599d2e136a911002f955c97f59aa8692581bbf6b8e7af5f82604c810256'; if((Get-FileHash -Algorithm SHA512 $zip).Hash -ne $expected){throw 'SDK hash mismatch'}; if(-not(Test-Path (Join-Path $env:GLIDE_SDK 'dotnet.exe'))){Expand-Archive -Path $zip -DestinationPath $env:GLIDE_SDK}; & (Join-Path $env:GLIDE_SDK 'dotnet.exe') --info | Out-File -Encoding utf8 (Join-Path $env:GLIDE_ROOT '.artifacts\windows-dotnet.txt')"
if not "%ERRORLEVEL%"=="0" exit /b 1
echo Local development SDK is ready. No system PATH changes made.
