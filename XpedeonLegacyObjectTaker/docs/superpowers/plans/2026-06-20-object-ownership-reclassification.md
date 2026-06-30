# Object Ownership Reclassification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `update_ownership.bat` / `update_ownership.ps1` that reclassifies `MIG.OBJECT_OWNERSHIP` rows (Layer=DATABASE, currently LEGACY) to `BLAZOR_OWNED` / `SHARED` / `RETIRED` based on whether the object is referenced in the Blazor codebase (`D:\XpedeonSaas`).

**Architecture:** Pure classification/parsing logic lives in a PowerShell module (`OwnershipLib.psm1`) so it can be tested without a database or filesystem. An orchestration script (`update_ownership.ps1`) handles all I/O (sqlcmd queries, filesystem scan, logging, writeback) and calls into the module. A thin `.bat` wrapper matches the existing `list_folders.bat` double-click UX.

**Tech Stack:** PowerShell 5.1 (Windows built-in), `sqlcmd` CLI, batch script wrapper. No git in this repo — no commit steps in this plan.

---

### Task 1: Pure classification/parsing module

**Files:**
- Create: `D:\WORK\AI Projects\XpedeonLegacyObjectTaker\OwnershipLib.psm1`

- [ ] **Step 1: Write the module**

```powershell
function Get-DbConfig {
    param([string]$ConfigPath)
    if (-not (Test-Path $ConfigPath)) {
        throw "Config file not found: $ConfigPath"
    }
    $config = @{}
    foreach ($line in Get-Content $ConfigPath) {
        if ($line -match '^([^=]+)=(.*)$') {
            $config[$matches[1]] = $matches[2]
        }
    }
    return $config
}

function Get-NewCategory {
    param([int]$MatchCount)
    if ($MatchCount -eq 0) { return 'RETIRED' }
    elseif ($MatchCount -eq 1) { return 'BLAZOR_OWNED' }
    else { return 'SHARED' }
}

function Get-UsageCount {
    param(
        [string]$ObjectName,
        [string]$ObjectType,
        [string[]]$FileContents
    )
    if ($ObjectType -eq 'TABLE') {
        $pattern = '\[Table\("' + [regex]::Escape($ObjectName) + '"\)\]'
    } else {
        $pattern = '\b' + [regex]::Escape($ObjectName) + '\b'
    }
    $count = 0
    foreach ($content in $FileContents) {
        if ([string]::IsNullOrEmpty($content)) { continue }
        $count += [regex]::Matches($content, $pattern, 'IgnoreCase').Count
    }
    return $count
}

function Parse-CandidateOutput {
    param([string[]]$Lines)
    $results = @()
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '\|'
        if ($parts.Count -lt 4) { continue }
        $results += [PSCustomObject]@{
            Layer      = $parts[0].Trim()
            ObjectName = $parts[1].Trim()
            ObjectType = $parts[2].Trim()
            FormId     = [int]($parts[3].Trim())
        }
    }
    return $results
}

Export-ModuleMember -Function Get-DbConfig, Get-NewCategory, Get-UsageCount, Parse-CandidateOutput
```

- [ ] **Step 2: Verify the module imports cleanly**

Run: `powershell -NoProfile -Command "Import-Module 'D:\WORK\AI Projects\XpedeonLegacyObjectTaker\OwnershipLib.psm1' -Force; Get-Command -Module OwnershipLib"`
Expected: lists `Get-DbConfig`, `Get-NewCategory`, `Get-UsageCount`, `Parse-CandidateOutput` with no errors.

---

### Task 2: Tests for the module

**Files:**
- Create: `D:\WORK\AI Projects\XpedeonLegacyObjectTaker\tests\Test-OwnershipLib.ps1`

- [ ] **Step 1: Write the failing test script**

