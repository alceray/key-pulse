# Build-Release.ps1
# Local equivalent of the GitHub Actions release workflow.
# Usage: .\scripts\Build-Release.ps1 [-Version "1.2.0"]
# If -Version is omitted, the version in KeyPulse.csproj is used as-is.

param(
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

Write-Host "=== KeyPulse Signal Local Release Build ===" -ForegroundColor Cyan

# Resolve version
if (-not $Version) {
    [xml]$csproj = Get-Content "KeyPulse.csproj"
    $Version = $csproj.SelectSingleNode('//Project/PropertyGroup/Version').InnerText
    Write-Host "No version specified — using csproj default: $Version" -ForegroundColor Yellow
}

$fileVersion = "$Version.0"
Write-Host "Version:     $Version"
Write-Host "FileVersion: $fileVersion"

# Restore & Publish
Write-Host "`n--- Restore & Publish ---" -ForegroundColor Cyan
dotnet restore "KeyPulse Signal.sln"
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

dotnet publish "KeyPulse.csproj" -c Release -r win-x64 --no-self-contained -o publish\ /p:Version=$Version /p:FileVersion=$fileVersion
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Build installer
Write-Host "`n--- Building Installer ---" -ForegroundColor Cyan
$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    throw "iscc.exe not found. Install Inno Setup and ensure it is on PATH."
}

& iscc.exe /DAppVersion=$Version "installer\KeyPulse.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup build failed." }

# Confirm output
$installer = Get-ChildItem "installer\output\KeyPulse-Signal-Setup-*.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($installer) {
    Write-Host "`n=== Build complete ===" -ForegroundColor Green
    Write-Host "Installer: $($installer.FullName)"
} else {
    throw "Installer output not found. Check Inno Setup output."
}

