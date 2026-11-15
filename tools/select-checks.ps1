# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# select-checks.ps1 - which render cases and whether the stock smoke an order's changes need.
#
# Reads the files changed between -Base and the work tree (committed, staged, unstaged and new files), places each one with
# tools\check-map.tsv into the areas of tools\check-areas.tsv (tools\CheckSelector.cs decides, the unit tests check it), and
# prints what to run:
#   render: none | render.ps1 -All | render.ps1 -All -OnlyCases '<regex>' -Backends <backend> (one line per backend)
#   smoke:  yes (a file in an area that runs it: the session, the protocol, inventory, the map, login, the automation, and code that sends or reads grid messages) | no
# A file the map cannot place selects the full set, and is named. The .NET suite and S-1 run for every order whatever this says.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\select-checks.ps1 [-Base <commit>] [-Repo <folder>] [-Json <file>]
#   -Base  the commit the order started from (default: the merge base with origin/main)
#   -Json  also writes the selection as JSON (files, areas, cases, backends, smoke, full, unplaced)
# Exit 0, or 2 when git cannot list the changes.
# =====================================================================================
[CmdletBinding()]
param([string]$Base = "", [string]$Repo = "", [string]$Json = "")
$ErrorActionPreference = "Stop"
if (-not $Repo) { $Repo = Split-Path -Parent $PSScriptRoot }
Add-Type -Path (Join-Path $PSScriptRoot "CheckSelector.cs")
if (-not $Base) { $Base = (& git -C $Repo merge-base HEAD origin/main 2>$null); if ($LASTEXITCODE -ne 0 -or -not $Base) { Write-Output "select-checks: no base given and no merge base with origin/main"; exit 2 } }
$files = New-Object System.Collections.Generic.List[string]
foreach ($cmd in @(@("diff", "--name-only", "$Base"), @("ls-files", "--others", "--exclude-standard"))) {
    $out = & git -C $Repo @cmd
    if ($LASTEXITCODE -ne 0) { Write-Output "select-checks: git $($cmd -join ' ') failed"; exit 2 }
    foreach ($f in $out) { if ($f -and -not $files.Contains($f)) { $files.Add($f) } }
}
$map = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "check-map.tsv"))
$areas = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "check-areas.tsv"))
$s = [NvTools.CheckSelector]::Select([string[]]$files.ToArray(), $map, $areas)
Write-Output ("select-checks: {0} changed files since {1}" -f $files.Count, $Base)
foreach ($f in $files) { Write-Output ("  {0,-24} {1}" -f (($s.FileAreas[$f]) -join ","), $f) }
Write-Output ("areas:  {0}" -f $(if ($s.Areas.Count) { $s.Areas -join ", " } else { "none" }))
$render = @([NvTools.CheckSelector]::RenderLines($s))   # run as printed: a narrow selection is -All filtered by -OnlyCases
foreach ($r in $render) { Write-Output "render: $r" }
Write-Output ("smoke:  {0}" -f $(if ($s.Smoke) { "yes" } else { "no" }))
if ($s.Unplaced.Count) { Write-Output ("unplaced (these select the full set): {0}" -f ($s.Unplaced -join ", ")) }
if ($Json) {
    [pscustomobject]@{ base = $Base; files = @($files); areas = @($s.Areas); full = $s.Full; cases = $s.Cases; backends = @($s.Backends); smoke = $s.Smoke; unplaced = @($s.Unplaced); render = $render } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Json -Encoding utf8
}
exit 0
