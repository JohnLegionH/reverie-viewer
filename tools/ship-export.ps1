# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# ship-export.ps1 - copy an exported build into the folder the viewer is run from.
#   -Source        the export (the S-1 output folder)
#   -Destination   the folder the viewer is run from
#   -MoveAsideTool a script that takes -Label "<why>" and absolute paths and puts each path aside, keeping its folders (default: the
#                  environment variable NV_MOVE_ASIDE_TOOL). Nothing is ever deleted: with no tool the script stops before it touches anything.
#   -WaitMinutes   how long to wait for a viewer started from -Destination to end (default 15); then the copy is skipped (exit 2).
# What it does, in order:
#   1. waits (up to -WaitMinutes) while a Reverie.exe or Reverie.console.exe from -Destination (its program inside that folder) is
#      running, and copies nothing while one is; a viewer whose program is anywhere else is listed and ignored
#   2. moves aside every file in -Destination that the export lacks (the export's own run state, appdata\ and s1-result*, is not part of
#      the export, so the same files in -Destination are among them)
#   3. copies every export file that is missing or different, then compares the SHA-256 of every export file with its copy
# Exit: 0 all files match; 1 a file differs, a move failed or a tool is missing; 2 a viewer from -Destination was still running.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination,
    [string]$MoveAsideTool = $env:NV_MOVE_ASIDE_TOOL,
    [int]$WaitMinutes = 15
)
$ErrorActionPreference = "Stop"
$Source = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
if (-not $MoveAsideTool -or -not (Test-Path -LiteralPath $MoveAsideTool)) {
    Write-Output "ship-export: no move-aside tool (-MoveAsideTool or NV_MOVE_ASIDE_TOOL); nothing was copied or moved"
    exit 1
}
if (-not (Test-Path -LiteralPath $Source -PathType Container)) { Write-Output "ship-export: no export at the source folder"; exit 1 }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

function Get-Viewers {
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -in @("Reverie", "Reverie.console") }
}
# Only a viewer whose program is inside the destination holds the copy: its files are the ones being replaced. A viewer started
# from anywhere else (a test copy, another export) uses its own files; it is listed and ignored.
function Test-InsideFolder($process, [string]$folder) {
    return [bool]($process.Path -and $process.Path.StartsWith($folder + "\", [StringComparison]::OrdinalIgnoreCase))
}
function Get-ViewersFrom([string]$folder) {
    Get-Viewers | Where-Object { Test-InsideFolder $_ $folder }
}
foreach ($v in @(Get-Viewers | Where-Object { -not (Test-InsideFolder $_ $Destination) })) {
    $where = if ($v.Path) { $v.Path } else { "(its program's path could not be read)" }
    Write-Output "ship-export: a viewer not started from the destination is running, ignored: process $($v.Id), $where"
}
$deadline = (Get-Date).AddMinutes($WaitMinutes)
while ($true) {
    $running = @(Get-ViewersFrom $Destination)
    if ($running.Count -eq 0) { break }
    if ((Get-Date) -ge $deadline) {
        Write-Output "ship-export: SKIPPED, a viewer from the destination is still running (process $($running[0].Id)); nothing was copied or moved"
        exit 2
    }
    Start-Sleep -Seconds 10
}

# the export: every file but its own run state
$srcFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -File -Force | Where-Object {
    $rel = $_.FullName.Substring($Source.Length + 1)
    -not ($rel -like "appdata\*") -and -not ($rel -like "s1-result*")
})
$srcSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($f in $srcFiles) { [void]$srcSet.Add($f.FullName.Substring($Source.Length + 1)) }

$extra = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Force | Where-Object { -not $srcSet.Contains($_.FullName.Substring($Destination.Length + 1)) } | ForEach-Object { $_.FullName })
$movedAside = 0
if ($extra.Count -gt 0) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $MoveAsideTool -Label "files the export lacks, ship-export" @extra | Out-Null
    $left = @($extra | Where-Object { Test-Path -LiteralPath $_ })
    $movedAside = $extra.Count - $left.Count
    if ($left.Count -gt 0) { Write-Output "ship-export: FAILED, $($left.Count) files could not be moved aside, first: $($left[0])"; exit 1 }
}

$copied = 0
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring($Source.Length + 1)
    $to = Join-Path $Destination $rel
    $same = (Test-Path -LiteralPath $to) -and ((Get-Item -LiteralPath $to).Length -eq $f.Length) -and ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash)
    if ($same) { continue }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $to -Force
    $copied++
}
$bad = 0
foreach ($f in $srcFiles) {
    $rel = $f.FullName.Substring($Source.Length + 1)
    $to = Join-Path $Destination $rel
    if (-not (Test-Path -LiteralPath $to) -or (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash) { $bad++; Write-Output "ship-export: DIFFERS $rel" }
}
Write-Output "ship-export: export files $($srcFiles.Count), copied $copied, moved aside $movedAside, differing after the copy $bad"
if ($bad -gt 0) { exit 1 }
exit 0
