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
Assert-Equal (Get-NewCategory -MatchCount 0 -ObjectType 'PROC') 'RETIRING' 'proc zero hits -> RETIRING'
Assert-Equal (Get-NewCategory -MatchCount 1 -ObjectType 'PROC') 'BLAZOR_OWNED' 'proc one hit -> BLAZOR_OWNED'
Assert-Equal (Get-NewCategory -MatchCount 5 -ObjectType 'PROC') 'SHARED' 'proc multi hits -> SHARED'
Assert-Equal (Get-NewCategory -MatchCount 0 -ObjectType 'TABLE') $null 'table zero hits -> null (never RETIRING, stays LEGACY)'
Assert-Equal (Get-NewCategory -MatchCount 1 -ObjectType 'TABLE') 'BLAZOR_OWNED' 'table one hit -> BLAZOR_OWNED'
Assert-Equal (Get-NewCategory -MatchCount 3 -ObjectType 'TABLE') 'SHARED' 'table multi hits -> SHARED'

# Get-UsageCount - TABLE
$tableContent = '[Table("GTM_TAN_MASTER")]'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @($tableContent)) 1 'table match counts 1'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @('no match here')) 0 'table no match counts 0'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @($tableContent, $tableContent)) 2 'table match across 2 files counts 2'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @('GTM_TAN_MASTER appears as plain text only')) 0 'table plain-text mention without attribute does not count'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @('[table("gtm_tan_master")]')) 1 'table match is case-insensitive'
Assert-Equal (Get-UsageCount -ObjectName 'GTM_TAN_MASTER' -ObjectType 'TABLE' -FileContents @('[Table("GTM_TAN_MASTER_X")]')) 0 'table match does not match on superset name (boundary check)'

# Get-UsageCount - PROC
$procContent = 'new SqlCommand("SPN_GTM_GET_TDS_TAN_MAP", conn)'
Assert-Equal (Get-UsageCount -ObjectName 'SPN_GTM_GET_TDS_TAN_MAP' -ObjectType 'PROC' -FileContents @($procContent)) 1 'proc match counts 1'
Assert-Equal (Get-UsageCount -ObjectName 'SPN_OTHER' -ObjectType 'PROC' -FileContents @($procContent)) 0 'proc no match counts 0'
Assert-Equal (Get-UsageCount -ObjectName 'SPN_GTM_GET_TDS_TAN_MAP' -ObjectType 'PROC' -FileContents @('spn_gtm_get_tds_tan_map')) 1 'proc match is case-insensitive'
Assert-Equal (Get-UsageCount -ObjectName 'SPN_GTM_GET_TDS_TAN_MAP' -ObjectType 'PROC' -FileContents @('SPN_GTM_GET_TDS_TAN_MAP_V2')) 0 'proc word boundary rejects substring of a longer identifier'

# Get-DbConfig
try {
    Get-DbConfig -ConfigPath 'D:\does\not\exist.txt'
    Write-Host "FAIL: Get-DbConfig throws on missing path - expected throw, got no error"
    $script:failures++
} catch {
    Write-Host "PASS: Get-DbConfig throws on missing path"
}

$tmpConfig = Join-Path $PSScriptRoot 'tmp_test_config.txt'
@('SQLSERVER=192.168.1.1', 'SQLUSER=testuser', 'SQLPASS=p@ss=word') | Set-Content -Path $tmpConfig
$cfg = Get-DbConfig -ConfigPath $tmpConfig
Assert-Equal $cfg['SQLSERVER'] '192.168.1.1' 'Get-DbConfig parses SQLSERVER'
Assert-Equal $cfg['SQLUSER'] 'testuser' 'Get-DbConfig parses SQLUSER'
Assert-Equal $cfg['SQLPASS'] 'p@ss=word' 'Get-DbConfig parses value containing an extra equals sign'
Remove-Item $tmpConfig -ErrorAction SilentlyContinue

# Parse-CandidateOutput
$lines = @('DATABASE|GTM_TAN_MASTER|TABLE|17', 'DATABASE|SPN_GTM_GET_TDS_TAN_MAP|PROC|17', '')
$parsed = Parse-CandidateOutput -Lines $lines
Assert-Equal $parsed.Count 2 'parses 2 candidate rows, skips blank line'
Assert-Equal $parsed[0].ObjectName 'GTM_TAN_MASTER' 'parses ObjectName column'
Assert-Equal $parsed[0].ObjectType 'TABLE' 'parses ObjectType column'
Assert-Equal $parsed[0].FormId 17 'parses FormId column as int'

$malformedLines = @('DATABASE|GTM_TAN_MASTER|TABLE|17', 'DATABASE|TOO_SHORT|TABLE', 'DATABASE|BAD_FORMID|TABLE|notanumber')
$malformedParsed = Parse-CandidateOutput -Lines $malformedLines
Assert-Equal $malformedParsed.Count 1 'skips short row and non-numeric FormId row, keeps valid row'

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) test(s) FAILED"
    exit 1
} else {
    Write-Host "All tests passed"
    exit 0
}
