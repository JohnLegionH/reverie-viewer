# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# live-takeback.ps1 - the take-back step every live check that rezzes something shares. Dot-source it after the script's own Build
# function (the automation's build command) is defined:
#
#   . (Join-Path $PSScriptRoot "live-takeback.ps1")
#   Add-Rezzed $box.local                       # every object the run makes or rezzes, by local id, as soon as it appears
#   $tb = Invoke-TakeBack -Seconds 30           # takes each back by id (no pick, no camera), then re-reads the region
#
# Invoke-TakeBack sends a take for every recorded object (its root, once) through the build command's take_back action, then re-reads
# the region's own objects until none of them is left, sending the take once more for any still there halfway through. It answers
# rezzed (the ids recorded), sent (the roots whose take was sent), remaining (the ids still in the region at the end) and ok (nothing
# remains). A take by id never depends on what the pointer would hit, so a box in front of another is still the one taken.
# =====================================================================================

$script:rezzed = New-Object System.Collections.Generic.List[uint32]

function Add-Rezzed([uint32]$local) { if ($local -ne 0 -and -not $script:rezzed.Contains($local)) { $script:rezzed.Add($local) } }

function Get-OwnLocals() { @(@((Build @{ action = "state" }).own_prims) | ForEach-Object { [uint32]$_.local }) }

function Invoke-TakeBack([int]$Seconds = 30) {
    $ids = @($script:rezzed)
    $sent = New-Object System.Collections.Generic.List[uint32]
    if ($ids.Count -eq 0) { return [ordered]@{ rezzed = @(); sent = @(); remaining = @(); ok = $true } }
    $ask = { param($which) @((Build @{ action = "take_back"; text = (($which | ForEach-Object { "$_" }) -join ",") }).taken) | ForEach-Object { if (-not $sent.Contains([uint32]$_)) { $sent.Add([uint32]$_) } } }
    & $ask $ids
    $end = (Get-Date).AddSeconds($Seconds); $retried = $false; $left = $ids
    while ((Get-Date) -lt $end) {
        Start-Sleep -Milliseconds 500
        $own = Get-OwnLocals
        $left = @($ids | Where-Object { $own -contains $_ })
        if ($left.Count -eq 0) { break }
        if (-not $retried -and (Get-Date) -gt $end.AddSeconds(-$Seconds / 2)) { & $ask $left; $retried = $true }
    }
    return [ordered]@{ rezzed = $ids; sent = @($sent); remaining = @($left); ok = ($left.Count -eq 0) }
}
