@echo off
setlocal
set "GLIDE_EVIDENCE=%~dp0..\.artifacts\ux-verification"
if not exist "%GLIDE_EVIDENCE%" mkdir "%GLIDE_EVIDENCE%"
if not exist "%GLIDE_EVIDENCE%" exit /b 2
set "GLIDE_RUN_LOG=%GLIDE_EVIDENCE%\run-%RANDOM%-%RANDOM%.log"
if exist "%GLIDE_RUN_LOG%" exit /b 2
set "GLIDE_STATUS=%GLIDE_EVIDENCE%\latest-status.txt"
> "%GLIDE_RUN_LOG%" echo %DATE% %TIME% START UX verification
call :stage native "%~dp0Build-Native.cmd"
if errorlevel 1 goto failed
call :stage presentation "%~dp0Test-Presentation.cmd"
if errorlevel 1 goto failed
call :stage app "%~dp0Build-App.cmd"
if errorlevel 1 goto failed
> "%GLIDE_STATUS%" echo %DATE% %TIME% PASS log=%GLIDE_RUN_LOG%
echo PASS UX verification. Log: %GLIDE_RUN_LOG%
exit /b 0

:failed
> "%GLIDE_STATUS%" echo %DATE% %TIME% FAIL stage=%GLIDE_STAGE% exit=%GLIDE_STAGE_EXIT% log=%GLIDE_RUN_LOG%
type "%GLIDE_RUN_LOG%"
exit /b %GLIDE_STAGE_EXIT%

:stage
set "GLIDE_STAGE=%~1"
> "%GLIDE_STATUS%" echo %DATE% %TIME% RUNNING stage=%GLIDE_STAGE% log=%GLIDE_RUN_LOG%
echo Running %GLIDE_STAGE%...
>> "%GLIDE_RUN_LOG%" echo %DATE% %TIME% START %GLIDE_STAGE%
call "%~2" >> "%GLIDE_RUN_LOG%" 2>&1
set "GLIDE_STAGE_EXIT=%ERRORLEVEL%"
>> "%GLIDE_RUN_LOG%" echo %DATE% %TIME% END %GLIDE_STAGE% exit=%GLIDE_STAGE_EXIT%
exit /b %GLIDE_STAGE_EXIT%
