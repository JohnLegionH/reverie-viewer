# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# where the local-only fixtures (captures, cached assets, local goldens) are read from. Dot-sourced by render.ps1
# and contact-sheets.ps1; the .NET tests resolve the same variable in TestPaths.FixturesLocal.
#   NV_FIXTURES_LOCAL unset or empty: <repo>\fixtures-local (the default).
#   NV_FIXTURES_LOCAL set: that folder (absolute, must exist), READ-ONLY to the run. A fresh clone at close-out points it
#   at the working repo's fixtures-local; nothing the run does may write there (render.ps1 refuses -Propose).
$script:FixturesLocalVariable = "NV_FIXTURES_LOCAL"

function Test-FixturesLocalOverride { return -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($script:FixturesLocalVariable)) }

function Get-FixturesLocal([string]$repo) {
    $o = [Environment]::GetEnvironmentVariable($script:FixturesLocalVariable)
    if ([string]::IsNullOrWhiteSpace($o)) { return (Join-Path $repo "fixtures-local") }
    if (-not [System.IO.Path]::IsPathRooted($o) -or -not ([System.IO.Path]::GetPathRoot($o)).Contains("\")) { throw "$($script:FixturesLocalVariable) must be an absolute path: '$o'" }
    if (-not (Test-Path -LiteralPath $o -PathType Container)) { throw "$($script:FixturesLocalVariable) points at a folder that does not exist: '$o'" }
    return [System.IO.Path]::GetFullPath($o)
}
