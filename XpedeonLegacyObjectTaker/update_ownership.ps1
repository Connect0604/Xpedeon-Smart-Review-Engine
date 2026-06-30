param(
    [switch]$DryRun
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Import-Module (Join-Path $ScriptDir 'OwnershipLib.psm1') -Force

$ConfigPath = Join-Path $ScriptDir 'db_config.txt'
$LogPath    = Join-Path $ScriptDir 'ownership_update_log.txt'
$RepoRoot   = 'D:\XpedeonSaas'

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Write-Host "sqlcmd not found on PATH. Install the SQL Server command-line tools."
    exit 1
}

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
    Where-Object { $_.FullName -match 'SharedModels\\Pocos\\' }
$dataProviderFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'Grpc\\Services\\DataProvider\\' }

$pocoContents = @($pocoFiles | ForEach-Object { Get-Content -Raw $_.FullName })
$dataProviderContents = @($dataProviderFiles | ForEach-Object { Get-Content -Raw $_.FullName })

Write-Host "Scanned $($pocoFiles.Count) Pocos file(s), $($dataProviderFiles.Count) DataProvider file(s)."

$formGroups = $candidates | Group-Object -Property FormId
$anyFailures = $false

foreach ($group in $formGroups) {
    $formId = [int]$group.Name
    $formFailed = $false
    $today = Get-Date -Format 'yyyy-MM-dd'

    foreach ($obj in @($group.Group)) {
        $contents = if ($obj.ObjectType -eq 'TABLE') { $pocoContents } else { $dataProviderContents }
        $count = Get-UsageCount -ObjectName $obj.ObjectName -ObjectType $obj.ObjectType -FileContents $contents
        $newCategory = Get-NewCategory -MatchCount $count -ObjectType $obj.ObjectType

        if ($null -eq $newCategory) {
            Write-Host "SKIP: $($obj.ObjectName) (TABLE, hits=0) - kept as LEGACY, never RETIRING"
            Write-Log "$($obj.ObjectName) | LEGACY (unchanged, table never retired) | hits=$count | SKIPPED"
            continue
        }

        if ($DryRun) {
            Write-Host "[DryRun] $($obj.ObjectName) ($($obj.ObjectType)) hits=$count -> $newCategory"
            Write-Log "$($obj.ObjectName) | LEGACY->$newCategory | hits=$count | DRYRUN"
            continue
        }

        $escName = $obj.ObjectName -replace "'", "''"
        $escType = $obj.ObjectType -replace "'", "''"
        $updateQuery = "BEGIN TRAN; UPDATE MIG.OBJECT_OWNERSHIP SET OwnershipCategory='$newCategory', Remarks='Ownership updated $today', ModifiedBy='BatchScript', ModifiedDate=GETDATE() WHERE ObjectName='$escName' AND ObjectType='$escType' AND FormId=$([int]$obj.FormId); COMMIT TRAN;"
        $updResult = Invoke-Sql -Query $updateQuery

        if ($updResult.ExitCode -ne 0) {
            $formFailed = $true
            $anyFailures = $true
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
        Invoke-Sql -Query "UPDATE MIG.FORM SET ownership_updated='Y', Remarks=NULL WHERE FormId=$formId" | Out-Null
        Write-Log "FormId $formId | ownership_updated set to Y"
    }
}

Write-Log "====== Run finished ======"
if ($anyFailures) {
    Write-Host "Done with failures. Log: $LogPath"
    exit 1
}
Write-Host "Done. Log: $LogPath"
