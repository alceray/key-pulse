$script:CatalogAliasesPath = Join-Path $PSScriptRoot 'aliases.json'
$script:CatalogAliasCache = $null

function Get-CatalogAliases {
    if ($null -ne $script:CatalogAliasCache) { return $script:CatalogAliasCache }
    $map = Read-CatalogJson $script:CatalogAliasesPath
    if ($map.schemaVersion -ne 1) { throw 'Unsupported catalog alias version.' }
    $entities = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $exact = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    $profiles = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $manufacturers = @{}
    $keycapDefaults = @{}
    $switchManufacturers = @{}
    $profileDefaults = @{}
    $namePrefixes = @{ switches = @{}; keycaps = @{} }
    foreach ($group in $map.entities) {
        foreach ($alias in @($group.canonical) + @($group.aliases)) {
            if (-not $alias -or $alias -cne (Get-CatalogName $alias)) { throw "Invalid catalog alias in $($group.canonical): '$alias'" }
            if ($entities.ContainsKey($alias) -and $entities[$alias] -cne $group.canonical) { throw "Conflicting catalog alias: $alias" }
            $entities[$alias] = $group.canonical
        }
        if ($group.Contains('exactAliases')) {
            foreach ($alias in $group.exactAliases) {
                if ($entities.ContainsKey($alias) -or $exact.ContainsKey($alias)) { throw "Conflicting exact catalog alias: $alias" }
                $exact[$alias] = $group.canonical
            }
        }
        if ($group.Contains('manufacturer')) { $manufacturers[$group.canonical] = $group.manufacturer }
        if ($group.Contains('keycapDefaults')) { $keycapDefaults[$group.canonical] = $group.keycapDefaults }
        if ($group.Contains('switchManufacturer')) {
            if ($group.switchManufacturer -isnot [bool]) { throw "Switch manufacturer flag must be true or false on $($group.canonical)" }
            if ($group.switchManufacturer) { $switchManufacturers[$group.canonical] = $true }
        }
    }
    foreach ($alias in $exact.Keys) {
        if ($entities.ContainsKey($alias)) { throw "Exact alias also has a case-insensitive mapping: $alias" }
    }
    foreach ($group in $map.entities) {
        if ($group.Contains('namePrefixes')) {
            foreach ($kind in $group.namePrefixes.Keys) {
                if ($kind -notin @('switches', 'keycaps')) { throw "Invalid name-prefix catalog: $kind" }
            }
        }
        foreach ($kind in @('switches', 'keycaps')) {
            $preferred = $group.canonical
            if ($group.Contains('namePrefix')) { $preferred = $group.namePrefix }
            if ($group.Contains('namePrefixes') -and $group.namePrefixes.Contains($kind)) { $preferred = $group.namePrefixes[$kind] }
            $resolved = if ($exact.ContainsKey($preferred)) { $exact[$preferred] } elseif ($entities.ContainsKey($preferred)) { $entities[$preferred] } else { $null }
            if ($resolved -cne $group.canonical) { throw "Name prefix must be an alias of $($group.canonical): $preferred" }
            $namePrefixes[$kind][$group.canonical] = $preferred
        }
    }
    foreach ($group in $map.profiles) {
        foreach ($alias in @($group.canonical) + @($group.aliases)) {
            if ($profiles.ContainsKey($alias) -and $profiles[$alias] -cne $group.canonical) { throw "Conflicting profile alias: $alias" }
            $profiles[$alias] = $group.canonical
        }
        # A profile made by one company states its maker, and sometimes its plastic.
        $defaults = [ordered]@{}
        foreach ($field in @('manufacturer', 'material')) {
            if (-not $group.Contains($field)) { continue }
            if ($field -eq 'manufacturer' -and -not ($entities.ContainsKey($group.manufacturer) -or $exact.ContainsKey($group.manufacturer))) {
                throw "Unknown profile manufacturer on $($group.canonical): $($group.manufacturer)"
            }
            if ($field -eq 'material' -and $group.material -cnotin @('ABS', 'PBT', 'POM', 'PC')) {
                throw "Unsupported profile material on $($group.canonical): $($group.material)"
            }
            $defaults[$field] = $group[$field]
        }
        if ($defaults.Count) { $profileDefaults[$group.canonical] = $defaults }
    }
    # Only a company that determines the field may declare it, and a declared profile must
    # be a shape this map already recognizes.
    foreach ($canonical in $keycapDefaults.Keys) {
        $defaults = $keycapDefaults[$canonical]
        foreach ($field in $defaults.Keys) {
            if ($field -notin @('manufacturer', 'profile', 'material', 'unlessProfile')) { throw "Unsupported keycap default '$field' on $canonical" }
        }
        # true means the company makes what it sells; a name means someone else makes it.
        if ($defaults.Contains('manufacturer') -and $defaults.manufacturer -isnot [bool]) {
            if ($defaults.manufacturer -isnot [string] -or -not ($entities.ContainsKey($defaults.manufacturer) -or $exact.ContainsKey($defaults.manufacturer))) {
                throw "Keycap manufacturer default must be true, false, or a known company on ${canonical}: $($defaults.manufacturer)"
            }
        }
        foreach ($field in @('profile', 'unlessProfile')) {
            if ($defaults.Contains($field) -and -not $profiles.ContainsKey($defaults[$field])) { throw "Unknown keycap $field on ${canonical}: $($defaults[$field])" }
        }
        if ($defaults.Contains('material') -and $defaults.material -cnotin @('ABS', 'PBT', 'POM', 'PC')) { throw "Unsupported keycap material default on ${canonical}: $($defaults.material)" }
    }
    # Longest first prevents a short alias from consuming an expanded brand name.
    $regularPattern = (@($entities.Keys | Sort-Object @{ Expression = { $_.Length }; Descending = $true }, { $_ } | ForEach-Object { [regex]::Escape($_) }) -join '|')
    $exactPattern = (@($exact.Keys | Sort-Object | ForEach-Object { [regex]::Escape($_) }) -join '|')
    $tokenPattern = "(?:(?-i:$exactPattern)|(?i:$regularPattern))(?=$|[\s,/(])"
    $script:CatalogAliasCache = @{
        entities = $entities; exact = $exact; profiles = $profiles; manufacturers = $manufacturers
        keycapDefaults = $keycapDefaults
        switchManufacturers = $switchManufacturers
        profileDefaults = $profileDefaults
        namePrefixes = $namePrefixes
        manufacturerLabels = $map.manufacturerLabels
        prefix = [regex]::new("^$tokenPattern", [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        name = [regex]::new("(?:^|(?<= [xX] ))$tokenPattern", [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    }
    $script:CatalogAliasCache
}

function Get-CatalogEntityName([string]$Text) {
    $map = Get-CatalogAliases
    if ($map.exact.ContainsKey($Text)) { return $map.exact[$Text] }
    if ($map.entities.ContainsKey($Text)) { return $map.entities[$Text] }
    $Text
}

function Get-CatalogAliasName([string]$Text, [ValidateSet('', 'switches', 'keycaps')][string]$Kind = '') {
    $map = Get-CatalogAliases
    # Only brand prefixes and explicit x collaborations, never arbitrary model words.
    # Comparisons use the full identity; display names can prefer a catalog-specific prefix.
    $map.name.Replace($Text, [Text.RegularExpressions.MatchEvaluator]{
        param($match)
        $canonical = Get-CatalogEntityName $match.Value
        if ($Kind) { return $map.namePrefixes[$Kind][$canonical] }
        $canonical
    })
}

function Get-CatalogCreditName([string]$Text) {
    $whole = Get-CatalogEntityName $Text
    if ($whole -cne $Text) { return $whole }
    # Credits may contain people as well as companies. Match whole credit components only.
    $parts = [regex]::Split($Text, '(\s+[xX&]\s+|\s+and\s+|\s*/\s*|,\s*)')
    for ($i = 0; $i -lt $parts.Length; $i += 2) { $parts[$i] = Get-CatalogEntityName $parts[$i] }
    $parts -join ''
}

function Get-CatalogProfileName([string]$Text) {
    $map = Get-CatalogAliases
    if ($map.profiles.ContainsKey($Text)) { return $map.profiles[$Text] }
    $Text
}

function Get-NormalizedCatalogEntry($Entry, [string]$Kind) {
    $result = Get-OrderedCatalogEntry $Entry $Kind
    $map = Get-CatalogAliases
    $result.name = Get-CatalogAliasName $result.name $Kind
    foreach ($field in @('manufacturer', 'brand', 'designer')) {
        if ($result.Contains($field)) { $result[$field] = Get-CatalogCreditName $result[$field] }
    }
    if ($result.Contains('profile')) { $result.profile = Get-CatalogProfileName $result.profile }
    if ($Kind -eq 'keycaps' -and -not $result.Contains('profile') -and $result.name -match '^GMK (CYL|MTNU)\b') {
        $result.profile = Get-CatalogProfileName $Matches[1].ToUpperInvariant()
    }
    if ($Kind -eq 'switches') {
        $prefix = $map.prefix.Match($result.name)
        $prefixEntity = Get-CatalogEntityName $prefix.Value
        if ($prefix.Success -and $map.manufacturers.ContainsKey($prefixEntity)) {
            if (-not $result.Contains('brand')) { $result.brand = $prefixEntity }
            if (-not $result.Contains('manufacturer')) { $result.manufacturer = $map.manufacturers[$prefixEntity] }
        }
        if ($Entry.Contains('manufacturer')) {
            foreach ($label in $map.manufacturerLabels) {
                if ($Entry.manufacturer -ieq $label.label) {
                    $result.manufacturer = $label.manufacturer
                }
            }
        }
        if ($result.Contains('manufacturer') -and $map.manufacturers.ContainsKey($result.manufacturer)) {
            if (-not $result.Contains('brand')) { $result.brand = $result.manufacturer }
            $result.manufacturer = $map.manufacturers[$result.manufacturer]
        }
    }
    Get-OrderedCatalogEntry $result $Kind
}

# Fills fields a product name already states, and only those. Runs on the merged entry so
# every source value wins, and a reviewed omission still removes what this adds.
function Add-CatalogInferredFields($Entry, [string]$Kind) {
    $map = Get-CatalogAliases
    $result = Get-OrderedCatalogEntry $Entry $Kind
    $name = [string]$result.name
    $prefix = $map.prefix.Match($name)
    $canonical = if ($prefix.Success) { Get-CatalogEntityName $prefix.Value } else { $null }
    if ($Kind -eq 'keycaps') {
        $leading = ($name -split ' ', 2)[0]
        if (-not $result.Contains('profile') -and $map.profiles.ContainsKey($leading)) { $result.profile = $map.profiles[$leading] }
        # A set named for its shape states the profile, not who sells it. Cherry is both a
        # profile and a company, and on a keycap set the leading word is the profile.
        if ($map.profiles.ContainsKey($leading)) { $canonical = $null }
        if ($canonical -and $map.keycapDefaults.ContainsKey($canonical)) {
            $defaults = $map.keycapDefaults[$canonical]
            if ($defaults.Contains('manufacturer') -and $defaults.manufacturer -and -not $result.Contains('manufacturer')) {
                $result.manufacturer = if ($defaults.manufacturer -is [bool]) { $canonical } else { Get-CatalogEntityName $defaults.manufacturer }
            }
            # A set naming the exception profile is that profile, so the company's usual
            # shape and plastic give way to whatever that profile states.
            $excepted = $defaults.Contains('unlessProfile') -and $name -match ('\b' + [regex]::Escape($defaults.unlessProfile) + '\b')
            if ($excepted -and -not $result.Contains('profile')) { $result.profile = Get-CatalogProfileName $defaults.unlessProfile }
            if (-not $excepted) {
                foreach ($field in @('profile', 'material')) {
                    if ($defaults.Contains($field) -and -not $result.Contains($field)) { $result[$field] = $defaults[$field] }
                }
            }
        }
        if ($result.Contains('profile') -and $map.profileDefaults.ContainsKey($result.profile)) {
            $defaults = $map.profileDefaults[$result.profile]
            foreach ($field in @('manufacturer', 'material')) {
                if ($defaults.Contains($field) -and -not $result.Contains($field)) { $result[$field] = $defaults[$field] }
            }
        }
    } elseif ($canonical -and -not $result.Contains('manufacturer') -and $map.switchManufacturers.ContainsKey($canonical)) {
        $result.manufacturer = $canonical
    }
    # Whoever made it, the company leading the name is the one selling it.
    if ($canonical -and -not $result.Contains('brand')) { $result.brand = $canonical }
    Get-OrderedCatalogEntry $result $Kind
}
