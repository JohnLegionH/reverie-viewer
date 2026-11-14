# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# worn-frames-live.ps1 - one logged-in viewer crosses from one region into the next and back, N times each way, with NV_WORN_FRAMES=1
# (tools\crossing-live.ps1 needs a second account; this is the one-account version). The viewer's log is read when it exits: every
# change of the set of drawn worn pieces (worn.frame, with the engine frame number) and every object change that concerns a worn piece
# (worn.event) are matched to the crossings by time, and a piece that was drawn, then not, then drawn again is reported as a gap with the
# number of frames it lasted and the events around it.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\worn-frames-live.ps1 -Out <new folder> [-Exe <development export>]
#     [-Crossings 5] [-Fly] [-FlyMetres 100] [-Start <uri:Region&x&y&z>] [-HeadingDeg 90] [-Wear] [-CrossingFrames] [-FrameBurst] [-HoldAfterSeconds 1]
#     [-ClimbSeconds 2.5]
#
# -ClimbSeconds is how long the up key is held after flight starts (default 2.5 s, as before); a short climb flies the route low, near
# the ground and what stands on it.
#
# -HoldAfterSeconds keeps the forward key down that long after each crossing is seen (default 0: let go at once), as a person flying across
# holds it on; let go at once, the agent slows down right after the border and the crossing is not a held-key crossing.
#
# -CrossingFrames turns on the per-frame crossing trace (NV_CROSSING_FRAMES=1: every frame from a second before each crossing to a second after it,
# under the viewer's log folder in crossing-frames) and the crossing message record (the development automation's neighbour_debug trace_on).
# -FrameBurst turns on the frame burst (NV_FRAME_BURST=1).
#
# -Crossings is the number of out-and-back cycles (2 crossings each). -Fly takes the avatar up and keeps it flying all the way; the route is
# then: fly to the border, FlyMetres further in, turn and fly back across. Settings come from the environment or the untracked live
# settings file, as for crossing-live.ps1 (NV_TEST_ACCOUNTS, NV_GRID_INFO_URL, NV_CROSS_WALKER, NV_CROSS_WALKER_START, NV_CROSS_HEADING_DEG,
# NV_CROSS_WALKER_ID, NV_PRESENCE_COMMAND). Run it under an avatar claim for the account and a live-login claim.
# Exit: 0 every crossing arrived and no worn piece had a gap, 1 a crossing failed or a gap was found, 3 blocked, 5 the viewer did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Exe = "",
    [int]$Crossings = 5,
    [switch]$Fly,
    [int]$FlyMetres = 100,
    [string]$Start = "",
    [double]$HeadingDeg = [double]::NaN,
    [int]$LegSeconds = 90,
    [int]$StallSeconds = 20,
    [string[]]$Wear = @(),   # names (or parts of names) of inventory items to put on first; the account must be a scratch account
    [switch]$CrossingFrames,
    [switch]$FrameBurst,
    [double]$HoldAfterSeconds = 0,   # how long the forward key stays down after the crossing is seen (a person flying across holds it on)
    [double]$ClimbSeconds = 2.5   # how long the up key is held after flight starts
)
$ErrorActionPreference = "Stop"
$env:NV_WORN_TRACE = "1"
$env:NV_WORN_FRAMES = "1"
if ($CrossingFrames) { $env:NV_CROSSING_FRAMES = "1" }
if ($FrameBurst) { $env:NV_FRAME_BURST = "1" }
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$liveEnv = Join-Path $PSScriptRoot "live-env.ps1"
function Setting([string]$name) { & $liveEnv $name }

$accounts = Setting NV_TEST_ACCOUNTS; $gridInfo = Setting NV_GRID_INFO_URL
$alias = Setting NV_CROSS_WALKER; $walkerId = Setting NV_CROSS_WALKER_ID
if (-not $Start) { $Start = Setting NV_CROSS_WALKER_START }
if ([double]::IsNaN($HeadingDeg)) { $HeadingDeg = if (Setting NV_CROSS_HEADING_DEG) { [double](Setting NV_CROSS_HEADING_DEG) } else { 90.0 } }
$presenceCommand = Setting NV_PRESENCE_COMMAND
$missing = @(@{ n = "NV_TEST_ACCOUNTS"; v = $accounts }, @{ n = "NV_GRID_INFO_URL"; v = $gridInfo }, @{ n = "NV_CROSS_WALKER"; v = $alias }, @{ n = "start"; v = $Start }) | Where-Object { -not $_.v } | ForEach-Object { $_.n }
if ($missing.Count -gt 0) { Write-Host ("BLOCKED: settings not set: " + ($missing -join ", ")); exit 3 }
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$result = [ordered]@{}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note($k, $v) { $result[$k] = $v; Write-Host ("[{0,8:N1}s] {1} = {2}" -f $clock.Elapsed.TotalSeconds, $k, ($v | ConvertTo-Json -Compress -Depth 8)) }

