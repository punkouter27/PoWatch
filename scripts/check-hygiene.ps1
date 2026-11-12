$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $repoRoot 'src'

foreach ($projectDir in Get-ChildItem -LiteralPath $sourceRoot -Directory | Where-Object Name -notin 'bin', 'obj') {
    if ($projectDir.Name -cnotmatch '^PoWatch\.[A-Z][A-Za-z]+$') {
        throw "Invalid project name: $($projectDir.Name)"
    }
    foreach ($source in Get-ChildItem -LiteralPath $projectDir.FullName -File -Recurse) {
        $relativePath = [IO.Path]::GetRelativePath($projectDir.FullName, $source.FullName)
        $parts = $relativePath -split '[\\/]'
        if ($parts[0] -in 'bin', 'obj', 'wwwroot') { continue }
        if ($parts.Count -gt 3) { throw "Source exceeds two directory levels: $relativePath" }
        if ($source.Extension -eq '.cs' -and $source.BaseName -cnotmatch '^[A-Z][A-Za-z0-9.]*$') {
            throw "Use PascalCase source filenames: $relativePath"
        }
    }
    $projectPath = Join-Path $projectDir.FullName "$($projectDir.Name).csproj"
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    if ($project.SelectNodes('//PackageReference[@Version]').Count -gt 0) {
        throw "Package versions must live in Directory.Packages.props: $projectPath"
    }
}

if (@(Get-ChildItem -LiteralPath $repoRoot -Filter '*.ps1' -File).Count -gt 0) {
    throw 'Developer scripts belong in scripts.'
}
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    if ($script.BaseName -cnotmatch '^[a-z]+(-[a-z]+)*$') { throw "Use lowercase script names: $($script.Name)" }
}

$indexPath = Join-Path $sourceRoot 'PoWatch.Client/wwwroot/index.html'
$index = Get-Content -LiteralPath $indexPath -Raw
foreach ($match in [regex]::Matches($index, '(?:href|src)="((?:js|css|lib|media)/[^"]+)"')) {
    $assetPath = Join-Path (Split-Path $indexPath -Parent) $match.Groups[1].Value
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Missing shell asset: $assetPath" }
}
# The app is a stats webcam, not a care monitor: no caregiver-era vocabulary in code, pages or tests.
# Vendored libraries (wwwroot/lib) and build output are exempt; "shift" and "acknowledge" are only
# banned in their caregiver sense, since both are ordinary words elsewhere (bit shifts, batch acks).
$banned = '(?i)caregiver|clinical|handoff|nurse|patient|shift ?(window|clock|report)|mid-shift|acknowledg(e)?ment'
$scanned = '.cs', '.razor', '.js', '.css', '.json', '.html', '.http'
$hits = foreach ($root in 'src', 'tests') {
    Get-ChildItem -LiteralPath (Join-Path $repoRoot $root) -File -Recurse |
        Where-Object { $_.Extension -in $scanned -and $_.FullName -notmatch '[\\/](bin|obj|lib)[\\/]' } |
        Select-String -Pattern $banned
}
if ($hits) {
    $hits | ForEach-Object { Write-Host "$([IO.Path]::GetRelativePath($repoRoot, $_.Path)):$($_.LineNumber): $($_.Line.Trim())" }
    throw "Caregiver-era vocabulary found ($(@($hits).Count) lines)."
}

# Every class a stylesheet defines must be used somewhere in markup, code or scripts. Radzen's own
# classes (rz-*) are styled, never written by us, so they are exempt.
$clientRoot = Join-Path $sourceRoot 'PoWatch.Client'
$usage = (Get-ChildItem -LiteralPath $clientRoot -File -Recurse |
    Where-Object { $_.Extension -in '.razor', '.cs', '.js', '.html' -and $_.FullName -notmatch '[\\/](bin|obj|lib)[\\/]' } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$orphans = foreach ($sheet in Get-ChildItem -LiteralPath $clientRoot -Filter '*.css' -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|lib)[\\/]' }) {
    $css = [regex]::Replace((Get-Content -LiteralPath $sheet.FullName -Raw), '/\*[\s\S]*?\*/|url\([^)]*\)', '')
    foreach ($class in [regex]::Matches($css, '\.([A-Za-z][\w-]*)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) {
        if ($class -like 'rz-*') { continue }
        if ($usage -notmatch "(?<![\w-])$([regex]::Escape($class))(?![\w-])") { "$($sheet.Name): .$class" }
    }
}
if ($orphans) {
    $orphans | ForEach-Object { Write-Host $_ }
    throw "Unused CSS classes found ($(@($orphans).Count))."
}

# A component nobody renders is dead weight a reviewer will not notice. Every .razor that is not a
# routable page (@page) and not a framework file (_Imports) must be named by another .razor or .cs —
# as markup (<Name …>), as a type (typeof(Name)), or as a layout (@layout Name) — or it goes.
$componentPool = @(Get-ChildItem -LiteralPath $clientRoot -File -Recurse |
    Where-Object { $_.Extension -in '.razor', '.cs' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.BaseName -cnotmatch '^_' })
$deadComponents = foreach ($component in $componentPool | Where-Object Extension -eq '.razor') {
    $text = Get-Content -LiteralPath $component.FullName -Raw
    if ($text -match '(?m)^\s*@page\b') { continue }
    $name = [regex]::Escape($component.BaseName)
    $pattern = "<$name(?=[\s/>])|typeof\(\s*$name\s*\)|@layout\s+(?:[\w.]+\.)?$name\b"
    $used = @($componentPool | Where-Object { $_.FullName -ne $component.FullName } |
        Where-Object { Select-String -LiteralPath $_.FullName -Pattern $pattern -Quiet })
    if ($used.Count -eq 0) { [IO.Path]::GetRelativePath($clientRoot, $component.FullName) }
}
if ($deadComponents) {
    $deadComponents | ForEach-Object { Write-Host $_ }
    throw "Unused components found ($(@($deadComponents).Count))."
}

Write-Host 'Directory structure, naming, package versions, shell assets, vocabulary, CSS classes, and components passed.'