```powershell
Import-Module "$PSScriptRoot\..\OwnershipLib.psm1" -Force

$script:failures = 0

function Assert-Equal {
    param($Actual, $Expected, [string]$TestName)
    if ($Actual -ne $Expected) {
        Write-Host "FAIL: $TestName - expected '$Expected' got '$Actual'"
        $script:failures++
    } else {
        Write-Host "PASS: $TestName"
    }
}

# Get-NewCategory
Assert-Equal (Get-NewCategory -MatchCount 0) 'RETIRED' 'zero hits -> RETIRED'
Assert-Equal (Get-NewCategory -MatchCount 1) 'BLAZOR_OWNED' 'one hit -> BLAZOR_OWNED'
Assert-Equal (Get-NewCategory -MatchCount 5) 'SHARED' 'multi hits -> SHARED'

# Get-UsageCount - TABLE
$tableContent = '[Table("GTM_TAN_MASTER")]'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @($tableContent)) 1 'table match counts 1'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @('no match here')) 0 'table no match counts 0'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @($tableContent, $tableContent)) 2 'table match across 2 files counts 2'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @('GTM_TAN_MASTER appears as plain text only')) 0 'table plain-text mention without attribute does not count'

# Get-UsageCount - PROC
$procContent = 'new SqlCommand("SPN_GTM_GET_TDS_TAN_MAP", conn)'
Assert-Equal (Get-UsageCount -ObjectName 'SPN_GTM_GET_TDS_TAN_MAP' -ObjectType 'PROC' -FileContents @($procContent)) 1 'proc match counts 1'
Assert-Equal (Get-UsageCount -ObjectName 'SPN_OTHER' -ObjectType 'PROC' -FileContents @($procContent)) 0 'proc no match counts 0'

# Parse-CandidateOutput
$lines = @('DATABASE|GTM_TAN_MASTER|TABLE|17', 'DATABASE|SPN_GTM_GET_TDS_TAN_MAP|PROC|17', '')
$parsed = Parse-CandidateOutput -Lines $lines
Assert-Equal $parsed.Count 2 'parses 2 candidate rows, skips blank line'
Assert-Equal $parsed[0].ObjectName 'GTM_TAN_MASTER' 'parses ObjectName column'
Assert-Equal $parsed[0].ObjectType 'TABLE' 'parses ObjectType column'
Assert-Equal $parsed[0].FormId 17 'parses FormId column as int'

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) test(s) FAILED"
    exit 1
} else {
    Write-Host "All tests passed"
    exit 0
}
```

- [ ] **Step 2: Run it and verify it passes**

Run: `powershell -NoProfile -File "D:\WORK\AI Projects\XpedeonLegacyObjectTaker\tests\Test-OwnershipLib.ps1"`
Expected: every line prefixed `PASS:`, final line `All tests passed`, exit code 0.

If any line is `FAIL:`, fix `OwnershipLib.psm1` (not the test) until all pass — the test assertions above are the spec's match rules verbatim (see design doc's Match Rule and Classification tables).

---

### Task 3: Orchestration script

**Files:**
- Create: `D:\WORK\AI Projects\XpedeonLegacyObjectTaker\update_ownership.ps1`

- [ ] **Step 1: Write the orchestration script**

