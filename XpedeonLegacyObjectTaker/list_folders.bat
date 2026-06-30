@echo off
if "%~1"=="" (
    cmd /k "%~f0" run
    exit /b
)

set "SKIPPAUSE=0"
if /i "%~2"=="auto" set "SKIPPAUSE=1"

set "SRC=\\192.168.169.160\tdd\CodexGenerated"
set "OUT=%~dp0folder_list.txt"
set "EXISTING=%~dp0existing_forms.txt"

set "CONFIG=%~dp0db_config.txt"
if not exist "%CONFIG%" (
    echo Config file not found: %CONFIG%
    if "%SKIPPAUSE%"=="0" pause
    exit /b 1
)
for /f "usebackq tokens=1,* delims==" %%A in ("%CONFIG%") do set "%%A=%%B"

set "SQLCMDPASSWORD=%SQLPASS%"

setlocal enabledelayedexpansion

echo ============================================
echo STEP 1: Checking source folder
echo ============================================
if not exist "%SRC%" (
    echo Cannot access %SRC%
    if "%SKIPPAUSE%"=="0" pause
    exit /b 1
)
echo Source OK: %SRC%
echo.

echo ============================================
echo STEP 2: Listing folders from source
echo ============================================
dir /b /ad "%SRC%" > "%OUT%"

set "SKIP=%~dp0skipfolders.txt"
if exist "%SKIP%" (
    findstr /v /i /x /g:"%SKIP%" "%OUT%" > "%OUT%.tmp"
    move /y "%OUT%.tmp" "%OUT%" >nul
    echo Skipped folders ^(from skipfolders.txt^):
    type "%SKIP%"
    echo.
)

echo Folder list saved to %OUT%
type "%OUT%"
echo.

echo ============================================
echo STEP 3: Fetching existing FormName values from MIG.FORM
echo ============================================
sqlcmd -S %SQLSERVER% -U %SQLUSER% -d %SQLDB% -h -1 -W -Q "SET NOCOUNT ON; SELECT FormName FROM MIG.FORM" -o "%EXISTING%"
echo Existing records saved to %EXISTING%
type "%EXISTING%"
echo.

echo ============================================
echo STEP 4: Comparing folder list with existing records
echo ============================================

set "LOG=%~dp0insert_log.txt"
set "TMPOUT=%~dp0_insert_tmp.txt"
set "SQLFILE=%~dp0_insert_tmp.sql"
set "OOLOG=%~dp0ownership_log.txt"
set "TMPOUT2=%~dp0_insert_tmp2.txt"
set "SQLFILE2=%~dp0_ownership_tmp.sql"
set "SUMMARYFILE=%~dp0_ownership_summary.txt"
set "NEWFORMS=%~dp0new_forms.txt"
set "LOOKUPDB=xe34webdb"
echo ====== Run started %date% %time% ======>> "%LOG%"
break > "%NEWFORMS%"

echo Folder Name                          ^| Status        ^| Action
echo --------------------------------------+---------------+------------------
for /f "usebackq delims=" %%F in ("%OUT%") do call :checkrow "%%F"

goto :step4done

:checkrow
set "FOLDER=%~1"
set "PADDED=%FOLDER%                                    "
set "PADDED=%PADDED:~0,38%"
findstr /i /x /c:"%FOLDER%" "%EXISTING%" >nul
if errorlevel 1 goto :notfound
echo %PADDED%^| EXISTS        ^| Skip
goto :eof

:notfound
set "ESC=%FOLDER:'=''%"

sqlcmd -S %SQLSERVER% -U %SQLUSER% -d %SQLDB% -b -Q "SET NOCOUNT ON; BEGIN TRAN; INSERT INTO MIG.FORM (FormId, FormName, Status, HandoffDate, CreatedBy, CreatedDate) VALUES ((SELECT ISNULL(MAX(FormId),0)+1 FROM MIG.FORM), '%ESC%', 'M', CAST(GETDATE() AS DATE), 'BatchScript', GETDATE()); COMMIT TRAN;" >"%TMPOUT%" 2>&1
if errorlevel 1 goto :insertfail

