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

# Confirm tag does not already exist
$existing = git tag --list $tag
if ($existing) {
    throw "Tag '$tag' already exists. Delete it first if you want to re-release: git tag -d $tag"
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

