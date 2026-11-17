# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# live-driver.ps1 - the shared driver for live checks: one way to start the development export with the automation on, log in, find a
# control by its name, bring it into view and press it as a hand does. Dot-source it from a live check; new live checks use it instead of
# their own click helpers.
#
#   . (Join-Path $PSScriptRoot "live-driver.ps1")
#   Start-LiveViewer -Sandbox <folder> -Exe <Reverie.console.exe>        the viewer through tools\godot.ps1 (APPDATA and LOCALAPPDATA in the sandbox)
#   Enter-LiveWorld -Credentials <file> -Account <alias> -LoginUri <uri> -Start <uri:...>   logs in, waits for the world and complete terrain
#   Invoke-LiveApi <command> @{...}                                       one automation call, its answer parsed
#   Find-LiveControl <name>                                               where the control stands: reach visible | clipped | outside, point, rects
#   Show-LiveControl <name>                                               scrolled into view when it is not wholly shown (ui reveal), read again
#                                                                         until the scroll is drawn; the last answer either way
#   Invoke-LiveClick <name>                                               Show-LiveControl, then (only when wholly shown) a press and release of
#                                                                         the left button at its point; @{ clicked; reach; detail }
#   Invoke-LivePointer <x> <y> [-Button left|right] / Invoke-LiveKey <key> [-Ctrl] [-Shift]   the pointer and keys at a point or on the focus
#   Wait-LiveUntil { ... } <seconds>                                      polls a condition every 250 ms; its first true answer, or $null
#   Stop-LiveViewer                                                       logs out, quits and waits for the viewer to end
#
# Controls are found by their node names through the automation's ui command (development builds only), searched over the whole shell, so
# a check names what a person sees ("colour_ok") instead of screen positions. A control that a scroll area or the window edge cuts is
# scrolled into view first; one that stays cut is reported (reach clipped or outside) and never pressed. Every press and key goes through
# the automation's input command, the engine's input path as the operating system's events arrive (never by calling the action).
# The caller holds the claims (an avatar claim on the account and a build claim; tools\claim-guard.ps1 checks them).
# =====================================================================================

$script:LdPort = $null; $script:LdToken = $null; $script:LdProc = $null
$script:LdClock = [System.Diagnostics.Stopwatch]::StartNew()

function Write-LiveNote([string]$what) { [Console]::Out.WriteLine(("{0,7:N1} {1}" -f $script:LdClock.Elapsed.TotalSeconds, $what)) }

