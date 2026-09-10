Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../Common.ps1"
. "$PSScriptRoot/../Sources.ps1"
. "$PSScriptRoot/../Catalog.ps1"
. "$PSScriptRoot/Test-SwitchSources.ps1"
. "$PSScriptRoot/Test-KeycapLendar.ps1"
. "$PSScriptRoot/Test-Aliases.ps1"

$script:Passed = 0
$script:Failures = @()
$testRoot = Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) ('artifacts/catalog-import/tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; return }
    throw "Expected failure matching '$Pattern'."
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    try { & $Action; $script:Passed++; Write-Host "PASS $Name" }
    catch { $script:Failures += "$Name`: $($_.Exception.Message) at $($_.ScriptStackTrace)"; Write-Host "FAIL $Name`: $($_.Exception.Message)" }
}
function Copy-Value($Value) { ConvertTo-CatalogMap (ConvertFrom-Json (ConvertTo-CatalogJson $Value)) }
function New-TestRecords {
    @(
        (Convert-CatalogProduct (Read-CatalogJson "$PSScriptRoot/Fixtures/switches.json").products[0] 'switchoddities'),
        (Convert-CatalogProduct (Read-CatalogJson "$PSScriptRoot/Fixtures/products.json").products[0] 'divinikey'),
        (Convert-CatalogMatrix ([IO.File]::ReadAllText("$PSScriptRoot/Fixtures/matrix.md")) @{ sourcePath = 'docs/gmk-keycaps/Example-R2.md' } ('a' * 40))
    )
}
function New-TestState { (New-CatalogCandidate (Read-CatalogState (Join-Path $testRoot 'empty')) (New-TestRecords)).state }
function Save-TestFetch([string]$Run) {
    $manifest = [ordered]@{ schemaVersion = 1; fetchedAt = '2026-09-09T00:00:00Z'; sources = [ordered]@{} }
    foreach ($source in $script:CatalogSources.Keys) {
        if ($source -eq 'dcswiki') {
            $files = @()
            foreach ($extension in @('html', 'js')) {
                $relative = "raw/dcswiki/catalog.$extension"
                $path = Join-Path $Run $relative
                [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
                [IO.File]::Copy("$PSScriptRoot/Fixtures/dcswiki.$extension", $path)
                $role = if ($extension -eq 'html') { 'page' } else { 'bundle' }
                $url = if ($extension -eq 'html') { 'https://dcs.wiki/keycaps' } else { 'https://dcs.wiki/_next/static/chunks/123-changing-hash.js' }
                $files += [ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $url; role = $role }
            }
            $manifest.sources[$source] = [ordered]@{ fetchedAt = '2026-09-09T01:00:00Z'; files = $files }
        } elseif ($source -eq 'keycaplendar') {
            $relative = 'raw/keycaplendar/page-1.json'
            $path = Join-Path $Run $relative
            Write-CatalogJson $path (Read-CatalogJson "$PSScriptRoot/Fixtures/keycaplendar.json")
            $manifest.sources[$source] = [ordered]@{ files = @([ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = Get-KeycapLendarPageUrl }) }
        } elseif ($source -eq 'theremingoat') {
            $relative = 'raw/theremingoat/collection.xlsx'
            $path = Join-Path $Run $relative
            Save-TestCollection $path (New-TestCollectionItems)
            $manifest.sources[$source] = [ordered]@{ files = @([ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $script:CatalogSources.theremingoat.endpoint }) }
        } elseif ($source -eq 'theremingoatscores') {
            $relative = 'raw/theremingoatscores/scores.csv'
            $path = Join-Path $Run $relative
            Save-TestScoreSheet $path (New-TestScoreSheetText)
            $manifest.sources[$source] = [ordered]@{ files = @([ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $script:CatalogSources.theremingoatscores.endpoint }) }
        } elseif ($source -eq 'matrix') {
            $relative = 'raw/matrix/Example-R2.md'
            $path = Join-Path $Run $relative
            [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
            [IO.File]::Copy("$PSScriptRoot/Fixtures/matrix.md", $path)
            $manifest.sources[$source] = [ordered]@{ commit = ('a' * 40); files = @([ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; sourcePath = 'docs/gmk-keycaps/Example-R2.md' }) }
        } else {
            $fixture = if ($source -eq 'switchoddities') { 'switches' } else { 'products' }
            $data = if ($source -eq 'unikeys') { [ordered]@{ products = @((New-TestUniKeysProduct)) } } else { Read-CatalogJson "$PSScriptRoot/Fixtures/$fixture.json" }
            if ($source -eq 'kbdfans') {
                $data.products[0].title = 'Different Set'; $data.products[0].id = 301
                $data.products[1].title = 'Different Novelties'; $data.products[1].id = 302
            }
            $files = @()
            foreach ($page in 1..2) {
                $relative = "raw/$source/page-$page.json"
                $path = Join-Path $Run $relative
                $content = if ($page -eq 1) { $data } else { [ordered]@{ products = @() } }
                Write-CatalogJson $path $content
                $files += [ordered]@{ path = $relative; sha256 = Get-CatalogHash $path }
            }
            $manifest.sources[$source] = [ordered]@{ files = $files }
        }
    }
    Write-CatalogJson (Join-Path $Run 'fetch.json') $manifest
}

Test-SwitchSources
Test-KeycapLendar
Test-CatalogAliases

Test-Case 'DCS Wiki discovers changing bundles and decodes data without running JavaScript' {
    $html = [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/dcswiki.html")
    $bundle = [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/dcswiki.js")
    $urls = Get-DcsWikiBundleUrls ($html.Replace('123-changing-hash', '456-new-hash'))
    Assert ($urls.Count -eq 1 -and $urls[0].EndsWith('/456-new-hash.js')) 'Bundle discovery was hardcoded or allowed an external script.'
    $script:DcsFixtureExecuted = $false
    $items = Get-DcsWikiData ($bundle + '; $script:DcsFixtureExecuted = $true;')
    Assert (-not $script:DcsFixtureExecuted) 'Downloaded script was executed.'
    $records = @(Convert-DcsWikiRecords $items $html)
    Assert ($records.Count -eq 3 -and $records[0].entry.name -ceq 'DCS Explorer''s "R2"') 'JS/JSON quote escapes were not decoded.'
    Assert ($records[0].entry.designer -ceq 'Alice & Bob' -and $records[0].entry.profile -eq 'DCS') 'Unicode escape or profile lost; legend style is not profile.'
    Assert (-not $records[0].entry.Contains('description') -and -not $records[0].entry.Contains('imageUrl')) 'Unsupported source fields imported.'
    Assert (-not $records[1].excluded -and -not $records[2].excluded) 'Add-on kit or complete monokit was excluded.'
    Assert (-not $records[2].entry.Contains('designer') -and -not $records[2].entry.Contains('manufacturer')) 'Unknown metadata was retained.'
    Assert ((ConvertFrom-CatalogJsString "'\x41\u0042\\'") -ceq 'AB\') 'Hex/backslash escape failed.'
}
Test-Case 'DCS Wiki rejects changed schemas, ambiguous arrays, missing links, and incomplete caches' {
    $html = [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/dcswiki.html")
    $bundle = [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/dcswiki.js")
    Assert-Throws { Get-DcsWikiData ($bundle.Replace('"id":"502"', '"id":"501"')) } 'duplicate DCS Wiki ID'
    Assert-Throws { Get-DcsWikiData ($bundle.Replace('"name":', '"renamedField":')) } 'missing valid name'
    Assert-Throws { Get-DcsWikiData ($bundle + $bundle) } 'Ambiguous'
    Assert-Throws { ConvertFrom-CatalogJsString "'\xZZ'" } 'hex escape'
    Assert-Throws { ConvertFrom-CatalogJsString "'\q'" } 'Unsupported'
    Assert-Throws { Convert-DcsWikiRecords (Get-DcsWikiData $bundle) '' } 'page/bundle mismatch'
    $run = Join-Path $testRoot 'dcs-cache'
    Save-TestFetch $run
    $manifest = Read-CatalogJson (Join-Path $run 'fetch.json')
    $files = $manifest.sources.dcswiki.files
    Assert-Throws { Read-DcsWikiFetch $run @($files[0]) } 'Incomplete'
    $files[1].url = 'https://dcs.wiki/_next/static/chunks/stale.js'
    Assert-Throws { Read-DcsWikiFetch $run $files } 'not referenced'
    $manifest.sources.Remove('dcswiki')
    Write-CatalogJson (Join-Path $run 'fetch.json') $manifest
    Assert-Throws { Read-CatalogFetch $run } 'incomplete fetch manifest'
}
Test-Case 'DCS Wiki fetch caches the discovered data and fails when it disappears' {
    function Get-CatalogResponse([string]$Url) {
        if ($Url -eq 'https://dcs.wiki/keycaps') { return [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/dcswiki.html") }
        if ($Url -eq 'https://dcs.wiki/_next/static/chunks/123-changing-hash.js') { return [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/dcswiki.js") }
        throw 'Unexpected request'
    }
    $run = Join-Path $testRoot 'dcs-fetch'
    $snapshot = Save-DcsWikiFetch $run
    Assert ($snapshot.files.Count -eq 2 -and (Read-DcsWikiFetch $run $snapshot.files).Count -eq 3) 'Fetch could not be replayed offline.'
    [IO.File]::AppendAllText((Join-Path $run 'raw/dcswiki/catalog.js'), ' ')
    Assert-Throws { Read-DcsWikiFetch $run $snapshot.files } 'checksum mismatch'
    function Get-CatalogResponse([string]$Url) {
        if ($Url -eq 'https://dcs.wiki/keycaps') { return [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/dcswiki.html") }
        return 'JSON.parse(''[]'')'
    }
    Assert-Throws { Save-DcsWikiFetch (Join-Path $testRoot 'dcs-empty') } 'catalog data not found'
}

Test-Case 'Product labels, HTML entities, standalone kits, and retailer attribution' {
    $products = (Read-CatalogJson "$PSScriptRoot/Fixtures/products.json").products
    $p = Convert-CatalogProduct $products[0] 'divinikey'
    Assert ($p.entry.name -ceq 'Example & Friends') 'Name normalization failed.'
    Assert ($p.entry.manufacturer -eq 'Example Factory' -and $p.entry.brand -eq 'Example Brand') 'Manufacturer and brand were conflated.'
    Assert ($p.entry.designer -eq 'Alice & Bob' -and $p.entry.material -eq 'ABS' -and $p.entry.profile -eq 'Cherry') 'Explicit metadata lost.'
    Assert (-not (Convert-CatalogProduct $products[1] 'divinikey').excluded) 'Standalone novelty kit was excluded.'
    $s = (New-TestRecords)[0]
    Assert (-not $s.entry.Contains('manufacturer') -and -not $s.entry.Contains('brand') -and $s.entry.switchType -eq 'linear') 'Retailer/prose invented switch metadata.'
    $table = Get-CatalogText '<table><tr><td><p>Manufacturer</p></td><td><p>JWK</p></td></tr><tr><td>Designer</td><td>Durock</td></tr></table>'
    Assert ((Get-CatalogLabel $table 'Manufacturer(?:[ \t]*:|[ \t]+)') -eq 'JWK') 'Table-cell layout lost metadata.'
}
Test-Case 'Keycap add-ons are retained across sources while references and accessories remain excluded' {
    $product = (Read-CatalogJson "$PSScriptRoot/Fixtures/products.json").products[1]
    foreach ($source in @('divinikey', 'kbdfans')) {
        foreach ($name in @('Example Spacebars', 'Example Accent Kit', 'Example International Add-on', 'Example Artisan', 'Example Relegendables', 'Example 40s Kit Collection')) {
            $product.title = $name
            $record = Convert-CatalogProduct $product $source
            Assert (-not $record.excluded -and $record.entry.name -ceq $name) "Keycap add-on lost: $source/$name"
        }
        $product.title = 'Example Keycap Puller'
        Assert ((Convert-CatalogProduct $product $source).excluded) 'Non-keycap accessory was accepted.'
    }
    foreach ($name in @('Example 40s Addon', 'Example Modifiers', 'Example Accent', 'Example Spacebars', 'Example Relegendables')) {
        $record = Convert-CatalogMatrix "title: $name" @{ sourcePath = 'docs/gmk-keycaps/Example.md' } ('a' * 40)
        Assert (-not $record.excluded -and $record.entry.name -ceq "GMK $name") "Matrix add-on lost: $name"
    }
    foreach ($name in @('GMK Keycaps', 'Standard Color Codes')) {
        Assert ((Convert-CatalogMatrix "title: $name" @{ sourcePath = 'docs/gmk-keycaps/reference.md' } ('a' * 40)).excluded) 'Reference page was accepted.'
    }
}
Test-Case 'Unicode names and meaningful rounds survive parsing' {
    $p = (New-TestRecords)[2]
    $expected = 'GMK Example R2 ' + [char]0x793A + [char]0x4F8B
    Assert ($p.entry.name -ceq $expected -and $p.entry.designer -eq 'Example Studio') 'Matrix fields lost.'
    Assert ((Get-CatalogMatchName 'GMK Example R2') -ne (Get-CatalogMatchName 'GMK Example R1')) 'Rounds collapsed.'
    Assert ((Get-CatalogMatchName 'GMK Example R2') -eq (Get-CatalogMatchName $p.entry.name)) 'Translation matching failed.'
    Assert ((Get-CatalogSlug 'GMK Olivia++') -ne (Get-CatalogSlug 'GMK Olivia')) 'Meaningful plus signs collapsed.'
    Assert (-not (Get-CatalogLabel "* Designer:  `n* GB Time: 2020-01" 'Designer\s*:')) 'Blank label consumed the next line.'
}
Test-Case 'Pagination rejects repeated IDs and malformed responses' {
    $data = Read-CatalogJson "$PSScriptRoot/Fixtures/products.json"
    $seen = @{}
    Test-CatalogProductPage $data $seen
    Assert-Throws { Test-CatalogProductPage $data $seen } 'Repeated product'
    Assert-Throws { Test-CatalogProductPage @{ products = 'wrong' } @{} } 'products array'
    Assert-Throws { Test-CatalogProductPage @{ products = @(@{ id = 1; title = '' }) } @{} } 'missing its ID or name'
}
Test-Case 'Replay requires a complete checksummed fetch and expands switch weights' {
    $run = Join-Path $testRoot 'fetch'
    Save-TestFetch $run
    $records = Read-CatalogFetch $run
    $switches = @($records | Where-Object { $_.source -eq 'switchoddities' })
    Assert ($switches.Count -eq 2 -and $switches[0].sourceId -ne $switches[1].sourceId) 'Weights were collapsed.'
    Assert ($switches[0].entry.name -match '38g' -and $switches[1].entry.name -match '53g') 'Weight names lost.'
    [IO.File]::AppendAllText((Join-Path $run 'raw/switchoddities/page-1.json'), ' ')
    Assert-Throws { Read-CatalogFetch $run } 'checksum mismatch'
    Assert-Throws { Read-CatalogFetch (Join-Path $testRoot 'missing') } 'fetch.json'
}
Test-Case 'No-op refresh is byte stable and does not bump versions' {
    $state = New-TestState
    $next = New-CatalogCandidate $state (New-TestRecords)
    Assert ((ConvertTo-CatalogJson $state) -ceq (ConvertTo-CatalogJson $next.state)) 'No-op changed state.'
    Assert ($next.report.changes.Count -eq 0 -and $next.report.additions.Count -eq 0) 'No-op reported changes.'
}
Test-Case 'Renames retain IDs, overrides persist, and missing sources retain history' {
    $state = New-TestState
    $records = New-TestRecords
    $records[0].entry.name = 'Corrected Switch Name'
    $next = New-CatalogCandidate $state $records
    Assert ($next.state.catalogs.switches.entries[0].id -eq $state.catalogs.switches.entries[0].id) 'Rename changed ID.'
    Assert ($next.state.catalogs.switches.catalogVersion -eq 2 -and $next.state.catalogs.keycaps.catalogVersion -eq 1) 'Wrong catalogs versioned.'
    $id = $state.catalogs.switches.entries[0].id
    $state.overrides.entries["switches/$id"] = [ordered]@{ name = 'Reviewed Name'; manufacturer = 'Verified Factory' }
    $next = New-CatalogCandidate $state $records
    Assert ($next.state.catalogs.switches.entries[0].name -eq 'Reviewed Name') 'Override lost.'
    $missing = New-CatalogCandidate $next.state @($records | Where-Object { $_.source -ne 'switchoddities' })
    Assert ($missing.state.catalogs.switches.entries[0].manufacturer -eq 'Verified Factory' -and $missing.report.missingSources.Count -eq 1) 'Retired entry lost.'
}
Test-Case 'Cross-source matches need explicit decisions and metadata conflicts block' {
    $state = New-TestState
    $records = @(New-TestRecords)
    $extra = Copy-Value $records[1]
    $extra.source = 'kbdfans'; $extra.sourceId = '501'
    $records += $extra
    $next = New-CatalogCandidate $state $records
    Assert ($next.report.duplicates.Count -eq 1) 'Duplicate auto-merged.'
    $id = @($state.bindings | Where-Object { $_.source -eq 'divinikey' })[0].id
    $state.overrides.bindings['kbdfans:501'] = $id
    $extra.entry.material = 'PBT'
    $next = New-CatalogCandidate $state $records
    Assert ($next.report.duplicates.Count -eq 0 -and $next.report.conflicts.Count -eq 1) 'Metadata conflict not reported.'
    $state.overrides.entries["keycaps/$id"] = [ordered]@{ material = 'ABS' }
    $next = New-CatalogCandidate $state $records
    Assert ($next.report.conflicts.Count -eq 0) 'Reviewed conflict did not resolve.'
}
Test-Case 'Validation rejects duplicate IDs, unsupported fields, and orphan mappings' {
    $state = New-TestState
    $state.catalogs.switches.entries += $state.catalogs.switches.entries[0]
    Assert-Throws { Test-CatalogState $state } 'Duplicate ID'
    $state = New-TestState
    $state.catalogs.switches.entries[0].material = 'POM'
    Assert-Throws { Test-CatalogState $state } 'Unsupported field'
    $state = New-TestState
    $state.bindings[0].id = 'missing'
    Assert-Throws { Test-CatalogState $state } 'missing ID'
}
Test-Case 'Promotion checks candidates, baselines, overrides, and complete state' {
    $root = Join-Path $testRoot 'promotion-root'
    $run = Join-Path $testRoot 'promotion-run'
    Save-TestFetch $run
    Save-CatalogCandidate $root $run
    Publish-CatalogCandidate $root $run
    Test-CatalogState (Read-CatalogState $root)
    $provenance = Read-CatalogJson (Join-Path $root 'Scripts/Catalogs/sources.json')
    Assert ($provenance.sourceFetchDates.dcswiki -eq '2026-09-09T01:00:00Z' -and $provenance.sourceFetchDates.matrix -eq '2026-09-09T00:00:00Z') 'Individual source retrieval dates were lost.'
    Assert-Throws { Publish-CatalogCandidate $root $run } 'Accepted files changed'
    Save-CatalogCandidate $root $run
    $file = Join-Path $run 'candidate/Assets/Catalogs/switches.json'
    [IO.File]::AppendAllText($file, ' ')
    Assert-Throws { Publish-CatalogCandidate $root $run } 'Candidate checksum mismatch'
    Save-CatalogCandidate $root $run
    Write-CatalogJson (Join-Path $root 'Scripts/Catalogs/overrides.json') (Read-CatalogState $root).overrides
    Assert-Throws { Publish-CatalogCandidate $root $run } 'inputs or report changed'
}
Test-Case 'Traversal paths are rejected' {
    Assert-Throws { Get-CatalogRelativePath $testRoot '../outside.json' } 'leaves its root'
}

Test-Case 'Failed network fetch leaves no complete manifest' {
    function Get-CatalogResponse([string]$Url) {
        if ($Url -match 'page=1$') { return [IO.File]::ReadAllText("$PSScriptRoot/Fixtures/switches.json") }
        throw 'Simulated network failure'
    }
    $run = Join-Path $testRoot 'failed-fetch'
    Assert-Throws { Save-CatalogFetch $run } 'Simulated network failure'
    Assert (-not (Test-Path -LiteralPath (Join-Path $run 'fetch.json'))) 'Failed fetch published a manifest.'
}
Test-Case 'Reused snapshots carry verified downloads and their original fetch dates' {
    $source = Join-Path $testRoot 'reuse-source'
    Save-TestFetch $source
    # Only the sources missing from the reused snapshot may reach the network.
    function Get-CatalogResponse([string]$Url) { throw "Unexpected download: $Url" }
    $run = Join-Path $testRoot 'reuse-run'
    Save-CatalogFetch $run $source
    $manifest = Read-CatalogJson (Join-Path $run 'fetch.json')
    Assert ($manifest.sources.Count -eq $script:CatalogSources.Count) 'Reuse produced an incomplete manifest.'
    foreach ($name in $script:CatalogSources.Keys) {
        $carried = $manifest.sources[$name]
        Assert ($carried.reusedFrom -ceq (Split-Path $source -Leaf)) "Source $name was not marked as reused."
        Assert ($carried.fetchedAt -ceq '2026-09-09T00:00:00Z' -or $carried.fetchedAt -ceq '2026-09-09T01:00:00Z') "Source $name lost its original retrieval date."
        foreach ($file in @($carried.files)) {
            Assert ((Get-CatalogHash (Get-CatalogRelativePath $run $file.path)) -ceq $file.sha256) "Reused file $($file.path) did not match its recorded hash."
        }
    }
    # A complete reused snapshot must still replay without touching the network.
    $records = Read-CatalogFetch $run
    Assert ($records.Count -gt 0) 'A reused snapshot did not replay.'
    Assert-Throws { Save-CatalogFetch $run $source } 'new run directory'
}
Test-Case 'Reuse rejects altered caches and drops sources that are no longer registered' {
    $source = Join-Path $testRoot 'reuse-altered-source'
    Save-TestFetch $source
    [IO.File]::AppendAllText((Join-Path $source 'raw/switchoddities/page-1.json'), ' ')
    function Get-CatalogResponse([string]$Url) { throw "Unexpected download: $Url" }
    Assert-Throws { Save-CatalogFetch (Join-Path $testRoot 'reuse-altered-run') $source } 'checksum mismatch'
    $retired = Join-Path $testRoot 'reuse-retired-source'
    Save-TestFetch $retired
    $manifest = Read-CatalogJson (Join-Path $retired 'fetch.json')
    $manifest.sources['retiredsource'] = [ordered]@{ files = @([ordered]@{ path = 'raw/switchoddities/page-1.json'; sha256 = Get-CatalogHash (Join-Path $retired 'raw/switchoddities/page-1.json') }) }
    Write-CatalogJson (Join-Path $retired 'fetch.json') $manifest
    $run = Join-Path $testRoot 'reuse-retired-run'
    Save-CatalogFetch $run $retired
    $result = Read-CatalogJson (Join-Path $run 'fetch.json')
    Assert (-not $result.sources.Contains('retiredsource')) 'An unregistered source was carried into the new snapshot.'
    Assert ($result.sources.Count -eq $script:CatalogSources.Count) 'Dropping a retired source changed the registered set.'
}
Test-Case 'Failed replay invalidates earlier promotion receipt' {
    $root = Join-Path $testRoot 'failed-replay-root'
    $run = Join-Path $testRoot 'failed-replay-run'
    Save-TestFetch $run
    Save-CatalogCandidate $root $run
    [IO.File]::AppendAllText((Join-Path $run 'raw/switchoddities/page-1.json'), ' ')
    Assert-Throws { Save-CatalogCandidate $root $run } 'checksum mismatch'
    Assert (-not (Test-Path -LiteralPath (Join-Path $run 'candidate/receipt.json'))) 'Stale receipt remained.'
}
Test-Case 'Ordinary promotion write failure restores earlier replacements' {
    $root = Join-Path $testRoot 'rollback-root'
    $run = Join-Path $testRoot 'rollback-run'
    Save-TestFetch $run
    Save-CatalogCandidate $root $run
    Publish-CatalogCandidate $root $run
    $state = Read-CatalogState $root
    $id = $state.catalogs.switches.entries[0].id
    $state.overrides.entries["switches/$id"] = [ordered]@{ name = 'Changed switch name' }
    Write-CatalogJson (Join-Path $root 'Scripts/Catalogs/overrides.json') $state.overrides
    Save-CatalogCandidate $root $run
    $switchPath = Join-Path $root 'Assets/Catalogs/switches.json'
    $keycapPath = Join-Path $root 'Assets/Catalogs/keycaps.json'
    $before = Get-CatalogHash $switchPath
    $attributes = [IO.File]::GetAttributes($keycapPath)
    try {
        [IO.File]::SetAttributes($keycapPath, ($attributes -bor [IO.FileAttributes]::ReadOnly))
        Assert-Throws { Publish-CatalogCandidate $root $run } 'denied|read.only|access'
        Assert ((Get-CatalogHash $switchPath) -ceq $before) 'Earlier catalog replacement was not rolled back.'
    } finally { [IO.File]::SetAttributes($keycapPath, $attributes) }
}
Test-Case 'Large source omissions block an update instead of silently shrinking coverage' {
    $records = @(New-TestRecords)
    foreach ($n in 1..25) {
        $record = Copy-Value $records[0]
        $record.sourceId = [string](9000 + $n)
        $record.entry.name = "Distinct Switch $n"
        $records += $record
    }
    $state = (New-CatalogCandidate (Read-CatalogState (Join-Path $testRoot 'empty')) $records).state
    Assert-Throws { New-CatalogCandidate $state (New-TestRecords) } 'Suspicious source count drop'
}
Test-Case 'Only an explicit exclusion can remove a mistakenly accepted listing' {
    $state = New-TestState
    $records = New-TestRecords
    $records[1].excluded = 'New adapter filter'
    $next = New-CatalogCandidate $state $records
    Assert ($next.state.catalogs.keycaps.entries.Count -eq 2 -and $next.report.removals.Count -eq 0) 'Adapter filter deleted history.'
    $state.overrides.excluded['divinikey:101'] = 'Reviewed incorrect listing'
    $next = New-CatalogCandidate $state $records
    Assert ($next.state.catalogs.keycaps.entries.Count -eq 1 -and $next.report.removals.Count -eq 1) 'Explicit correction was not applied.'
}

Write-Host "$script:Passed test groups passed; $($script:Failures.Count) failed."
if ($script:Failures.Count) { throw ($script:Failures -join "`n") }
