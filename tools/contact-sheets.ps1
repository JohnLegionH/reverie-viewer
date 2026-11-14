# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# contact sheets for review (NOT tests, NOT goldens). Each scene is rendered by the exported build on vulkan
# from 8 bearings around its centre (0 N... 315 NW, slightly elevated) and assembled into one labelled 2 x 4 sheet:
#   tools\test-results\sheets\<scene>-sheet.png
# Scenes: prim, mesh, texture and sculpt zoos (committed, synthetic) and region A v4 (LOCAL-ONLY grid content: its sheet
# goes only to the private review repo, never into this repo). Every Godot run goes through tools\godot.ps1.
#     powershell -NoProfile -File <repo>\tools\contact-sheets.ps1
param([switch]$ShowPaths)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "fixtures-local.ps1")
$local = Get-FixturesLocal $repo   # NV_FIXTURES_LOCAL wins (read-only: sheets go to test-results only)
if ($ShowPaths) { Write-Output "fixtures-local: $local"; exit 0 }
$build = Join-Path $repo "host\Viewer.Godot\build"
$exe = Join-Path $build "Reverie.console.exe"
$godot = Join-Path $PSScriptRoot "godot.ps1"
$out = Join-Path $PSScriptRoot "test-results\sheets"
New-Item -ItemType Directory -Force -Path $out | Out-Null
if (-not (Test-Path -LiteralPath $exe)) { Write-Output "no exported build at $exe (run tools\s1.ps1 first)"; exit 1 }
$assets = Join-Path $local "assets"
$scenes = [ordered]@{
    "prim-zoo" = "`"--tr-fixture=$(Join-Path $repo 'fixtures\prims\prim-zoo.primzoo.json')`""
    "mesh-zoo" = "`"--tr-fixture=$(Join-Path $repo 'fixtures\meshes\mesh-zoo.meshzoo.json')`""
    "texture-zoo" = "`"--tr-fixture=$(Join-Path $repo 'fixtures\textures\texture-zoo.texzoo.json')`""
    "sculpt-zoo" = "`"--tr-fixture=$(Join-Path $repo 'fixtures\sculpts\sculpt-zoo.sculptzoo.json')`""
    "region-a-v4" = "`"--tr-fixture=$(Join-Path $local 'capture-region-a-v4.jsonl.gz')`" `"--tr-assets=$assets`""
    "region-a-v5" = "`"--tr-fixture=$(Join-Path $local 'capture-region-a-v5.jsonl.gz')`" `"--tr-assets=$assets`""
    # region A v5 under a tight texture budget (96 MiB: forces eviction), for the review repo only
    "region-a-v5-tight" = "`"--tr-fixture=$(Join-Path $local 'capture-region-a-v5.jsonl.gz')`" `"--tr-assets=$assets`" --tr-budget=96m"
    "water-sky" = "`"--tr-fixture=$(Join-Path $repo 'fixtures\environment\water-sky.scene.json')`""
    # the synthetic heavy scene (600 texture ids, ~2.3 GiB at full resolution) at a floor-class budget and at 2 GiB
    "heavy-256m" = "--tr-fixture=synthetic-heavy --tr-budget=256m"
    "heavy-2048m" = "--tr-fixture=synthetic-heavy --tr-budget=2048m"
}
foreach ($name in $scenes.Keys) {
    if ($name -match "^(region-a-v\d+)" -and -not (Test-Path -LiteralPath (Join-Path $local "capture-$($Matches[1]).jsonl.gz"))) { Write-Output "SKIP $name (local capture absent)"; continue }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $godot -Exe $exe -AppDataDir (Join-Path $build "appdata") -TimeoutSeconds 300 `
        -LogFile (Join-Path $out "$name.log") -ArgLine "--rendering-driver vulkan -- --test-render `"--tr-out=$out`" --tr-tag=$name --tr-orbit=1 $($scenes[$name])" | Out-Null
    $sheet = Join-Path $out "$name-sheet.png"
    Write-Output ("{0}: exit {1}, sheet {2}" -f $name, $LASTEXITCODE, $(if (Test-Path -LiteralPath $sheet) { $sheet } else { "MISSING" }))
}
