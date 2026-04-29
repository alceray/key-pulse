param(
    [string[]]$ArtifactPaths = @(
        "publish\KeyPulse.exe",
        "installer\output\KeyPulse-Setup-1.0.0.exe"
    ),
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-SignToolPath {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "signtool.exe not found. Install Windows SDK and ensure signtool is on PATH."
    }

    return $command.Source
}

function Sign-Artifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SignTool,
        [Parameter(Mandatory = $true)]
        [string]$PathToSign,
        [Parameter(Mandatory = $true)]
        [string]$Timestamp
    )

    if (-not (Test-Path -LiteralPath $PathToSign)) {
        Write-Warning "Skipping missing artifact: $PathToSign"
        return
    }

    if ($env:KEYPULSE_SIGN_CERT_THUMBPRINT) {
        & $SignTool sign /sha1 $env:KEYPULSE_SIGN_CERT_THUMBPRINT /fd SHA256 /td SHA256 /tr $Timestamp "$PathToSign"
    }
    elseif ($env:KEYPULSE_SIGN_PFX_PATH -and $env:KEYPULSE_SIGN_PFX_PASSWORD) {
        & $SignTool sign /f $env:KEYPULSE_SIGN_PFX_PATH /p $env:KEYPULSE_SIGN_PFX_PASSWORD /fd SHA256 /td SHA256 /tr $Timestamp "$PathToSign"
    }
    else {
        throw "Signing configuration missing. Set KEYPULSE_SIGN_CERT_THUMBPRINT or KEYPULSE_SIGN_PFX_PATH and KEYPULSE_SIGN_PFX_PASSWORD."
    }

    Write-Host "Signed artifact: $PathToSign"
}

$signToolPath = Get-SignToolPath
foreach ($artifact in $ArtifactPaths) {
    Sign-Artifact -SignTool $signToolPath -PathToSign $artifact -Timestamp $TimestampUrl
}

