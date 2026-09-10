$script:CatalogFiles = @('Assets/Catalogs/switches.json', 'Assets/Catalogs/keycaps.json', 'Scripts/Catalogs/sources.json')

function Read-CatalogState([string]$Root) {
    $state = [ordered]@{ catalogs = [ordered]@{}; bindings = @(); overrides = [ordered]@{ schemaVersion = 1; bindings = [ordered]@{}; entries = [ordered]@{}; excluded = [ordered]@{} } }
    foreach ($kind in @('switches', 'keycaps')) {
        $path = Get-CatalogRelativePath $Root "Assets/Catalogs/$kind.json"
        $state.catalogs[$kind] = if (Test-Path -LiteralPath $path) { Read-CatalogJson $path } else { [ordered]@{ schemaVersion = 1; catalogVersion = 0; totalCount = 0; entries = @() } }
    }
    $path = Get-CatalogRelativePath $Root 'Scripts/Catalogs/sources.json'
    if (Test-Path -LiteralPath $path) {
        $sources = Read-CatalogJson $path
        if ($sources.schemaVersion -ne 1) { throw 'Unsupported source mapping version.' }
        $state.bindings = @($sources.bindings)
    }
    $path = Get-CatalogRelativePath $Root 'Scripts/Catalogs/overrides.json'
    if (Test-Path -LiteralPath $path) { $state.overrides = Read-CatalogJson $path }
    $state
}

