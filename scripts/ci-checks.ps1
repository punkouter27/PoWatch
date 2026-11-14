# PoWatch's extra build-time checks. The shared .github/workflows/deploy.yml runs this from the repo
# root after the Release build and the Unit tier; any failure fails the build (and so blocks the deploy).
# Order: cheap static checks first, then the trim gate, then the test tiers that need Docker/Playwright.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

function Invoke-Native([string]$What, [scriptblock]$Command) {
    Write-Host "::group::$What"
    & $Command
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($code -ne 0) { throw "$What failed (exit $code)." }
}

# Naming, layout, package versions, shell assets, vocabulary, unused CSS and components.
./scripts/check-hygiene.ps1

# Bicep is never applied from here (infra.yml is manual), but a broken template still fails the build.
$template = Join-Path ([IO.Path]::GetTempPath()) 'powatch-template.json'
Invoke-Native 'Compile infrastructure' { az bicep build --file infra/main.bicep --outfile $template }

# The client is trim-analyzed, so a trimmed publish must stay warning-free: a new DTO reached by
# reflection only shows up here (or as a runtime failure in someone's browser).
$trimOut = Join-Path ([IO.Path]::GetTempPath()) 'powatch-trim-gate'
Invoke-Native 'Trim gate (client publish)' {
    dotnet publish src/PoWatch.Client/PoWatch.Client.csproj -c Release --no-build -p:PublishTrimmed=true -o $trimOut
}

# Per-tier test-case caps (TestCaseLimit in each test csproj).
./scripts/check-test-caps.ps1

Invoke-Native 'dotnet format' { dotnet format PoWatch.slnx --verify-no-changes --verbosity minimal }

# Integration, API E2E and UI E2E run against Azurite via Testcontainers (Docker); UI E2E drives
# Playwright's Chromium against an in-process host (E2E_LOCAL=1). Unit already ran in deploy.yml.
$playwright = './tests/PoWatch.E2EUI/bin/Release/net10.0/playwright.ps1'
if ($IsLinux) {
    Invoke-Native 'Install Playwright Chromium' { & $playwright install --with-deps chromium }
}
else {
    Invoke-Native 'Install Playwright Chromium' { & $playwright install chromium }
}
$env:E2E_LOCAL = '1'
foreach ($suite in 'Integration', 'E2EAPI', 'E2EUI') {
    Invoke-Native "$suite tests" { dotnet test "tests/PoWatch.$suite/PoWatch.$suite.csproj" -c Release --no-build }
}

Write-Host 'PoWatch CI checks passed.'
