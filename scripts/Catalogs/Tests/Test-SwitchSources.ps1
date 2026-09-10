function Save-TestCollection([string]$Path, [array]$Items, [string]$Header = 'NUMBER', [switch]$Formula) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.IO.Compression
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    $rows = '<row r="3"><c r="A3" t="s"><v>0</v></c><c r="J3" t="s"><v>1</v></c><c r="N3" t="s"><v>2</v></c></row><row r="4"><c r="A4" t="s"><v>3</v></c></row>'
    $row = 5
    foreach ($item in $Items) {
        $f = if ($Formula) { '<f>1+1</f>' } else { '' }
        $rows += '<row r="{0}"><c r="A{0}">{4}<v>{1}</v></c><c r="J{0}" t="inlineStr"><is><t>{2}</t></is></c><c r="N{0}" t="inlineStr"><is><t>{3}</t></is></c></row>' -f $row, $item.number, [Security.SecurityElement]::Escape($item.type), [Security.SecurityElement]::Escape($item.manufacturer), $f
        $note = if ($item.ContainsKey('notes')) { [Security.SecurityElement]::Escape($item.notes) } else { '' }
        $rows += '<row r="{0}"><c r="A{0}" t="inlineStr"><is><r><t>{1}</t></r></is></c><c r="M{0}" t="inlineStr"><is><t>{2}</t></is></c></row>' -f ($row + 1), [Security.SecurityElement]::Escape($item.name), $note
        $row += 2
    }
    $parts = @{
        'xl/workbook.xml' = '<workbook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet3" r:id="rId4"/></sheets></workbook>'
        'xl/_rels/workbook.xml.rels' = '<Relationships><Relationship Id="rId4" Target="worksheets/sheet1.xml"/></Relationships>'
        'xl/sharedStrings.xml' = "<sst><si><r><t>$Header</t></r></si><si><t>TYPE</t></si><si><t>MANUFACTURER</t></si><si><t>NAME</t></si></sst>"
        'xl/worksheets/sheet1.xml' = "<worksheet><sheetData>$rows</sheetData></worksheet>"
    }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($part in $parts.Keys) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($part).Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($parts[$part]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}

function New-TestCollectionItems {
    @(
        @{ number = '1.0'; name = 'Collection Item & One'; type = 'Silent Tactile'; manufacturer = 'Example Factory'; notes = 'Mold Type A' },
        @{ number = '3'; name = 'Collection Item Two'; type = 'Linear'; manufacturer = 'Unknown' },
        @{ number = '3'; name = 'Collection Item Three'; type = 'Clicky and Linear'; manufacturer = 'Factory?' }
    )
}

function New-TestUniKeysProduct {
    [ordered]@{
        id = 901; title = 'Example Silent Linear Switch Factory Lubed (10PCS) - Restock in August'; handle = 'example'; vendor = 'UniKeys'
        body_html = '<table><tr><td>Manufacturer</td><td>Example Factory</td></tr><tr><td>Designer</td><td>Alice</td></tr></table>'
        options = @(@{ name = 'Type' })
        variants = @(@{ id = 11; title = '45g X10 in loose packaging' }; @{ id = 12; title = '53g X90 in fine packaging' })
    }
}

function New-TestScoreSheetText {
    param([string]$NameHeader = 'Switch Name', [string]$Summary = 'AVERAGE OF ALL', [switch]$ConflictingRepeat)
    # Mirrors the published export: a wrapped title cell, a manufacturer table in the
    # columns to the right, a per-column average, and a repeat of the same switches by type.
    $repeated = if ($ConflictingRepeat) { 'Other Factory' } else { 'Unknown' }
    @"
,ThereminGoat's Master Score Sheet,,,,,9/6/2026,,"Total
Scored",3,,,
 - TOTAL RANKINGS -,,,,,
Rank,$NameHeader,Date,Manufacturer,Type,Push Feel,,,,,,,,,Rank,Manufacturer Name
1,"Score Item, One",8/6/2023,Example Factory,Silent Linear,31,,,,,,,,,1,Example Factory
2,Score Item Two,1/2/2024,Unknown,Tactile,20,,,,,,,,,2,Other Factory
3,Score Item Three,3/4/2024,Factory?,Hall Effect,10
4,Score Item Two,5/6/2024,$repeated,Tactile,9
AVG,$Summary,,-,-,27.0
,,,,,
 - LINEAR RANKINGS -,,,,,
Rank,Switch Name,Date,Manufacturer,Type,Push Feel
1,Second Section Only,8/6/2023,Example Factory,Linear,31
"@
}

function Save-TestScoreSheet([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $true))
}

