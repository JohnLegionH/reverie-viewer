# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# crash-trim.ps1 - at the end of a render run, drops the crash dumps of a run in which every case passed.
#   -CrashDir       the folder tools\crash-watch.ps1 writes to (<DiagDir>\crash): crash-<pid>.txt / .dmp and dotnet-<pid>.dmp
#   -Since          only files written at or after this time are looked at (the start of the run); a dump from an earlier run stays
#   -AllPassed      every case of the run passed. Each crash's note (.txt) is kept and its dump (.dmp, the whole process, many MiB) is put
#                   aside. Without this switch (a run with a failing case) both are kept.
#   -MoveAsideTool  a script that takes -Label "<why>" and absolute paths and puts each path aside, keeping its folders (default: the
#                   environment variable NV_MOVE_ASIDE_TOOL). Nothing is ever deleted: with no tool the dumps stay and the output says so.
# Exit: 0 always for a missing folder, nothing to do or no tool; 1 when a dump could not be moved aside.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$CrashDir,
    [string]$Since = "",
    [switch]$AllPassed,
    [string]$MoveAsideTool = $env:NV_MOVE_ASIDE_TOOL
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $CrashDir -PathType Container)) { Write-Output "crash-trim: no crash folder, nothing to do"; exit 0 }
$from = if ($Since) { [datetime]::Parse($Since, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) } else { [datetime]::MinValue }
$files = @(Get-ChildItem -LiteralPath $CrashDir -File -Force | Where-Object { $_.Name -match '^(crash|dotnet)-.*' -and $_.LastWriteTime -ge $from })
$dumps = @($files | Where-Object { $_.Extension -ieq ".dmp" })
$notes = @($files | Where-Object { $_.Extension -ieq ".txt" })
if (-not $AllPassed) {
    Write-Output "crash-trim: a case failed, kept $($notes.Count) crash note(s) and $($dumps.Count) dump(s)"
    exit 0
}
if ($dumps.Count -eq 0) { Write-Output "crash-trim: every case passed, no crash dump to put aside (kept $($notes.Count) note(s))"; exit 0 }
if (-not $MoveAsideTool -or -not (Test-Path -LiteralPath $MoveAsideTool)) {
    Write-Output "crash-trim: every case passed, but there is no move-aside tool (-MoveAsideTool or NV_MOVE_ASIDE_TOOL): $($dumps.Count) dump(s) kept in $CrashDir, nothing deleted"
    exit 0
}
$paths = @($dumps | ForEach-Object { $_.FullName })
& powershell -NoProfile -ExecutionPolicy Bypass -File $MoveAsideTool -Label "crash dumps of a run where every case passed, crash-trim" @paths | Out-Null
$left = @($paths | Where-Object { Test-Path -LiteralPath $_ })
if ($left.Count -gt 0) { Write-Output "crash-trim: FAILED, $($left.Count) dump(s) could not be moved aside, first: $($left[0])"; exit 1 }
Write-Output "crash-trim: every case passed, kept $($notes.Count) crash note(s), put $($paths.Count) dump(s) aside"
exit 0
