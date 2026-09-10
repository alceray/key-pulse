$script:CatalogSources = [ordered]@{
    switchoddities = @{ kind = 'switches'; endpoint = 'https://switchoddities.com/collections/switch-samples/products.json'; host = 'https://switchoddities.com' }
    theremingoat = @{ kind = 'switches'; endpoint = 'https://drive.google.com/uc?export=download&id=1lEsJaTX4nwtxcx2WL1EcwuwokWyqnuDv' }
    theremingoatscores = @{ kind = 'switches'; endpoint = 'https://raw.githubusercontent.com/ThereminGoat/switch-scores/refs/heads/master/1-Composite%20Overall%20Total%20Score%20Sheet.csv' }
    unikeys = @{ kind = 'switches'; endpoint = 'https://unikeyboards.com/collections/keyboard-switches/products.json'; host = 'https://unikeyboards.com' }
    matrix = @{ kind = 'keycaps'; repository = 'matrixzj/matrixzj.github.io' }
    divinikey = @{ kind = 'keycaps'; endpoint = 'https://divinikey.com/collections/keycap-sets/products.json'; host = 'https://divinikey.com' }
    kbdfans = @{ kind = 'keycaps'; endpoint = 'https://kbdfans.com/collections/keycaps/products.json'; host = 'https://kbdfans.com' }
    dcswiki = @{ kind = 'keycaps'; endpoint = 'https://dcs.wiki/keycaps' }
    keycaplendar = @{ kind = 'keycaps'; endpoint = 'https://firestore.googleapis.com/v1/projects/keycaplendar/databases/(default)/documents/keysets' }
}
. "$PSScriptRoot/DcsWiki.ps1"
. "$PSScriptRoot/ThereminGoat.ps1"
. "$PSScriptRoot/ThereminGoatScores.ps1"
. "$PSScriptRoot/UniKeys.ps1"
. "$PSScriptRoot/KeycapLendar.ps1"

function Get-CatalogResponse([string]$Url, [string]$OutFile) {
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        try {
            $parameters = @{ UseBasicParsing = $true; Uri = $Url; TimeoutSec = 30; Headers = @{ 'User-Agent' = 'KeyPulse-Catalog-Import/1.0' } }
            if ($OutFile) { $parameters.OutFile = $OutFile }
            $response = Invoke-WebRequest @parameters
            Start-Sleep -Milliseconds 200
            if ($OutFile) { return }
            return $response.Content
        } catch {
            $response = if ($_.Exception.PSObject.Properties['Response']) { $_.Exception.Response } else { $null }
            if ($null -ne $response) {
                $status = [int]$response.StatusCode
                if ($status -ne 429 -and $status -lt 500) { throw }
            }
            if ($attempt -eq 3) { throw }
            $delay = [math]::Pow(2, $attempt + 1)
            if ($null -ne $response -and $response.Headers['Retry-After']) {
                $retry = $response.Headers['Retry-After']
                $seconds = 0
                if ([int]::TryParse($retry, [ref]$seconds)) { $delay = [math]::Max($delay, $seconds) }
                else {
                    $date = [DateTimeOffset]::MinValue
                    if ([DateTimeOffset]::TryParse($retry, [ref]$date)) { $delay = [math]::Max($delay, ($date - [DateTimeOffset]::UtcNow).TotalSeconds) }
                }
            }
            if ($delay -gt 60) { throw 'Source requested a long retry delay. Retry the fetch later.' }
            Start-Sleep -Seconds ([int][math]::Ceiling($delay))
        }
    }
}

function Test-CatalogProductPage($Data, $Seen) {
    if (-not $Data.Contains('products') -or $Data.products -isnot [array]) { throw 'Expected a products array.' }
    foreach ($p in $Data.products) {
        if (-not $p.Contains('id') -or -not $p.Contains('title') -or -not (Get-CatalogName $p.title)) { throw 'Product is missing its ID or name.' }
        $key = [string]$p.id
        if ($Seen.ContainsKey($key)) { throw "Repeated product/page detected: $key. Retry a fresh fetch." }
        $Seen[$key] = $true
    }
}

