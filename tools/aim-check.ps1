# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# aim-check.ps1 - the picture check for live pictures taken with the aim command (aim-lib.ps1 saves <name>.aim.json beside each picture).
# It fails a picture whose target does not fill enough of the frame or is not in the middle. The verdict is worked out again from the
# target's box on the screen, so a report that claims a pass over a tiny or off-centre box still fails.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\aim-check.ps1 -Report <name>.aim.json [-Picture <name>.png]
#
# Checks, with the reason each failure prints:
#   not_aimed         the report is of a camera that was not aimed
#   pose_not_applied  the engine's camera was not yet at the aimed pose when the report was taken
#   behind_camera     part of the target is behind the camera
#   too_small         the box's longer side fills less than check.min_fill of the frame side it runs along
#   cut_off           less than 90 % of the box is inside the part of the frame the interface bars leave (the report's main_area, in picture
#                     pixels; a report without one is judged against the whole frame)
#   engine_disagrees  the engine puts the target behind the camera, or more than 5 % of the frame from where the box's middle is
#   off_centre        the box's middle is further than check.max_offset of the frame size from the frame's middle
#   picture_missing   -Picture names a file that is not there (or is not a PNG)
#   picture_size      the picture is not the size of the frame the report was taken in
# Exit: 0 pass, 1 fail, 2 the report is missing or unreadable.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Report,
    [string]$Picture = ""
)
$ErrorActionPreference = "Stop"
$inv = [System.Globalization.CultureInfo]::InvariantCulture
function Fmt([double]$x) { return $x.ToString("0.###", $inv) }

if (-not (Test-Path -LiteralPath $Report -PathType Leaf)) { Write-Output "AIM CHECK ERROR: report not found: $Report"; exit 2 }
try { $r = Get-Content -Raw -LiteralPath $Report | ConvertFrom-Json } catch { Write-Output "AIM CHECK ERROR: report is not JSON: $Report"; exit 2 }
if ($null -eq $r) { Write-Output "AIM CHECK ERROR: report is empty: $Report"; exit 2 }

$reasons = New-Object System.Collections.Generic.List[string]
$figures = ""
if (-not $r.aimed) {
    $reasons.Add("not_aimed")
} else {
    if (-not $r.engine -or -not $r.engine.applied) { $reasons.Add("pose_not_applied") }
    $fw = [double]$r.frame[0]; $fh = [double]$r.frame[1]
    $b = $r.box
    if (-not $b.in_front) {
        $reasons.Add("behind_camera")
    } else {
        $w = [double]$b.right - [double]$b.left; $h = [double]$b.bottom - [double]$b.top
        $fill = [math]::Max($w / $fw, $h / $fh)
        # the top and bottom interface bars cover the frame: what lies under them is not on the screen
        $areaL = 0.0; $areaT = 0.0; $areaR = $fw; $areaB = $fh
        if ($r.main_area) { $areaL = [double]$r.main_area.left; $areaT = [double]$r.main_area.top; $areaR = [double]$r.main_area.right; $areaB = [double]$r.main_area.bottom }
        $insideW = [math]::Max(0.0, [math]::Min([double]$b.right, $areaR) - [math]::Max([double]$b.left, $areaL))
        $insideH = [math]::Max(0.0, [math]::Min([double]$b.bottom, $areaB) - [math]::Max([double]$b.top, $areaT))
        $area = $w * $h
        $visible = if ($area -le 1e-6) { 1.0 } else { $insideW * $insideH / $area }
        $offset = [math]::Max([math]::Abs(([double]$b.left + [double]$b.right) / 2 - $fw / 2) / $fw, [math]::Abs(([double]$b.top + [double]$b.bottom) / 2 - $fh / 2) / $fh)
        $minFill = if ($null -ne $r.check.min_fill) { [double]$r.check.min_fill } else { 0.5 }
        $maxOffset = if ($null -ne $r.check.max_offset) { [double]$r.check.max_offset } else { 0.1 }
        if ($fill -lt $minFill) { $reasons.Add("too_small") }
        if ($visible -lt 0.9) { $reasons.Add("cut_off") }
        if ($offset -gt $maxOffset) { $reasons.Add("off_centre") }
        # the engine's own projection of the target must land where the arithmetic puts the box's middle (within 5 % of the frame): when the
        # engine says the target is behind the camera or elsewhere, the picture shows something else than the framing says
        $ec = $r.engine.centre
        if ($null -eq $ec -or $ec.Count -lt 2) { $reasons.Add("engine_disagrees") }
        else {
            $mx = ([double]$b.left + [double]$b.right) / 2; $my = ([double]$b.top + [double]$b.bottom) / 2
            if ([math]::Abs([double]$ec[0] - $mx) / $fw -gt 0.05 -or [math]::Abs([double]$ec[1] - $my) / $fh -gt 0.05) { $reasons.Add("engine_disagrees") }
        }
        $figures = " fill=$(Fmt $fill) (least $(Fmt $minFill)) visible=$(Fmt $visible) offset=$(Fmt $offset) (most $(Fmt $maxOffset))"
    }
    if ($Picture) {
        $ok = $false
        if (Test-Path -LiteralPath $Picture -PathType Leaf) {
            $bytes = New-Object byte[] 24
            $fs = [System.IO.File]::OpenRead($Picture)
            try { $n = $fs.Read($bytes, 0, 24) } finally { $fs.Dispose() }
            if ($n -eq 24 -and $bytes[1] -eq 0x50 -and $bytes[2] -eq 0x4E -and $bytes[3] -eq 0x47) {
                $ok = $true
                $pw = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
                $ph = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
                if ($pw -ne [int]$fw -or $ph -ne [int]$fh) { $reasons.Add("picture_size"); $figures += " picture=${pw}x${ph} frame=$([int]$fw)x$([int]$fh)" }
            }
        }
        if (-not $ok) { $reasons.Add("picture_missing") }
    }
}
if ($reasons.Count -eq 0) { Write-Output ("AIM CHECK PASS" + $figures); exit 0 }
Write-Output ("AIM CHECK FAIL " + ($reasons -join ",") + $figures)
exit 1
