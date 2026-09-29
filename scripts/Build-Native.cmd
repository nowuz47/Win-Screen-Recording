@echo off
setlocal
set "GLIDE_ROOT=%~dp0.."
set "GLIDE_BUILD=%LOCALAPPDATA%\GlideDev\native-x64"
if not exist "%GLIDE_BUILD%" mkdir "%GLIDE_BUILD%"
set "GLIDE_SOURCES=%GLIDE_BUILD%\source"
if not exist "%GLIDE_SOURCES%" mkdir "%GLIDE_SOURCES%"
copy /Y "%GLIDE_ROOT%\native\Glide.Capture\glide_capture.cpp" "%GLIDE_SOURCES%\glide_capture.cpp" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
copy /Y "%GLIDE_ROOT%\native\Glide.Capture\glide_capture.h" "%GLIDE_SOURCES%\glide_capture.h" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
copy /Y "%GLIDE_ROOT%\native\Glide.Capture\*.h" "%GLIDE_SOURCES%\" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
copy /Y "%GLIDE_ROOT%\native\Glide.Capture\render_pipeline.inl" "%GLIDE_SOURCES%\render_pipeline.inl" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
copy /Y "%GLIDE_ROOT%\native\Glide.Capture\*.inl" "%GLIDE_SOURCES%\" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
copy /Y "%GLIDE_ROOT%\native\Glide.Capture\capture_probe.cpp" "%GLIDE_SOURCES%\capture_probe.cpp" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
copy /Y "%GLIDE_ROOT%\tests\native\recording_timeline_test.cpp" "%GLIDE_SOURCES%\recording_timeline_test.cpp" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
copy /Y "%GLIDE_ROOT%\tests\native\audio_queue_test.cpp" "%GLIDE_SOURCES%\audio_queue_test.cpp" >nul
if not "%ERRORLEVEL%"=="0" exit /b 1
if not exist "%GLIDE_ROOT%\.artifacts" mkdir "%GLIDE_ROOT%\.artifacts"
set "GLIDE_HOST=x64"
if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "GLIDE_HOST=arm64"
set "GLIDE_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%GLIDE_VSWHERE%" exit /b 2
for /f "usebackq tokens=*" %%I in (`"%GLIDE_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "GLIDE_VS=%%I"
if not defined GLIDE_VS exit /b 2
call "%GLIDE_VS%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=%GLIDE_HOST% > "%GLIDE_BUILD%\vs-env.log" 2>&1
if not "%ERRORLEVEL%"=="0" goto envfailed
pushd "%GLIDE_BUILD%"
cl /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /utf-8 /DUNICODE /D_UNICODE /LD "%GLIDE_SOURCES%\glide_capture.cpp" /Fe:Glide.Capture.dll /link windowsapp.lib d3d11.lib d3dcompiler.lib dxgi.lib dwmapi.lib mfplat.lib mfreadwrite.lib mfuuid.lib ole32.lib user32.lib gdi32.lib avrt.lib > "%GLIDE_BUILD%\compile.log" 2>&1
if not "%ERRORLEVEL%"=="0" goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT /utf-8 /DUNICODE /D_UNICODE "%GLIDE_SOURCES%\capture_probe.cpp" Glide.Capture.lib /Fe:glide-capture-probe.exe /link windowsapp.lib ole32.lib user32.lib gdi32.lib dwmapi.lib avrt.lib >> "%GLIDE_BUILD%\compile.log" 2>&1
if not "%ERRORLEVEL%"=="0" goto failed
echo PASS native build >> "%GLIDE_BUILD%\compile.log"
cl /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT "%GLIDE_SOURCES%\recording_timeline_test.cpp" /Fe:recording-timeline-test.exe >> "%GLIDE_BUILD%\compile.log" 2>&1
if not "%ERRORLEVEL%"=="0" goto failed
recording-timeline-test.exe >> "%GLIDE_BUILD%\compile.log" 2>&1
if not "%ERRORLEVEL%"=="0" goto failed
cl /nologo /std:c++20 /EHsc /W4 /WX /O2 /MT "%GLIDE_SOURCES%\audio_queue_test.cpp" /Fe:audio-queue-test.exe >> "%GLIDE_BUILD%\compile.log" 2>&1
if not "%ERRORLEVEL%"=="0" goto failed
audio-queue-test.exe >> "%GLIDE_BUILD%\compile.log" 2>&1
if not "%ERRORLEVEL%"=="0" goto failed
powershell -NoProfile -Command "Get-FileHash -Algorithm SHA256 -Path '%GLIDE_BUILD%\Glide.Capture.dll','%GLIDE_BUILD%\glide-capture-probe.exe','%GLIDE_BUILD%\recording-timeline-test.exe','%GLIDE_BUILD%\audio-queue-test.exe','%GLIDE_SOURCES%\*.cpp','%GLIDE_SOURCES%\*.h','%GLIDE_SOURCES%\*.inl' | Select-Object Path,Hash | ConvertTo-Json" > "%GLIDE_BUILD%\build-manifest.json"
if not "%ERRORLEVEL%"=="0" goto failed
copy /Y "%GLIDE_BUILD%\build-manifest.json" "%GLIDE_ROOT%\.artifacts\native-build-manifest.json" >nul
copy /Y "%GLIDE_BUILD%\compile.log" "%GLIDE_ROOT%\.artifacts\native-build.log" >nul
popd
exit /b 0
:failed
echo FAIL native build >> "%GLIDE_BUILD%\compile.log"
copy /Y "%GLIDE_BUILD%\compile.log" "%GLIDE_ROOT%\.artifacts\native-build.log" >nul
popd
exit /b 1
:envfailed
copy /Y "%GLIDE_BUILD%\vs-env.log" "%GLIDE_ROOT%\.artifacts\native-build.log" >nul
exit /b 1
