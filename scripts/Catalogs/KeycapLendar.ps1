function Get-KeycapLendarPageUrl([string]$PageToken = '') {
    $url = $script:CatalogSources.keycaplendar.endpoint + '?pageSize=300'
    # Request catalog facts only; user/editor identities and images are not import inputs.
    foreach ($field in @('alias', 'colorway', 'profile', 'designer', 'gbLaunch', 'gbEnd', 'icDate', 'details')) {
        $url += '&mask.fieldPaths=' + $field
    }
    if ($PageToken) { $url += '&pageToken=' + [Uri]::EscapeDataString($PageToken) }
    $url
}

function Get-KeycapLendarString($Fields, [string]$Name, [switch]$Required) {
    if (-not $Fields.Contains($Name)) {
        if ($Required) { throw "KeycapLendar record missing $Name." }
        return ''
    }
    $value = $Fields[$Name]
    if ($value -isnot [Collections.IDictionary] -or -not $value.Contains('stringValue') -or $value.stringValue -isnot [string]) {
        throw "Invalid KeycapLendar string field: $Name"
    }
    $text = Get-CatalogName $value.stringValue
    if ($Required -and -not $text) { throw "KeycapLendar record missing $Name." }
    $text
}

function Convert-KeycapLendarDocument($Document) {
    $prefix = 'projects/keycaplendar/databases/(default)/documents/keysets/'
    if (-not $Document.Contains('name') -or $Document.name -isnot [string] -or -not $Document.name.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw 'Unexpected KeycapLendar document path.'
    }
    $id = $Document.name.Substring($prefix.Length)
    if ($id -cnotmatch '^[A-Za-z0-9_-]+$' -or -not $Document.Contains('fields') -or $Document.fields -isnot [Collections.IDictionary]) {
        throw 'Invalid KeycapLendar document ID or fields.'
    }
    $fields = $Document.fields
    $category = Get-KeycapLendarString $fields 'profile' -Required
    $colorway = Get-KeycapLendarString $fields 'colorway' -Required
    $alias = Get-KeycapLendarString $fields 'alias'
    $entry = [ordered]@{ name = "$category $colorway" }
    $designers = @()
    if ($fields.Contains('designer')) {
        $designerField = $fields.designer
        if ($designerField -isnot [Collections.IDictionary] -or -not $designerField.Contains('arrayValue') -or $designerField.arrayValue -isnot [Collections.IDictionary]) {
            throw 'Invalid KeycapLendar designer array.'
        }
        if ($designerField.arrayValue.Contains('values')) {
            if ($designerField.arrayValue.values -isnot [array]) { throw 'Invalid KeycapLendar designer array values.' }
            foreach ($value in $designerField.arrayValue.values) {
                $designer = Get-KeycapLendarString @{ designer = $value } 'designer' -Required
                if ($designer -notmatch '^(?i)(unknown|n/?a|none|null|tbd|\?+|-)$') { $designers += $designer }
            }
        }
    }
    if ($designers.Count) { $entry.designer = ($designers | Select-Object -Unique) -join ' & ' }
    # The upstream "profile" is a mixed category: GMK/PBTfans are not profiles.
    # Only explicit shape/material labels are extracted; manufacturer/brand stay unassigned.
    if ($category -cmatch '^(Cherry|CYL|SA|SA-R3|SA-P|DSA|DCS|DSS|KAT|KAM|XDA|OEM|MT3|MTNU|MDA|KSA|ASA|HSA|G20|MBK|PBS|SLK)(?: (?:ABS|PBT|Alps))?$') {
        $entry.profile = Get-CatalogProfileName $Matches[1]
    }
    if ($category -cmatch ' (ABS|PBT|POM|PC|Aluminum)$') { $entry.material = $Matches[1] }
    $notes = @("Upstream category: $category")
    foreach ($field in @('icDate', 'gbLaunch', 'gbEnd', 'details')) {
        $value = Get-KeycapLendarString $fields $field
        if ($value) { $notes += "${field}: $value" }
    }
    # Cancellation labels, rounds, plus signs and add-on names remain as published.
    $query = if ($alias) { 'keysetAlias=' + [Uri]::EscapeDataString($alias) } else { 'keysetId=' + [Uri]::EscapeDataString($id) }
    [ordered]@{ source = 'keycaplendar'; sourceId = $id; kind = 'keycaps'; url = "https://keycaplendar.firebaseapp.com/?$query"; entry = $entry; excluded = $null; variants = @(); notes = $notes -join '; ' }
}

