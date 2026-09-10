function Get-DcsWikiBundleUrls([string]$Html) {
    $urls = @([regex]::Matches($Html, '(?is)<script\b[^>]*\bsrc\s*=\s*["''](?<src>[^"'']+)["'']') | ForEach-Object {
        $src = [Net.WebUtility]::HtmlDecode($_.Groups['src'].Value)
        # Only public same-origin Next.js bundles; never follow arbitrary script URLs from the page.
        if ($src -match '^/_next/static/chunks/[a-zA-Z0-9_/-]+\.js$') { 'https://dcs.wiki' + $src }
    } | Select-Object -Unique)
    if ($urls.Count -eq 0 -or $urls.Count -gt 30) { throw 'Missing or unexpected DCS Wiki script listing.' }
    return ,$urls
}

function ConvertFrom-CatalogJsString([string]$Literal) {
    # Decode a string literal only. Downloaded JavaScript is never evaluated.
    if ($Literal.Length -lt 2 -or $Literal[0] -notin @([char]39, [char]34) -or $Literal[0] -cne $Literal[$Literal.Length - 1]) { throw 'Invalid JavaScript string literal.' }
    $builder = [Text.StringBuilder]::new()
    for ($i = 1; $i -lt $Literal.Length - 1; $i++) {
        $c = $Literal[$i]
        if ($c -ne [char]92) {
            if ($c -eq $Literal[0] -or $c -in @([char]10, [char]13)) { throw 'Unescaped character in JavaScript string.' }
            [void]$builder.Append($c)
            continue
        }
        $i++
        if ($i -ge $Literal.Length - 1) { throw 'Incomplete JavaScript escape.' }
        $c = $Literal[$i]
        switch -CaseSensitive ($c) {
            'n' { [void]$builder.Append([char]10) }
            'r' { [void]$builder.Append([char]13) }
            't' { [void]$builder.Append([char]9) }
            'b' { [void]$builder.Append([char]8) }
            'f' { [void]$builder.Append([char]12) }
            'v' { [void]$builder.Append([char]11) }
            { $_ -in @('x', 'u') } {
                $length = if ($c -ceq 'x') { 2 } else { 4 }
                if ($i + $length -ge $Literal.Length - 1) { throw 'Incomplete JavaScript hex escape.' }
                $hex = $Literal.Substring($i + 1, $length)
                if ($hex -notmatch '^[a-fA-F0-9]+$') { throw 'Invalid JavaScript hex escape.' }
                [void]$builder.Append([char][Convert]::ToInt32($hex, 16)); $i += $length
            }
            { $_ -in @([char]10, [char]13) } {
                if ($c -eq [char]13 -and $Literal[$i + 1] -eq [char]10) { $i++ }
            }
            { $_ -in @([char]92, [char]39, [char]34, [char]47) } { [void]$builder.Append($c) }
            default { throw "Unsupported JavaScript escape: $c" }
        }
    }
    $builder.ToString()
}

function Get-DcsWikiData([string]$Bundle) {
    $candidates = @()
    $pattern = 'JSON\.parse\(\s*(?<literal>''(?:\\[\s\S]|[^''\\])*''|"(?:\\[\s\S]|[^"\\])*")\s*\)'
    foreach ($match in [regex]::Matches($Bundle, $pattern)) {
        $literal = $match.Groups['literal'].Value
        if ($literal -notmatch 'DCS' -or $literal -notmatch 'profile') { continue }
        $data = ConvertFrom-Json (ConvertFrom-CatalogJsString $literal)
        if ($data -isnot [array] -or $data.Count -eq 0) { throw 'Expected a nonempty DCS Wiki data array.' }
        $candidates += ,$data
    }
    if ($candidates.Count -gt 1) { throw 'Ambiguous DCS Wiki data arrays.' }
    if ($candidates.Count -eq 0) { return ,@() }
    $seen = @{}
    foreach ($item in $candidates[0]) {
        foreach ($field in @('id', 'name', 'profile')) {
            if (-not $item.PSObject.Properties[$field] -or $item.$field -isnot [string] -or -not (Get-CatalogName $item.$field)) { throw "DCS Wiki record missing valid $field." }
        }
        if ($item.profile -cne 'DCS' -or $seen.ContainsKey($item.id)) { throw 'Unexpected profile or duplicate DCS Wiki ID.' }
        $seen[$item.id] = $true
    }
    return ,$candidates[0]
}