```powershell
param(
    [switch]$DryRun
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $ScriptDir 'OwnershipLib.psm1') -Force

$ConfigPath = Join-Path $ScriptDir 'db_config.txt'
$LogPath    = Join-Path $ScriptDir 'ownership_update_log.txt'
$RepoRoot   = 'D:\XpedeonSaas'

$config = Get-DbConfig -ConfigPath $ConfigPath
$env:SQLCMDPASSWORD = $config['SQLPASS']

function Write-Log {
    param([string]$Message)
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | $Message" | Add-Content -Path $LogPath
}

function Invoke-Sql {
    param([string]$Query, [switch]$NoHeaders)
    $sqlArgs = @('-S', $config['SQLSERVER'], '-U', $config['SQLUSER'], '-d', $config['SQLDB'], '-b')
    if ($NoHeaders) { $sqlArgs += @('-h', '-1', '-W', '-s', '|') }
    $sqlArgs += @('-Q', $Query)
    $output = & sqlcmd @sqlArgs 2>&1
    return [PSCustomObject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

Write-Log "====== Run started ======"

$candidateQuery = "SET NOCOUNT ON; SELECT Layer, ObjectName, ObjectType, FormId FROM MIG.OBJECT_OWNERSHIP WHERE OwnershipCategory='LEGACY' AND Layer='DATABASE' AND Remarks='Category update pending' AND FormId IN (SELECT FormId FROM MIG.FORM WHERE ownership_updated='N')"
$result = Invoke-Sql -Query $candidateQuery -NoHeaders
if ($result.ExitCode -ne 0) {
    Write-Log "FATAL: candidate query failed: $($result.Output -join ' ')"
    Write-Host "Candidate query failed. See $LogPath"
    exit 1
}
$candidates = Parse-CandidateOutput -Lines $result.Output

if ($candidates.Count -eq 0) {
    Write-Log "No candidate objects found."
    Write-Host "No candidate objects found."
    Write-Log "====== Run finished ======"
    exit 0
}

Write-Host "Found $($candidates.Count) candidate object(s)."

Write-Host "Scanning $RepoRoot for Pocos and DataProvider files..."
$pocoFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\SharedModels\\Pocos\\' }
$dataProviderFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\Grpc\\Services\\DataProvider\\' }

$pocoContents = @($pocoFiles | ForEach-Object { Get-Content -Raw $_.FullName })
$dataProviderContents = @($dataProviderFiles | ForEach-Object { Get-Content -Raw $_.FullName })

Write-Host "Scanned $($pocoFiles.Count) Pocos file(s), $($dataProviderFiles.Count) DataProvider file(s)."

$formGroups = $candidates | Group-Object -Property FormId

foreach ($group in $formGroups) {
    $formId = $group.Name
    $formFailed = $false
    $today = Get-Date -Format 'yyyy-MM-dd'

    foreach ($obj in $group.Group) {
        $contents = if ($obj.ObjectType -eq 'TABLE') { $pocoContents } else { $dataProviderContents }
        $count = Get-UsageCount -ObjectName $obj.ObjectName -ObjectType $obj.ObjectType -FileContents $contents
        $newCategory = Get-NewCategory -MatchCount $count

        if ($DryRun) {
            Write-Host "[DryRun] $($obj.ObjectName) ($($obj.ObjectType)) hits=$count -> $newCategory"
            Write-Log "$($obj.ObjectName) | LEGACY->$newCategory | hits=$count | DRYRUN"
            continue
        }

        $escName = $obj.ObjectName -replace "'", "''"
        $updateQuery = "BEGIN TRAN; UPDATE MIG.OBJECT_OWNERSHIP SET OwnershipCategory='$newCategory', Remarks='Ownership updated $today', ModifiedBy='BatchScript', ModifiedDate=GETDATE() WHERE ObjectName='$escName' AND ObjectType='$($obj.ObjectType)' AND FormId=$($obj.FormId); COMMIT TRAN;"
        $updResult = Invoke-Sql -Query $updateQuery

        if ($updResult.ExitCode -ne 0) {
            $formFailed = $true
            Write-Log "$($obj.ObjectName) | FAILED | $($updResult.Output -join ' ')"
            Write-Host "FAILED: $($obj.ObjectName) - see log"
        } else {
            Write-Log "$($obj.ObjectName) | LEGACY->$newCategory | hits=$count | OK"
            Write-Host "OK: $($obj.ObjectName) -> $newCategory (hits=$count)"
        }
    }

    if ($DryRun) { continue }

    if ($formFailed) {
        $errEsc = ("Error:***Ownership update failed for one or more objects, $today") -replace "'", "''"
        Invoke-Sql -Query "UPDATE MIG.FORM SET Remarks='$errEsc' WHERE FormId=$formId" | Out-Null
        Write-Log "FormId $formId | NOT flagged ownership_updated (failures present)"
    } else {
        Invoke-Sql -Query "UPDATE MIG.FORM SET ownership_updated='Y' WHERE FormId=$formId" | Out-Null
        Write-Log "FormId $formId | ownership_updated set to Y"
    }
}

Write-Log "====== Run finished ======"
Write-Host "Done. Log: $LogPath"
```

- [ ] **Step 2: Verify the script parses with no syntax errors**

Run: `powershell -NoProfile -Command "$null = Get-Command -Syntax 'D:\WORK\AI Projects\XpedeonLegacyObjectTaker\update_ownership.ps1'; [System.Management.Automation.PSParser]::Tokenize((Get-Content 'D:\WORK\AI Projects\XpedeonLegacyObjectTaker\update_ownership.ps1' -Raw), [ref]$null) | Out-Null; Write-Host 'Parsed OK'"`
Expected: `Parsed OK`, no parser exceptions.

---

### Task 4: Batch wrapper

**Files:**
- Create: `D:\WORK\AI Projects\XpedeonLegacyObjectTaker\update_ownership.bat`

- [ ] **Step 1: Write the wrapper**

