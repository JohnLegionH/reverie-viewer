# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# login-proof.ps1 - the SHIPPING export reaches its login screen with the grid address filled in.
#
# Starts the exported viewer the way a person does (a plain start, no S-1 or test-render mode) with
# --grid=<address>, through tools\godot.ps1 (-PlaceWindow: a quarter-size window in the bottom-right corner of the
# primary monitor, without taking focus). The harness finds the viewer's window, captures it from outside
# (PrintWindow, the window's own picture), asks it to close, and checks:
#   - the viewer logged "login.screen" with grid-filled=true, grid-source=option and the address's length (never the address);
#   - the capture is a real picture of the window (many colours, not one flat colour) and is saved as login-screen.png;
#   - the viewer closed on the window's close request and exited 0.
# Nothing is typed or sent: no login is attempted. The address is a reserved example name.
#     powershell -NoProfile -File <repo>\tools\login-proof.ps1 -Exe <build>\Reverie.console.exe -AppDataDir <dir> -OutDir <dir> [-DiagDir <dir>] [-Driver vulkan]
# Exit 0: proved. Exit 1: not proved (reason printed).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$AppDataDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$DiagDir = "",
    [string]$Driver = "vulkan",
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "job-guard.ps1")
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
. (Join-Path $PSScriptRoot "boot-logo-check.ps1")   # Test-BootLogo: the engine's start-up splash
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$tag = [guid]::NewGuid().ToString("N").Substring(0, 12)
$grid = "http://login-proof-$tag.example.org:8002/"
$log = Join-Path $OutDir "login-proof-$Driver.log"
$png = Join-Path $OutDir "login-screen-$Driver.png"

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NvWin {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();   // so the window's size and picture are its real pixels, not a scaled-down part of them
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

[void][NvWin]::SetProcessDPIAware()
$diagArgs = if ($DiagDir) { @("-DiagDir", $DiagDir) } else { @() }
$argLine = "--rendering-driver $Driver -- --grid=$grid"
$launched = Get-Date
$runner = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList (@("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"") + $diagArgs +
    @("-Exe", "`"$Exe`"", "-AppDataDir", "`"$AppDataDir`"", "-PlaceWindow", "-TimeoutSeconds", "$TimeoutSeconds", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`""))

$problems = @()
$hwnd = [IntPtr]::Zero
$deadline = (Get-Date).AddSeconds($TimeoutSeconds - 20)
$bmp = $null
$steady = 0
while ((Get-Date) -lt $deadline -and -not $runner.HasExited) {
    # our viewer: the process whose command line carries this run's unique address
    $mine = Get-CimInstance Win32_Process -Filter "Name = 'Reverie.exe'" | Where-Object { $_.CommandLine -like "*$tag*" } | Select-Object -First 1
    if ($mine) {
        $p = Get-Process -Id $mine.ProcessId -ErrorAction SilentlyContinue
        if ($p -and $p.MainWindowHandle -ne [IntPtr]::Zero) {
            $hwnd = $p.MainWindowHandle
            $r = New-Object NvWin+RECT
            [void][NvWin]::GetClientRect($hwnd, [ref]$r)
            $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
            if ($w -gt 100 -and $h -gt 100) {
                $b = New-Object System.Drawing.Bitmap $w, $h
                $g = [System.Drawing.Graphics]::FromImage($b)
                $hdc = $g.GetHdc()
                [void][NvWin]::PrintWindow($hwnd, $hdc, 3)   # PW_CLIENTONLY | PW_RENDERFULLCONTENT
                $g.ReleaseHdc($hdc); $g.Dispose()
                $colours = New-Object 'System.Collections.Generic.HashSet[int]'
                for ($y = 0; $y -lt $h; $y += 3) {
                    for ($x = 0; $x -lt $w; $x += 3) {
                        [void]$colours.Add($b.GetPixel($x, $y).ToArgb())
                    }
                }
                # the engine's start-up splash: its flat background with its logo in the middle (boot-logo-check.ps1); the viewer's accent colour never decides it
                $splash = (Test-BootLogo -Bitmap $b).Showing
                # the login screen: many colours (text and controls) and not the engine's start-up splash; seen twice running so a half-drawn frame is not taken
                if ($colours.Count -gt 20 -and -not $splash) { $steady++ } else { $steady = 0 }
                if ($steady -ge 2) { $bmp = $b; $script:colourCount = $colours.Count; break }
                $b.Dispose()
            }
        }
    }
    Start-Sleep -Milliseconds 700
}
if ($bmp) {
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    [void][NvWin]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE: the viewer's own close request
} else {
    $problems += "no picture of the viewer's window within the time (window handle $hwnd)"
}
if (-not $runner.WaitForExit(40000)) { $problems += "the viewer did not close on the window's close request within 40 s" }
$exit = if ($runner.HasExited) { $runner.ExitCode } else { -1 }
if ($runner.HasExited -eq $false) { Stop-Process -Id $runner.Id -Force -ErrorAction SilentlyContinue }

$text = if (Test-Path -LiteralPath $log) { Get-Content -Raw -LiteralPath $log } else { "" }
$m = [regex]::Match($text, 'login\.screen[^\r\n]*')
if (-not $m.Success) { $problems += "no login.screen line in the viewer's log" }
else {
    if ($m.Value -notmatch 'grid-filled=true') { $problems += "grid field not filled: $($m.Value)" }
    if ($m.Value -notmatch 'grid-source=option') { $problems += "grid field not from the option: $($m.Value)" }
    if ($m.Value -notmatch "grid-chars=$($grid.Length)\b") { $problems += "grid field length is not $($grid.Length): $($m.Value)" }
}
if ($text -like "*$tag*") { $problems += "the viewer's log names the address" }
if ($exit -ne 0) { $problems += "exit $exit (expected 0)" }

if ($problems.Count -eq 0) {
    Write-Output "login-proof PASS ($Driver): login screen up with the address filled from --grid (source option, $($grid.Length) characters), window captured ($($script:colourCount) sampled colours) to $png, closed on request, exit 0"
    exit 0
}
Write-Output ("login-proof FAIL ($Driver): " + ($problems -join "; "))
exit 1
