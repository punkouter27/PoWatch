# Runs the browser tier (E2EUI) against the TRIMMED RELEASE publish - the same build that deploys -
# so a trimming or publish-only fault shows up here, not in production.
#
#   pwsh scripts/run-browser-tests.ps1 [-Port 7429] [-Filter <test filter>] [-Repeat 1] [-Headed]
#
# 1. dotnet publish src/PoWatch.Api -c Release (the client is trimmed exactly as in deploy.yml)
# 2. Azurite in Docker on 21100-21102 (its own container, removed afterwards)
# 3. the published app: ASPNETCORE_ENVIRONMENT=Test, guest sign-in on, Key Vault off, no cloud AI
# 4. dotnet test tests/PoWatch.E2EUI against it (E2E_BASE_URL), -Repeat times in a row, then stops
#    everything it started. Exit code: 0 only when every run passed.
param(
    [int] $Port = 7429,
    [string] $Filter = '',
    [int] $Repeat = 1,
    [switch] $Headed
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path ([IO.Path]::GetTempPath()) "powatch-e2eui-$Port"
$container = "powatch-e2eui-azurite-$Port"
$baseUrl = "http://localhost:$Port"
$envNames = 'ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'AzureStorage__ConnectionString', 'AzureStorage__ServiceUri',
    'FeatureFlags__DeveloperBypassAuth', 'FeatureFlags__EnableKeyVault', 'AiProvider__Provider', 'AzureOpenAi__Endpoint',
    'ApplicationInsights__ConnectionString', 'E2E_BASE_URL', 'E2E_HEADED'
$app = $null
$code = 1

try {
    Write-Host "Publishing the Release build to $publish"
    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    dotnet publish (Join-Path $root 'src/PoWatch.Api/PoWatch.Api.csproj') -c Release -o $publish --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    docker info *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Docker is not running (Azurite needs it).' }
    docker rm -f $container *> $null
    docker run -d --rm --name $container -p 21100:10000 -p 21101:10001 -p 21102:10002 `
        mcr.microsoft.com/azure-storage/azurite:latest azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 `
        --tableHost 0.0.0.0 --skipApiVersionCheck --loose | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Azurite did not start' }

    $env:ASPNETCORE_ENVIRONMENT = 'Test'
    $env:ASPNETCORE_URLS = $baseUrl
    $env:AzureStorage__ConnectionString = 'DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:21100/devstoreaccount1;QueueEndpoint=http://127.0.0.1:21101/devstoreaccount1;TableEndpoint=http://127.0.0.1:21102/devstoreaccount1;'
    $env:AzureStorage__ServiceUri = ''
    $env:FeatureFlags__DeveloperBypassAuth = 'true'
    $env:FeatureFlags__EnableKeyVault = 'false'
    $env:AiProvider__Provider = 'Template'
    $env:AzureOpenAi__Endpoint = ''
    $env:ApplicationInsights__ConnectionString = ''
    $app = Start-Process dotnet -ArgumentList (Join-Path $publish 'PoWatch.Api.dll') -WorkingDirectory $publish `
        -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $publish 'app.out.log') -RedirectStandardError (Join-Path $publish 'app.err.log')

    $up = $false
    foreach ($i in 1..90) {
        try { if ((Invoke-WebRequest "$baseUrl/health/live" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $up = $true; break } } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $up) { throw "The published app did not answer on $baseUrl (see $publish/app.*.log)" }
    Write-Host "Published app is up on $baseUrl"

    $env:E2E_BASE_URL = $baseUrl
    if ($Headed) { $env:E2E_HEADED = '1' }
    $testArgs = @('test', (Join-Path $root 'tests/PoWatch.E2EUI'), '-c', 'Release', '--nologo')
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    $code = 0
    foreach ($run in 1..$Repeat) {
        Write-Host "E2EUI run $run of $Repeat"
        dotnet @testArgs
        if ($LASTEXITCODE -ne 0) { $code = $LASTEXITCODE }
    }
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    docker rm -f $container *> $null
    foreach ($name in $envNames) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
}
exit $code
