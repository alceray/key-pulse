function Get-UniKeysVariantName([string]$Title) {
    $name = Get-CatalogName $Title
    # Packaging is not a switch variant; spring lengths, weights and modifications are.
    $name = $name -replace '(?i)\s*\(\d+ switches in special packaging can\)', ''
    $name = $name -replace '(?i)\s*X\d+ in (?:loose packaging|fine packaging|switch container|film box|KD packaging)\s*$', ''
    $name = $name -replace '(?i)\s*\(NEW\)\s*$', ''
    return $name.Trim()
}

function Convert-UniKeysProduct($Product) {
    $record = Convert-CatalogProduct $Product 'unikeys'
    $name = Get-CatalogName $Product.title
    $name = $name -replace '(?i)\s*-\s*Restock in [a-z]+\s*$', ''
    $name = $name -replace '(?i)\s*\(\d+\s*PCS\)\s*$', ''
    $name = $name -replace '(?i)\s*\(Hand-Lubed Edition Available\)\s*$', ''
    $name = $name -replace '(?i)\s+Switch(?:es)?(?:\s+Factory Lubed(?:/Hand Lubed)?(?: Edition)?)?\s*$', ''
    $record.entry.name = $name.Trim()
    Set-CatalogSwitchProduct $record
    if ($record.excluded) { return ,@($record) }
    $options = @($Product.options | ForEach-Object { $_.name })
    if (@($options | Where-Object { $_ -notmatch '^(?i)(title|options?|type|spring weights?|bottom[ -]out force)$' }).Count) {
        throw "Unrecognized UniKeys switch variant options for $($Product.title); add an explicit parsing rule."
    }
    $variants = @($Product.variants)
    if ($variants.Count -eq 0) { throw "Missing UniKeys product variants: $($Product.id)" }
    if ($variants.Count -eq 1 -and $variants[0].title -eq 'Default Title') { return ,@($record) }
    $labels = @($variants | ForEach-Object { Get-UniKeysVariantName $_.title })
    if (@($labels | Where-Object { $_ }).Count -eq 0) { return ,@($record) }
    if (@($labels | Where-Object { -not $_ }).Count -gt 0) { throw "Mixed packaging and unnamed UniKeys variants: $($Product.id)" }
    $records = @()
    $seen = @{}
    for ($index = 0; $index -lt $variants.Count; $index++) {
        $variant = $variants[$index]
        if ($seen.ContainsKey([string]$variant.id)) { throw "Duplicate UniKeys variant ID: $($variant.id)" }
        $seen[[string]$variant.id] = $true
        $item = Convert-CatalogProduct $Product 'unikeys'
        $item.sourceId += "/variant/$($variant.id)"
        $item.entry.name = $record.entry.name + ' - ' + $labels[$index]
        # Weight and modification options arrive in the variant label, not the title.
        Set-CatalogSwitchProduct $item
        $records += $item
    }
    return ,$records
}
