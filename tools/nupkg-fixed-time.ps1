# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# Sets the modification time of every entry of a .nupkg (a zip file) to midnight on the first of January 2026, in place.
#     powershell -NoProfile -File <repo>\tools\nupkg-fixed-time.ps1 -Path <file.nupkg>
# NuGet stamps each entry with the time of the pack, so two packs of the same source differ in those bytes only.
# Only the two time fields of each local file header and of each central directory header are overwritten; no entry
# is read, decompressed or recompressed, so the result does not depend on the compression library of the machine.
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = "Stop"
$dosDate = [uint16]((2026 - 1980) -shl 9 -bor (1 -shl 5) -bor 1)   # the first of January 2026
$dosTime = [uint16]0                                               # 00:00:00
$bytes = [IO.File]::ReadAllBytes($Path)
function U16([int]$o) { [int]$bytes[$o] -bor ([int]$bytes[$o + 1] -shl 8) }
function U32([int]$o) { [int]((U16 $o) -bor ((U16 ($o + 2)) -shl 16)) }
function Put16([int]$o, [uint16]$v) { $bytes[$o] = $v -band 0xFF; $bytes[$o + 1] = ($v -shr 8) -band 0xFF }

# end of central directory record: signature 0x06054b50, searched backwards (it may be followed by a comment)
$eocd = -1
for ($i = $bytes.Length - 22; $i -ge 0; $i--) {
    if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4b -and $bytes[$i + 2] -eq 5 -and $bytes[$i + 3] -eq 6) { $eocd = $i; break }
}
if ($eocd -lt 0) { throw "$Path is not a zip file (no end of central directory)" }
$count = U16 ($eocd + 10)
$cd = U32 ($eocd + 16)
$p = $cd
for ($n = 0; $n -lt $count; $n++) {
    if ((U32 $p) -ne 0x02014b50) { throw "bad central directory header at $p" }
    $local = U32 ($p + 42)
    if ((U32 $local) -ne 0x04034b50) { throw "bad local header at $local" }
    Put16 ($local + 10) $dosTime; Put16 ($local + 12) $dosDate
    Put16 ($p + 12) $dosTime;     Put16 ($p + 14) $dosDate
    $p += 46 + (U16 ($p + 28)) + (U16 ($p + 30)) + (U16 ($p + 32))
}
[IO.File]::WriteAllBytes($Path, $bytes)
Write-Output "$count entries set to 2026-01-01 00:00:00 in $Path"