```bat
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

if "%SKIPPAUSE%"=="0" pause
```

- [ ] **Step 2: Verify it launches the PowerShell script in dry-run mode without pausing**

Run: `cmd /c ""D:\WORK\AI Projects\XpedeonLegacyObjectTaker\update_ownership.bat" run dryrun auto"`
Expected: console shows `====== Run started ======`-style output from the PowerShell script (either "No candidate objects found." or `[DryRun] ...` lines), command returns without hanging on `pause`.

---

### Task 5: End-to-end dry-run verification against real DB/repo

**Files:** none (verification only)

- [ ] **Step 1: Confirm candidate rows exist**

Run: `sqlcmd -S 192.168.169.181 -U XEWEB_USER -d BZMGTLDB -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM MIG.OBJECT_OWNERSHIP WHERE OwnershipCategory='LEGACY' AND Layer='DATABASE' AND Remarks='Category update pending' AND FormId IN (SELECT FormId FROM MIG.FORM WHERE ownership_updated='N')"`
Expected: a count > 0 (use the rows already seeded for FormId 17 from the demo, e.g. `GTM_TAN_MASTER`, `SPN_GTM_GET_TDS_TAN_MAP`).

- [ ] **Step 2: Run the full pipeline in dry-run mode**

Run: `cmd /c ""D:\WORK\AI Projects\XpedeonLegacyObjectTaker\update_ownership.bat" run dryrun auto"`
Expected: one `[DryRun] <ObjectName> (<ObjectType>) hits=<N> -> <Category>` line per candidate row. Sanity-check at least one known case: `GTM_TAN_MASTER` should resolve via `D:\XpedeonSaas\Xpedeon.GlobalTaxManagement.SharedModels\Pocos\TanMaster.cs` (`[Table("GTM_TAN_MASTER")]`) with hits=1 -> `BLAZOR_OWNED` (unless the same table is also declared in another repo's Pocos file, in which case hits=2+ -> `SHARED`).

- [ ] **Step 3: Inspect the dry-run log**

Run: `powershell -NoProfile -Command "Get-Content 'D:\WORK\AI Projects\XpedeonLegacyObjectTaker\ownership_update_log.txt' -Tail 20"`
Expected: matching `| DRYRUN` lines for every object seen in Step 2, bracketed by `====== Run started ======` / `====== Run finished ======`.

- [ ] **Step 4: Run for real (writeback) once dry-run output looks correct**

Run: `cmd /c ""D:\WORK\AI Projects\XpedeonLegacyObjectTaker\update_ownership.bat" run auto"`
Expected: console shows `OK: <ObjectName> -> <Category> (hits=<N>)` per object; no `FAILED:` lines.

- [ ] **Step 5: Verify DB state changed correctly**

Run: `sqlcmd -S 192.168.169.181 -U XEWEB_USER -d BZMGTLDB -h -1 -W -Q "SET NOCOUNT ON; SELECT ObjectName, OwnershipCategory, Remarks FROM MIG.OBJECT_OWNERSHIP WHERE FormId=17 AND Layer='DATABASE'; SELECT FormId, ownership_updated, Remarks FROM MIG.FORM WHERE FormId=17"`
Expected: `OwnershipCategory` now one of `BLAZOR_OWNED`/`SHARED`/`RETIRED` (no `LEGACY` left for FormId 17's DATABASE rows), `Remarks` reads `Ownership updated <date>`, and `MIG.FORM.ownership_updated='Y'` for FormId 17 (assuming no failures occurred).

---

## Self-Review Notes

- **Spec coverage:** candidate query (Task 3), repo scan + match rules (Tasks 1-2-3), classification thresholds (Tasks 1-2), writeback + Remarks format (Task 3), form-level flag incl. failure path with `Error:***` prefix (Task 3), logging format (Task 3), `.bat` wrapper with `auto` pause-skip (Task 4) — all covered.
- **No placeholders:** every step has literal runnable code/commands, no TBDs.
- **Type consistency:** `Get-UsageCount`/`Get-NewCategory`/`Parse-CandidateOutput`/`Get-DbConfig` signatures are identical between module (Task 1), tests (Task 2), and orchestration script (Task 3).
- **No git commit steps:** this directory is not a git repo (confirmed in environment), so commit steps are omitted; each task ends with a runnable verification command instead.