function Convert-DcsWikiRecords($Items, [string]$Html) {
    $links = @{}
    foreach ($anchor in [regex]::Matches($Html, '(?is)<a\b[^>]*\bhref\s*=\s*["''](?<href>/keycaps/[^"''?#]+)["''][^>]*>(?<body>.*?)</a>')) {
        $heading = [regex]::Match($anchor.Groups['body'].Value, '(?is)<h3\b[^>]*>(.*?)</h3>')
        if (-not $heading.Success) { continue }
        $name = Get-CatalogName (Get-CatalogText $heading.Groups[1].Value)
        $url = 'https://dcs.wiki' + [Net.WebUtility]::HtmlDecode($anchor.Groups['href'].Value)
        if ($links.ContainsKey($name) -and $links[$name] -cne $url) { throw "Ambiguous DCS Wiki detail link: $name" }
        $links[$name] = $url
    }
    foreach ($item in $Items) {
        $name = Get-CatalogName $item.name
        if (-not $links.ContainsKey($name)) { throw "DCS Wiki page/bundle mismatch: $name" }
        $entry = [ordered]@{ name = $name }
        foreach ($field in @('manufacturer', 'designer', 'profile', 'material')) {
            if (-not $item.PSObject.Properties[$field] -or $null -eq $item.$field) { continue }
            if ($item.$field -isnot [string]) { throw "Unexpected DCS Wiki $field for $name" }
            $value = Get-CatalogName $item.$field
            if ($value -and $value -notmatch '^(?i)(unknown|n/?a|none|null|tbd|\?+|-)$') { $entry[$field] = $value }
        }
        # Legend style (Cherry/Gorton), descriptions, images, prices and dates are not catalog fields.
        [ordered]@{ source = 'dcswiki'; sourceId = $item.id; kind = 'keycaps'; url = $links[$name]; entry = $entry; excluded = $null; variants = @() }
    }
}

function Save-DcsWikiFetch([string]$Run) {
    $info = $script:CatalogSources.dcswiki
    $html = Get-CatalogResponse $info.endpoint
    $files = @()
    $relative = 'raw/dcswiki/keycaps.html'
    $path = Get-CatalogRelativePath $Run $relative
    [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
    [IO.File]::WriteAllText($path, $html, (New-Object Text.UTF8Encoding $false))
    $files += [ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $info.endpoint; role = 'page' }
    foreach ($url in (Get-DcsWikiBundleUrls $html)) {
        $bundle = Get-CatalogResponse $url
        $items = Get-DcsWikiData $bundle
        if ($items.Count -eq 0) { continue }
        if ($files.Count -gt 1) { throw 'Multiple DCS Wiki catalog bundles; review the source structure.' }
        $records = @(Convert-DcsWikiRecords $items $html)
        $relative = 'raw/dcswiki/catalog.js'
        $path = Get-CatalogRelativePath $Run $relative
        [IO.File]::WriteAllText($path, $bundle, (New-Object Text.UTF8Encoding $false))
        $files += [ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $url; role = 'bundle' }
        Write-Host "DCS Wiki: $($records.Count) records"
    }
    if ($files.Count -ne 2) { throw 'DCS Wiki catalog data not found in the current page bundles.' }
    [ordered]@{ fetchedAt = [DateTime]::UtcNow.ToString('o'); files = $files }
}

function Read-DcsWikiFetch([string]$Run, $Files) {
    $pages = @($Files | Where-Object { $_.role -eq 'page' })
    $bundles = @($Files | Where-Object { $_.role -eq 'bundle' })
    if ($Files.Count -ne 2 -or $pages.Count -ne 1 -or $bundles.Count -ne 1) { throw 'Incomplete DCS Wiki snapshot; expected one page and one catalog bundle.' }
    foreach ($file in $Files) {
        $path = Get-CatalogRelativePath $Run $file.path
        if ((Get-CatalogHash $path) -cne $file.sha256) { throw "Fetch checksum mismatch: $($file.path)" }
    }
    $html = [IO.File]::ReadAllText((Get-CatalogRelativePath $Run $pages[0].path))
    if ($pages[0].url -cne $script:CatalogSources.dcswiki.endpoint -or $bundles[0].url -cnotin (Get-DcsWikiBundleUrls $html)) { throw 'DCS Wiki bundle is not referenced by its catalog page.' }
    $items = Get-DcsWikiData ([IO.File]::ReadAllText((Get-CatalogRelativePath $Run $bundles[0].path)))
    if ($items.Count -eq 0) { throw 'Empty DCS Wiki catalog snapshot.' }
    return ,@(Convert-DcsWikiRecords $items $html)
}