try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $gridInfo).Content).gridinfo.login } catch { $loginUri = $null }
if (-not $loginUri) { Write-Host "BLOCKED: grid info did not answer"; exit 3 }

class Viewer { [int]$Port; [string]$Token; $Proc }
function Invoke-Api([Viewer]$v, [string]$command, $body = "{}") {
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
function J([Viewer]$v, [string]$command, $body = "{}") { try { (Invoke-Api $v $command $body).Body | ConvertFrom-Json } catch { $null } }
function Obs([Viewer]$v) { J $v "observe" }
function Nbrs([Viewer]$v) { $r = J $v "neighbours"; if ($r -and -not $r.regions) { $r | Add-Member -NotePropertyName regions -NotePropertyValue @() -Force }; $r }
function Wait-Until([scriptblock]$cond, [int]$seconds, [int]$pollMs = 1000) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds $pollMs }
    return $null
}
function Presence([string]$id) { if (-not $presenceCommand -or -not $id) { return "unknown" }; $o = @(& cmd /c "$presenceCommand $id 2>nul"); return ($o | Select-Object -First 1) }
function Key([Viewer]$v, [string]$key, [bool]$down) { [void](Invoke-Api $v "keys" (@{ key = $key; down = $down } | ConvertTo-Json -Compress)) }
function Face([Viewer]$v, [double]$deg) {
    for ($k = 0; $k -lt 40; $k++) {
        $s = (Obs $v).self
        if (-not $s) { return $false }
        $d = (($deg - [double]$s.region_heading_deg + 540) % 360) - 180
        if ([math]::Abs($d) -lt 12) { return $true }
        $turn = if ($d -gt 0) { "A" } else { "D" }
        Key $v $turn $true; Start-Sleep -Milliseconds 250; Key $v $turn $false; Start-Sleep -Milliseconds 150
    }
    return $false
}
function CurrentOf($n) { @($n.regions | Where-Object { $_.current }) | Select-Object -First 1 }
function SameGrid($a, $b) { $a -and $b -and $a[0] -eq $b[0] -and $a[1] -eq $b[1] }

