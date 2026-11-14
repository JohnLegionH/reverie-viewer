# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# prefs-auto-check.ps1 - automated checks of the Preferences settings that only had unit tests, on the development export, offline
# (the login screen is enough). Each check drives the viewer through its settings file and the automation interface and measures the
# result from outside the viewer (the operating system's window data, this script's own clock), with a PASS/FAIL line and its figures:
#   backend   the saved backend choice starts the viewer again once with that driver and the engine reports it; a made-up device
#             report (development builds read NV_TEST_DEVICE_REPORT) with Vulkan 1.2 makes the choice fall back to Direct3D 12 and the
#             reverse to Vulkan; neither passing shows the plain message and exits 4; --backend= overrides the saved choice
#   window    windowed, borderless and fullscreen give the window the expected size and style, read from the operating system
#   fps       a limit of 30 gives a frame interval near 33 ms (frames counted by the engine, time by this script's clock)
#   vsync     on: frames paced to the display's refresh rate; off: faster than that
# Output: the sandbox folder (prefs-auto.txt, prefs-auto.json, each viewer's log). Exit 0 all passed, 1 a check failed.
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\prefs-auto-check.ps1 -Sandbox <new folder> [-Only backend,window,fps,vsync]
# Needs the development export (tools\s1.ps1 makes it) and a granted lane claim.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Exe = "",
    [string[]]$Only = @("backend", "window", "fps", "vsync"),
    [int]$StartSeconds = 90
)
$ErrorActionPreference = "Stop"
$Only = @($Only | ForEach-Object { $_ -split "," })   # a list given to powershell -File arrives as one comma-separated text
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "BLOCKED: the development export is missing: $Exe"; exit 3 }
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$textLog = Join-Path $Sandbox "prefs-auto.txt"
$results = [System.Collections.Generic.List[object]]::new()
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
function Record([string]$name, [bool]$pass, [string]$detail) {
    $script:results.Add([ordered]@{ check = $name; pass = $pass; detail = $detail })
    Say ("{0}: {1}  {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail)
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NvPa
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] public static extern bool EnumDisplaySettings(string device, int mode, byte[] devMode);
    public static int Refresh(string device)
    {
        var b = new byte[220];
        BitConverter.GetBytes((ushort)220).CopyTo(b, 68);
        return EnumDisplaySettings(device, -1, b) ? BitConverter.ToInt32(b, 120) : 0;
    }
}
"@
[void][NvPa]::SetProcessDPIAware()

function Invoke-Api($v, [string]$command, $body) {
    $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Compress -Depth 5 }
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $v.Port)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($json)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: 127.0.0.1:$($v.Port)`r`nAuthorization: Bearer $($v.Token)`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $all = (New-Object System.IO.StreamReader($s)).ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function Api($v, [string]$command, $body) { $r = Invoke-Api $v $command $body; if ($r.Body) { try { $r.Body | ConvertFrom-Json } catch { $null } } else { $null } }
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }
    return $null
}

# one viewer: its own settings folder (the given settings file text), its own log, started through godot.ps1 as every Godot run is.
# $argLine goes after "--" (the viewer's own arguments); no --rendering-driver is passed, so the engine starts with the project's default.
function Start-Viewer([string]$label, [string]$settingsJson, [string]$userArgs, [hashtable]$env, [bool]$place) {
    $local = Join-Path $Sandbox "localappdata-$label"
    $appdata = Join-Path $Sandbox "appdata-$label"
    New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
    if ($settingsJson) { Set-Content -LiteralPath (Join-Path $appdata "ReverieViewer\settings.json") -Value $settingsJson -Encoding utf8 }
    $log = Join-Path $Sandbox "viewer-$label.log"
    $old = @{}
    foreach ($k in $env.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, [string]$env[$k]) }
    try {
        $args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"", "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"",
            "-TimeoutSeconds", "300", "-HangSnapshotSeconds", "0", "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"-- --automation $userArgs`"")
        if ($place) { $args2 += "-PlaceWindow" }
        $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $args2
    } finally { foreach ($k in $env.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k]) } }
    return @{ Label = $label; Proc = $p; Log = $log; Local = $local; Port = 0; Token = "" }
}
function Connect-Viewer($v, [int]$seconds) {
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $v.Local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $seconds
    if (-not $tf) { return $false }
    Start-Sleep -Milliseconds 400
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $v.Port = [int]$j.port; $v.Token = [string]$j.token
    return $true
}
function Stop-Viewer($v) {
    try { if ($v.Port) { [void](Invoke-Api $v "quit" '{}') } } catch { }
    if (-not $v.Proc.WaitForExit(30000)) { Stop-Process -Id $v.Proc.Id -Force -ErrorAction SilentlyContinue }   # only the launcher this script started
    Start-Sleep -Milliseconds 800
}
function Engine($v) { Api $v "engine" '{}' }
function LogText($v) { if (Test-Path -LiteralPath $v.Log) { Get-Content -Raw -LiteralPath $v.Log } else { "" } }

