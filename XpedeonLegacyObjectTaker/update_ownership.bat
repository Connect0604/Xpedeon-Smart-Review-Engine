@echo off
if "%~1"=="" (
    cmd /k "%~f0" run
    exit /b
)

set "SKIPPAUSE=0"
set "DRYRUNFLAG="

if /i "%~2"=="auto" set "SKIPPAUSE=1"
if /i "%~2"=="dryrun" set "DRYRUNFLAG=-DryRun"
if /i "%~3"=="auto" set "SKIPPAUSE=1"
if /i "%~3"=="dryrun" set "DRYRUNFLAG=-DryRun"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update_ownership.ps1" %DRYRUNFLAG%
set "PSEXIT=%ERRORLEVEL%"

if "%SKIPPAUSE%"=="0" pause
exit /b %PSEXIT%
