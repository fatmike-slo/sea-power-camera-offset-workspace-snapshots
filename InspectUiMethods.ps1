# Read-only method-name audit for discovering Sea Power's legitimate window open/close APIs.
# Does not launch the game or load/execute its assemblies.
[CmdletBinding()]
param([string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power')
$ErrorActionPreference = 'Stop'
$assembly = Join-Path $GameDir 'Sea Power_Data\Managed\Seapower-Scripts.dll'
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Game assembly missing: $assembly" }
$project = Join-Path $PSScriptRoot 'tools\UIAudit\UIAudit.csproj'
$outDir = Join-Path $PSScriptRoot '_camera-workspace-wip4-test'
New-Item -Path $outDir -ItemType Directory -Force | Out-Null
$out = Join-Path $outDir 'UI_METHOD_AUDIT.txt'
$lines = @(& dotnet run --project $project -c Release -- "$assembly")
if ($LASTEXITCODE -ne 0) { throw 'Read-only UI metadata scan failed.' }
$lines | Set-Content -LiteralPath $out -Encoding UTF8
Write-Host "READ-ONLY API AUDIT WRITTEN: $out" -ForegroundColor Green
Write-Host 'No game files were changed.'
