@echo off
cd /d "%~dp0"
echo Building... log: %~dp0build.log
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" > "%~dp0build.log" 2>&1
echo Exitcode %ERRORLEVEL% >> "%~dp0build.log"
type "%~dp0build.log"
pause
