# Read only the collection's named cells. No Excel installation, formula evaluation,
# external relationships, archive extraction, or spreadsheet packages are needed.
function Read-CatalogXlsxXml($Archive, [string]$Part) {
    $parts = @($Archive.Entries | Where-Object { $_.FullName -ceq $Part })
    if ($parts.Count -ne 1 -or $parts[0].Length -gt 32MB) { throw "Missing, duplicate, or oversized XLSX part: $Part" }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 32MB
    $stream = $parts[0].Open()
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        $xml = [Xml.XmlDocument]::new()
        $xml.XmlResolver = $null
        $xml.Load($reader)
        return ,$xml
    } finally { if ($reader) { $reader.Dispose() }; $stream.Dispose() }
}

function Read-ThereminGoatCollection([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $workbook = Read-CatalogXlsxXml $archive 'xl/workbook.xml'
        $sheets = @($workbook.SelectNodes('//*[local-name()="sheets"]/*'))
        if ($sheets.Count -ne 1 -or $sheets[0].GetAttribute('name') -cne 'Sheet3') { throw 'Unexpected ThereminGoat workbook sheets.' }
        $relationshipId = $sheets[0].GetAttribute('id', 'http://schemas.openxmlformats.org/officeDocument/2006/relationships')
        $relationships = Read-CatalogXlsxXml $archive 'xl/_rels/workbook.xml.rels'
        $links = @($relationships.DocumentElement.ChildNodes | Where-Object { $_.GetAttribute('Id') -ceq $relationshipId })
        if ($links.Count -ne 1 -or $links[0].GetAttribute('Target') -cne 'worksheets/sheet1.xml' -or $links[0].GetAttribute('TargetMode') -eq 'External') { throw 'Unexpected ThereminGoat worksheet relationship.' }
        $stringsXml = Read-CatalogXlsxXml $archive 'xl/sharedStrings.xml'
        $strings = @($stringsXml.DocumentElement.ChildNodes | ForEach-Object {
            ($_.SelectNodes('./*[local-name()="t"]|./*[local-name()="r"]/*[local-name()="t"]') | ForEach-Object { $_.InnerText }) -join ''
        })
        $sheet = Read-CatalogXlsxXml $archive 'xl/worksheets/sheet1.xml'
        $cells = @{}
        foreach ($cell in $sheet.SelectNodes('//*[local-name()="sheetData"]/*/*[local-name()="c"]')) {
            $address = $cell.GetAttribute('r')
            if ($address -notmatch '^(A|J|M|N|O)[1-9][0-9]*$') { continue }
            if ($cells.ContainsKey($address)) { throw "Duplicate collection cell: $address" }
            # Identity and metadata must be literals, not potentially stale formula caches.
            if ($cell.SelectSingleNode('./*[local-name()="f"]')) { throw "Formula in collection input: $address" }
            $value = $cell.SelectSingleNode('./*[local-name()="v"]')
            $text = if ($value) { $value.InnerText } else { '' }
            switch ($cell.GetAttribute('t')) {
                's' {
                    $index = 0
                    if (-not [int]::TryParse($text, [ref]$index) -or $index -lt 0 -or $index -ge $strings.Count) { throw "Invalid shared string: $address" }
                    $text = $strings[$index]
                }
                'inlineStr' { $text = ($cell.SelectNodes('./*[local-name()="is"]//*[local-name()="t"]') | ForEach-Object { $_.InnerText }) -join '' }
                'e' { throw "Spreadsheet error in collection input: $address" }
            }
            $cells[$address] = Get-CatalogName $text
        }
    } finally { $archive.Dispose() }
    foreach ($header in @{ A3 = 'NUMBER'; A4 = 'NAME'; J3 = 'TYPE'; N3 = 'MANUFACTURER' }.GetEnumerator()) {
        if ($cells[$header.Key] -cne $header.Value) { throw "Changed ThereminGoat collection header: $($header.Key)" }
    }
    $items = [Collections.Generic.List[object]]::new()
    $maxRow = ($cells.Keys | ForEach-Object { [int]($_ -replace '^[A-Z]+', '') } | Measure-Object -Maximum).Maximum
    $trailingTemplate = $false
    for ($row = 5; $row -le $maxRow; $row += 2) {
        $name = $cells["A$($row + 1)"]
        $number = $cells["A$row"]
        $type = $cells["J$row"]
        $manufacturer = $cells["N$row"]
        if (-not $name) {
            if ($type -or $manufacturer) { throw "Incomplete collection record at row $row" }
            $trailingTemplate = $true
            continue
        }
        if ($trailingTemplate -or $number -notmatch '^[1-9][0-9]*(?:\.0+)?$') { throw "Invalid collection record at row $row" }
        $notes = Get-CatalogName ((@('M', 'N', 'O') | ForEach-Object { $cells["$_$($row + 1)"] }) -join ' ')
        $items.Add(@{ number = ([int][double]$number).ToString(); name = $name; type = $type; manufacturer = $manufacturer; notes = $notes })
    }
    if ($items.Count -eq 0) { throw 'Empty ThereminGoat collection.' }
    $counts = @{}
    foreach ($item in $items) { if (-not $counts.ContainsKey($item.number)) { $counts[$item.number] = 0 }; $counts[$item.number]++ }
    $seen = @{}
    $records = [Collections.Generic.List[object]]::new()
    foreach ($item in $items) {
        $id = $item.number
        # The published workbook repeats 3215. A name digest distinguishes specimens
        # without tying identity to their current row positions.
        if ($counts[$id] -gt 1) {
            $hash = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes((Get-CatalogMatchName $item.name -SourceIdentity)))).Replace('-', '').ToLowerInvariant() }
            finally { $hash.Dispose() }
            $id += '/' + $digest.Substring(0, 12)
        }
        if ($seen.ContainsKey($id)) { throw "Repeated collection identity: $id" }
        $seen[$id] = $true
        $entry = [ordered]@{ name = $item.name }
        $warnings = @()
        if ($item.manufacturer -and $item.manufacturer -notmatch '^(?i)(unknown|n/?a|-)$|\?') { $entry.manufacturer = $item.manufacturer }
        elseif ($item.manufacturer -match '\?') { $warnings += "Uncertain manufacturer omitted: $($item.manufacturer)" }
        if ($item.type -match '^(?i)(?:silent )?(linear|tactile|clicky)$') { $entry.switchType = $Matches[1].ToLowerInvariant() }
        elseif ($item.type -and $item.type -ne '-') { $warnings += "Unsupported type omitted: $($item.type)" }
        $excluded = if ($item.name -match '(?i)\bstabilizer\b' -or $item.type -eq 'Stabilizer') { 'Stabilizer, not a switch' } else { $null }
        $record = [ordered]@{ source = 'theremingoat'; sourceId = $id; kind = 'switches'; url = $script:CatalogSources.theremingoat.endpoint; entry = $entry; excluded = $excluded; variants = @(); warnings = $warnings; notes = $item.notes }
        Set-CatalogSwitchProduct $record
        $records.Add($record)
    }
    return ,$records.ToArray()
}

function Save-ThereminGoatFetch([string]$Run) {
    $relative = 'raw/theremingoat/collection.xlsx'
    $path = Get-CatalogRelativePath $Run $relative
    [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
    $url = $script:CatalogSources.theremingoat.endpoint
    Get-CatalogResponse $url -OutFile $path
    $records = Read-ThereminGoatCollection $path
    Write-Host "ThereminGoat: $($records.Count) named collection records"
    [ordered]@{ fetchedAt = [DateTime]::UtcNow.ToString('o'); files = @([ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $url }) }
}

function Read-ThereminGoatFetch([string]$Run, [array]$Files) {
    if ($Files.Count -ne 1 -or $Files[0].url -cne $script:CatalogSources.theremingoat.endpoint) { throw 'Incomplete or unexpected ThereminGoat fetch.' }
    $path = Get-CatalogRelativePath $Run $Files[0].path
    if ((Get-CatalogHash $path) -cne $Files[0].sha256) { throw 'ThereminGoat fetch checksum mismatch.' }
    Read-ThereminGoatCollection $path
}
