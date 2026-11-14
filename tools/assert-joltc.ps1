# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# assert-joltc.ps1 - asserts that EVERY joltc*.dll under a directory is the PATCHED joltc.
#
# The viewer uses the grid's patched joltc (per-system TempAllocator); see
# native/joltc/PROVENANCE.md and native/joltc/README.md. The stock JoltPhysics.Native 1.0.4
# joltc must never be accepted. Modelled on the grid's assert-patched-joltc.ps1 (in its Jolt physics module,
# branch feature/ais-v3),
# including its PHYS-4b lesson: check EVERY copy, because which one the loader picks depends on
# paths this script does not control. Stricter than the grid's: joltc_double.dll is not exempt.
#
# Usage:
#     powershell -NoProfile -File <repo>\tools\assert-joltc.ps1 -Path <directory>
# Exit 0 = at least one joltc*.dll found and every one is the patched build.
# Exit 1 = any copy is stock or unknown, or none was found.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path
)

$ErrorActionPreference = "Stop"

$patchedHash = "16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5"
$stockHash   = "67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910"

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Output "JOLTC GUARD FAIL: directory not found: $Path"
    exit 1
}

$files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Filter "joltc*.dll")
if ($files.Count -eq 0) {
    Write-Output "JOLTC GUARD FAIL: no joltc*.dll under $Path (a guard that checks nothing proves nothing)"
    exit 1
}

$bad = 0
foreach ($f in $files) {
    $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    if ($h -eq $patchedHash) {
        Write-Output ("  OK    {0}  {1}" -f $h, $f.FullName)
    } else {
        $bad++
        $kind = if ($h -eq $stockHash) { "STOCK JoltPhysics.Native 1.0.4 joltc ($stockHash) - must never be deployed" } else { "UNKNOWN build" }
        Write-Output ("  FAIL  {0}  {1}   <== {2}" -f $h, $f.FullName, $kind)
    }
}

if ($bad -gt 0) {
    Write-Output "JOLTC GUARD FAIL: $bad of $($files.Count) joltc*.dll under $Path are not the patched build ($patchedHash)"
    exit 1
}
Write-Output "JOLTC GUARD OK: $($files.Count) joltc*.dll under $Path, all patched ($patchedHash)"
exit 0
