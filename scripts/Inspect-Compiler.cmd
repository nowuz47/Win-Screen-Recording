@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
powershell.exe -NoProfile -Command "$vs='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools'; & { Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -ErrorAction SilentlyContinue; Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Recurse -Filter cl.exe -ErrorAction SilentlyContinue | Select-Object FullName | Format-List; Get-Content (Join-Path $env:LOCALAPPDATA 'GlideDev\native-arm64\vs-env.log') } | Out-File -Encoding utf8 (Join-Path $env:GLIDE_ROOT '.artifacts\compiler-paths.txt')"
