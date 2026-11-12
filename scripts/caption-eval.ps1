# Scores the caption models (wwwroot/js/caption-models.js) on a fixed frame set: latency, how many
# replies the app would keep, and how close they are to human captions. See scripts/caption-eval/eval.mjs.
#   ./scripts/caption-eval.ps1                                  # every model, 40 COCO indoor frames
#   ./scripts/caption-eval.ps1 -Models florence-2 -Count 10
#   ./scripts/caption-eval.ps1 -Frames C:\frames                # your own frames + captions.json
param([string]$Models = '', [int]$Count = 40, [string]$Frames = '')

$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'caption-eval'
if (-not (Test-Path (Join-Path $dir 'node_modules'))) {
    npm install --prefix $dir --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
}
$arguments = @((Join-Path $dir 'eval.mjs'), '--count', $Count)
if ($Models) { $arguments += @('--models', $Models) }
if ($Frames) { $arguments += @('--frames', $Frames) }
node @arguments
if ($LASTEXITCODE -ne 0) { throw 'Caption eval failed.' }