function Read-CatalogProductPage([string]$Path) {
    $data = ConvertFrom-Json ([IO.File]::ReadAllText($Path))
    if (-not $data.PSObject.Properties['products'] -or $data.products -isnot [array]) { throw 'Expected a products array.' }
    # Project before converting nested objects: photos, prices and shipping data are not import inputs.
    $products = @($data.products | ForEach-Object {
        $p = $_
        $values = [ordered]@{}
        foreach ($field in @('id', 'title', 'handle', 'body_html', 'vendor', 'options')) {
            if ($p.PSObject.Properties[$field]) { $values[$field] = ConvertTo-CatalogMap $p.$field }
        }
        $values.variants = @($p.variants | ForEach-Object { [ordered]@{ id = $_.id; title = $_.title } })
        $values
    })
    [ordered]@{ products = $products }
}

# Copy an earlier snapshot's verified downloads so adding one adapter does not re-download
# every source. Each file is checked before and after copying, and the original retrieval
# date travels with the source rather than being restamped as today.
function Copy-CatalogSnapshot([string]$Run, [string]$Reuse) {
    $previous = Read-CatalogJson (Join-Path $Reuse 'fetch.json')
    if ($previous.schemaVersion -ne 1) { throw 'Unsupported reused fetch manifest.' }
    $carried = [ordered]@{}
    foreach ($source in $script:CatalogSources.Keys) {
        if (-not $previous.sources.Contains($source)) { continue }
        $snapshot = $previous.sources[$source]
        $files = @()
        foreach ($file in @($snapshot.files)) {
            $from = Get-CatalogRelativePath $Reuse $file.path
            if ((Get-CatalogHash $from) -cne $file.sha256) { throw "Reused snapshot checksum mismatch: $source/$($file.path)" }
            $to = Get-CatalogRelativePath $Run $file.path
            [IO.Directory]::CreateDirectory((Split-Path $to -Parent)) | Out-Null
            [IO.File]::Copy($from, $to)
            if ((Get-CatalogHash $to) -cne $file.sha256) { throw "Reused snapshot copy mismatch: $source/$($file.path)" }
            $files += $file
        }
        if ($files.Count -eq 0) { throw "Empty reused source: $source" }
        $entry = [ordered]@{}
        foreach ($field in $snapshot.Keys) { if ($field -ne 'files') { $entry[$field] = $snapshot[$field] } }
        if (-not $entry.Contains('fetchedAt')) { $entry.fetchedAt = $previous.fetchedAt }
        $entry.files = $files
        $entry.reusedFrom = Split-Path $Reuse -Leaf
        $carried[$source] = $entry
        Write-Host "Reusing $source from $($entry.reusedFrom) ($($files.Count) file(s), fetched $($entry.fetchedAt))"
    }
    $dropped = @($previous.sources.Keys | Where-Object { -not $script:CatalogSources.Contains($_) })
    if ($dropped.Count) { Write-Host "Dropped unregistered source(s): $($dropped -join ', ')" }
    $carried
}

