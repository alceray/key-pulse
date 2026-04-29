# New-Release.ps1
# Tags and pushes a release in one step, triggering the GitHub Actions release workflow.
# Usage: .\scripts\New-Release.ps1 -Version "1.2.0"

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$tag = "v$Version"

# Confirm working tree is clean
$status = git status --porcelain
if ($status) {
    throw "Working tree is not clean. Commit or stash changes before releasing.`n$status"
}

# Delete existing tag if it exists (local and remote)
$existing = git tag --list $tag
if ($existing) {
    Write-Host "Tag '$tag' already exists locally." -ForegroundColor Yellow
    $confirmation = Read-Host "Delete existing tag and create a new one? (y/n)"
    
    if ($confirmation -ne "y" -and $confirmation -ne "Y" -and $confirmation -ne "") {
        Write-Host "Release cancelled." -ForegroundColor Yellow
        exit 0
    }
    
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

