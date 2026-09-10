Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-CatalogMap($Value) {
    if ($null -eq $Value) { return $null }
    # PowerShell 5.1 may wrap strings from JSON arrays as PSCustomObject values.
    if ($Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [System.Collections.IDictionary]) { return $Value }
    if ($Value -is [pscustomobject]) {
        $map = [ordered]@{}
        foreach ($p in $Value.PSObject.Properties) { $map[$p.Name] = ConvertTo-CatalogMap $p.Value }
        return $map
    }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { ConvertTo-CatalogMap $_ }) }
    return $Value
}

function Read-CatalogJson([string]$Path) {
    ConvertTo-CatalogMap (ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path)))
}

# Most values need no escaping at all, so the common case skips the rewrite entirely.
$script:CatalogJsonPlain = [regex]::new('^[^"\\\x00-\x1F]*$', [Text.RegularExpressions.RegexOptions]::Compiled)
$script:CatalogJsonEscape = [regex]::new('["\\\x00-\x1F]', [Text.RegularExpressions.RegexOptions]::Compiled)
$script:CatalogJsonIndents = @(0..60 | ForEach-Object { '  ' * $_ })

function ConvertTo-CatalogJsonString([string]$Text) {
    # Only the characters JSON requires. Everything else stays as written, so a name in
    # another script reads normally in a diff instead of as an escape sequence.
    if ($script:CatalogJsonPlain.IsMatch($Text)) { return '"' + $Text + '"' }
    $escaped = $script:CatalogJsonEscape.Replace($Text, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        switch ($match.Value) {
            '"' { '\"' }
            '\' { '\\' }
            "`b" { '\b' }
            "`f" { '\f' }
            "`n" { '\n' }
            "`r" { '\r' }
            "`t" { '\t' }
            default { '\u{0:x4}' -f [int][char]$match.Value }
        }
    })
    '"' + $escaped + '"'
}

function Add-CatalogJsonValue([Text.StringBuilder]$Builder, $Value, [int]$Level) {
    if ($Level -ge $script:CatalogJsonIndents.Count) { throw 'Catalog JSON nesting is too deep.' }
    if ($null -eq $Value) { [void]$Builder.Append('null'); return }
    if ($Value -is [string]) { [void]$Builder.Append((ConvertTo-CatalogJsonString $Value)); return }
    if ($Value -is [bool]) { [void]$Builder.Append($(if ($Value) { 'true' } else { 'false' })); return }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal] -or $Value -is [single]) {
        [void]$Builder.Append([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0}', $Value)); return
    }
    $indent = $script:CatalogJsonIndents[$Level]
    $nested = $script:CatalogJsonIndents[$Level + 1]
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.Count -eq 0) { [void]$Builder.Append('{}'); return }
        [void]$Builder.Append("{`n")
        $first = $true
        foreach ($key in $Value.Keys) {
            if (-not $first) { [void]$Builder.Append(",`n") }
            $first = $false
            [void]$Builder.Append($nested).Append((ConvertTo-CatalogJsonString ([string]$key))).Append(': ')
            Add-CatalogJsonValue $Builder $Value[$key] ($Level + 1)
        }
        [void]$Builder.Append("`n").Append($indent).Append('}')
        return
    }
    if ($Value -is [Collections.IEnumerable]) {
        $items = @($Value)
        if ($items.Count -eq 0) { [void]$Builder.Append('[]'); return }
        [void]$Builder.Append("[`n")
        for ($index = 0; $index -lt $items.Count; $index++) {
            if ($index) { [void]$Builder.Append(",`n") }
            [void]$Builder.Append($nested)
            Add-CatalogJsonValue $Builder $items[$index] ($Level + 1)
        }
        [void]$Builder.Append("`n").Append($indent).Append(']')
        return
    }
    throw "Unsupported catalog JSON value: $($Value.GetType().FullName)"
}

function ConvertTo-CatalogJson($Value) {
    $builder = [Text.StringBuilder]::new()
    Add-CatalogJsonValue $builder $Value 0
    [void]$builder.Append("`n")
    $builder.ToString()
}

function Write-CatalogJson([string]$Path, $Value) {
    $parent = Split-Path $Path -Parent
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    [IO.File]::WriteAllText($Path, (ConvertTo-CatalogJson $Value), (New-Object Text.UTF8Encoding $false))
}

function Get-CatalogHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'absent' }
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CatalogText([string]$Text) {
    $text = [regex]::Replace($Text, '(?is)<(script|style)\b[^>]*>.*?</\1>', '')
    # Preserve table row boundaries while removing layout paragraphs inside cells.
    $text = [regex]::Replace($text, '(?is)<tr\b[^>]*>.*?</tr>', [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        "`n" + (Get-CatalogName ([regex]::Replace($match.Value, '<[^>]*>', ' '))) + "`n"
    })
    $text = [regex]::Replace($text, '(?i)</(?:p|li|div|h[1-6]|tr)>|<br\s*/?>', "`n")
    $text = [regex]::Replace($text, '(?i)</t[dh]>', "`t")
    $text = [regex]::Replace($text, '<[^>]*>', '')
    [Net.WebUtility]::HtmlDecode($text).Replace([char]0xA0, ' ')
}

function Get-CatalogName([string]$Text) {
    ([regex]::Replace([Net.WebUtility]::HtmlDecode($Text), '\s+', ' ')).Trim()
}