echo %PADDED%^| NOT FOUND     ^| INSERTED
echo %date% %time% ^| %FOLDER% ^| INSERTED OK>> "%LOG%"
echo %FOLDER%>> "%NEWFORMS%"
goto :eof

:insertfail
echo %PADDED%^| NOT FOUND     ^| INSERT FAILED
echo %date% %time% ^| %FOLDER% ^| INSERT FAILED>> "%LOG%"
type "%TMPOUT%" >> "%LOG%"
echo.>> "%LOG%"
goto :eof

:step4done
if exist "%TMPOUT%" del "%TMPOUT%"
if exist "%SQLFILE%" del "%SQLFILE%"

echo ====== Run finished %date% %time% ======>> "%LOG%"
echo.

echo ============================================
echo STEP 5: Updating ProcessCode / StepCode from %LOOKUPDB%.dbo.PC_PROCESS_STEPS_DEFAULT
echo ============================================
sqlcmd -S %SQLSERVER% -U %SQLUSER% -d %SQLDB% -b -h -1 -W -Q "SET NOCOUNT ON; UPDATE f SET f.ProcessCode = p.PROCESS_CODE, f.StepCode = p.STEP_CODE, f.ModifiedBy = 'BatchScript', f.ModifiedDate = GETDATE() FROM MIG.FORM f JOIN %LOOKUPDB%.dbo.PC_PROCESS_STEPS_DEFAULT p ON p.STEP_NAME = f.FormName WHERE f.ProcessCode IS NULL; SELECT @@ROWCOUNT;" > "%TMPOUT%" 2>&1
if errorlevel 1 (
    echo Update FAILED
    echo %date% %time% ^| ProcessCode/StepCode UPDATE FAILED>> "%LOG%"
    type "%TMPOUT%" >> "%LOG%"
) else (
    set "UPDCOUNT="
    for /f "usebackq delims=" %%R in ("%TMPOUT%") do set "UPDCOUNT=%%R"
    echo Rows updated: !UPDCOUNT!
    echo %date% %time% ^| ProcessCode/StepCode UPDATE OK ^| RowsUpdated=!UPDCOUNT!>> "%LOG%"
)
if exist "%TMPOUT%" del "%TMPOUT%"
echo.

echo ============================================
echo STEP 6: Scanning form folders for object ownership
echo ============================================
echo ====== Run started %date% %time% ======>> "%OOLOG%"

for /f %%C in ('find /v /c "" ^< "%NEWFORMS%"') do set "NEWCOUNT=%%C"
if "%NEWCOUNT%"=="0" (
    echo No new forms this run - nothing to scan.
) else (
    for /f "usebackq delims=" %%F in ("%NEWFORMS%") do call :scanform "%%F"
)

if exist "%TMPOUT2%" del "%TMPOUT2%"
if exist "%NEWFORMS%" del "%NEWFORMS%"
echo ====== Run finished %date% %time% ======>> "%OOLOG%"
echo Ownership scan log: %OOLOG%
echo.

goto :step6done

:scanform
set "FOLDER=%~1"
set "FPATH=%SRC%\%FOLDER%"
set "ESCFORM=%FOLDER:'=''%"
set "OBJCOUNT=0"

if exist "%SQLFILE2%" del "%SQLFILE2%"
if exist "%SUMMARYFILE%" del "%SUMMARYFILE%"
echo SET NOCOUNT ON;>> "%SQLFILE2%"
echo DECLARE @FormId INT = (SELECT FormId FROM MIG.FORM WHERE FormName='%ESCFORM%');>> "%SQLFILE2%"
echo IF @FormId IS NOT NULL BEGIN>> "%SQLFILE2%"