function New-TestSwitchListing([string]$Title) {
    [ordered]@{ id = 950; title = $Title; handle = 'example'; vendor = 'SwitchOddities'; body_html = ''; options = @(); variants = @() }
}

function Test-SwitchSources {
    Test-Case 'Specimen and modification listings are rejected while shipped options are kept' {
        foreach ($title in @('Aflion Pink Cow Prototype', 'Gazzew Boba U4 Proto (62g.)', 'Keygeek Raw Sample', 'Meirun Factory Sample (Dry)')) {
            Assert ((Convert-CatalogProduct (New-TestSwitchListing $title) 'switchoddities').excluded -match 'specimen') "Specimen listing was accepted: $title"
        }
        foreach ($title in @('Broken-In CHERRY MX - MX2A Black 1M', 'Gateron Tangerine (modded)', 'KTT Strawberry Switch Hand Lubed Edition', 'Lubed, Filmed, And Spring Swapped CHERRY MX - Nixie')) {
            Assert ((Convert-CatalogProduct (New-TestSwitchListing $title) 'switchoddities').excluded -match 'modification') "Modification listing was accepted: $title"
        }
        # A shipped lube option is a purchase choice, so it stays and loses only the wording.
        foreach ($pair in @(
            @{ title = 'Gateron Plus Linear - Factory Lubed'; name = 'Gateron Plus Linear' }
            @{ title = 'Gateron Plus Linear - Dry'; name = 'Gateron Plus Linear' }
            @{ title = 'Wingtree Nezuko (Lubed, 55g)'; name = 'Wingtree Nezuko (55g)' }
            @{ title = 'Keygeek Raw (Flat Pole, Dry)'; name = 'Keygeek Raw (Flat Pole)' }
            @{ title = 'Akko Wine White (Prelubed)'; name = 'Akko Wine White' }
        )) {
            $record = Convert-CatalogProduct (New-TestSwitchListing $pair.title) 'switchoddities'
            Assert (-not $record.excluded) "Shipped lube option was rejected: $($pair.title)"
            Assert ($record.entry.name -ceq $pair.name) "Lube wording was not removed from $($pair.title): got $($record.entry.name)"
        }
        # NovelKeys' Dry line keeps its model word, and Proto is only a whole word.
        foreach ($title in @('NK Dry Black', 'NovelKeys NK Dry Black', 'NK Dry Yellow V1', 'Protozoa Linear')) {
            $record = Convert-CatalogProduct (New-TestSwitchListing $title) 'switchoddities'
            Assert (-not $record.excluded) "Product line was rejected: $title"
            Assert ($record.entry.name -ceq (Get-CatalogName $title)) "Product line name was altered: $title -> $($record.entry.name)"
        }
        # Keycap listings keep their own rules; only the specimen check is shared.
        $keycap = Convert-CatalogProduct (New-TestSwitchListing 'Example Dry Set') 'divinikey'
        Assert ($keycap.entry.name -ceq 'Example Dry Set') 'Switch lube wording was stripped from a keycap set.'
    }
    Test-Case 'Score sheet reads the composite ranking only and collapses a repeated listing' {
        $path = Join-Path $testRoot 'scores.csv'
        Save-TestScoreSheet $path (New-TestScoreSheetText)
        $records = Read-ThereminGoatScores $path
        Assert ($records.Count -eq 3) 'Repeated ranking sections or the summary row were imported.'
        Assert (@($records | Where-Object { $_.entry.name -ceq 'Second Section Only' }).Count -eq 0) 'A per-type ranking section was imported again.'
        Assert ($records[0].entry.name -ceq 'Score Item, One') 'A quoted value containing a comma was split.'
        Assert ($records[0].entry.manufacturer -ceq 'Example Factory' -and $records[0].entry.switchType -ceq 'linear') 'Silent classification or manufacturer was lost.'
        Assert (-not $records[1].entry.Contains('manufacturer') -and $records[1].entry.switchType -ceq 'tactile') 'An unknown manufacturer was invented.'
        Assert (-not $records[2].entry.Contains('manufacturer') -and -not $records[2].entry.Contains('switchType')) 'Uncertain metadata was imported.'
        Assert ($records[2].warnings.Count -eq 2) 'Omitted manufacturer and type were not reported.'
        Assert (@($records[1].warnings | Where-Object { $_ -match 'Repeated listing' }).Count -eq 1) 'A repeated identical listing was not reported.'
        # The manufacturer ranking sharing these rows must not become switch records.
        Assert (@($records | Where-Object { $_.entry.name -ceq 'Other Factory' }).Count -eq 0) 'Columns from the manufacturer table were imported.'
        $moved = Join-Path $testRoot 'scores-moved.csv'
        Save-TestScoreSheet $moved ((New-TestScoreSheetText) -replace '(?m)^1,"Score Item, One"', '9,"Score Item, One"')
        $again = Read-ThereminGoatScores $moved
        Assert ($records[0].sourceId -ceq (@($again | Where-Object { $_.entry.name -ceq 'Score Item, One' })[0]).sourceId) 'Source identity depends on a switch ranking.'
    }
    Test-Case 'Score sheet rejects changed headers, summaries, conflicting repeats, and altered caches' {
        $path = Join-Path $testRoot 'scores-header.csv'
        Save-TestScoreSheet $path (New-TestScoreSheetText -NameHeader 'Renamed')
        Assert-Throws { Read-ThereminGoatScores $path } 'header'
        $path = Join-Path $testRoot 'scores-summary.csv'
        Save-TestScoreSheet $path (New-TestScoreSheetText -Summary 'TOTALS CHANGED')
        Assert-Throws { Read-ThereminGoatScores $path } 'summary'
        $path = Join-Path $testRoot 'scores-conflict.csv'
        Save-TestScoreSheet $path (New-TestScoreSheetText -ConflictingRepeat)
        Assert-Throws { Read-ThereminGoatScores $path } 'Conflicting repeated'
        $path = Join-Path $testRoot 'scores-unranked.csv'
        Save-TestScoreSheet $path ((New-TestScoreSheetText) -replace '(?m)^3,Score Item Three', ',Score Item Three')
        Assert-Throws { Read-ThereminGoatScores $path } 'rank'
        $run = Join-Path $testRoot 'scores-run'
        $relative = 'raw/theremingoatscores/scores.csv'
        $cached = Join-Path $run $relative
        Save-TestScoreSheet $cached (New-TestScoreSheetText)
        $files = @([ordered]@{ path = $relative; sha256 = Get-CatalogHash $cached; url = $script:CatalogSources.theremingoatscores.endpoint })
        Assert ((Read-ThereminGoatScoresFetch $run $files).Count -eq 3) 'A checksummed score cache did not replay.'
        Save-TestScoreSheet $cached ((New-TestScoreSheetText) -replace 'Example Factory', 'Edited Factory')
        Assert-Throws { Read-ThereminGoatScoresFetch $run $files } 'checksum'
        $files[0].url = 'https://example.invalid/scores.csv'
        Assert-Throws { Read-ThereminGoatScoresFetch $run $files } 'unexpected'
    }
    Test-Case 'Collection literal cells, paired rows, explicit metadata, and duplicate numbers' {
        $path = Join-Path $testRoot 'collection.xlsx'
        Save-TestCollection $path (New-TestCollectionItems)
        $records = Read-ThereminGoatCollection $path
        Assert ($records.Count -eq 3 -and $records[0].sourceId -ceq '1') 'Named rows or numeric identities were lost.'
        Assert ($records[0].entry.name -ceq 'Collection Item & One' -and $records[0].entry.switchType -ceq 'tactile') 'Rich text, entities or silent classification failed.'
        Assert (-not $records[1].entry.Contains('manufacturer') -and -not $records[2].entry.Contains('manufacturer') -and -not $records[2].entry.Contains('switchType')) 'Unknown or ambiguous metadata was invented.'
        Assert ($records[1].sourceId -ne $records[2].sourceId -and $records[2].warnings.Count -eq 2) 'Duplicate numbering or source warnings were lost.'
        Assert ($records[0].notes -ceq 'Mold Type A') 'Variant note was discarded.'
        $reordered = Join-Path $testRoot 'collection-reordered.xlsx'
        $items = New-TestCollectionItems
        Save-TestCollection $reordered @($items[0], $items[2], $items[1])
        $again = Read-ThereminGoatCollection $reordered
        Assert ($records[1].sourceId -ceq $again[2].sourceId) 'Source identities depend on worksheet row positions.'
    }
    Test-Case 'Collection rejects changed headers, formulas, missing names, and repeated identities' {
        $items = New-TestCollectionItems
        $path = Join-Path $testRoot 'collection-header.xlsx'
        Save-TestCollection $path $items 'RENAMED'
        Assert-Throws { Read-ThereminGoatCollection $path } 'header'
        $path = Join-Path $testRoot 'collection-formula.xlsx'
        Save-TestCollection $path $items -Formula
        Assert-Throws { Read-ThereminGoatCollection $path } 'Formula'
        $path = Join-Path $testRoot 'collection-incomplete.xlsx'
        $items[0].name = ''
        Save-TestCollection $path $items
        Assert-Throws { Read-ThereminGoatCollection $path } 'Incomplete collection record'
        $path = Join-Path $testRoot 'collection-duplicate.xlsx'
        Save-TestCollection $path @($items[1], $items[1])
        Assert-Throws { Read-ThereminGoatCollection $path } 'Repeated collection identity'
    }
    Test-Case 'Collection fetch validates binary caches and replay checks hashes' {
        function Get-CatalogResponse([string]$Url, [string]$OutFile) {
            Assert ($Url -ceq $script:CatalogSources.theremingoat.endpoint) 'Unexpected collection endpoint.'
            Save-TestCollection $OutFile (New-TestCollectionItems)
        }
        $run = Join-Path $testRoot 'collection-fetch'
        $snapshot = Save-ThereminGoatFetch $run
        Assert ((Read-ThereminGoatFetch $run $snapshot.files).Count -eq 3) 'Collection fetch cannot replay.'
        Assert-Throws { Read-ThereminGoatFetch $run @() } 'Incomplete'
        $snapshot.files[0].sha256 = 'bad'
        Assert-Throws { Read-ThereminGoatFetch $run $snapshot.files } 'checksum'
    }
    Test-Case 'UniKeys retains switch weights and explicit metadata without pack-size names' {
        $product = New-TestUniKeysProduct
        $records = Convert-UniKeysProduct $product
        Assert ($records.Count -eq 2 -and $records[0].sourceId -ceq '901/variant/11') 'Variant provenance was lost.'
        Assert ($records[0].entry.name -ceq 'Example Silent Linear - 45g' -and $records[1].entry.name.EndsWith('53g')) 'Packaging or marketing leaked into names.'
        Assert ($records[0].entry.manufacturer -ceq 'Example Factory' -and $records[0].entry.designer -ceq 'Alice' -and $records[0].entry.switchType -ceq 'linear') 'Explicit UniKeys metadata was lost.'
        Assert (-not $records[0].entry.Contains('brand')) 'UniKeys retailer was treated as a brand.'
        $product.variants[0].title = 'Factory Lubed'
        $product.variants[1].title = 'Hand Lubed/Filmed/Spring Swapped'
        $records = Convert-UniKeysProduct $product
        Assert ($records[0].entry.name -ne $records[1].entry.name) 'Different modifications were collapsed.'
    }
    Test-Case 'UniKeys packaging-only products collapse and unknown variants stop replay' {
        $product = New-TestUniKeysProduct
        $product.variants[0].title = 'X10 in Loose Packaging'
        $product.variants[1].title = 'X35 in Film Box'
        $records = Convert-UniKeysProduct $product
        Assert ($records.Count -eq 1 -and $records[0].sourceId -ceq '901') 'Pack sizes became separate switches.'
        $product.options[0].name = 'Unrecognized Option'
        Assert-Throws { Convert-UniKeysProduct $product } 'Unrecognized'
        $product.title = 'UniKeys Keyboard Switch Tester'
        Assert ((Convert-UniKeysProduct $product)[0].excluded) 'Tester options were expanded as switch records.'
        $product = New-TestUniKeysProduct
        $product.variants[1].id = $product.variants[0].id
        Assert-Throws { Convert-UniKeysProduct $product } 'Duplicate UniKeys variant'
    }
    Test-Case 'Reviewed collection variants stay separate and notes survive promotion state' {
        $path = Join-Path $testRoot 'collection-distinct.xlsx'
        Save-TestCollection $path (New-TestCollectionItems)
        $records = Read-ThereminGoatCollection $path
        $records[1].entry.name = $records[0].entry.name
        $state = New-TestState
        $state.overrides.bindings['theremingoat:1'] = 'collection-mold-a'
        $state.overrides.bindings["theremingoat:$($records[1].sourceId)"] = 'collection-mold-b'
        $state.overrides.entries['switches/collection-mold-a'] = @{ name = 'Collection Item Mold A' }
        $state.overrides.entries['switches/collection-mold-b'] = @{ name = 'Collection Item Mold B' }
        $result = New-CatalogCandidate $state @((New-TestRecords) + $records)
        Assert ($result.report.duplicates.Count -eq 0 -and $result.report.conflicts.Count -eq 0) 'Explicit variant separation failed.'
        $a = @($result.state.bindings | Where-Object { $_.source -eq 'theremingoat' -and $_.sourceId -eq '1' })[0]
        Assert ($a.id -ceq 'collection-mold-a' -and $a.notes -ceq 'Mold Type A') 'Collection provenance was lost.'
        $again = New-CatalogCandidate $result.state @((New-TestRecords) + $records)
        Assert ($again.report.changes.Count -eq 0 -and $again.report.additions.Count -eq 0) 'Reviewed variants do not replay consistently.'
    }
}
