# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# assert-no-linden.ps1 - asserts that NO Linden viewer data is under a directory.
#
# LibreMetaverse's NuGet package ships Linden data as contentFiles (linden/**: avatar_lad.xml, avatar_skeleton.xml,
# .llm meshes, static assets). The repo excludes them (Directory.Build.props, NvLibreMetaverseExcludeAssets); this is
# the guard that proves none reached an output. Fails on ANY of:
#   - a directory named "linden" (any case) anywhere under -Path
#   - a file named avatar_lad.xml or avatar_skeleton.xml
#   - a file with the .llm extension
#
# Usage:
#     powershell -NoProfile -File <repo>\tools\assert-no-linden.ps1 -Path <directory> [-Path <directory>...] [-Allow <directory>...]
# -Allow (several folders, or one argument of folders separated by ";") names folders that hold Linden data on purpose (the shipped build's avatar data folder, which goes with its
# notice): files under them are counted but not judged. Linden data anywhere else still fails.
# Exit 0 = every directory exists, holds files, and carries none of the above.
# Exit 1 = anything found, or a directory is missing or empty (a guard that checks nothing proves nothing).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [string[]]$Allow = @()
)

$ErrorActionPreference = "Stop"
$bad = 0
$checked = 0
# -Allow takes several folders, or one argument with folders separated by ";" (powershell -File passes no arrays)
$allowed = @($Allow | ForEach-Object { $_ -split ';' } | Where-Object { $_.Trim() } | ForEach-Object { [IO.Path]::GetFullPath($_.Trim()).TrimEnd([char]92) + [char]92 })
function Test-Allowed([string]$full) {
    foreach ($a in $allowed) { if ($full.StartsWith($a, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    return $false
}

foreach ($dir in $Path) {
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        Write-Output "LINDEN GUARD FAIL: directory not found: $dir"
        exit 1
    }
    $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force)
    if ($files.Count -eq 0) {
        Write-Output "LINDEN GUARD FAIL: no files under $dir (a guard that checks nothing proves nothing)"
        exit 1
    }
    $checked += $files.Count

    foreach ($d in @(Get-ChildItem -LiteralPath $dir -Recurse -Directory -Force | Where-Object { $_.Name -ieq "linden" -and -not (Test-Allowed ($_.FullName + [char]92)) })) {
        $n = @(Get-ChildItem -LiteralPath $d.FullName -Recurse -File -Force).Count
        Write-Output ("  FAIL  linden directory ({0} files): {1}" -f $n, $d.FullName)
        $bad++
    }
    foreach ($f in $files) {
        if (Test-Allowed $f.FullName) { continue }
        if ($f.Name -ieq "avatar_lad.xml" -or $f.Name -ieq "avatar_skeleton.xml") {
            Write-Output ("  FAIL  Linden avatar definition: {0}" -f $f.FullName)
            $bad++
        } elseif ($f.Extension -ieq ".llm") {
            Write-Output ("  FAIL  Linden .llm mesh: {0}" -f $f.FullName)
            $bad++
        }
    }
}

if ($bad -gt 0) {
    Write-Output "LINDEN GUARD FAIL: $bad Linden item(s) under $($Path -join ', ')"
    exit 1
}
Write-Output "LINDEN GUARD OK: $checked files under $($Path -join ', '), no linden directory, avatar_lad.xml, avatar_skeleton.xml or .llm"
exit 0
