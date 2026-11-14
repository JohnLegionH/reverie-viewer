# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# assert-no-logo.ps1 - asserts that no Linden Lab logo (the eye-in-hand, the Second Life logos and application icons) and no
# InWorldz logo or artwork is among the images under a folder.
#
# Fails on ANY of:
#   - an image file whose name says it is such a logo or artwork (linden, secondlife, second_life, sl_logo, inworldz,
#     eye-in-hand, startup_logo);
#   - with -Reference: an image whose bytes equal one of the reference images (SHA-256). The reference is the official
#     viewer's source tree (indra\newview): every image in its icons folder and every image whose name holds "logo" in its
#     skins folder. Without -Reference only the names are judged, and the summary line says so.
# Images: .png .jpg .jpeg .tga .bmp .gif .webp .ico .icns .svg .dds .ktx .ktx2 .exr .hdr, and Godot's imported copies (.ctex).
#
# Usage:
#     powershell -NoProfile -File <repo>\tools\assert-no-logo.ps1 -Path <folder> [-Path <folder>...] (or one -Path of folders separated by ";") [-Reference <indra\newview>]
# Exit 0 = every folder exists and carries none of the above. Exit 1 = anything found, or a folder is missing.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [string]$Reference = ""
)

$ErrorActionPreference = "Stop"
$imageExt = @(".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".webp", ".ico", ".icns", ".svg", ".dds", ".ktx", ".ktx2", ".exr", ".hdr", ".ctex")
$badName = [regex]'(?i)linden|secondlife|second[_ -]life|sl[_ -]?logo|inworldz|eye[_ -]?in[_ -]?hand|startup[_ -]logo'
function Get-Hash([string]$file) { (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash }

$refHashes = @{}
if ($Reference) {
    if (-not (Test-Path -LiteralPath $Reference -PathType Container)) { Write-Output "LOGO GUARD FAIL: reference folder not found: $Reference"; exit 1 }
    $refs = @()
    $icons = Join-Path $Reference "icons"
    if (Test-Path -LiteralPath $icons) { $refs += @(Get-ChildItem -LiteralPath $icons -Recurse -File | Where-Object { $imageExt -contains $_.Extension.ToLowerInvariant() }) }
    $skins = Join-Path $Reference "skins"
    if (Test-Path -LiteralPath $skins) { $refs += @(Get-ChildItem -LiteralPath $skins -Recurse -File | Where-Object { $_.Name -match '(?i)logo' -and $imageExt -contains $_.Extension.ToLowerInvariant() }) }
    if ($refs.Count -eq 0) { Write-Output "LOGO GUARD FAIL: no reference images under $Reference (a guard that checks nothing proves nothing)"; exit 1 }
    foreach ($r in $refs) { $refHashes[(Get-Hash $r.FullName)] = $r.Name }
}

$bad = 0; $images = 0
# -Path takes several folders, or one argument with folders separated by ";" (powershell -File passes no arrays)
$Path = @($Path | ForEach-Object { $_ -split ';' } | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
foreach ($dir in $Path) {
    if (-not (Test-Path -LiteralPath $dir)) { Write-Output "LOGO GUARD FAIL: not found: $dir"; exit 1 }
    $files = if (Test-Path -LiteralPath $dir -PathType Leaf) { @(Get-Item -LiteralPath $dir) } else { @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force) }
    foreach ($f in $files) {
        if ($imageExt -notcontains $f.Extension.ToLowerInvariant()) { continue }
        $images++
        if ($badName.IsMatch($f.Name)) { Write-Output "LOGO GUARD: logo by name: $($f.FullName)"; $bad++; continue }
        if ($refHashes.Count -gt 0) {
            $h = Get-Hash $f.FullName
            if ($refHashes.ContainsKey($h)) { Write-Output "LOGO GUARD: $($f.FullName) is the official viewer's $($refHashes[$h])"; $bad++ }
        }
    }
}
if ($bad -gt 0) { Write-Output "LOGO GUARD FAIL: $bad finding(s)"; exit 1 }
$how = if ($refHashes.Count -gt 0) { "names and $($refHashes.Count) reference images" } else { "names only (no reference folder given)" }
Write-Output "LOGO GUARD OK: $images images checked by $how"
exit 0