function Invoke-LiveRaw([string]$command, [string]$body = "{}") {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $script:LdPort)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($body)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: 127.0.0.1:$($script:LdPort)`r`nAuthorization: Bearer $($script:LdToken)`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $all = (New-Object System.IO.StreamReader($s)).ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}

function Invoke-LiveApi([string]$command, [hashtable]$body = @{}) {
    $r = Invoke-LiveRaw $command ($body | ConvertTo-Json -Compress -Depth 6)
    if (-not $r.Body) { return $null }
    try { return $r.Body | ConvertFrom-Json } catch { return $null }
}

function Wait-LiveUntil([scriptblock]$cond, [double]$seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 250 }
    return $null
}

# Why a program is not a complete export, or $null when it is: the program exists and a data_ folder holding the .NET assemblies stands
# beside it. Started without that folder, an exported .NET build stops on an error dialog about the .NET assemblies directory, on the
# screen of whoever is at the machine, so the driver checks this itself (godot.ps1 checks only an export whose .pck lies beside it).
function Test-LiveExport([string]$Exe) {
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe -PathType Leaf)) { return "the program is not found: $Exe" }
    $dir = Split-Path -Parent ([IO.Path]::GetFullPath($Exe))
    $data = @(Get-ChildItem -LiteralPath $dir -Directory -Filter "data_*" -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "GodotSharp.dll") -PathType Leaf })
    if ($data.Count -eq 0) { return "no data_ folder with the .NET assemblies (GodotSharp.dll) beside $Exe (an incomplete export)" }
    return $null
}

function Start-LiveViewer([string]$Sandbox, [string]$Exe, [int]$StartSeconds = 60, [int]$TimeoutSeconds = 600) {
    $why = Test-LiveExport $Exe
    if ($why) {
        # refused before anything starts, and said where the run's evidence goes
        New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
        $line = "not started: $why"
        Add-Content -LiteralPath (Join-Path $Sandbox "live-driver.log") -Value $line -Encoding utf8
        Write-LiveNote $line
        return $false
    }
    . (Join-Path $PSScriptRoot "claim-guard.ps1")
    Assert-ClaimGranted   # no viewer without a granted lane claim (claim-guard.ps1), whoever dot-sources the driver
    $godot = Join-Path $PSScriptRoot "godot.ps1"
    $appdata =Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
    $script:LdProc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "$TimeoutSeconds", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
    Write-LiveNote "viewer started through godot.ps1, pid $($script:LdProc.Id)"
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = Wait-LiveUntil { @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 } $StartSeconds
    if (-not $tf) { return $false }
    $j = Wait-LiveUntil { try { Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json } catch { $null } } 5
    if (-not $j) { return $false }
    $script:LdPort = [int]$j.port; $script:LdToken = [string]$j.token
    return $true
}

function Enter-LiveWorld([string]$Credentials, [string]$Account, [string]$LoginUri, [string]$Start, [int]$Seconds = 150) {
    $login = Invoke-LiveRaw "session" (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $LoginUri; start = $Start } | ConvertTo-Json -Compress)
    Write-LiveNote "login requested: $($login.Status)"
    return Wait-LiveUntil { $o = Invoke-LiveApi "observe"; if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) { $o } } $Seconds
}

function Find-LiveControl([string]$name) { Invoke-LiveApi "ui" @{ action = "find"; name = $name } }

function Show-LiveControl([string]$name, [double]$seconds = 3) {
    $f = Find-LiveControl $name
    if (-not $f -or -not $f.found -or $f.reach -eq "visible") { return $f }
    [void](Invoke-LiveApi "ui" @{ action = "reveal"; name = $name })
    $shown = Wait-LiveUntil { $g = Find-LiveControl $name; if ($g -and $g.reach -eq "visible") { $g } } $seconds
    if ($shown) { return $shown }
    return Find-LiveControl $name
}

function Invoke-LivePointer([double]$x, [double]$y, [string]$Button = "left") {
    [void](Invoke-LiveApi "input" @{ kind = "mouse"; button = "none"; down = $false; shift = $false; ctrl = $false; alt = $false; x = $x; y = $y }); Start-Sleep -Milliseconds 120
    [void](Invoke-LiveApi "input" @{ kind = "mouse"; button = $Button; down = $true; shift = $false; ctrl = $false; alt = $false; x = $x; y = $y }); Start-Sleep -Milliseconds 120
    [void](Invoke-LiveApi "input" @{ kind = "mouse"; button = $Button; down = $false; shift = $false; ctrl = $false; alt = $false; x = $x; y = $y }); Start-Sleep -Milliseconds 200
}

function Invoke-LiveKey([string]$key, [switch]$Ctrl, [switch]$Shift) {
    [void](Invoke-LiveApi "input" @{ kind = "key"; key = $key; down = $true; shift = [bool]$Shift; alt = $false; ctrl = [bool]$Ctrl })
    [void](Invoke-LiveApi "input" @{ kind = "key"; key = $key; down = $false; shift = [bool]$Shift; alt = $false; ctrl = [bool]$Ctrl })
}

function Invoke-LiveClick([string]$name) {
    $c = Show-LiveControl $name
    if (-not $c -or -not $c.found) { return @{ clicked = $false; reach = "missing"; detail = "no control named $name" } }
    $where = "rect [{0}] shown [{1}]" -f (@($c.rect) -join ","), (@($c.shown_rect) -join ",")
    if ($c.reach -ne "visible" -or -not $c.point) { return @{ clicked = $false; reach = [string]$c.reach; detail = "$name is $($c.reach) after scrolling: $where" } }
    Invoke-LivePointer ([double]$c.point[0]) ([double]$c.point[1])
    return @{ clicked = $true; reach = "visible"; detail = "$name pressed at [$(@($c.point) -join ',')]: $where" }
}

function Stop-LiveViewer([int]$Seconds = 60) {
    $asked = $false
    if ($script:LdPort -and $script:LdToken) {
        try { [void](Invoke-LiveRaw "session" '{"action":"logout"}'); Start-Sleep -Seconds 3; [void](Invoke-LiveRaw "quit"); $asked = $true } catch { }
    }
    if (-not $script:LdProc) { return }
    if ($asked -and $script:LdProc.WaitForExit($Seconds * 1000)) { return }
    # it did not quit when asked (or could not be asked): the driver stops what it started, its whole process tree, so nothing is left running
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($script:LdProc.Id)
    for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $all) { if ([int]$c.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$c.ProcessId)) { $ids.Add([int]$c.ProcessId) } } }
    Write-LiveNote ("the viewer did not quit when asked: stopping the {0} processes it started" -f $ids.Count)
    for ($i = $ids.Count - 1; $i -ge 0; $i--) { Stop-Process -Id $ids[$i] -Force -ErrorAction SilentlyContinue }
}