function Get-CatalogMatchName([string]$Text, [switch]$SourceIdentity) {
    $text = Get-CatalogName $Text
    # Upstream identity digests retain their original rules even when display aliases change.
    if (-not $SourceIdentity) { $text = Get-CatalogAliasName $text }
    $text = $text.Normalize([Text.NormalizationForm]::FormKC).ToLowerInvariant()
    if ($SourceIdentity) { $text = $text -replace '^kkb\b', 'keykobo' }
    $text = $text -replace '^gmk cyl\b', 'gmk'
    $text = $text.Replace('+', ' plus ')
    # Matrix appends translations after the Latin set name. A colorway beginning
    # in CJK (e.g. ePBT names) is the identity itself, not a trailing translation.
    if ($text -match '^gmk\s+[a-z0-9]') { $text = $text -replace '\s+[\p{IsCJKUnifiedIdeographs}].*$', '' }
    ($text -replace '[^\p{L}\p{N}]', '')
}

# NovelKeys sells a Dry line, where the word is the model rather than a lube state.
$script:CatalogLubeModelName = '(?:NK|NovelKeys)\s+Dry\b'

function Get-CatalogSpecimenReason([string]$Name) {
    if ($Name -match '(?i)\b(?:prototypes?|proto)\b') { return 'Prototype specimen, not a listed product' }
    if ($Name -match '(?i)\bsamples?\b') { return 'Sample specimen, not a distinct product' }
    $null
}

# Aftermarket work on another switch is not its own product. Factory and pre-lubed
# options are how a switch ships, so they stay.
function Get-CatalogModificationReason([string]$Name) {
    $patterns = @('^\s*broken[- ]?in\b', '\bhand[- ]?lubed\b', '\bmodded\b', '\bspring[- ]?swapped\b', '\blubed,\s*filmed\b')
    foreach ($pattern in $patterns) {
        if ($Name -match "(?i)$pattern") { return 'Aftermarket modification of another switch' }
    }
    $null
}

# A switch offered dry or factory lubed is one switch with a purchase option, so the
# lube wording does not belong in its name.
function Get-CatalogLubeFreeName([string]$Name) {
    if ($Name -match "(?i)\b$script:CatalogLubeModelName") { return Get-CatalogName $Name }
    $name = $Name -replace '(?i),\s*(?:factory\s+|pre-?|hand\s+)?(?:lubed|unlubed|dry)\s*\)', ')'
    $name = $name -replace '(?i)\s*\((?:factory\s+|pre-?|hand\s+)?(?:lubed|unlubed|dry),?\s*', ' ('
    $name = $name -replace '\s*\(\s*\)', ''
    $name = $name -replace '(?i)\s*[-–]\s*(?:factory\s+|pre-?|hand\s+)?(?:lubed|unlubed|dry)\s*$', ''
    $name = $name -replace '(?i)\s+(?:factory\s+|pre-?|hand\s+)?(?:lubed|unlubed|dry)\b', ' '
    $name = $name -replace '(?i)^\s*(?:factory\s+|pre-?|hand\s+)?(?:lubed|unlubed|dry)\s+', ''
    $name = Get-CatalogName ($name -replace '\s+\)', ')')
    if (-not $name) { return Get-CatalogName $Name }
    $name
}

# One decision point for whether a switch listing is a product and what it is called.
function Set-CatalogSwitchProduct($Record) {
    if ($Record.excluded) { return }
    $reason = Get-CatalogModificationReason $Record.entry.name
    if (-not $reason) { $reason = Get-CatalogSpecimenReason $Record.entry.name }
    if ($reason) { $Record.excluded = $reason; return }
    $Record.entry.name = Get-CatalogLubeFreeName $Record.entry.name
}

function Get-CatalogSlug([string]$Name) {
    $name = $Name.Replace('+', ' plus ').Normalize([Text.NormalizationForm]::FormD) -replace '\p{M}', ''
    $slug = ($name.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if (-not $slug) { throw "Cannot derive an ID for '$Name'; add an explicit mapping." }
    $slug
}

function Get-CatalogFields([string]$Kind) {
    $fields = @('id', 'name', 'manufacturer', 'brand', 'designer')
    if ($Kind -eq 'switches') { return $fields + 'switchType' }
    $fields + @('profile', 'material')
}

function Get-CatalogSortedStrings($Values) {
    [string[]]$items = @($Values)
    [Array]::Sort($items, [StringComparer]::Ordinal)
    return ,$items
}

function Get-OrderedCatalogEntry($Entry, [string]$Kind) {
    $result = [ordered]@{}
    foreach ($field in (Get-CatalogFields $Kind)) {
        if ($Entry.Contains($field)) { $result[$field] = $Entry[$field] }
    }
    $result
}

function Get-CatalogRelativePath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative)) { throw 'Expected a relative catalog path.' }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Catalog path leaves its root.' }
    # Do not follow a junction or symbolic link when reading/promoting an artifact.
    $check = $path
    while ($check -and $check.Length -ge $rootPath.TrimEnd('\', '/').Length) {
        if (Test-Path -LiteralPath $check) {
            if ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Catalog path contains a reparse point: $check"
            }
        }
        $check = Split-Path $check -Parent
    }
    $path
}

. "$PSScriptRoot/Aliases.ps1"
