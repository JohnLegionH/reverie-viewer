# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# boot-logo-check.ps1 - tells the engine's start-up splash from the viewer's own first screen in a picture of the window.
#     powershell -NoProfile -File <repo>\tools\boot-logo-check.ps1 -Png <picture>
# Prints one line: "boot-logo: yes|no flat=<share> logo=<share>". Dot-sourced without -Png it only defines Test-BootLogo.
#
# The engine's splash is its flat dark background (36, 36, 36) over nearly the whole window with its logo, in the logo's
# blue (71, 140, 191), drawn in the middle of the window. So the picture is the splash when both hold:
#   flat  - at least 80 % of the sampled pixels are the engine's background colour;
#   logo  - at least 1 % of the sampled pixels in the middle box (a square of side 40 % of the window's height, centred
#           on the window) are the logo's blue.
# The viewer's own accent colour never decides it: a first screen is panel-coloured, so it fails the first test whatever its
# accent is, and an accent of the logo's blue outside the middle box fails the second.
# =====================================================================================
param([string]$Png = "")

function Test-BootLogo {
    param([Parameter(Mandatory = $true)][System.Drawing.Bitmap]$Bitmap)
    $w = $Bitmap.Width; $h = $Bitmap.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = New-Object byte[] ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $stride = $data.Stride
    $Bitmap.UnlockBits($data)
    $half = [int](0.2 * $h)
    $x0 = [int]($w / 2) - $half; $x1 = [int]($w / 2) + $half
    $y0 = [int]($h / 2) - $half; $y1 = [int]($h / 2) + $half
    $total = 0; $flat = 0; $boxTotal = 0; $logo = 0
    for ($y = 0; $y -lt $h; $y += 2) {
        $row = $y * $stride
        for ($x = 0; $x -lt $w; $x += 2) {
            $i = $row + $x * 4
            $b = $bytes[$i]; $g = $bytes[$i + 1]; $r = $bytes[$i + 2]   # BGRA
            $total++
            if ([math]::Abs($r - 36) -le 2 -and [math]::Abs($g - 36) -le 2 -and [math]::Abs($b - 36) -le 2) { $flat++ }
            if ($x -ge $x0 -and $x -lt $x1 -and $y -ge $y0 -and $y -lt $y1) {
                $boxTotal++
                if ([math]::Abs($r - 71) -le 14 -and [math]::Abs($g - 140) -le 14 -and [math]::Abs($b - 191) -le 14) { $logo++ }
            }
        }
    }
    $flatShare = if ($total) { $flat / $total } else { 0.0 }
    $logoShare = if ($boxTotal) { $logo / $boxTotal } else { 0.0 }
    [pscustomobject]@{ Showing = ($flatShare -ge 0.80 -and $logoShare -ge 0.01); Flat = $flatShare; Logo = $logoShare }
}

if ($Png) {
    Add-Type -AssemblyName System.Drawing
    $b = New-Object System.Drawing.Bitmap $Png
    $r = Test-BootLogo -Bitmap $b
    $b.Dispose()
    Write-Output ([string]::Format([cultureinfo]::InvariantCulture, "boot-logo: {0} flat={1:0.000} logo={2:0.000}", $(if ($r.Showing) { "yes" } else { "no" }), $r.Flat, $r.Logo))
}
