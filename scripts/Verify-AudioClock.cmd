@echo off
setlocal
rem Opt-in raw PCM diagnostics apply only to the disposable integration probes.
set "GLIDE_DEV_DIAGNOSTICS=1"
set "GLIDE_AUDIO_TRACE_PCM=1"
call "%~dp0Verify-Modes.cmd"
exit /b %ERRORLEVEL%