function Save-CatalogFetch([string]$Run, [string]$Reuse = '') {
    if (Test-Path -LiteralPath $Run) { throw 'Fetch requires a new run directory; use Replay for an existing run.' }
    [IO.Directory]::CreateDirectory($Run) | Out-Null
    $manifest = [ordered]@{ schemaVersion = 1; fetchedAt = [DateTime]::UtcNow.ToString('o'); sources = [ordered]@{} }
    $carried = if ($Reuse) { Copy-CatalogSnapshot $Run $Reuse } else { [ordered]@{} }
    foreach ($source in $script:CatalogSources.Keys) {
        if ($carried.Contains($source)) { $manifest.sources[$source] = $carried[$source]; continue }
        $files = @()
        $info = $script:CatalogSources[$source]
        Write-Host "Fetching $source..."
        if ($source -eq 'dcswiki') {
            $manifest.sources[$source] = Save-DcsWikiFetch $Run
        } elseif ($source -eq 'keycaplendar') {
            $manifest.sources[$source] = Save-KeycapLendarFetch $Run
        } elseif ($source -eq 'theremingoat') {
            $manifest.sources[$source] = Save-ThereminGoatFetch $Run
        } elseif ($source -eq 'theremingoatscores') {
            $manifest.sources[$source] = Save-ThereminGoatScoresFetch $Run
        } elseif ($source -eq 'matrix') {
            $commitData = ConvertFrom-Json (Get-CatalogResponse "https://api.github.com/repos/$($info.repository)/commits/master")
            $commit = $commitData.sha
            if ($commit -notmatch '^[a-f0-9]{40}$') { throw 'Invalid Matrix commit.' }
            $listing = ConvertFrom-Json (Get-CatalogResponse "https://api.github.com/repos/$($info.repository)/contents/docs/gmk-keycaps?ref=$commit")
            $listing = @($listing)
            if ($listing.Count -eq 0 -or $listing.Count -ge 1000) { throw 'Empty or potentially truncated Matrix listing.' }
            $docs = @($listing | Where-Object { $_.type -eq 'file' -and $_.name.EndsWith('.md') } | Sort-Object name)
            $index = 0
            foreach ($doc in $docs) {
                $url = "https://raw.githubusercontent.com/$($info.repository)/$commit/$($doc.path)"
                $content = Get-CatalogResponse $url
                # Repository filenames may contain ':' and other characters Windows cannot store.
                $relative = ('raw/matrix/{0:D4}.md' -f $index)
                $path = Get-CatalogRelativePath $Run $relative
                [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
                [IO.File]::WriteAllText($path, $content, (New-Object Text.UTF8Encoding $false))
                $files += [ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; sourcePath = $doc.path; url = $url }
                $index++
                if ($index % 50 -eq 0) { Write-Host "Matrix: $index / $($docs.Count) documents" }
            }
            $manifest.sources[$source] = [ordered]@{ commit = $commit; files = $files }
        } else {
            $seen = @{}
            for ($page = 1; $page -le 100; $page++) {
                $url = "$($info.endpoint)?limit=250&page=$page"
                $data = ConvertTo-CatalogMap (ConvertFrom-Json (Get-CatalogResponse $url))
                Test-CatalogProductPage $data $seen
                $relative = "raw/$source/page-$page.json"
                $path = Get-CatalogRelativePath $Run $relative
                Write-CatalogJson $path $data
                $files += [ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $url }
                Write-Host "$source page ${page}: $($data.products.Count) products"
                if ($data.products.Count -eq 0) { break }
            }
            if ($seen.Count -eq 0 -or $page -gt 100) { throw "Empty or incomplete $source import." }
            $manifest.sources[$source] = [ordered]@{ files = $files }
        }
    }
    # A missing manifest identifies an incomplete fetch. Replay never accepts partial downloads.
    Write-CatalogJson (Join-Path $Run 'fetch.json') $manifest
}

function Get-CatalogLabel([string]$Body, [string]$Pattern) {
    $values = @([regex]::Matches($Body, "(?im)^[ \t]*(?:[-*][ \t]*)?(?:$Pattern)[ \t]*:?[ \t]*(\S[^\r\n]*?)[ \t]*$") | ForEach-Object {
        Get-CatalogName ($_.Groups[1].Value -replace '\[([^\]]+)\]\([^)]+\)', '$1')
    } | Where-Object { $_ -and $_ -notmatch '^(unknown|n/?a|tbd|\?+)$|^https?://|^\+\s*Designer\b' } | Sort-Object -Unique)
    if ($values.Count -eq 1 -and $values[0].Length -le 100) { return $values[0] }
    return $null
}

function Convert-CatalogProduct($Product, [string]$Source) {
    $kind = $script:CatalogSources[$Source].kind
    $name = Get-CatalogName $Product.title
    $name = $name -replace '(?i)^\s*\[(?:group buy|gb|pre-?order|in.stock|restock|extras)\]\s*', ''
    $name = $name -replace '(?i)\s*\(\d+\s*(?:pcs|pieces|pack)\)\s*$', ''
    $name = $name -replace '(?i)\s+keycaps?(?:\s+set)?(?:\s+(?:dye[ -]?sub|double(?:/triple)?shot|double[ -]?shot)\s+(?:ABS|PBT))?\s*$', ''
    $name = $name.Trim()
    $reason = $null
    # Modification wording is read before the lube wording is dropped, or a hand-lubed
    # listing would look like an ordinary product.
    if ($kind -eq 'switches') {
        $reason = Get-CatalogModificationReason $name
        if (-not $reason) { $name = Get-CatalogLubeFreeName $name }
    }
    $entry = [ordered]@{ name = $name }
    if (-not $reason) { $reason = Get-CatalogSpecimenReason $name }
    if ($name -match '(?i)^Configurator\b') { $reason = 'Configuration placeholder, not a named product' }
    if ($name -match '(?i)\b(tester|sampler|sample pack|switch pack|mystery|random|grab bag|puller|opener|keychain|deskmat|storage|display case|stabilizer)\b') { $reason = 'Accessory or assorted pack' }
    $body = Get-CatalogText ([string]$Product.body_html)
    $manufacturer = Get-CatalogLabel $body 'Manufactured by|Manufacturer(?:[ \t]*:|[ \t]+)'
    $designer = Get-CatalogLabel $body 'Designed by|(?:Keycaps? set )?Designer(?:[ \t]*:|[ \t]+)'
    if ($manufacturer) { $entry.manufacturer = $manufacturer }
    if ($designer) { $entry.designer = $designer }
    $vendor = Get-CatalogName ([string]$Product.vendor)
    if ($vendor -and $vendor -notmatch '^(SwitchOddities|Divinikey|KBDfans|Unikeys|Default|Unknown|Third Party)$' -and $vendor -ine $manufacturer) { $entry.brand = $vendor }
    if ($kind -eq 'switches') {
        $types = @([regex]::Matches($name, '(?i)\b(linear|tactile|clicky)\b') | ForEach-Object { $_.Value.ToLowerInvariant() } | Sort-Object -Unique)
        if ($types.Count -eq 1) { $entry.switchType = $types[0] }
        # Only explicit labels are used from descriptions, never comparisons in review prose.
        $labelType = Get-CatalogLabel $body 'Switch type(?:[ \t]*:|[ \t]+)|Type[ \t]*:'
        if ($types.Count -eq 0 -and $labelType -match '^(?i)(linear|tactile|clicky)$') { $entry.switchType = $labelType.ToLowerInvariant() }
    } else {
        $profiles = @([regex]::Matches($body, '(?im)^[ \t]*(?:(?:CYL,[ \t]*)?(Cherry|CYL|MTNU|SA|DSA|KAT|KAM|XDA|OEM|MT3|MDA|DCS|DSS|KSA)[ \t]+Profile|Profile[ \t]*:[ \t]*(Cherry|CYL|MTNU|SA|DSA|KAT|KAM|XDA|OEM|MT3|MDA|DCS|DSS|KSA)(?:[ \t]+profile)?)(?:[ \t]*\(Rows[^\r\n]*\))?[ \t]*$') | ForEach-Object {
            if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value }
        } | Sort-Object -Unique)
        $profiles = @($profiles | ForEach-Object { Get-CatalogProfileName $_.ToUpperInvariant() } | Sort-Object -Unique)
        if ($profiles.Count -eq 1) { $entry.profile = $profiles[0] }
        $materials = @([regex]::Matches($body, '(?im)^[ \t]*(?:(ABS|PBT|POM|PC)[ \t]+Material|Material[ \t]*:[ \t]*(ABS|PBT|POM|PC)(?:[ \t]+material)?)[ \t]*$') | ForEach-Object {
            if ($_.Groups[1].Success) { $_.Groups[1].Value.ToUpperInvariant() } else { $_.Groups[2].Value.ToUpperInvariant() }
        } | Sort-Object -Unique)
        if ($materials.Count -eq 1) { $entry.material = $materials[0] }
    }
    $variantNames = @($Product.variants | ForEach-Object { [string]$_.title })
    [ordered]@{ source = $Source; sourceId = [string]$Product.id; kind = $kind; url = "$($script:CatalogSources[$Source].host)/products/$($Product.handle)"; entry = $entry; excluded = $reason; variants = $variantNames }
}

