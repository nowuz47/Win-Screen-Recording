@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
if not exist "%GLIDE_ROOT%\.artifacts" mkdir "%GLIDE_ROOT%\.artifacts"
if exist "%LOCALAPPDATA%\Glide\diagnostics\app-errors.log" copy /Y "%LOCALAPPDATA%\Glide\diagnostics\app-errors.log" "%GLIDE_ROOT%\.artifacts\windows-app-errors.log" >nul
powershell -NoProfile -Command "Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-15)} -ErrorAction SilentlyContinue | Where-Object { $_.Message -like '*Glide.App*' } | Select-Object TimeCreated,Id,ProviderName,Message | Format-List" > "%GLIDE_ROOT%\.artifacts\windows-app-launch.txt" 2>&1
type "%GLIDE_ROOT%\.artifacts\windows-app-launch.txt"
