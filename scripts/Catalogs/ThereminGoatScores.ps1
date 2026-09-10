# The published sheet is a spreadsheet export: quoted cells carry commas and newlines,
# and several ranking tables sit below and beside each other in one file.
function Read-CatalogCsvRows([string]$Text) {
    $rows = [Collections.Generic.List[object]]::new()
    $row = [Collections.Generic.List[string]]::new()
    $cell = [Text.StringBuilder]::new()
    $quoted = $false
    for ($i = 0; $i -lt $Text.Length; $i++) {
        $character = $Text[$i]
        if ($quoted) {
            if ($character -ne [char]'"') { [void]$cell.Append($character) }
            elseif ($i + 1 -lt $Text.Length -and $Text[$i + 1] -eq [char]'"') { [void]$cell.Append([char]'"'); $i++ }
            else { $quoted = $false }
            continue
        }
        if ($character -eq [char]'"') { $quoted = $true }
        elseif ($character -eq [char]',') { $row.Add($cell.ToString()); [void]$cell.Clear() }
        elseif ($character -eq [char]"`n") { $row.Add($cell.ToString()); [void]$cell.Clear(); $rows.Add($row.ToArray()); $row.Clear() }
        elseif ($character -ne [char]"`r") { [void]$cell.Append($character) }
    }
    if ($quoted) { throw 'Unterminated quoted value in the score sheet.' }
    if ($cell.Length -or $row.Count) { $row.Add($cell.ToString()); $rows.Add($row.ToArray()) }
    return ,$rows.ToArray()
}

function Get-CatalogCsvCell($Row, [int]$Index) {
    if ($Index -ge $Row.Count) { return '' }
    Get-CatalogName $Row[$Index]
}

function Read-ThereminGoatScores([string]$Path) {
    $text = [IO.File]::ReadAllText($Path)
    if ($text.Length -gt 8MB) { throw 'Oversized ThereminGoat score sheet.' }
    $rows = Read-CatalogCsvRows ($text.TrimStart([char]0xFEFF))
    $headers = @()
    for ($index = 0; $index -lt $rows.Count; $index++) {
        # Detect every ranking table by its rank column, so a renamed heading fails the
        # check below instead of quietly importing a later table.
        if ((Get-CatalogCsvCell $rows[$index] 0) -ceq 'Rank') { $headers += $index }
    }
    if ($headers.Count -eq 0) { throw 'Missing ThereminGoat score sheet ranking header.' }
    $expected = @('Rank', 'Switch Name', 'Date', 'Manufacturer', 'Type')
    for ($column = 0; $column -lt $expected.Count; $column++) {
        if ((Get-CatalogCsvCell $rows[$headers[0]] $column) -cne $expected[$column]) { throw "Changed ThereminGoat score sheet header: column $column" }
    }
    # The remaining ranking tables repeat these switches by type, and a separate manufacturer
    # ranking sits in the columns to the right. Only the first table's first five columns are input.
    $end = if ($headers.Count -gt 1) { $headers[1] } else { $rows.Count }
    $records = [Collections.Generic.List[object]]::new()
    $seen = @{}
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        for ($index = $headers[0] + 1; $index -lt $end; $index++) {
            $name = Get-CatalogCsvCell $rows[$index] 1
            if (-not $name) { continue }
            $rank = Get-CatalogCsvCell $rows[$index] 0
            # A per-column average closes the table. Any other unranked row is an unreviewed change.
            if ($rank -ceq 'AVG') {
                if ($name -cne 'AVERAGE OF ALL') { throw "Unexpected ThereminGoat score sheet summary on line $($index + 1)" }
                break
            }
            if ($rank -notmatch '^[1-9][0-9]*$') { throw "Invalid ThereminGoat score sheet rank on line $($index + 1)" }
            $manufacturer = Get-CatalogCsvCell $rows[$index] 3
            $type = Get-CatalogCsvCell $rows[$index] 4
            $entry = [ordered]@{ name = $name }
            $warnings = @()
            if ($manufacturer -and $manufacturer -notmatch '^(?i)(unknown|n/?a|-)$|\?') { $entry.manufacturer = $manufacturer }
            elseif ($manufacturer -match '\?') { $warnings += "Uncertain manufacturer omitted: $manufacturer" }
            if ($type -match '^(?i)(?:silent )?(linear|tactile|clicky)$') { $entry.switchType = $Matches[1].ToLowerInvariant() }
            elseif ($type -and $type -ne '-') { $warnings += "Unsupported type omitted: $type" }
            # Ranks move between publications, so identity follows the reviewed name instead.
            $identity = Get-CatalogMatchName $name -SourceIdentity
            if (-not $identity) { throw "ThereminGoat score sheet name without a usable identity on line $($index + 1)" }
            $id = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity))).Replace('-', '').ToLowerInvariant().Substring(0, 12)
            $excluded = if ($name -match '(?i)\bstabilizer\b') { 'Stabilizer, not a switch' } else { $null }
            $record = [ordered]@{ source = 'theremingoatscores'; sourceId = $id; kind = 'switches'; url = $script:CatalogSources.theremingoatscores.endpoint; entry = $entry; excluded = $excluded; variants = @(); warnings = $warnings }
            # Identity stays on the published name so existing bindings survive, but the
            # repeat comparison uses the reviewed product so two rows agree after cleanup.
            Set-CatalogSwitchProduct $record
            if ($seen.ContainsKey($id)) {
                $first = $seen[$id]
                # The sheet lists a reviewed switch twice when it was scored on two dates.
                if ((ConvertTo-CatalogJson $first.entry) -cne (ConvertTo-CatalogJson $record.entry)) { throw "Conflicting repeated ThereminGoat score listing: $name" }
                $first.warnings += "Repeated listing ignored at rank $rank"
                continue
            }
            $seen[$id] = $record
            $records.Add($record)
        }
    } finally { $sha.Dispose() }
    if ($records.Count -eq 0) { throw 'Empty ThereminGoat score sheet.' }
    return ,$records.ToArray()
}

function Save-ThereminGoatScoresFetch([string]$Run) {
    $relative = 'raw/theremingoatscores/scores.csv'
    $path = Get-CatalogRelativePath $Run $relative
    [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
    $url = $script:CatalogSources.theremingoatscores.endpoint
    Get-CatalogResponse $url -OutFile $path
    $records = Read-ThereminGoatScores $path
    Write-Host "ThereminGoat scores: $($records.Count) scored switch records"
    [ordered]@{ fetchedAt = [DateTime]::UtcNow.ToString('o'); files = @([ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $url }) }
}

function Read-ThereminGoatScoresFetch([string]$Run, [array]$Files) {
    if ($Files.Count -ne 1 -or $Files[0].url -cne $script:CatalogSources.theremingoatscores.endpoint) { throw 'Incomplete or unexpected ThereminGoat score fetch.' }
    $path = Get-CatalogRelativePath $Run $Files[0].path
    if ((Get-CatalogHash $path) -cne $Files[0].sha256) { throw 'ThereminGoat score fetch checksum mismatch.' }
    Read-ThereminGoatScores $path
}
