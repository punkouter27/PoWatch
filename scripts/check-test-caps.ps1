param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$previousLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$maximums = @{ Unit = 100; Integration = 50; E2EAPI = 25; E2EUI = 25 }
try {
    foreach ($suite in 'Unit', 'Integration', 'E2EAPI', 'E2EUI') {
        $projectPath = Join-Path $repoRoot "tests/PoWatch.$suite/PoWatch.$suite.csproj"
        [xml]$project = Get-Content -LiteralPath $projectPath -Raw
        $limit = [int]$project.SelectSingleNode('//TestCaseLimit').InnerText
        if ($limit -le 0) { throw "$suite must declare a positive TestCaseLimit." }
        if ($limit -gt $maximums[$suite]) { throw "$suite cannot raise its $($maximums[$suite]) maximum." }
        $discovery = & dotnet test $projectPath -c $Configuration --no-build --list-tests 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Test discovery failed for ${suite}: $discovery" }
        $count = @($discovery | Where-Object { "$_" -match '^    PoWatch\.' }).Count
        if ($count -eq 0) { throw "No tests discovered for $suite. Build the solution first." }
        Write-Host "${suite}: $count / $limit discovered test cases"
        if ($count -gt $limit) { throw "$suite exceeds its $limit test-case cap ($count discovered)." }
    }
}
finally {
    $env:DOTNET_CLI_UI_LANGUAGE = $previousLanguage
}
