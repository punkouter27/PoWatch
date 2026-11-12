#Requires -Version 7
<#
.SYNOPSIS
  One-shot local bootstrap for PoWatch: toolchain, local Azurite, and Azure auth.
  Idempotent — safe to re-run. Run from any directory.
#>
[CmdletBinding()]
param(
    [switch]$SkipAzureLogin
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Write-Host "PoWatch setup — repo: $repo" -ForegroundColor Cyan

function Test-Cmd($name) { [bool](Get-Command $name -ErrorAction SilentlyContinue) }

# 1. Toolchain via WinGet (no-ops if already installed)
if (Test-Cmd winget) {
    $pkgs = @('Microsoft.DotNet.SDK.10', 'Docker.DockerDesktop', 'Microsoft.AzureCLI')
    foreach ($p in $pkgs) {
        winget list --id $p -e 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Installing $p ..." -ForegroundColor Yellow
            winget install --id $p -e --accept-source-agreements --accept-package-agreements
        } else { Write-Host "$p already present." -ForegroundColor DarkGray }
    }
} else {
    Write-Warning "WinGet not found — install .NET 10 SDK, Docker Desktop, and Azure CLI manually."
}

# 2. HTTPS dev certificate
dotnet dev-certs https --trust | Out-Null
Write-Host "HTTPS dev certificate ready." -ForegroundColor Green

# 3. Local Azure Table Storage emulator (Azurite) named after the solution
if (Test-Cmd docker) {
    Push-Location $repo
    docker compose up -d azurite
    Pop-Location
    Write-Host "Azurite container 'PoWatch' running (ports 10000-10002)." -ForegroundColor Green
} else {
    Write-Warning "Docker not found — start Azurite manually before running the app."
}

# 4. Azure sign-in for Managed Identity / Key Vault access
if (-not $SkipAzureLogin -and (Test-Cmd az)) {
    az account show 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { az login } else { Write-Host "Azure CLI already signed in." -ForegroundColor DarkGray }
}

# 5. Git hooks: pre-commit runs the format gate and the hygiene check (scripts/hooks/pre-commit)
if (Test-Cmd git) {
    git -C $repo config core.hooksPath scripts/hooks
    Write-Host "Git hooks installed (core.hooksPath = scripts/hooks)." -ForegroundColor Green
}

# 6. Restore + build
Push-Location $repo
dotnet restore PoWatch.slnx
dotnet build PoWatch.slnx -c Debug --nologo
Pop-Location

Write-Host "Setup complete. Press F5 (or 'dotnet run --project src/PoWatch.Api') to launch." -ForegroundColor Cyan