$sandbox = Join-Path $Out "walker-localappdata"; $appdata = Join-Path $Out "walker-appdata"; $vlog = Join-Path $Out "walker-viewer.log"
if ($presenceCommand -and $walkerId -and (Presence $walkerId) -notin @("offline", "unknown")) { Write-Host "BLOCKED: the test account is not offline"; exit 3 }
$env:NV_OUTFIT_SCRATCH_OK = "1"
$env:NV_LOG_DIR = Join-Path $Out "viewer-logs"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$sandbox`"", "-TimeoutSeconds", "3000", "-LogFile", "`"$vlog`"", "-DiagDir", "`"$(Join-Path $Out 'diag')`"",
    "-ArgLine", "`"--rendering-driver vulkan -- --automation --neighbours=on`"")
Remove-Item Env:NV_OUTFIT_SCRATCH_OK -ErrorAction SilentlyContinue
$tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $sandbox "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } 120
if (-not $tf) { Write-Host "no token file"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
$v = [Viewer]::new(); $v.Port = [int]$j.port; $v.Token = [string]$j.token; $v.Proc = $p

$ok = $true
$legs = New-Object System.Collections.Generic.List[object]
try {
    $null = Invoke-Api $v "session" @{ action = "login"; accounts_file = $accounts; account = $alias; login_uri = $loginUri; start = $Start }
    $st = Wait-Until { $s = J $v "session" '{"action":"state"}'; if ($s -and $s.has_session -and -not $s.ended -and $s.stage -eq "InWorld") { $s } } 150
    if (-not $st) { throw "the viewer did not reach the world" }
    Start-Sleep -Seconds 20   # the body, the worn piece and the neighbours settle
    if ($CrossingFrames) { Note "message_trace" (J $v "neighbour_debug" '{"action":"trace_on"}') }
    if ($Wear.Count -gt 0) {
        function Ap($body) { J $v "inventory" $body }
        foreach ($k in @("CurrentOutfit", "Clothing", "Bodypart", "Texture", "Object")) { $null = Ap @{ action = "open"; kind = $k } }
        $null = Ap @{ action = "search"; text = "a" }
        $loaded = Wait-Until { $s = Ap @{ action = "status" }; if ($s -and $s.unloaded -eq 0 -and $s.in_flight -eq 0) { $s } } 300
        $null = Ap @{ action = "search"; text = "" }
        $worn = @()
        foreach ($item in $Wear) {
            $before = (Obs $v).self.appearance.worn_count
            $att = Ap @{ action = "attach"; text = $item }
            $after = Wait-Until { $w = (Obs $v).self.appearance.worn_count; if ($w -gt $before) { $w } } 40
            $worn += , @{ item_matched = [bool]$after; before = $before; after = $after; reply = $att }
            Start-Sleep -Seconds 5
        }
        Note "wear" @{ inventory_loaded = [bool]$loaded; items = $worn; worn_count = (Obs $v).self.appearance.worn_count }
        Start-Sleep -Seconds 15   # the rebake and the pieces settle
    }
    $startGrid = @((CurrentOf (Nbrs $v)).grid)
    Note "start" @{ grid = $startGrid; regions = @((Nbrs $v).regions | ForEach-Object { @{ grid = $_.grid; size = $_.size; current = $_.current } }); worn = (Obs $v).self.appearance.worn_count; fly = [bool]$Fly }

    function Go([double]$metres, [int]$maxSec) {
        $p0 = (Obs $v).self.region_position; if (-not $p0) { return 0 }
        Key $v "W" $true
        $sw = [System.Diagnostics.Stopwatch]::StartNew(); $gone = 0
        while ($sw.Elapsed.TotalSeconds -lt $maxSec) {
            Start-Sleep -Milliseconds 300
            $q = (Obs $v).self.region_position; if (-not $q) { continue }
            $gone = [math]::Sqrt([math]::Pow($q[0] - $p0[0], 2) + [math]::Pow($q[1] - $p0[1], 2))
            if ($gone -ge $metres) { break }
        }
        Key $v "W" $false
        return [math]::Round($gone)
    }
    function FlyUp() { Key $v "F" $true; Start-Sleep -Milliseconds 200; Key $v "F" $false; Start-Sleep -Milliseconds 500; Key $v "E" $true; Start-Sleep -Milliseconds ([int]($ClimbSeconds * 1000)); Key $v "E" $false }
    function Leg([string]$name, [double]$heading, $fromGrid, $wantGrid) {
        $leg = [ordered]@{ leg = $name; arrived = $false; arrive_s = $null; stalled = $false }
        $leg.faced = Face $v $heading
        Key $v "W" $true
        $t0 = [System.Diagnostics.Stopwatch]::StartNew(); $lastPos = $null; $lastMove = [System.Diagnostics.Stopwatch]::StartNew()
        while ($t0.Elapsed.TotalSeconds -lt $LegSeconds) {
            Start-Sleep -Milliseconds 100
            $n = Nbrs $v; $cn = if ($n) { CurrentOf $n } else { $null }
            if ($cn -and -not (SameGrid $cn.grid $fromGrid) -and ($null -eq $wantGrid -or (SameGrid $cn.grid $wantGrid))) { $leg.arrived = $true; $leg.arrived_at = (Get-Date).ToUniversalTime(); $leg.arrive_s = [math]::Round($t0.Elapsed.TotalSeconds, 2); $leg.arrived_grid = @($cn.grid); break }
            $pos = (Obs $v).self.region_position
            $pp = if ($pos) { "{0:N1},{1:N1}" -f $pos[0], $pos[1] } else { "none" }
            if ($pp -ne $lastPos) { $lastPos = $pp; $lastMove.Restart() }
            if ($lastMove.Elapsed.TotalSeconds -gt $StallSeconds) { $leg.stalled = $true; break }
        }
        if ($leg.arrived -and $HoldAfterSeconds -gt 0) { Start-Sleep -Milliseconds ([int]($HoldAfterSeconds * 1000)) }
        Key $v "W" $false
        $legs.Add($leg)
        Note "leg_$name" $leg
        return $leg
    }

    if ($Fly) { FlyUp }
    for ($c = 1; $c -le $Crossings; $c++) {
        $o = Leg ("out_{0}" -f $c) $HeadingDeg $startGrid $null
        if (-not $o.arrived) { Note "stopped" "out leg $c did not arrive"; break }
        $other = @($o.arrived_grid)
        if ($Fly) { Note ("inside_out_{0}" -f $c) @{ flew_m = (Go $FlyMetres 60) } }
        Start-Sleep -Seconds 4   # the frames after the crossing settle in the log
        $b = Leg ("back_{0}" -f $c) (($HeadingDeg + 180) % 360) $other $startGrid
        if (-not $b.arrived) { Note "stopped" "back leg $c did not arrive"; break }
        if ($Fly) { Note ("inside_back_{0}" -f $c) @{ flew_m = (Go ([math]::Min($FlyMetres, 40)) 40) } }
        Start-Sleep -Seconds 4
        # back at the start region: return to where the next cycle begins (the start is a few metres from the border)
        $now = (Obs $v).self.region_position
        Note ("after_back_{0}" -f $c) @{ pos = $now }
    }
    $ok = ($legs.Count -eq 2 * $Crossings) -and (@($legs | Where-Object { -not $_.arrived }).Count -eq 0)
}
catch { Note "error" $_.Exception.Message; $ok = $false }
finally {
    try { Key $v "W" $false } catch { }
    try { $lo = (Invoke-Api $v "session" '{"action":"logout"}').Body | ConvertFrom-Json; Note "logout" @{ ended = $lo.ended; reason = $lo.end_reason } } catch { Note "logout" "no session" }
    try { Invoke-Api $v "quit" "{}" | Out-Null } catch { }
    if (-not $v.Proc.WaitForExit(60000)) { Note "exit" "still running" } else { Note "exit" $v.Proc.ExitCode }

    # ---- the log: gaps in the drawn worn pieces, matched to the crossings ----
    function Stamp([string]$line) { [datetime]::Parse(($line -split ' ')[0], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() }
    $lines = if (Test-Path -LiteralPath $vlog) { @(Get-Content -LiteralPath $vlog | Where-Object { $_ -match ' worn\.(frame|event) ' }) } else { @() }
    Set-Content -LiteralPath (Join-Path $Out "worn-frames.txt") -Value $lines -Encoding utf8
    $frames = @($lines | Where-Object { $_ -match ' worn\.frame ' } | ForEach-Object {
        [pscustomobject]@{ t = (Stamp $_); frame = [long]([regex]::Match($_, ' frame=(\d+)').Groups[1].Value); state = [regex]::Match($_, ' state=(\S+)').Groups[1].Value; line = $_ } })
    $events = @($lines | Where-Object { $_ -match ' worn\.event ' } | ForEach-Object {
        [pscustomobject]@{ t = (Stamp $_); frame = [long]([regex]::Match($_, ' frame=(\d+)').Groups[1].Value); text = ($_ -replace '^\S+ \S+ \S+ worn\.event ', '') } })
    $lastShown = @{}; $downAt = @{}; $gaps = New-Object System.Collections.Generic.List[object]
    foreach ($f in $frames) {
        foreach ($m in [regex]::Matches($f.state, '([0-9a-f]{8})=([01])@')) {
            $piece = $m.Groups[1].Value; $shown = $m.Groups[2].Value -eq "1"
            if ($lastShown.ContainsKey($piece) -and $lastShown[$piece] -and -not $shown) { $downAt[$piece] = $f }
            elseif ($shown -and $downAt.ContainsKey($piece)) {
                $d = $downAt[$piece]; $downAt.Remove($piece)
                $near = @($events | Where-Object { $_.frame -ge $d.frame - 12 -and $_.frame -le $f.frame + 4 } | ForEach-Object { "f$($_.frame) $($_.text)" })
                $leg = $legs | Where-Object { $_.arrived_at -and [math]::Abs(($_.arrived_at - $d.t).TotalSeconds) -lt 8 } | Select-Object -First 1
                $gaps.Add([ordered]@{ piece = $piece; frames_missed = ($f.frame - $d.frame); from_frame = $d.frame; to_frame = $f.frame; at = $d.t.ToString("o"); crossing = $(if ($leg) { $leg.leg } else { $null }); state_when_lost = $d.state; state_when_back = $f.state; events = $near })
            }
            $lastShown[$piece] = $shown
        }
    }
    foreach ($leg in $legs) { if ($leg.arrived_at) { $leg.arrived_at = $leg.arrived_at.ToString("o") } }
    Note "worn_frame_lines" @{ frame_lines = $frames.Count; event_lines = $events.Count }
    Note "gaps" @{ count = $gaps.Count; frames_missed_total = [int](($gaps | ForEach-Object { $_.frames_missed } | Measure-Object -Sum).Sum) }
    Note "gap_list" @($gaps | ForEach-Object { $_ })
    if ($frames.Count -lt 2) { $ok = $false; Note "no_trace" "the log holds too few worn.frame lines" }
    if ($gaps.Count -gt 0) { $ok = $false }
    Start-Sleep -Seconds 3
    Note "presence_after_logout" @{ walker = (Presence $walkerId) }
    Note "pass" ([bool]$ok)
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Out "worn-frames-live.json") -Encoding utf8
}
exit ($(if ($ok) { 0 } else { 1 }))
