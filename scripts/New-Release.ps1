# New-Release.ps1
# Tags and pushes a release in one step, triggering the GitHub Actions release workflow.
# Usage: .\Scripts\New-Release.ps1 [-Version "1.2.0"]
# If -Version is omitted, the Version in KeyPulse.csproj is used.

param(
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Version) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $root "KeyPulse.csproj") -Raw
    $versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "KeyPulse.csproj has no Version value. Set it or pass -Version explicitly."
    }
    $Version = $versionNode.InnerText.Trim()
    Write-Host "Using project version: $Version" -ForegroundColor Cyan
}

$tag = "v$Version"

# Confirm working tree is clean
$status = git status --porcelain
if ($status) {
    throw "Working tree is not clean. Commit or stash changes before releasing.`n$status"
}

# Confirm the release before changing local or remote tags.
$existing = git tag --list $tag
if ($existing) {
    Write-Host "Tag '$tag' already exists locally." -ForegroundColor Yellow
    $confirmation = Read-Host "Replace '$tag' locally and on origin, then publish this release? [y/N]"
} else {
    $confirmation = Read-Host "Create and push '$tag' to origin to publish this release? [y/N]"
}

if ($confirmation -notmatch '^(y|yes)$') {
    Write-Host "Release cancelled." -ForegroundColor Yellow
    return
}

# Delete an existing tag only after replacement has been confirmed.
if ($existing) {
    Write-Host "Deleting local tag..." -ForegroundColor Yellow
    git tag -d $tag
    if ($LASTEXITCODE -ne 0) { throw "Failed to delete local tag." }
    
    Write-Host "Attempting to delete remote tag..." -ForegroundColor Yellow
    git push origin ":$tag" -f
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Note: Remote tag may not have existed or could not be deleted." -ForegroundColor Yellow
    }
}

Write-Host "Tagging release: $tag" -ForegroundColor Cyan
git tag $tag
if ($LASTEXITCODE -ne 0) { throw "git tag failed." }

Write-Host "Pushing tag to origin..." -ForegroundColor Cyan
git push origin $tag
if ($LASTEXITCODE -ne 0) {
    git tag -d $tag
    throw "git push failed. Local tag removed."
}

Write-Host "`nRelease tag '$tag' pushed." -ForegroundColor Green
Write-Host "Monitor the GitHub Actions workflow at: https://github.com/$(git remote get-url origin | Select-String '(?<=github\.com[:/])(.+?)(?:\.git)?$' | ForEach-Object { $_.Matches[0].Groups[1].Value })/actions"

