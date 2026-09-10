function Test-CatalogAliases {
    Test-Case 'Product prefixes use abbreviations while company fields retain full names' {
        foreach ($kind in @('switches', 'keycaps')) {
            $expected = if ($kind -eq 'switches') { 'KFA' } else { 'kfaPBT' }
            foreach ($sourceName in @('KFA', 'kfaPBT', 'KeebsForAll')) {
                $entry = Get-NormalizedCatalogEntry ([ordered]@{ id = 'stable-id'; name = "$sourceName Example R2"; brand = 'KFA'; designer = 'kfaPBT'; manufacturer = 'Other Factory' }) $kind
                Assert ($entry.name -ceq "$expected Example R2" -and $entry.id -ceq 'stable-id') 'Catalog-specific prefix or stable ID changed.'
                Assert ($entry.brand -ceq 'KeebsForAll' -and $entry.designer -ceq 'KeebsForAll' -and $entry.manufacturer -ceq 'Other Factory') 'Display abbreviation leaked into company metadata.'
                Assert ((ConvertTo-CatalogJson $entry) -ceq (ConvertTo-CatalogJson (Get-NormalizedCatalogEntry $entry $kind))) 'Normalization is not idempotent.'
                Assert ((Get-CatalogMatchName $entry.name) -ceq (Get-CatalogMatchName 'KeebsForAll Example R2')) 'Display preference changed alias matching.'
            }
        }
        $entry = Get-NormalizedCatalogEntry ([ordered]@{ name = 'Milkyway Example'; manufacturer = 'MW'; designer = 'Hello? X SWG' }) 'keycaps'
        Assert ($entry.name -ceq 'MW Example' -and $entry.manufacturer -ceq 'Milkyway Keys' -and $entry.designer -ceq 'Hello? X Swagkeys') 'Field-specific aliases or designer punctuation changed.'
        $entry = Get-NormalizedCatalogEntry ([ordered]@{ name = 'KeebsForAll x Wuque Studio Example (50g)'; designer = 'KFA x WS' }) 'switches'
        Assert ($entry.name -ceq 'KFA x WS Example (50g)' -and $entry.designer -ceq 'KeebsForAll x Wuque Studio') 'Collaboration prefixes or variant details changed.'
        Assert ((Get-CatalogAliasName 'Divinikey Example' 'switches') -ceq 'dk Example') 'Divinikey abbreviation lost.'
        Assert ((Get-CatalogAliasName 'Dangkeebs Example' 'switches') -ceq 'DK Example') 'Dangkeebs abbreviation lost.'
    }
    Test-Case 'Brand aliases respect exact case, word boundaries, and separate identities' {
        foreach ($name in @('Keykobo', 'KeyKobo', 'Key Kobo', 'KKB')) {
            Assert ((Get-CatalogAliasName "$name Example R2") -ceq 'KeyKobo Example R2') "Missed alias: $name"
        }
        Assert ((Get-CatalogAliasName 'dk Oni') -ceq 'Divinikey Oni') 'Lowercase dk was conflated.'
        Assert ((Get-CatalogAliasName 'DK Creamery') -ceq 'Dangkeebs Creamery') 'Uppercase DK was conflated.'
        Assert ((Get-CatalogEntityName 'Dk') -ceq 'Dk') 'Unreviewed mixed-case abbreviation was guessed.'
        Assert ((Get-CatalogMatchName 'dk Example') -cne (Get-CatalogMatchName 'DK Example')) 'Matching discarded the dk distinction.'
        Assert ((Get-CatalogEntityName 'TX') -cne (Get-CatalogEntityName 'Typeplus')) 'TX and Typeplus collapsed.'
        foreach ($name in @('Glove', 'glove.studio', 'Glove Studio')) {
            Assert ((Get-CatalogEntityName $name) -ceq 'Glove Studio') 'Glove alias missed.'
        }
        foreach ($name in @('KFA', 'kfapbt', 'keebsforall')) {
            Assert ((Get-CatalogEntityName $name) -ceq 'KeebsForAll') 'KFA alias missed.'
        }
        Assert ((Get-CatalogAliasName 'GATERON Cherry Blossom') -ceq 'Gateron Cherry Blossom') 'Model words changed.'
        Assert ((Get-CatalogAliasName 'GGBOY MW V3 Fire Phoenix') -ceq 'GGBOY MW V3 Fire Phoenix') 'Internal abbreviation was guessed.'
        Assert ((Get-CatalogAliasName 'KKBird Example') -ceq 'KKBird Example') 'Partial word matched.'
        Assert ((Get-CatalogAliasName 'KFA x KEYGEEK Example') -ceq 'KeebsForAll x Keygeek Example') 'Collaboration aliases missed.'
    }
    Test-Case 'OEM relationships, profile aliases, and designer punctuation remain distinct' {
        foreach ($brand in @('Durock', 'JWICK')) {
            $entry = Get-NormalizedCatalogEntry ([ordered]@{ id = 'sample'; name = "$brand Sample"; manufacturer = $brand }) 'switches'
            Assert ($entry.manufacturer -ceq 'JWK' -and $entry.brand -ceq $brand) 'OEM replaced the selling brand.'
        }
        $entry = Get-NormalizedCatalogEntry ([ordered]@{ name = 'Other Sample'; manufacturer = 'Durock/JWK'; brand = 'Other Brand' }) 'switches'
        Assert ($entry.manufacturer -ceq 'JWK' -and $entry.brand -ceq 'Other Brand') 'Explicit selling brand overwritten.'
        $entry = Get-NormalizedCatalogEntry ([ordered]@{ name = 'JWICK Red'; manufacturer = 'Durock/JWK' }) 'switches'
        Assert ($entry.manufacturer -ceq 'JWK' -and $entry.brand -ceq 'JWICK') 'Legacy manufacturer label overrode JWICK branding.'
        $entry = Get-NormalizedCatalogEntry ([ordered]@{ name = 'Owlab Tungsten'; manufacturer = 'Durock/JWK' }) 'switches'
        Assert ($entry.manufacturer -ceq 'JWK' -and -not $entry.Contains('brand')) 'Legacy manufacturer label invented a selling brand.'
        $entry = Get-NormalizedCatalogEntry ([ordered]@{ name = 'DCS Deadbeats'; designer = 'Hello? X Rubrehose'; profile = 'DCS' }) 'keycaps'
        Assert ($entry.designer -ceq 'Hello? X Rubrehose') 'Designer punctuation changed.'
        Assert ((Get-CatalogAliasName 'GMK Why?') -ceq 'GMK Why?') 'Literal product punctuation changed.'
        $cyl = Get-NormalizedCatalogEntry ([ordered]@{ name = 'GMK CYL Example' }) 'keycaps'
        $mtnu = Get-NormalizedCatalogEntry ([ordered]@{ name = 'GMK MTNU Example' }) 'keycaps'
        Assert ($cyl.profile -ceq 'Cherry' -and $mtnu.profile -ceq 'MTNU') 'GMK profiles conflated.'
        Assert ((Get-CatalogMatchName $cyl.name) -cne (Get-CatalogMatchName $mtnu.name)) 'GMK profile variants matched.'
        Assert ((Get-CatalogProfileName 'CYL') -ceq (Get-CatalogProfileName 'Cherry')) 'CYL profile alias missed.'
    }
    Test-Case 'Alias normalization retains product IDs and original source spellings' {
        $records = New-TestRecords
        $records[0].entry = [ordered]@{ name = 'KEYGEEK Example 50g'; manufacturer = 'KEYGEEK' }
        $other = Copy-Value $records[0]
        $other.sourceId = 'alias-variant'
        $other.entry = [ordered]@{ name = 'Keygeek Example 60g'; manufacturer = 'KeyGeek' }
        $state = (New-CatalogCandidate (Read-CatalogState (Join-Path $testRoot 'alias-empty')) ($records + @($other))).state
        $beforeIds = @($state.catalogs.switches.entries.id)
        $records[0].entry.name = 'Keygeek Example 50g'
        $result = New-CatalogCandidate $state ($records + @($other))
        Assert (($result.state.catalogs.switches.entries.id -join ',') -ceq ($beforeIds -join ',')) 'Aliases changed existing IDs.'
        Assert ($result.state.catalogs.switches.entries.Count -eq 2) 'Weight variants merged.'
        Assert (($result.state.bindings | Where-Object sourceId -eq 'alias-variant').observed.manufacturer -ceq 'KeyGeek') 'Source spelling lost.'
        $other.sourceId = 'new-alias-source'
        $other.entry.name = 'KeyGeek Example 50g'
        $result = New-CatalogCandidate $state ($records + @($other))
        Assert ($result.report.duplicates.Count -eq 1) 'An alias match bypassed review.'
    }
    Test-Case 'Explicit duplicate rebindings retire only an unreferenced duplicate' {
        $records = New-TestRecords
        $records[0].entry = [ordered]@{ name = 'IKEYX Example' }
        $duplicate = Copy-Value $records[0]
        $duplicate.sourceId = 'spelling-duplicate'
        $duplicate.entry.name = 'IKYEX Example'
        $records += $duplicate
        $state = (New-CatalogCandidate (Read-CatalogState (Join-Path $testRoot 'duplicate-empty')) $records).state
        $target = ($state.bindings | Where-Object { $_.source -eq $records[0].source -and $_.sourceId -eq $records[0].sourceId }).id
        $old = ($state.bindings | Where-Object sourceId -eq 'spelling-duplicate').id
        $state.overrides.bindings['switchoddities:spelling-duplicate'] = $target
        $result = New-CatalogCandidate $state $records
        Assert ($result.state.catalogs.switches.entries.Count -eq 1 -and $result.report.removals.Count -eq 1) 'Reviewed duplicate was not retired.'
        Assert ($result.state.bindings.Count -eq $state.bindings.Count) 'Duplicate source provenance lost.'
        Assert (($result.state.bindings | Where-Object sourceId -eq 'spelling-duplicate').observed.name -ceq 'IKYEX Example') 'Original typo was lost.'
        $again = New-CatalogCandidate $result.state $records
        Assert ($again.report.removals.Count -eq 0 -and $again.report.changes.Count -eq 0) 'Duplicate returned on replay.'
        $retained = Copy-Value $state.bindings[0]
        $retained.source = 'switchoddities'; $retained.sourceId = 'retained-variant'; $retained.kind = 'switches'; $retained.id = $old
        $state.bindings += $retained
        $result = New-CatalogCandidate $state $records
        Assert ($result.state.catalogs.switches.entries.Count -eq 2) 'Still-referenced entry was removed.'
    }
    Test-Case 'Retailer CYL and Cherry labels agree while MTNU remains separate' {
        $product = (Read-CatalogJson "$PSScriptRoot/Fixtures/products.json").products[0]
        $product.body_html = '<p>Profile: CYL</p><p>Cherry Profile</p>'
        Assert ((Convert-CatalogProduct $product 'divinikey').entry.profile -ceq 'Cherry') 'Equivalent profile labels conflicted.'
        $product.body_html = '<p>Profile: MTNU</p>'
        Assert ((Convert-CatalogProduct $product 'divinikey').entry.profile -ceq 'MTNU') 'MTNU profile missing.'
    }
    Test-Case 'Reviewed metadata omissions take precedence over alias relationships' {
        $records = New-TestRecords
        $records[0].entry = [ordered]@{ name = 'Durock Example' }
        $state = (New-CatalogCandidate (Read-CatalogState (Join-Path $testRoot 'omission-empty')) $records).state
        $state.overrides.entries['switches/durock-example'] = [ordered]@{ manufacturer = $null; brand = $null }
        $result = New-CatalogCandidate $state $records
        $entry = $result.state.catalogs.switches.entries[0]
        Assert (-not $entry.Contains('manufacturer') -and -not $entry.Contains('brand')) 'Alias metadata overrode a deliberate omission.'
    }
    Test-Case 'Keycap names supply the shape and plastic their company determines' {
        function Get-TestInferred([string]$Name, $Seed) {
            $entry = [ordered]@{ name = $Name }
            if ($Seed) { foreach ($field in $Seed.Keys) { $entry[$field] = $Seed[$field] } }
            Add-CatalogInferredFields (Get-NormalizedCatalogEntry $entry 'keycaps') 'keycaps'
        }
        $gmk = Get-TestInferred 'GMK Example' $null
        Assert ($gmk.manufacturer -ceq 'GMK' -and $gmk.profile -ceq 'Cherry' -and $gmk.material -ceq 'ABS') 'Declared keycap defaults were not applied.'
        $kkb = Get-TestInferred 'KKB Example' $null
        Assert ($kkb.manufacturer -ceq 'KeyKobo' -and $kkb.material -ceq 'ABS' -and -not $kkb.Contains('profile')) 'A profile was invented for a company that does not declare one.'
        # The exception profile replaces the usual shape and states its own plastic.
        $mtnu = Get-TestInferred 'GMK MTNU Example' $null
        Assert ($mtnu.profile -ceq 'MTNU' -and $mtnu.material -ceq 'PBT') 'MTNU kept the company default plastic instead of its own.'
        $embedded = Get-TestInferred 'GMK JUST MTNU' $null
        Assert ($embedded.profile -ceq 'MTNU' -and $embedded.material -ceq 'PBT') 'A trailing MTNU was not recognized as that profile.'
        foreach ($shape in @('SA', 'DSA', 'KAT', 'MT3', 'DCS')) {
            Assert ((Get-TestInferred "$shape Example" $null).profile -ceq $shape) "Leading $shape did not supply its profile."
        }
        Assert ((Get-TestInferred 'Cherry Example' $null).profile -ceq 'Cherry') 'Leading Cherry did not supply its profile.'
        # NovelKeys and RAMA Works only sell, so a name of theirs states nothing more.
        foreach ($vendor in @('NK Example', 'RAMA Example')) {
            $entry = Get-TestInferred $vendor $null
            Assert (-not $entry.Contains('manufacturer') -and -not $entry.Contains('material')) "Selling brand $vendor was treated as a manufacturer."
        }
        # These companies make what they sell, so both fields name the same company.
        foreach ($maker in @(@{ name = 'MW Example'; company = 'Milkyway Keys' }, @{ name = 'WS Example'; company = 'Wuque Studio' },
                @{ name = 'PBTfans Example'; company = 'PBTfans' }, @{ name = 'XMI Example'; company = 'XMI' })) {
            $entry = Get-TestInferred $maker.name $null
            Assert ($entry.manufacturer -ceq $maker.company -and $entry.brand -ceq $maker.company) "$($maker.name) did not record $($maker.company) as both seller and maker."
        }
        # CRP sells the line that Hammerworks builds, so both roles are recorded.
        $crp = Get-TestInferred 'CRP Example' $null
        Assert ($crp.manufacturer -ceq 'Hammerworks' -and $crp.brand -ceq 'CRP') 'CRP did not record Hammerworks as its maker.'
        foreach ($alias in @('21KB Example', 'Xiami Example')) {
            Assert ((Get-TestInferred $alias $null).manufacturer -ceq 'XMI') "$alias did not resolve to XMI."
        }
        $explicit = Get-TestInferred 'GMK Example' ([ordered]@{ manufacturer = 'Other Factory'; profile = 'SA'; material = 'PBT' })
        Assert ($explicit.manufacturer -ceq 'Other Factory' -and $explicit.profile -ceq 'SA' -and $explicit.material -ceq 'PBT') 'An inferred value displaced a source value.'
        Assert ((Add-CatalogInferredFields (Get-NormalizedCatalogEntry ([ordered]@{ name = 'GMK Example' }) 'switches') 'switches').Contains('material') -eq $false) 'Keycap rules leaked into the switch catalog.'
    }
    Test-Case 'A leading company names the seller, and a declared factory also names the maker' {
        function Get-TestInferred2([string]$Name, [string]$Kind, $Seed) {
            $entry = [ordered]@{ name = $Name }
            if ($Seed) { foreach ($field in $Seed.Keys) { $entry[$field] = $Seed[$field] } }
            Add-CatalogInferredFields (Get-NormalizedCatalogEntry $entry $Kind) $Kind
        }
        # A declared factory makes what it names, so brand and manufacturer agree and brand goes.
        $gateron = Get-TestInferred2 'Gateron Oil King' 'switches' $null
        Assert ($gateron.manufacturer -ceq 'Gateron' -and $gateron.brand -ceq 'Gateron') 'Factory did not supply both roles.'
        # A selling brand keeps its own field when someone else made the switch.
        $vendor = Get-TestInferred2 'LEOBOG Reaper' 'switches' ([ordered]@{ manufacturer = 'SOAI' })
        Assert ($vendor.brand -ceq 'LEOBOG' -and $vendor.manufacturer -ceq 'SOAI') 'Selling brand was lost or overwrote the maker.'
        $unknown = Get-TestInferred2 'Skyloong Lychee' 'switches' $null
        Assert ($unknown.brand -ceq 'Skyloong' -and -not $unknown.Contains('manufacturer')) 'A seller was treated as a factory.'
        # Cherry is both a profile and a company; on a keycap set the leading word is the shape.
        $set = Get-TestInferred2 'Cherry Wisteria' 'keycaps' $null
        Assert ($set.profile -ceq 'Cherry' -and -not $set.Contains('brand')) 'A profile word was recorded as a selling brand.'
        $switch = Get-TestInferred2 'Cherry MX Black' 'switches' $null
        Assert ($switch.manufacturer -ceq 'Cherry') 'Cherry stopped naming its own switches.'
        # A profile made by one company states its maker, and MTNU also states its plastic.
        $dcs = Get-TestInferred2 'DCS Example' 'keycaps' $null
        Assert ($dcs.profile -ceq 'DCS' -and $dcs.manufacturer -ceq 'Signature Plastics') 'DCS did not supply its maker.'
        $mtnu = Get-TestInferred2 'MTNU Example' 'keycaps' $null
        Assert ($mtnu.profile -ceq 'MTNU' -and $mtnu.manufacturer -ceq 'GMK' -and $mtnu.material -ceq 'PBT') 'MTNU did not supply its maker and plastic.'
        Assert ((Get-TestInferred2 'GMK MTNU Welles' 'keycaps' $null).material -ceq 'PBT') 'A GMK MTNU set did not take the profile plastic.'
        Assert ((Get-TestInferred2 'GMK Olivia' 'keycaps' $null).material -ceq 'ABS') 'A plain GMK set lost its plastic.'
        # Source values still win over every rule above.
        $explicit = Get-TestInferred2 'Gateron Oil King' 'switches' ([ordered]@{ manufacturer = 'Other Factory'; brand = 'Other Brand' })
        Assert ($explicit.manufacturer -ceq 'Other Factory' -and $explicit.brand -ceq 'Other Brand') 'An inferred value displaced a source value.'
        foreach ($pair in @(@{ profile = 'DSS'; maker = 'Signature Plastics' }, @{ profile = 'KAT'; maker = 'Keyreative' }, @{ profile = 'KAM'; maker = 'Keyreative' })) {
            $entry = Get-TestInferred2 "$($pair.profile) Example" 'keycaps' $null
            Assert ($entry.profile -ceq $pair.profile -and $entry.manufacturer -ceq $pair.maker) "$($pair.profile) did not supply $($pair.maker)."
        }
        # Confirmed keycap lines. CRP-X is its own shape, and its hyphen keeps CRP's
        # Cherry default from reaching it.
        foreach ($case in @(
            @{ name = 'XMI Example'; profile = 'Cherry'; material = 'PBT'; maker = $null }
            @{ name = 'CRP Example'; profile = 'Cherry'; material = 'PBT'; maker = $null }
            @{ name = 'CRP-X Example'; profile = 'CRP-X'; material = 'PBT'; maker = $null }
            @{ name = 'JTK Example'; profile = 'Cherry'; material = 'ABS'; maker = 'JTK' }
            @{ name = 'HSA Example'; profile = 'HSA'; material = 'ABS'; maker = 'JTK' }
        )) {
            $entry = Get-TestInferred2 $case.name 'keycaps' $null
            Assert ($entry.profile -ceq $case.profile) "$($case.name) profile was $($entry.profile), expected $($case.profile)."
            Assert ($entry.material -ceq $case.material) "$($case.name) material was $($entry.material), expected $($case.material)."
            if ($case.maker) { Assert ($entry.manufacturer -ceq $case.maker) "$($case.name) did not record $($case.maker)." }
        }
        # SA and DSA are produced by several factories, so the shape names no maker.
        foreach ($profile in @('SA', 'DSA')) {
            $entry = Get-TestInferred2 "$profile Example" 'keycaps' $null
            Assert ($entry.profile -ceq $profile -and -not $entry.Contains('manufacturer')) "$profile invented a maker."
        }
    }
    Test-Case 'Inferred keycap fields yield to a reviewed omission' {
        $records = New-TestRecords
        $state = (New-CatalogCandidate (Read-CatalogState (Join-Path $testRoot 'inference-empty')) $records).state
        $entry = @($state.catalogs.keycaps.entries | Where-Object { $_.name -like 'GMK *' })[0]
        Assert ($entry.material -ceq 'ABS' -and $entry.profile -ceq 'Cherry') 'Merged entry did not gain the declared shape and plastic.'
        $state.overrides.entries["keycaps/$($entry.id)"] = [ordered]@{ material = $null; profile = $null }
        $result = New-CatalogCandidate $state $records
        $after = @($result.state.catalogs.keycaps.entries | Where-Object { $_.id -ceq $entry.id })[0]
        Assert (-not $after.Contains('material') -and -not $after.Contains('profile')) 'Inference overrode a deliberate omission.'
    }
    Test-Case 'Alias edits invalidate candidates and never alter upstream identity rules' {
        $originalPath = $script:CatalogAliasesPath
        $temporaryPath = Join-Path $testRoot 'aliases.json'
        [IO.File]::Copy($originalPath, $temporaryPath)
        try {
            $script:CatalogAliasesPath = $temporaryPath; $script:CatalogAliasCache = $null
            $root = Join-Path $testRoot 'alias-receipt-root'
            $run = Join-Path $testRoot 'alias-receipt-run'
            Save-TestFetch $run
            Save-CatalogCandidate $root $run
            [IO.File]::AppendAllText($temporaryPath, ' ')
            Assert-Throws { Publish-CatalogCandidate $root $run } 'alias map changed'
            Assert ((Get-CatalogMatchName 'IKYEX Example' -SourceIdentity) -ceq 'ikyexexample') 'Source digest followed display aliases.'
            Assert ((Get-CatalogMatchName 'IKYEX Example') -ceq 'ikeyxexample') 'Duplicate matcher missed spelling alias.'
        } finally { $script:CatalogAliasesPath = $originalPath; $script:CatalogAliasCache = $null }
    }
}
