@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
if not exist "%GLIDE_ROOT%\.artifacts" mkdir "%GLIDE_ROOT%\.artifacts"
powershell.exe -NoProfile -Command "$ErrorActionPreference='Continue'; $out=Join-Path $env:GLIDE_ROOT '.artifacts\windows-environment.txt'; & { Get-Date -Format o; Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,OSArchitecture | Format-List; Get-CimInstance Win32_Processor | Select-Object Name,Architecture,NumberOfLogicalProcessors | Format-List; Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion | Format-List; Get-Command dotnet,cmake,cl,git,winget -ErrorAction SilentlyContinue | Select-Object Name,Source | Format-List; $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'; if(Test-Path $vswhere){ & $vswhere -all -products * -format json }; Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\Include' -ErrorAction SilentlyContinue | Select-Object Name | Format-List; Get-ChildItem 'C:\Program Files\dotnet\sdk' -ErrorAction SilentlyContinue | Select-Object Name | Format-List } | Out-File -Encoding utf8 $out; Get-Content $out"
echo Inspection complete.