# ---------------- backend ----------------
if ($Only -contains "backend") {
    # label, saved choice, extra arguments, device report (or ""), expected driver ("" = the plain message and exit 4), expected number of second starts
    $cases = @(
        @("real-vulkan",      "vulkan", "",                  "",                                  "vulkan", 0),
        @("real-d3d12",       "d3d12",  "",                  "",                                  "d3d12",  1),
        @("real-auto",        "auto",   "",                  "",                                  "vulkan", 0),
        @("fake-auto-old",    "auto",   "",                  "vulkan=1.2.175;d3d12=12_0;adapter=Test Card", "d3d12", 1),
        @("fake-forced-old",  "vulkan", "",                  "vulkan=1.2.175;d3d12=12_0;adapter=Test Card", "d3d12", 1),
        @("fake-reverse",     "d3d12",  "",                  "vulkan=1.4.325;d3d12=11_0;adapter=Test Card", "vulkan", 0),
        @("override-arg",     "d3d12",  "--backend=vulkan",  "",                                  "vulkan", 0),
        @("fake-neither",     "auto",   "",                  "vulkan=1.2.175;d3d12=11_0;adapter=Test Card", "",       0)
    )
    foreach ($c in $cases) {
        $label, $saved, $extra, $fake, $want, $restarts = $c
        $envs = @{}
        if ($fake) { $envs["NV_TEST_DEVICE_REPORT"] = $fake }
        $v = Start-Viewer "backend-$label" "{`"graphics_backend`": `"$saved`"}" $extra $envs $true
        if ($want -eq "") {
            $exited = $v.Proc.WaitForExit(120000)
            $text = LogText $v
            $ok = $exited -and $text -match "neither is enough" -and $text -match "startup.refused"
            Record "backend $label" $ok "saved=$saved report=[$fake]: the plain message $(if ($text -match 'neither is enough') { 'shown' } else { 'NOT shown' }), launcher exited=$exited"
            if (-not $exited) { Stop-Process -Id $v.Proc.Id -Force -ErrorAction SilentlyContinue }
            continue
        }
        if (-not (Connect-Viewer $v $StartSeconds)) { Record "backend $label" $false "no automation token file within $StartSeconds s"; Stop-Viewer $v; continue }
        $e = Engine $v
        $tried = @($e.backend_tried)
        Stop-Viewer $v
        $text = LogText $v   # read when the launcher has ended, so the first start's lines are all there
        $seenRestarts = ([regex]::Matches($text, "backend\.restart ")).Count
        $ok = $e.rendering_driver -eq $want -and $seenRestarts -eq $restarts -and $tried.Count -eq $restarts
        Record "backend $label" $ok "saved=$saved extra=[$extra] report=[$fake]: running $($e.rendering_driver) $($e.api_version) (wanted $want), second starts logged by the first start $seenRestarts (wanted $restarts), drivers tried by this run: [$($tried -join ',')], debug_build=$($e.debug_build)"
    }
}

