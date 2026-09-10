<#
.SYNOPSIS
Fetch, replay, validate, or promote the offline switch/keycap catalogs.
.EXAMPLE
.\Scripts\Import-Catalogs.ps1 -Action Fetch -Run artifacts/catalog-import/2026-09-09
.EXAMPLE
.\Scripts\Import-Catalogs.ps1 -Action Fetch -Run artifacts/catalog-import/2026-09-10 -Reuse artifacts/catalog-import/2026-09-09
#>
param(
    [ValidateSet('Fetch', 'Replay', 'Validate', 'Promote')]
    [string]$Action = 'Validate',
    [string]$Run = '',
    [string]$Reuse = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Catalogs/Common.ps1"
. "$PSScriptRoot/Catalogs/Sources.ps1"
. "$PSScriptRoot/Catalogs/Catalog.ps1"
$root = Split-Path $PSScriptRoot -Parent
function Resolve-CatalogRun([string]$Value, [string]$Label) {
    $path = if ([IO.Path]::IsPathRooted($Value)) { [IO.Path]::GetFullPath($Value) } else { [IO.Path]::GetFullPath((Join-Path $root $Value)) }
    $cacheRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/catalog-import')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($cacheRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label must be a child of artifacts/catalog-import/." }
    Get-CatalogRelativePath $root $path.Substring($root.Length + 1)
}
$runPath = ''
$reusePath = ''
if ($Action -ne 'Validate') {
    if (-not $Run) { throw 'Supply -Run with a directory under artifacts/catalog-import/.' }
    $runPath = Resolve-CatalogRun $Run 'Run'
}
if ($Reuse) {
    if ($Action -ne 'Fetch') { throw 'Reuse applies to Fetch only.' }
    $reusePath = Resolve-CatalogRun $Reuse 'Reuse'
    if ($reusePath -eq $runPath) { throw 'Reuse must name a different snapshot than Run.' }
}
switch ($Action) {
    'Fetch' { Save-CatalogFetch $runPath $reusePath; Save-CatalogCandidate $root $runPath }
    'Replay' { Save-CatalogCandidate $root $runPath }
    'Validate' { Test-CatalogState (Read-CatalogState $root); Write-Host 'Catalogs and mappings are valid.' }
    'Promote' { Publish-CatalogCandidate $root $runPath }
}