if exist "%FPATH%\Tables" for %%G in ("%FPATH%\Tables\*.sql") do call :addobj "DATABASE" "TABLE" "%%~nG"
if exist "%FPATH%\Stored Procedures" for %%G in ("%FPATH%\Stored Procedures\*.sql") do call :addobj "DATABASE" "PROC" "%%~nG"
if exist "%FPATH%\BLL" for %%G in ("%FPATH%\BLL\*.cs") do call :addbll "%%~nG" "%%~nxG"
if exist "%FPATH%\DAL" for %%G in ("%FPATH%\DAL\*.cs") do call :adddal "%%~nG" "%%~nxG"
if exist "%FPATH%\Form" for %%G in ("%FPATH%\Form\*.cs") do call :addform "%%~nG" "%%~nxG"

echo END>> "%SQLFILE2%"

if "!OBJCOUNT!"=="0" (
    echo %FOLDER% ^| no objects found
    goto :scanformdone
)

sqlcmd -S %SQLSERVER% -U %SQLUSER% -d %SQLDB% -b -i "%SQLFILE2%" >"%TMPOUT2%" 2>&1
if errorlevel 1 (
    echo %FOLDER% ^| ownership batch ^| INSERT FAILED ^(!OBJCOUNT! objects^)
    echo %date% %time% ^| %FOLDER% ^| BATCH ^| !OBJCOUNT! objects ^| INSERT FAILED>> "%OOLOG%"
    type "%TMPOUT2%" >> "%OOLOG%"
) else (
    echo %FOLDER% ^| ownership batch OK ^(!OBJCOUNT! objects^)
    type "%SUMMARYFILE%" >> "%OOLOG%"
    echo %date% %time% ^| %FOLDER% ^| BATCH ^| !OBJCOUNT! objects ^| OK>> "%OOLOG%"
)

:scanformdone
if exist "%SQLFILE2%" del "%SQLFILE2%"
if exist "%SUMMARYFILE%" del "%SUMMARYFILE%"
goto :eof

:addbll
set "TYPE=BLL"
set "BASENAME=%~1"
if /i "%BASENAME:~0,1%"=="I" set "TYPE=BLL_INTF"
call :addobj "BLL" "%TYPE%" "%~2"
goto :eof

:adddal
set "TYPE=DAL"
set "BASENAME=%~1"
if /i "%BASENAME:~0,1%"=="I" set "TYPE=DAL_INTF"
call :addobj "DAL" "%TYPE%" "%~2"
goto :eof

:addform
set "BASENAME=%~1"
set "TYPE=UI_FORM"
if /i "%BASENAME:~-9%"==".Designer" set "TYPE=DESIGNER"
call :addobj "UI" "%TYPE%" "%~2"
goto :eof

:addobj
set "LAYER=%~1"
set "TYPE=%~2"
set "OBJNAME=%~3"

if /i "%TYPE%"=="TABLE" (
    if /i "%OBJNAME:~0,4%"=="dbo." set "OBJNAME=%OBJNAME:~4%"
)

set "ESCOBJ=%OBJNAME:'=''%"

set "OWNERSHIPCAT=RETIRING"
set "REMARKS=NULL"
if /i "%LAYER%"=="DATABASE" (
    set "OWNERSHIPCAT=LEGACY"
    set "REMARKS='Category update pending'"
)

echo INSERT INTO MIG.OBJECT_OWNERSHIP (Layer, ObjectName, ObjectType, FormId, OwnershipCategory, Remarks, CreatedBy, CreatedDate) SELECT '%LAYER%', '%ESCOBJ%', '%TYPE%', @FormId, '%OWNERSHIPCAT%', %REMARKS%, 'BatchScript', GETDATE() WHERE NOT EXISTS (SELECT 1 FROM MIG.OBJECT_OWNERSHIP WHERE ObjectName='%ESCOBJ%' AND ObjectType='%TYPE%');>> "%SQLFILE2%"
echo %date% %time% ^| %FOLDER% ^| %LAYER%/%TYPE% ^| %OBJNAME% ^| OK>> "%SUMMARYFILE%"
set /a OBJCOUNT+=1
goto :eof

:step6done

echo ============================================
echo STEP 7: Done. Insert log: %LOG%
echo ============================================
if "%SKIPPAUSE%"=="0" pause