# ---------------- window modes ----------------
if ($Only -contains "window") {
    foreach ($mode in @("windowed", "borderless", "fullscreen")) {
        $v = Start-Viewer "window-$mode" "{`"window_mode`": `"$mode`"}" "" @{} $false
        if (-not (Connect-Viewer $v $StartSeconds)) { Record "window $mode" $false "no automation token file"; Stop-Viewer $v; continue }
        Start-Sleep -Seconds 3
        $e = Engine $v
        $proc = Get-Process -Id $e.process -ErrorAction SilentlyContinue
        $h = if ($proc) { $proc.MainWindowHandle } else { [IntPtr]::Zero }
        if ($h -eq [IntPtr]::Zero) { Record "window $mode" $false "no window handle for process $($e.process)"; Stop-Viewer $v; continue }
        $r = New-Object NvPa+Rect
        [void][NvPa]::GetWindowRect($h, [ref]$r)
        $style = [NvPa]::GetWindowLong($h, -16)
        $caption = ($style -band 0xC00000) -eq 0xC00000
        $screen = [System.Windows.Forms.Screen]::FromRectangle((New-Object System.Drawing.Rectangle $r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top)))
        $b = $screen.Bounds
        $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
        $covers = $r.Left -le $b.Left -and $r.Top -le $b.Top -and $r.Right -ge $b.Right -and $r.Bottom -ge $b.Bottom
        # windowed: a title bar and not the whole monitor; borderless: no title bar, the same size as the windowed one; fullscreen: no title bar, the whole monitor
        if ($mode -eq "windowed") { $script:windowedSize = "${w}x$hh" }
        $ok = switch ($mode) { "windowed" { $caption -and -not $covers } "borderless" { (-not $caption) -and -not $covers -and "${w}x$hh" -eq $script:windowedSize } default { (-not $caption) -and $covers } }
        Record "window $mode" $ok "window ${w}x$hh at $($r.Left),$($r.Top); title bar $caption; covers its monitor $covers (monitor $($b.Width)x$($b.Height)); style 0x$('{0:x}' -f $style)"
        Stop-Viewer $v
    }
}

# ---------------- frame rate limit and vertical sync ----------------
function Measure-Interval($v, [int]$seconds) {
    # frames the engine drew between two readings, over this script's own clock
    $a = Engine $v; $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $seconds
    $b = Engine $v; $t = $sw.Elapsed.TotalMilliseconds
    return [math]::Round($t / [math]::Max(1, ($b.frames - $a.frames)), 2)
}
function Refresh-Of($v) {
    $e = Engine $v
    $h = (Get-Process -Id $e.process).MainWindowHandle
    $r = New-Object NvPa+Rect; [void][NvPa]::GetWindowRect($h, [ref]$r)
    $screen = [System.Windows.Forms.Screen]::FromRectangle((New-Object System.Drawing.Rectangle $r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top)))
    return [NvPa]::Refresh($screen.DeviceName)
}
if ($Only -contains "fps") {
    $v = Start-Viewer "fps30" "{`"fps_limit`": 30, `"vsync`": false}" "" @{} $false
    if (-not (Connect-Viewer $v $StartSeconds)) { Record "fps 30" $false "no automation token file" } else {
        Start-Sleep -Seconds 3
        $vals = 1..3 | ForEach-Object { Measure-Interval $v 5 }
        $med = ($vals | Sort-Object)[1]
        Record "fps 30" ($med -ge 30 -and $med -le 37) "frame interval over three 5 s windows: $($vals -join ', ') ms (median $med; 33.3 expected for a limit of 30)"
    }
    Stop-Viewer $v
}
if ($Only -contains "vsync") {
    $v = Start-Viewer "vsync-on" "{`"fps_limit`": 0, `"vsync`": true}" "" @{} $false
    $onMs = $null; $hz = 0
    if (Connect-Viewer $v $StartSeconds) {
        Start-Sleep -Seconds 3
        $hz = Refresh-Of $v
        $vals = 1..3 | ForEach-Object { Measure-Interval $v 5 }
        $onMs = ($vals | Sort-Object)[1]
        $want = if ($hz -gt 0) { [math]::Round(1000.0 / $hz, 2) } else { 0 }
        Record "vsync on" ($hz -gt 0 -and [math]::Abs($onMs - $want) -le [math]::Max(1.5, $want * 0.12)) "display refresh $hz Hz (interval $want ms); measured $($vals -join ', ') ms (median $onMs)"
    } else { Record "vsync on" $false "no automation token file" }
    Stop-Viewer $v
    $v = Start-Viewer "vsync-off" "{`"fps_limit`": 0, `"vsync`": false}" "" @{} $false
    if (Connect-Viewer $v $StartSeconds) {
        Start-Sleep -Seconds 3
        $vals = 1..3 | ForEach-Object { Measure-Interval $v 5 }
        $offMs = ($vals | Sort-Object)[1]
        $pass = $onMs -ne $null -and $hz -gt 0 -and $offMs -lt ($onMs * 0.8)
        Record "vsync off" $pass "measured $($vals -join ', ') ms (median $offMs) against $onMs ms with vsync on: not paced to the refresh when it is faster than that"
    } else { Record "vsync off" $false "no automation token file" }
    Stop-Viewer $v
}

$failed = @($results | Where-Object { -not $_.pass }).Count
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Sandbox "prefs-auto.json") -Encoding utf8
Say "SUMMARY: $($results.Count - $failed) of $($results.Count) passed"
if ($failed -gt 0) { exit 1 }
exit 0
