# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# build-kit.ps1 - assembles the portable hardware check kit (see run-laptop.ps1) into a folder, and optionally one zip.
#
# The kit holds: the exported viewer (tools\s1.ps1 makes it), the scripts that run it, the recorded region captures and
# their asset cache, the avatar character files, the crowd bake cache and the two small fixtures the busy views need.
# The captures, assets and character files are local-only content: they are never committed, and the kit is for the
# machine of the person who made it. Nothing is written into the repository.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\laptop-kit\build-kit.ps1 `
#       -OutDir <empty folder> -FixturesLocal <folder with capture-region-a-v9/v5 and assets\> -CharacterDir <character folder> `
#       [-BakeCache <folder>] [-BuildNote "<what the export was built from>"] [-Zip]
# The export must already exist in <repo>\host\Viewer.Godot\build (run tools\s1.ps1 first).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$FixturesLocal,
    [Parameter(Mandatory)][string]$CharacterDir,
    [string]$BakeCache = "",
    [string]$BuildNote = "",
    [switch]$Zip
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$build = Join-Path $repo "host\Viewer.Godot\build"
$tools = Join-Path $repo "tools"

if (-not (Test-Path -LiteralPath (Join-Path $build "Reverie.console.exe"))) { throw "no export in $build (run tools\s1.ps1 first)" }
if ((Test-Path -LiteralPath $OutDir) -and @(Get-ChildItem -LiteralPath $OutDir -Force).Count -gt 0) { throw "$OutDir is not empty" }
foreach ($need in @("capture-region-a-v9.jsonl.gz", "capture-region-a-v5.jsonl.gz", "assets")) {
    if (-not (Test-Path -LiteralPath (Join-Path $FixturesLocal $need))) { throw "missing $need in $FixturesLocal" }
}
if (-not (Test-Path -LiteralPath $CharacterDir)) { throw "no character folder $CharacterDir" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Copy-Tree([string]$from, [string]$to, [string[]]$skipTop = @()) {
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    foreach ($e in Get-ChildItem -LiteralPath $from -Force) {
        if ($skipTop -contains $e.Name -or $e.Name -like "s1-result*") { continue }
        if ($e.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
        if ($e.PSIsContainer) { Copy-Tree $e.FullName (Join-Path $to $e.Name) } else { Copy-Item -LiteralPath $e.FullName -Destination (Join-Path $to $e.Name) }
    }
}

# the viewer: the shipping export without the development export, the sandbox of earlier runs and S-1 result files
$viewer = Join-Path $OutDir "viewer"
Copy-Tree $build $viewer @("dev", "appdata")

# scripts and the helpers godot.ps1 uses
$kitTools = Join-Path $OutDir "tools"
New-Item -ItemType Directory -Force -Path $kitTools, (Join-Path $OutDir "scripts") | Out-Null
foreach ($f in @("godot.ps1", "job-guard.ps1", "test-launch.ps1", "TestWindowPlacement.cs", "crash-watch.ps1")) { Copy-Item -LiteralPath (Join-Path $tools $f) -Destination $kitTools }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "run-laptop.ps1") -Destination (Join-Path $OutDir "scripts")
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "RUN-LAPTOP.cmd") -Destination $OutDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "README.txt") -Destination $OutDir

# fixtures of the busy views (the file keeps its relative path to the texture it names)
$fx = Join-Path $OutDir "fixtures"
New-Item -ItemType Directory -Force -Path (Join-Path $fx "materials"), (Join-Path $fx "textures") | Out-Null
Copy-Item -LiteralPath (Join-Path $repo "fixtures\materials\projector.matzoo.json") -Destination (Join-Path $fx "materials")
Copy-Item -LiteralPath (Join-Path $repo "fixtures\textures\tex-grid-arrow.j2c") -Destination (Join-Path $fx "textures")

# recorded data
$data = Join-Path $OutDir "data"
New-Item -ItemType Directory -Force -Path (Join-Path $data "captures") | Out-Null
foreach ($c in @("capture-region-a-v9.jsonl.gz", "capture-region-a-v5.jsonl.gz")) { Copy-Item -LiteralPath (Join-Path $FixturesLocal $c) -Destination (Join-Path $data "captures") }
Copy-Tree (Join-Path $FixturesLocal "assets") (Join-Path $data "assets")
Copy-Tree $CharacterDir (Join-Path $data "character")
if ($BakeCache) { Copy-Tree $BakeCache (Join-Path $data "bake-cache") } else { New-Item -ItemType Directory -Force -Path (Join-Path $data "bake-cache") | Out-Null }

# manifest
$head = (& git -C $repo rev-parse --short HEAD 2>$null)
$size = (Get-ChildItem -LiteralPath $OutDir -Recurse -File | Measure-Object Length -Sum).Sum
$lines = @("Viewer hardware check kit", "Built: $((Get-Date).ToUniversalTime().ToString('o'))", "Kit tools commit: $head", "Export built from: $BuildNote", "Size: $([math]::Round($size / 1MB, 1)) MiB")
$lines += ""
$lines += "Top-level contents:"
foreach ($e in Get-ChildItem -LiteralPath $OutDir) {
    $s = if ($e.PSIsContainer) { (Get-ChildItem -LiteralPath $e.FullName -Recurse -File | Measure-Object Length -Sum).Sum } else { $e.Length }
    $lines += ("  {0,-22} {1,10:N1} MiB" -f $e.Name, ($s / 1MB))
}
Set-Content -LiteralPath (Join-Path $OutDir "KIT-MANIFEST.txt") -Value $lines -Encoding utf8
Write-Output ($lines -join "`n")

if ($Zip) {
    $zipPath = "$($OutDir.TrimEnd('\','/')).zip"
    if (Test-Path -LiteralPath $zipPath) { throw "$zipPath exists" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($OutDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Output ("zip: {0} ({1:N1} MiB)" -f $zipPath, ((Get-Item -LiteralPath $zipPath).Length / 1MB))
}