function Test-CatalogState($State, [switch]$AllowEmpty) {
    $null = Get-CatalogAliases
    $ids = @{}
    foreach ($kind in @('switches', 'keycaps')) {
        $catalog = $State.catalogs[$kind]
        if ($catalog.schemaVersion -ne 1 -or $catalog.catalogVersion -isnot [int] -or $catalog.catalogVersion -lt $(if ($AllowEmpty) { 0 } else { 1 })) { throw "Invalid $kind version." }
        if ($catalog.entries -isnot [array] -or (-not $AllowEmpty -and $catalog.entries.Count -eq 0)) { throw "Empty or invalid $kind catalog." }
        if (@($catalog.Keys | Where-Object { $_ -notin @('schemaVersion', 'catalogVersion', 'totalCount', 'entries') }).Count) { throw "Unknown $kind envelope field." }
        foreach ($entry in $catalog.entries) {
            if (-not $entry.Contains('id') -or $entry.id -isnot [string] -or $entry.id -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') { throw 'Invalid catalog ID.' }
            if (-not $entry.Contains('name') -or $entry.name -isnot [string] -or -not $entry.name.Trim()) { throw "Missing name: $($entry.id)" }
            $key = "$kind/$($entry.id)"
            if ($ids.ContainsKey($key)) { throw "Duplicate ID: $key" }
            $ids[$key] = $true
            foreach ($field in $entry.Keys) {
                if ($field -notin (Get-CatalogFields $kind)) { throw "Unsupported field '$field' on $key" }
                if ($entry[$field] -isnot [string] -or -not $entry[$field].Trim() -or $entry[$field] -cne (Get-CatalogName $entry[$field])) { throw "Invalid $field on $key" }
            }
            if ($entry.Contains('switchType') -and $entry.switchType -cnotin @('linear', 'tactile', 'clicky')) { throw "Invalid switch type: $key" }
        }
        if (-not $catalog.Contains('totalCount') -or $catalog.totalCount -isnot [int] -or $catalog.totalCount -ne $catalog.entries.Count) { throw "Invalid $kind total count." }
    }
    $seen = @{}
    $referenced = @{}
    foreach ($binding in $State.bindings) {
        $key = "$($binding.source):$($binding.sourceId)"
        if ($seen.ContainsKey($key)) { throw "Duplicate source mapping: $key" }
        $seen[$key] = $true
        $target = "$($binding.kind)/$($binding.id)"
        if (-not $ids.ContainsKey($target)) { throw "Source mapping points to missing ID: $target" }
        if (-not $script:CatalogSources.Contains($binding.source) -or $script:CatalogSources[$binding.source].kind -ne $binding.kind) { throw "Invalid source kind: $key" }
        if (-not $binding.Contains('observed') -or -not $binding.observed.Contains('name')) { throw "Missing source observation: $key" }
        $referenced[$target] = $true
    }
    foreach ($id in $ids.Keys) { if (-not $referenced.ContainsKey($id)) { throw "Missing provenance: $id" } }
    if ($State.overrides.schemaVersion -ne 1) { throw 'Unsupported overrides version.' }
    foreach ($section in @('bindings', 'entries', 'excluded')) {
        if (-not $State.overrides.Contains($section) -or $State.overrides[$section] -isnot [Collections.IDictionary]) { throw "Invalid overrides section: $section" }
    }
    foreach ($key in $State.overrides.entries.Keys) {
        if (-not $ids.ContainsKey($key)) { throw "Entry override points to missing ID: $key" }
    }
    foreach ($key in $State.overrides.bindings.Keys) {
        if (-not $seen.ContainsKey($key)) { throw "Binding override points to missing source: $key" }
    }
}

function New-CatalogCandidate($State, [array]$Records) {
    $baseline = [ordered]@{ catalogs = $State.catalogs; bindings = $State.bindings; overrides = [ordered]@{ schemaVersion = 1; bindings = [ordered]@{}; entries = [ordered]@{}; excluded = [ordered]@{} } }
    Test-CatalogState $baseline -AllowEmpty
    $report = [ordered]@{ additions = @(); changes = @(); removals = @(); duplicates = @(); conflicts = @(); missingSources = @(); rejected = @(); sourceWarnings = @(); sourceCounts = [ordered]@{} }
    $entries = @{}
    $previous = @{}
    $bindings = @{}
    $names = @{}
    $groups = @{}
    $reassigned = @{}
    $override = $State.overrides
    foreach ($kind in @('switches', 'keycaps')) {
        foreach ($entry in $State.catalogs[$kind].entries) {
            $key = "$kind/$($entry.id)"
            $entries[$key] = Get-NormalizedCatalogEntry $entry $kind
            $previous[$key] = Get-OrderedCatalogEntry $entry $kind
            $nameKey = "$kind/$(Get-CatalogMatchName $entry.name)"
            if (-not $names.ContainsKey($nameKey)) { $names[$nameKey] = @() }
            $names[$nameKey] += $entry.id
        }
    }
    foreach ($binding in $State.bindings) { $bindings["$($binding.source):$($binding.sourceId)"] = $binding }
    # Removal requires a deliberate maintainer exclusion. A missing listing or a new adapter
    # filter alone never deletes an accepted entry.
    foreach ($sourceKey in $override.excluded.Keys) {
        if ($bindings.ContainsKey($sourceKey)) {
            $binding = $bindings[$sourceKey]
            $key = "$($binding.kind)/$($binding.id)"
            $bindings.Remove($sourceKey)
            if (@($bindings.Values | Where-Object { "$($_.kind)/$($_.id)" -eq $key }).Count -eq 0) {
                $entries.Remove($key)
                $report.removals += [ordered]@{ entry = $key; reason = $override.excluded[$sourceKey] }
                $nameKey = "$($binding.kind)/$(Get-CatalogMatchName $previous[$key].name)"
                $remainingIds = @($names[$nameKey] | Where-Object { $_ -ne $binding.id })
                if ($remainingIds.Count) { $names[$nameKey] = $remainingIds } else { $names.Remove($nameKey) }
            }
        }
    }
    $observedKeys = @{}
    $newKeys = @{}
    $recordIndex = @{}
    foreach ($record in $Records) {
        $sourceKey = "$($record.source):$($record.sourceId)"
        if ($recordIndex.ContainsKey($sourceKey)) { throw "Repeated input source identity: $sourceKey" }
        $recordIndex[$sourceKey] = $record
    }
    foreach ($sourceKey in (Get-CatalogSortedStrings $recordIndex.Keys)) {
        $record = $recordIndex[$sourceKey]
        $sourceEntry = $record.entry
        $normalizedRecord = [ordered]@{}
        foreach ($field in $record.Keys) { $normalizedRecord[$field] = $record[$field] }
        $record = $normalizedRecord
        $record.entry = Get-NormalizedCatalogEntry $sourceEntry $record.kind
        $sourceKey = "$($record.source):$($record.sourceId)"
        if ($observedKeys.ContainsKey($sourceKey)) { throw "Repeated input source identity: $sourceKey" }
        $observedKeys[$sourceKey] = $true
        if (-not $report.sourceCounts.Contains($record.source)) { $report.sourceCounts[$record.source] = 0 }
        $report.sourceCounts[$record.source]++
        if ($record.Contains('warnings')) {
            foreach ($warning in $record.warnings) { $report.sourceWarnings += [ordered]@{ source = $sourceKey; name = $record.entry.name; warning = $warning } }
        }
        $reason = $record.excluded
        if ($override.excluded.Contains($sourceKey)) { $reason = $override.excluded[$sourceKey] }
        if ($reason) {
            $report.rejected += [ordered]@{ source = $sourceKey; name = $record.entry.name; reason = $reason }
            continue
        }
        $explicit = $override.bindings.Contains($sourceKey)
        if ($explicit) { $id = $override.bindings[$sourceKey] }
        elseif ($bindings.ContainsKey($sourceKey)) { $id = $bindings[$sourceKey].id }
        else {
            $id = Get-CatalogSlug $record.entry.name
            $nameKey = "$($record.kind)/$(Get-CatalogMatchName $record.entry.name)"
            if ($names.ContainsKey($nameKey)) {
                $report.duplicates += [ordered]@{ source = $sourceKey; name = $record.entry.name; proposedIds = @($names[$nameKey] | Sort-Object -Unique) }
            }
            $baseId = $id
            $suffix = 2
            while ($entries.ContainsKey("$($record.kind)/$id")) { $id = "$baseId-$suffix"; $suffix++ }
            if ($id -ne $baseId -and -not $names.ContainsKey($nameKey)) {
                $report.duplicates += [ordered]@{ source = $sourceKey; name = $record.entry.name; proposedIds = @($baseId); reason = 'Slug collision; explicit ID required' }
            }
        }
        if ($id -isnot [string] -or $id -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') { throw "Invalid binding ID: $sourceKey" }
        $key = "$($record.kind)/$id"
        if ($explicit -and $bindings.ContainsKey($sourceKey) -and $bindings[$sourceKey].id -ne $id) {
            $oldKey = "$($record.kind)/$($bindings[$sourceKey].id)"
            $reassigned[$oldKey] = $key
        }
        if (-not $entries.ContainsKey($key)) {
            $entries[$key] = [ordered]@{ id = $id; name = $record.entry.name }
            $newKeys[$key] = $true
            $nameKey = "$($record.kind)/$(Get-CatalogMatchName $record.entry.name)"
            if (-not $names.ContainsKey($nameKey)) { $names[$nameKey] = @() }
            $names[$nameKey] += $id
        }
        if (-not $groups.ContainsKey($key)) { $groups[$key] = @() }
        $groups[$key] += $record
        $bindings[$sourceKey] = [ordered]@{ source = $record.source; sourceId = $record.sourceId; kind = $record.kind; id = $id; url = $record.url; observed = $sourceEntry }
        if ($record.Contains('notes') -and $record.notes) { $bindings[$sourceKey].notes = $record.notes }
    }
    # A reviewed binding change may retire an exact duplicate, but never a still-referenced variant.
    $referenced = @{}
    foreach ($binding in $bindings.Values) { $referenced["$($binding.kind)/$($binding.id)"] = $true }
    foreach ($key in (Get-CatalogSortedStrings $reassigned.Keys)) {
        if (-not $referenced.ContainsKey($key)) {
            $entries.Remove($key)
            $report.removals += [ordered]@{ entry = $key; reason = "Reviewed duplicate rebound to $($reassigned[$key])" }
        }
    }
    # Keep past observations when listings disappear so identity and metadata survive retirement.
    foreach ($sourceKey in (Get-CatalogSortedStrings $bindings.Keys)) {
        if (-not $observedKeys.ContainsKey($sourceKey)) { $report.missingSources += $sourceKey }
    }
    foreach ($key in (Get-CatalogSortedStrings $entries.Keys)) {
        $kind, $id = $key -split '/', 2
        $entry = $entries[$key]
        $recordsForEntry = if ($groups.ContainsKey($key)) { @($groups[$key]) } else { @() }
        $fieldsOverride = if ($override.entries.Contains($key)) { $override.entries[$key] } else { [ordered]@{} }
        foreach ($field in $fieldsOverride.Keys) {
            if ($field -eq 'id' -or $field -notin (Get-CatalogFields $kind)) { throw "Invalid override field: $key/$field" }
        }
        foreach ($field in ((Get-CatalogFields $kind) | Where-Object { $_ -ne 'id' })) {
            if ($fieldsOverride.Contains($field)) {
                if ($null -eq $fieldsOverride[$field]) { $entry.Remove($field) }
                else { $entry[$field] = $fieldsOverride[$field] }
                continue
            }
            $values = @($recordsForEntry | Where-Object { $_.entry.Contains($field) } | ForEach-Object { $_.entry[$field] } | Sort-Object -Unique)
            if ($values.Count -gt 1) {
                # Alternate spellings can share an identity; preserve the accepted display name.
                $matchedNames = @($values | ForEach-Object { Get-CatalogMatchName $_ } | Sort-Object -Unique)
                if ($field -eq 'name' -and $matchedNames.Count -eq 1) { continue }
                $report.conflicts += [ordered]@{ entry = $key; field = $field; values = $values }
            } elseif ($values.Count -eq 1) { $entry[$field] = $values[0] }
        }
        $entries[$key] = Add-CatalogInferredFields (Get-NormalizedCatalogEntry $entry $kind) $kind
        # A reviewed omission takes precedence over optional metadata from alias relationships.
        foreach ($field in $fieldsOverride.Keys) {
            if ($null -eq $fieldsOverride[$field]) { $entries[$key].Remove($field) }
        }
        if ($newKeys.ContainsKey($key)) { $report.additions += $key }
        elseif ((ConvertTo-CatalogJson $previous[$key]) -cne (ConvertTo-CatalogJson $entries[$key])) {
            $report.changes += [ordered]@{ entry = $key; before = $previous[$key]; after = $entries[$key] }
        }
    }
    $candidate = [ordered]@{ catalogs = [ordered]@{}; bindings = @((Get-CatalogSortedStrings $bindings.Keys) | ForEach-Object { $bindings[$_] }); overrides = $override }
    foreach ($kind in @('switches', 'keycaps')) {
        $items = @((Get-CatalogSortedStrings $entries.Keys) | Where-Object { $_.StartsWith("$kind/") } | ForEach-Object { $entries[$_] })
        $old = $State.catalogs[$kind]
        $changed = (ConvertTo-CatalogJson $old.entries) -cne (ConvertTo-CatalogJson $items)
        $candidate.catalogs[$kind] = [ordered]@{ schemaVersion = 1; catalogVersion = [int]$old.catalogVersion + [int]$changed; totalCount = $items.Count; entries = $items }
    }
    # Unusual drops are visible even though existing records are never removed.
    foreach ($source in $script:CatalogSources.Keys) {
        $oldCount = @($State.bindings | Where-Object { $_.source -eq $source }).Count
        $currentCount = @($candidate.bindings | Where-Object { $_.source -eq $source -and $observedKeys.ContainsKey("$($_.source):$($_.sourceId)") }).Count
        if ($oldCount -ge 20 -and $currentCount -lt $oldCount * 0.5) { throw "Suspicious source count drop for $source ($oldCount to $currentCount). Accepted files were not changed." }
    }
    Test-CatalogState $candidate
    [ordered]@{ state = $candidate; report = $report }
}

function Save-CatalogCandidate([string]$Root, [string]$Run) {
    $script:CatalogAliasCache = $null
    $output = Join-Path $Run 'candidate'
    $receiptPath = Join-Path $output 'receipt.json'
    # A failed replay must not leave a previously promotable receipt behind.
    if (Test-Path -LiteralPath $receiptPath) { [IO.File]::Delete($receiptPath) }
    $state = Read-CatalogState $Root
    # Overrides may refer to entries first introduced in this candidate; full validation follows merging.
    $records = Read-CatalogFetch $Run
    $result = New-CatalogCandidate $state $records
    $receipt = [ordered]@{ schemaVersion = 1; baseline = [ordered]@{}; files = [ordered]@{}; fetchHash = Get-CatalogHash (Join-Path $Run 'fetch.json'); overridesHash = Get-CatalogHash (Join-Path $Root 'Scripts/Catalogs/overrides.json'); aliasesHash = Get-CatalogHash $script:CatalogAliasesPath }
    foreach ($kind in @('switches', 'keycaps')) { Write-CatalogJson (Join-Path $output "Assets/Catalogs/$kind.json") $result.state.catalogs[$kind] }
    $fetch = Read-CatalogJson (Join-Path $Run 'fetch.json')
    $sourceFetchDates = [ordered]@{}
    foreach ($source in $script:CatalogSources.Keys) {
        $snapshot = $fetch.sources[$source]
        $sourceFetchDates[$source] = if ($snapshot.Contains('fetchedAt')) { $snapshot.fetchedAt } else { $fetch.fetchedAt }
    }
    Write-CatalogJson (Join-Path $output 'Scripts/Catalogs/sources.json') ([ordered]@{
        schemaVersion = 1
        fetchedAt = $fetch.fetchedAt
        sourceFetchDates = $sourceFetchDates
        matrixCommit = $fetch.sources.matrix.commit
        bindings = $result.state.bindings
    })
    Write-CatalogJson (Join-Path $Run 'report.json') $result.report
    foreach ($relative in $script:CatalogFiles) {
        $receipt.baseline[$relative] = Get-CatalogHash (Get-CatalogRelativePath $Root $relative)
        $receipt.files[$relative] = Get-CatalogHash (Get-CatalogRelativePath $output $relative)
    }
    $receipt.reportHash = Get-CatalogHash (Join-Path $Run 'report.json')
    Write-CatalogJson $receiptPath $receipt
    Write-Host "Candidate: $output"
    Write-Host "Added: $($result.report.additions.Count); changed: $($result.report.changes.Count); removed by explicit decision: $($result.report.removals.Count); duplicate decisions: $($result.report.duplicates.Count); metadata conflicts: $($result.report.conflicts.Count); rejected: $($result.report.rejected.Count)"
    if ($result.report.duplicates.Count -or $result.report.conflicts.Count) { throw 'Review report.json and add overrides, then Replay before promotion.' }
}

function Publish-CatalogCandidate([string]$Root, [string]$Run) {
    $output = Join-Path $Run 'candidate'
    $receipt = Read-CatalogJson (Join-Path $output 'receipt.json')
    if ($receipt.schemaVersion -ne 1 -or $receipt.files.Count -ne $script:CatalogFiles.Count) { throw 'Invalid candidate receipt.' }
    if (-not $receipt.Contains('aliasesHash') -or $receipt.aliasesHash -cne (Get-CatalogHash $script:CatalogAliasesPath)) { throw 'Candidate alias map changed. Replay first.' }
    if ($receipt.fetchHash -cne (Get-CatalogHash (Join-Path $Run 'fetch.json')) -or $receipt.overridesHash -cne (Get-CatalogHash (Join-Path $Root 'Scripts/Catalogs/overrides.json')) -or $receipt.reportHash -cne (Get-CatalogHash (Join-Path $Run 'report.json'))) { throw 'Candidate inputs or report changed. Replay first.' }
    $report = Read-CatalogJson (Join-Path $Run 'report.json')
    if ($report.duplicates.Count -or $report.conflicts.Count) { throw 'Unresolved candidate conflicts.' }
    $paths = @()
    foreach ($relative in $script:CatalogFiles) {
        $target = Get-CatalogRelativePath $Root $relative
        $source = Get-CatalogRelativePath $output $relative
        if ($receipt.baseline[$relative] -cne (Get-CatalogHash $target)) { throw "Accepted files changed since Replay: $relative" }
        if ($receipt.files[$relative] -cne (Get-CatalogHash $source)) { throw "Candidate checksum mismatch: $relative" }
        $paths += [ordered]@{ target = $target; source = $source; prior = $(if (Test-Path -LiteralPath $target) { [IO.File]::ReadAllBytes($target) } else { $null }) }
    }
    $candidate = Read-CatalogState $output
    $candidate.overrides = (Read-CatalogState $Root).overrides
    Test-CatalogState $candidate
    $written = @()
    try {
        foreach ($pair in $paths) {
            [IO.Directory]::CreateDirectory((Split-Path $pair.target -Parent)) | Out-Null
            $temporary = $pair.target + '.import-' + [guid]::NewGuid().ToString('N')
            try {
                [IO.File]::Copy($pair.source, $temporary)
                if (Test-Path -LiteralPath $pair.target) { [IO.File]::Replace($temporary, $pair.target, [NullString]::Value) }
                else { [IO.File]::Move($temporary, $pair.target) }
                $written += $pair
            } finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
        }
    } catch {
        foreach ($pair in $written) {
            if ($null -eq $pair.prior) { [IO.File]::Delete($pair.target) }
            else { [IO.File]::WriteAllBytes($pair.target, $pair.prior) }
        }
        throw
    }
    Write-Host 'Promoted catalogs and source mappings. Review and commit these files with overrides.json.'
}
