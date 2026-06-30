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
    param([int]$MatchCount, [string]$ObjectType)
    if ($MatchCount -eq 0) {
        if ($ObjectType -eq 'TABLE') { return $null }
        return 'RETIRING'
    }
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
    $results = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split '\|'
        if ($parts.Count -lt 4) { continue }
        $formId = 0
        if (-not [int]::TryParse($parts[3].Trim(), [ref]$formId)) { continue }
        $results.Add([PSCustomObject]@{
            Layer      = $parts[0].Trim()
            ObjectName = $parts[1].Trim()
            ObjectType = $parts[2].Trim()
            FormId     = $formId
        })
    }
    return ,$results
}

Export-ModuleMember -Function Get-DbConfig, Get-NewCategory, Get-UsageCount, Parse-CandidateOutput