function Read-KeycapLendarPage([string]$Path, $SeenIds) {
    $data = Read-CatalogJson $Path
    if ($data -isnot [Collections.IDictionary] -or -not $data.Contains('documents') -or $data.documents -isnot [array]) {
        throw 'Expected a KeycapLendar documents array.'
    }
    $records = @()
    foreach ($document in $data.documents) {
        if ($document -isnot [Collections.IDictionary]) { throw 'Invalid KeycapLendar document.' }
        $record = Convert-KeycapLendarDocument $document
        if (-not $SeenIds.Add($record.sourceId)) { throw "Repeated KeycapLendar document: $($record.sourceId)" }
        $records += $record
    }
    $token = ''
    if ($data.Contains('nextPageToken')) {
        if ($data.nextPageToken -isnot [string] -or -not $data.nextPageToken) { throw 'Invalid KeycapLendar page token.' }
        $token = $data.nextPageToken
    }
    if ($token -and -not $records.Count) { throw 'Empty intermediate KeycapLendar page.' }
    [ordered]@{ records = $records; nextPageToken = $token }
}

function Save-KeycapLendarFetch([string]$Run) {
    $files = @()
    $seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenTokens = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $token = ''
    for ($page = 1; $page -le 100; $page++) {
        $url = Get-KeycapLendarPageUrl $token
        $relative = "raw/keycaplendar/page-$page.json"
        $path = Get-CatalogRelativePath $Run $relative
        [IO.Directory]::CreateDirectory((Split-Path $path -Parent)) | Out-Null
        Get-CatalogResponse $url $path
        $result = Read-KeycapLendarPage $path $seenIds
        $files += [ordered]@{ path = $relative; sha256 = Get-CatalogHash $path; url = $url }
        Write-Host "KeycapLendar page ${page}: $($result.records.Count) listings"
        $token = $result.nextPageToken
        if (-not $token) { break }
        if (-not $seenTokens.Add($token)) { throw 'Repeated KeycapLendar page token.' }
    }
    if ($token -or $seenIds.Count -eq 0) { throw 'Empty or incomplete KeycapLendar fetch.' }
    [ordered]@{ fetchedAt = [DateTime]::UtcNow.ToString('o'); files = $files }
}

function Read-KeycapLendarFetch([string]$Run, $Files) {
    if (-not $Files.Count -or $Files.Count -gt 100) { throw 'Empty or incomplete KeycapLendar snapshot.' }
    $seenIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenTokens = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $records = [Collections.Generic.List[object]]::new()
    $token = ''
    $terminal = $false
    foreach ($file in $Files) {
        if ($terminal) { throw 'Unexpected KeycapLendar page after terminal page.' }
        if ($file.url -cne (Get-KeycapLendarPageUrl $token)) { throw 'KeycapLendar pagination URL mismatch.' }
        $path = Get-CatalogRelativePath $Run $file.path
        if ((Get-CatalogHash $path) -cne $file.sha256) { throw "Fetch checksum mismatch: $($file.path)" }
        $result = Read-KeycapLendarPage $path $seenIds
        foreach ($record in $result.records) { $records.Add($record) }
        $token = $result.nextPageToken
        $terminal = -not $token
        if ($token -and -not $seenTokens.Add($token)) { throw 'Repeated KeycapLendar page token.' }
    }
    if (-not $terminal -or $records.Count -eq 0) { throw 'Empty or incomplete KeycapLendar snapshot.' }
    return ,$records.ToArray()
}