function Convert-CatalogMatrix([string]$Content, $File, [string]$Commit) {
    $titleMatch = [regex]::Match($Content, '(?m)^title:\s*(.+)$')
    if (-not $titleMatch.Success) { throw "Missing Matrix title: $($File.sourcePath)" }
    $title = Get-CatalogName ($titleMatch.Groups[1].Value.Trim('"', "'"))
    $reason = $null
    if ($title -match '(?i)\bcolor codes\b' -or $title -ieq 'GMK Keycaps') { $reason = 'Navigation or color reference' }
    $entry = [ordered]@{ name = ('GMK ' + ($title -replace '^GMK\s+', '')); manufacturer = 'GMK' }
    $designer = Get-CatalogLabel $Content 'Designer\s*:'
    if ($designer) { $entry.designer = $designer }
    # Matrix documents the row sequence rather than naming the Cherry profile; retain only its explicit fields.
    [ordered]@{ source = 'matrix'; sourceId = $File.sourcePath; kind = 'keycaps'; url = "https://github.com/matrixzj/matrixzj.github.io/blob/$Commit/$($File.sourcePath)"; entry = $entry; excluded = $reason; variants = @() }
}

function Read-CatalogFetch([string]$Run) {
    $manifest = Read-CatalogJson (Join-Path $Run 'fetch.json')
    if ($manifest.schemaVersion -ne 1 -or $manifest.sources.Count -ne $script:CatalogSources.Count) { throw 'Unsupported or incomplete fetch manifest.' }
    $records = [Collections.Generic.List[object]]::new()
    foreach ($source in $script:CatalogSources.Keys) {
        if (-not $manifest.sources.Contains($source)) { throw "Missing source: $source" }
        $files = @($manifest.sources[$source].files)
        if ($files.Count -eq 0) { throw "Empty source: $source" }
        if ($source -eq 'dcswiki') {
            foreach ($record in (Read-DcsWikiFetch $Run $files)) { $records.Add($record) }
            continue
        }
        if ($source -eq 'theremingoat') {
            foreach ($record in (Read-ThereminGoatFetch $Run $files)) { $records.Add($record) }
            continue
        }
        if ($source -eq 'theremingoatscores') {
            foreach ($record in (Read-ThereminGoatScoresFetch $Run $files)) { $records.Add($record) }
            continue
        }
        if ($source -eq 'keycaplendar') {
            foreach ($record in (Read-KeycapLendarFetch $Run $files)) { $records.Add($record) }
            continue
        }
        $seen = @{}
        $empty = $false
        foreach ($file in $files) {
            $path = Get-CatalogRelativePath $Run $file.path
            if ((Get-CatalogHash $path) -cne $file.sha256) { throw "Fetch checksum mismatch: $($file.path)" }
            if ($source -eq 'matrix') {
                if ($seen.ContainsKey($file.sourcePath)) { throw 'Duplicate Matrix source document.' }
                $seen[$file.sourcePath] = $true
                $records.Add((Convert-CatalogMatrix ([IO.File]::ReadAllText($path)) $file $manifest.sources.matrix.commit))
            } else {
                if ($empty) { throw 'Unexpected page after terminal empty page.' }
                $data = Read-CatalogProductPage $path
                Test-CatalogProductPage $data $seen
                $empty = $data.products.Count -eq 0
                foreach ($product in $data.products) {
                    if ($source -eq 'unikeys') {
                        foreach ($record in (Convert-UniKeysProduct $product)) { $records.Add($record) }
                        continue
                    }
                    $record = Convert-CatalogProduct $product $source
                    if ($record.kind -eq 'switches' -and -not $record.excluded -and @($product.variants).Count -gt 1) {
                        $options = @($product.options | ForEach-Object { $_.name })
                        if (@($options | Where-Object { $_ -notmatch '^(?i)(stem color|colou?r|spring weight|weight)$' }).Count) {
                            throw "Unrecognized switch variant options for $($product.title); add an explicit parsing rule."
                        }
                        foreach ($variant in $product.variants) {
                            $variantRecord = Convert-CatalogProduct $product $source
                            $variantRecord.sourceId += "/variant/$($variant.id)"
                            $variantRecord.entry.name += ' - ' + (Get-CatalogName $variant.title)
                            # The option title carries the lube or modification wording.
                            Set-CatalogSwitchProduct $variantRecord
                            $records.Add($variantRecord)
                        }
                    } else { $records.Add($record) }
                }
            }
        }
        if ($seen.Count -eq 0 -or ($source -ne 'matrix' -and -not $empty)) { throw "Incomplete pagination: $source" }
    }
    return ,$records.ToArray()
}
