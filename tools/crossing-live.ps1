# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# crossing-live.ps1 - the two-account crossing driver. Two copies of the development export are started: the walker logs in and walks
# across the border between two regions, N times each way; the observer stays in the first region and records what it holds of the walker.
# Per leg it records:
#   - the arrival time (seconds from the first key to the viewer reporting the other region as its own);
#   - the region server's log lines about the crossing, written between the leg's start and its end (when a region log folder is set);
#   - the observer's copy of the walker's look (bake ids, parameter hash, height byte) after the arrival, polled until it equals the
#     look it held before the first leg, or until the wait ends. The copy is read from whichever region's world the observer holds it in
#     (the "neighbours" report lists the avatars' looks per region).
# A leg that does not move (the walker reports nothing about itself, or its position stays put for -StallSeconds) is stopped early and the
# viewer's own account of why is written to the report (the "self_missing" part of the observe answer and the neighbours report).
# With -Rebake the walker, standing in the second region, changes its shape's height and saves (the grid rebakes), the observer's copy is
# read until it moves, the shape is put back and the copy is read until it is original again.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\crossing-live.ps1 -Out <new folder> [-Exe <development export>]
#     [-Crossings 3] [-Rebake] [-StallSeconds 20] [-NoAttachmentCheck]
#
# When the run is over, the walker's viewer log (written when the viewer exits) is read for the line it writes 1.5 s and 6 s after a crossing
# (attachments.after_crossing: the objects the region told the session the agent wears, how many of them are drawn, and how many wait for their
# wearer). The lines are matched to the legs by time. The run passes only when each leg has both lines, with every worn object drawn and none
# waiting. -NoAttachmentCheck leaves that out (an older viewer does not write the line).
#
# Settings come from the environment or the untracked live settings file (live-env.ps1); none is written to a report:
#   NV_TEST_ACCOUNTS, NV_GRID_INFO_URL                      as for the other live scripts
#   NV_CROSS_WALKER, NV_CROSS_OBSERVER                      the two accounts' aliases in the accounts file
#   NV_CROSS_WALKER_ID, NV_CROSS_OBSERVER_ID                their agent ids (the walker's id names its copy in the observer's report)
#   NV_CROSS_WALKER_START, NV_CROSS_OBSERVER_START          start locations (uri:Region&x&y&z), the walker's a few metres from the border
#   NV_CROSS_REGION_A, NV_CROSS_REGION_B                    the two regions' names (A is where the walker starts)
#   NV_CROSS_HEADING_DEG                                    the heading from A toward B (0 is east, 90 is north); default 90
#   NV_CROSS_REGION_LOG_DIR                                 (optional) the region server's log folder; the newest OpenSim*.log in it is read
#   NV_CROSS_WEAR                                           (optional) the name of an inventory item the walker attaches before the first leg
#   NV_PRESENCE_COMMAND                                     (optional) a command that takes an agent id and prints "offline" when it is
# Run it under an avatar claim for each account and a live-login claim (tools\claim-guard.ps1 refuses a viewer without a granted claim).
# -ObserverMotion: the observer records every region's avatars each frame (drawn and stated places, velocity, update count, agent id) and saves the
# record of each leg as observer-motion-<leg>.json; -StrollFirst N first has the walker walk 3 s away from the border, stop, turn and walk back N times.
# Exit: 0 every leg arrived clean, 1 a leg failed, 3 blocked (settings or presence), 5 a viewer or login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Exe = "",
    [int]$Crossings = 3,
    [int]$StallSeconds = 20,
    [int]$LegSeconds = 90,
    [int]$LookWaitSeconds = 30,
    [switch]$Rebake,
    [switch]$NoAttachmentCheck,
    [switch]$Inside,   # after each arrival in the second region: run InsideMetres in, fly InsideFlyMetres further, land and walk, then fly back to the border
    [int]$InsideMetres = 200,
    [int]$InsideFlyMetres = 400,
    [switch]$LegShots,   # a picture of the walker's own view after every arrival, with what it wears and what is drawn of each region
    [switch]$Shots,   # pictures (in the output folder, shots) from the walker in the first region (toward the border and away from it) and in the second, one login
    [switch]$ObserverMotion,   # the observer records, every frame, every region's avatars drawn against the statements it holds; saved per leg as observer-motion-<leg>.json
    [int]$StrollFirst = 0   # before the legs: that many times the walker walks 3 s away from the border, stops, turns and walks back, stops (observer-motion-stroll-<n>.json)
)
$ErrorActionPreference = "Stop"
$env:NV_WORN_TRACE = "1"   # the viewers log where the avatar is and each worn piece once a second
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$liveEnv = Join-Path $PSScriptRoot "live-env.ps1"
function Setting([string]$name) { & $liveEnv $name }

$accounts = Setting NV_TEST_ACCOUNTS
$gridInfo = Setting NV_GRID_INFO_URL
$walkerAlias = Setting NV_CROSS_WALKER; $observerAlias = Setting NV_CROSS_OBSERVER
$walkerId = Setting NV_CROSS_WALKER_ID; $observerId = Setting NV_CROSS_OBSERVER_ID
$walkerStart = Setting NV_CROSS_WALKER_START; $observerStart = Setting NV_CROSS_OBSERVER_START
$regionA = Setting NV_CROSS_REGION_A; $regionB = Setting NV_CROSS_REGION_B
$headingOut = if (Setting NV_CROSS_HEADING_DEG) { [double](Setting NV_CROSS_HEADING_DEG) } else { 90.0 }
$regionLogDir = Setting NV_CROSS_REGION_LOG_DIR
$wearItem = Setting NV_CROSS_WEAR
$presenceCommand = Setting NV_PRESENCE_COMMAND
$missing = @(@{ n = "NV_TEST_ACCOUNTS"; v = $accounts }, @{ n = "NV_GRID_INFO_URL"; v = $gridInfo }, @{ n = "NV_CROSS_WALKER"; v = $walkerAlias }, @{ n = "NV_CROSS_OBSERVER"; v = $observerAlias },
    @{ n = "NV_CROSS_WALKER_ID"; v = $walkerId }, @{ n = "NV_CROSS_WALKER_START"; v = $walkerStart }, @{ n = "NV_CROSS_OBSERVER_START"; v = $observerStart },
    @{ n = "NV_CROSS_REGION_A"; v = $regionA }, @{ n = "NV_CROSS_REGION_B"; v = $regionB }) | Where-Object { -not $_.v } | ForEach-Object { $_.n }
if ($missing.Count -gt 0) { Write-Host ("BLOCKED: settings not set: " + ($missing -join ", ")); exit 3 }
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\Reverie.console.exe" }
$godot = Join-Path $PSScriptRoot "godot.ps1"

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$script:ShotsDir = (Join-Path $Out "shots")
$result = [ordered]@{}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note($k, $v) { $result[$k] = $v; Write-Host ("[{0,8:N1}s] {1} = {2}" -f $clock.Elapsed.TotalSeconds, $k, ($v | ConvertTo-Json -Compress -Depth 8)) }

try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $gridInfo).Content).gridinfo.login } catch { $loginUri = $null }
if (-not $loginUri) { Write-Host "BLOCKED: grid info did not answer"; exit 3 }

class Viewer { [int]$Port; [string]$Token; $Proc; [string]$Name }
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
function State([Viewer]$v) { J $v "session" '{"action":"state"}' }
function Ap([Viewer]$v, $body) { J $v "inventory" $body }
function Obs([Viewer]$v) { J $v "observe" }
function Nbrs([Viewer]$v) { $r = J $v "neighbours"; if ($r -and -not $r.regions) { $r | Add-Member -NotePropertyName regions -NotePropertyValue @() -Force }; $r }
function Wait-Until([scriptblock]$cond, [int]$seconds, [int]$pollMs = 1000) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds $pollMs }
    return $null
}
function Start-V([string]$name) {
    $sandbox = Join-Path $Out "$name-localappdata"; $appdata = Join-Path $Out "$name-appdata"; $vlog = Join-Path $Out "$name-viewer.log"
    if ($name -eq "walker") { $env:NV_OUTFIT_SCRATCH_OK = "1" } else { Remove-Item Env:NV_OUTFIT_SCRATCH_OK -ErrorAction SilentlyContinue }
    $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$sandbox`"", "-TimeoutSeconds", "3000", "-LogFile", "`"$vlog`"", "-DiagDir", "`"$(Join-Path $Out 'diag')`"",
        "-ArgLine", "`"--rendering-driver vulkan -- --automation --neighbours=on`"")
    Remove-Item Env:NV_OUTFIT_SCRATCH_OK -ErrorAction SilentlyContinue
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $sandbox "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } 120
    if (-not $tf) { throw "$name : no token file" }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $v = [Viewer]::new(); $v.Port = [int]$j.port; $v.Token = [string]$j.token; $v.Proc = $p; $v.Name = $name
    return $v
}
function Login([Viewer]$v, [string]$account, [string]$start) {
    $null = Invoke-Api $v "session" @{ action = "login"; accounts_file = $accounts; account = $account; login_uri = $loginUri; start = $start }
    $st = Wait-Until { $s = State $v; if ($s -and $s.has_session -and -not $s.ended -and $s.stage -eq "InWorld") { $s } } 150
    if (-not $st) { throw "a viewer did not reach the world" }
}
function Presence([string]$id) { if (-not $presenceCommand -or -not $id) { return "unknown" }; $o = @(& cmd /c "$presenceCommand $id 2>nul"); return ($o | Select-Object -First 1) }

# ---- the region server's log, read between two marks ----
function LogMark() {
    if (-not $regionLogDir -or -not (Test-Path -LiteralPath $regionLogDir)) { return $null }
    $f = Get-ChildItem -LiteralPath $regionLogDir -Filter "OpenSim*.log" -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { return $null }
    return @{ File = $f.FullName; Length = $f.Length }
}
function LogSince($mark) {
    if (-not $mark) { return @() }
    $fs = [System.IO.File]::Open($mark.File, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -le $mark.Length) { return @() }
        [void]$fs.Seek($mark.Length, 'Begin')
        $text = (New-Object System.IO.StreamReader($fs)).ReadToEnd()
    } finally { $fs.Close() }
    return @($text -split "`r?`n" | Where-Object { $_ -match '(?i)cross|AddClient|agent update failed|CompleteMovement| ERROR ' } | Select-Object -First 40)
}

# ---- the viewers' reports ----
function Key([Viewer]$v, [string]$key, [bool]$down) { [void](Invoke-Api $v "keys" (@{ key = $key; down = $down } | ConvertTo-Json -Compress)) }
function Face([Viewer]$v, [double]$deg) {
    for ($k = 0; $k -lt 40; $k++) {
        $s = (Obs $v).self
        if (-not $s) { return $false }
        $h = [double]$s.region_heading_deg
        $d = (($deg - $h + 540) % 360) - 180
        if ([math]::Abs($d) -lt 12) { return $true }
        $turn = if ($d -gt 0) { "A" } else { "D" }
        Key $v $turn $true; Start-Sleep -Milliseconds 250; Key $v $turn $false; Start-Sleep -Milliseconds 150
    }
    return $false
}
function CurrentOf($n) { @($n.regions | Where-Object { $_.current }) | Select-Object -First 1 }
function SameGrid($a, $b) { $a -and $b -and $a[0] -eq $b[0] -and $a[1] -eq $b[1] }
function LookKey($l) { if (-not $l) { return "none" }; return ("bakes=" + $l.bakes + " hash=" + $l.params_hash + " h=" + $l.height_byte) }
# the observer's copy of the walker's look, from whichever region's world holds it
function CopyOfWalker() {
    $n = Nbrs $script:obsv
    if (-not $n) { return $null }
    foreach ($r in @($n.regions)) { foreach ($x in @($r.looks)) { if ($x -and ([string]$x.agent) -eq $walkerId) { return @{ look = $x.look; grid = @($r.grid) } } } }
    return $null
}
function Diagnose([Viewer]$v) {
    $o = Obs $v; $n = Nbrs $v
    return [ordered]@{
        stage = $o.stage; world = $o.world; self_missing = $o.self_missing
        regions = @($n.regions | ForEach-Object { [ordered]@{ grid = $_.grid; current = $_.current; avatars = @($_.avatars).Count; objects = $_.objects } })
        walker_holds_own_avatar = (@($n.regions | Where-Object { $_.current } | ForEach-Object { $_.avatars }) -contains $walkerId)
    }
}

# pictures of the walker's view in four directions about the avatar (the follow camera's orbit), the draw distance as it is set
function ShotSet([string]$where) {
    Start-Sleep -Seconds 8   # terrain, objects and textures settle
    $shots = @()
    foreach ($yaw in @(0, 90, 180, 270)) {
        $null = J $script:walker "orbit" (@{ yaw_deg = $yaw; pitch_deg = 8 } | ConvertTo-Json -Compress)
        Start-Sleep -Seconds 2
        $name = "{0}_yaw{1}" -f $where, $yaw
        $raw = try { Invoke-Api $script:walker "screenshot" (@{ dir = $script:ShotsDir; name = $name } | ConvertTo-Json -Compress) } catch { @{ Status = 0; Body = $_.Exception.Message } }
        $r = try { $raw.Body | ConvertFrom-Json } catch { $null }
        if (-not $r -or -not $r.file) { Note ("shot_failed_" + $name) @{ dir = $script:ShotsDir; exists = (Test-Path -LiteralPath $script:ShotsDir); status = $raw.Status; body = ([string]$raw.Body).Substring(0, [math]::Min(200, ([string]$raw.Body).Length)) } }
        $shots += , @{ name = $name; file = $r.file; width = $r.width; height = $r.height }
    }
    $null = J $script:walker "orbit" (@{ yaw_deg = 0; pitch_deg = 8 } | ConvertTo-Json -Compress)
    $grid = @((CurrentOf (Nbrs $script:walker)).grid)
    Note "shots_$where" @{ grid = $grid; shots = $shots; regions_drawn = @((Nbrs $script:walker).regions | ForEach-Object { @{ grid = $_.grid; drawn = $_.drawn; chunks = $_.chunks; instances = $_.instances; objects = $_.objects; terrain_patches = $_.terrain_patches; size = $_.size } }) }
}

$script:walker = $null; $script:obsv = $null
$ok = $true
$legs = New-Object System.Collections.Generic.List[object]
try {
    foreach ($id in @($walkerId, $observerId)) { if ($id -and ((Presence $id) -notin @("offline", "unknown"))) { Write-Host "BLOCKED: a test account is not offline"; exit 3 } }
    $script:walker = Start-V "walker"
    $script:obsv = Start-V "observer"
    Login $script:walker $walkerAlias $walkerStart
    Login $script:obsv $observerAlias $observerStart
    $seen = Wait-Until { $c = CopyOfWalker; if ($c -and $c.look -and $c.look.bakes) { $c } } 150
    Note "observer_sees_walker" @{ ok = [bool]$seen }
    if (-not $seen) { throw "the observer does not hold a look for the walker" }
    if ($wearItem) {
        # the walker puts the named inventory item on (an attachment), as the look to carry across; the accounts here are scratch accounts
        foreach ($k in @("CurrentOutfit", "Clothing", "Bodypart", "Texture", "Object")) { $null = Ap $script:walker @{ action = "open"; kind = $k } }
        $null = Ap $script:walker @{ action = "search"; text = "a" }
        $loaded = Wait-Until { $s = Ap $script:walker @{ action = "status" }; if ($s -and $s.unloaded -eq 0 -and $s.in_flight -eq 0) { $s } } 300
        $null = Ap $script:walker @{ action = "search"; text = "" }
        $att = Ap $script:walker @{ action = "attach"; text = $wearItem }
        $worn = Wait-Until { $w = (Obs $script:walker).self.appearance.worn_count; if ($w -ge 1) { $w } } 60
        Note "wear" @{ inventory_loaded = [bool]$loaded; attach = $att; worn_count = $worn }
        if (-not $worn) { throw "the item was not put on" }
        Start-Sleep -Seconds 10   # the rebake that follows
    }
    Start-Sleep -Seconds 8   # let the look settle
    function TakeMotion([string]$name) {
        if (-not $ObserverMotion) { return }
        $raw = try { (Invoke-Api $script:obsv "motion" '{"action":"take"}').Body } catch { "" }
        Set-Content -LiteralPath (Join-Path $Out "observer-motion-$name.json") -Value $raw -Encoding utf8
    }
    if ($ObserverMotion) { $null = Invoke-Api $script:obsv "motion" '{"action":"start_all"}' }
    for ($i = 1; $i -le $StrollFirst; $i++) {
        $null = Invoke-Api $script:obsv "motion" '{"action":"take"}'   # what came before this stroll
        $null = Face $script:walker (($headingOut + 180) % 360); Start-Sleep -Milliseconds 800
        Key $script:walker "W" $true; Start-Sleep -Seconds 3; Key $script:walker "W" $false; Start-Sleep -Seconds 1.5
        $null = Face $script:walker $headingOut; Start-Sleep -Milliseconds 800
        Key $script:walker "W" $true; Start-Sleep -Seconds 3; Key $script:walker "W" $false; Start-Sleep -Seconds 1.5
        TakeMotion "stroll-$i"
    }
    $baseline = CopyOfWalker
    $baseKey = LookKey $baseline.look
    Note "baseline" @{ copy = $baseline.look }
    if ($Shots) { New-Item -ItemType Directory -Force -Path $script:ShotsDir | Out-Null; ShotSet "first" }
    $startGrid = @((CurrentOf (Nbrs $script:walker)).grid)
    $otherGrid = $null

    function Leg([string]$name, [double]$heading, $fromGrid, $wantGrid) {
        $mark = LogMark
        $leg = [ordered]@{ leg = $name; arrived = $false; arrive_s = $null; stalled = $false; observer_copy_matches = $false; observer_wait_s = $null }
        $faced = Face $script:walker $heading
        $leg.faced = $faced
        Key $script:walker "W" $true
        $t0 = [System.Diagnostics.Stopwatch]::StartNew()
        $lastPos = $null; $lastMove = [System.Diagnostics.Stopwatch]::StartNew()
        while ($t0.Elapsed.TotalSeconds -lt $LegSeconds) {
            Start-Sleep -Milliseconds 150
            $n = Nbrs $script:walker
            $cn = if ($n) { CurrentOf $n } else { $null }
            if ($cn -and -not (SameGrid $cn.grid $fromGrid) -and ($null -eq $wantGrid -or (SameGrid $cn.grid $wantGrid))) { $leg.arrived = $true; $leg.arrived_at = (Get-Date).ToUniversalTime(); $leg.arrive_s = [math]::Round($t0.Elapsed.TotalSeconds, 2); $leg.arrived_grid = @($cn.grid); break }
            $pos = (Obs $script:walker).self.region_position
            $p = if ($pos) { "{0:N1},{1:N1}" -f $pos[0], $pos[1] } else { "none" }
            if ($p -ne $lastPos) { $lastPos = $p; $lastMove.Restart() }
            if ($lastMove.Elapsed.TotalSeconds -gt $StallSeconds) { $leg.stalled = $true; break }
        }
        if (-not $leg.arrived) { $leg.diagnosis = Diagnose $script:walker }
        $leg.region_log = LogSince $mark
        Key $script:walker "W" $false
        if ($leg.arrived) {
            # the observer's copy of the look, until it equals the baseline
            $w = [System.Diagnostics.Stopwatch]::StartNew(); $samples = 0; $bad = 0
            while ($w.Elapsed.TotalSeconds -lt $LookWaitSeconds) {
                $c = CopyOfWalker; $samples++
                if ($c -and (LookKey $c.look) -eq $baseKey) { $leg.observer_copy_matches = $true; $leg.observer_wait_s = [math]::Round($w.Elapsed.TotalSeconds, 2); break }
                $bad++; Start-Sleep -Milliseconds 500
            }
            $leg.observer_samples = $samples; $leg.observer_samples_not_baseline = $bad
            if (-not $leg.observer_copy_matches) { $leg.observer_copy_now = (CopyOfWalker).look }
            $leg.walker_look_after = (Ap $script:walker @{ action = "appearance" }).look
            $leg.walker_worn_count = (Obs $script:walker).self.appearance.worn_count
        }
        if ($leg.arrived -and -not $NoAttachmentCheck) { Start-Sleep -Seconds 7 }   # the viewer counts what is worn and drawn 1.5 s and 6 s after a crossing
        if ($LegShots -and $leg.arrived) {
            # the walker's own view after the arrival: the picture, the number of things it wears, and what is drawn of each region held
            Start-Sleep -Seconds 4
            New-Item -ItemType Directory -Force -Path $script:ShotsDir | Out-Null
            $null = J $script:walker "orbit" (@{ yaw_deg = 0; pitch_deg = 8 } | ConvertTo-Json -Compress)
            $raw = try { Invoke-Api $script:walker "screenshot" (@{ dir = $script:ShotsDir; name = "leg_$name" } | ConvertTo-Json -Compress) } catch { @{ Status = 0; Body = $_.Exception.Message } }
            $r = try { $raw.Body | ConvertFrom-Json } catch { $null }
            $leg.own_view_shot = if ($r -and $r.file) { $r.file } else { "failed" }
            $leg.own_view_worn_count = (Obs $script:walker).self.appearance.worn_count
            $leg.own_view_regions = @((Nbrs $script:walker).regions | ForEach-Object { @{ grid = $_.grid; current = $_.current; drawn = $_.drawn; chunks = $_.chunks; instances = $_.instances; objects = $_.objects; terrain_patches = $_.terrain_patches } })
        }        if ($Shots -and $leg.arrived -and $name -eq "out_1") { ShotSet "second" }
        TakeMotion $name
        $legs.Add($leg)
        Note "leg_$name" $leg
        return $leg
    }

    $script:forwardKey = "W"   # the forward key the route holds: W, and UP in the second cycle (the arrow key moves the avatar the same way)
    function KeyS([string]$key, [bool]$down) { Key $script:walker $key $down }
    function Pos() { $p = (Obs $script:walker).self.region_position; if ($p) { return @([double]$p[0], [double]$p[1]) } else { return $null } }
    # hold W (with shift: run) until the avatar has gone $metres from where it is, or $maxSec has passed; returns the metres gone
    function Go([double]$metres, [int]$maxSec) {
        $p0 = Pos; if (-not $p0) { return 0 }
        KeyS $script:forwardKey $true
        $sw = [System.Diagnostics.Stopwatch]::StartNew(); $gone = 0
        while ($sw.Elapsed.TotalSeconds -lt $maxSec) {
            Start-Sleep -Milliseconds 400
            $p = Pos; if (-not $p) { continue }
            $gone = [math]::Sqrt([math]::Pow($p[0] - $p0[0], 2) + [math]::Pow($p[1] - $p0[1], 2))
            if ($gone -ge $metres) { break }
        }
        KeyS $script:forwardKey $false
        return [math]::Round($gone)
    }
    function FlyUp() { Key $script:walker "F" $true; Start-Sleep -Milliseconds 200; Key $script:walker "F" $false; Start-Sleep -Milliseconds 500; Key $script:walker "E" $true; Start-Sleep -Milliseconds 2500; Key $script:walker "E" $false }
    function Inside([string]$name) {
        # walk (run) in, fly further in, land and walk a little; the avatar is then flown back to the border by the back leg
        $r = [ordered]@{ after = $name }
        $r.ran_m = Go $InsideMetres 120
        FlyUp
        $r.flew_m = Go $InsideFlyMetres 90
        Key $script:walker "F" $true; Start-Sleep -Milliseconds 200; Key $script:walker "F" $false   # fly off: land
        Start-Sleep -Seconds 6
        $r.walked_m = Go 40 40
        FlyUp   # fly again for the way back
        Note "inside_$name" $r
    }

    for ($c = 1; $c -le $Crossings; $c++) {
        $script:forwardKey = if ($c -eq 2) { "UP" } else { "W" }
        $o = Leg ("out_{0}" -f $c) $headingOut $startGrid $null
        if (-not $o.arrived) { Note "stopped" "out leg $c did not arrive"; break }
        $otherGrid = @($o.arrived_grid)
        if ($Inside) { Inside ("out_{0}" -f $c) }
        $b = Leg ("back_{0}" -f $c) (($headingOut + 180) % 360) $otherGrid $startGrid
        if (-not $b.arrived) { Note "stopped" "back leg $c did not arrive"; break }
        if ($Inside) { Key $script:walker "F" $true; Start-Sleep -Milliseconds 200; Key $script:walker "F" $false; Start-Sleep -Seconds 4 }   # land
    }
    $clean = @($legs | Where-Object { $_.arrived -and $_.observer_copy_matches }).Count
    Note "crossings_summary" @{ legs_run = $legs.Count; legs_expected = 2 * $Crossings; legs_clean = $clean }
    $ok = ($legs.Count -eq 2 * $Crossings) -and ($clean -eq $legs.Count)

    # the shape is changed with the walker in the second region: one more out leg takes it there (the observer stays in the first)
    if ($Rebake -and $ok) {
        $x = Leg "out_rebake" $headingOut $startGrid $null
        if (-not $x.arrived -or -not $x.observer_copy_matches) { $ok = $false }
    }
    if ($Rebake -and $ok) {
        $where = (CurrentOf (Nbrs $script:walker)).grid
        foreach ($k in @("CurrentOutfit", "Clothing", "Bodypart", "Texture")) { $null = Ap $script:walker @{ action = "open"; kind = $k } }
        $null = Ap $script:walker @{ action = "search"; text = "a" }
        $all = Wait-Until { $s = Ap $script:walker @{ action = "status" }; if ($s -and $s.unloaded -eq 0 -and $s.in_flight -eq 0) { $s } } 300
        $null = Ap $script:walker @{ action = "search"; text = "" }
        Note "inventory_loaded" @{ ok = [bool]$all }
        $null = Wait-Until { $p = Ap $script:walker @{ action = "ap_prepare" }; if ($p.ready) { $p } } 120
        $null = Wait-Until { $x = Ap $script:walker @{ action = "appearance" }; if (@($x.worn).Count -ge 4 -and $x.look) { $x } } 90
        $before = CopyOfWalker
        $null = Ap $script:walker @{ action = "ap_open"; text = "shape" }
        $st = Ap $script:walker @{ action = "appearance" }
        $h0 = [double](@($st.values | Where-Object { $_.id -eq 33 })[0].value)
        $h1 = if ($h0 -ge 0) { [math]::Max(-1.0, $h0 - 0.8) } else { [math]::Min(1.0, $h0 + 0.8) }
        $null = Ap $script:walker @{ action = "ap_set"; param = 33; a = $h1 }
        $c2 = (Ap $script:walker @{ action = "appearance" }).save_count
        $sv = Ap $script:walker @{ action = "ap_save" }
        $fin = Wait-Until { $s = Ap $script:walker @{ action = "appearance" }; if ($s.save_count -gt $c2 -and $s.save_state -ne "Saving") { $s } } 150 500
        $moved = Wait-Until { $x = CopyOfWalker; if ($x -and $x.look -and $x.look.height_byte -ne $before.look.height_byte) { $x } } 120
        Note "rebake_change" @{ walker_grid = $where; from = $h0; to = $h1; saved = $fin.last_save.saved; observer_copy_moved = [bool]$moved; observer_region_grid = $(if ($moved) { $moved.grid } else { $null }) }
        $null = Ap $script:walker @{ action = "ap_set"; param = 33; a = $h0 }
        $c3 = (Ap $script:walker @{ action = "appearance" }).save_count
        $null = Ap $script:walker @{ action = "ap_save" }
        $null = Wait-Until { $s = Ap $script:walker @{ action = "appearance" }; if ($s.save_count -gt $c3 -and $s.save_state -ne "Saving") { $s } } 150 500
        $null = Ap $script:walker @{ action = "ap_close" }
        $back = Wait-Until { $x = CopyOfWalker; if ($x -and $x.look -and $x.look.height_byte -eq $before.look.height_byte) { $x } } 120
        Note "rebake_restored" @{ observer_copy_original = [bool]$back }
        $pass = [bool]$moved -and [bool]$fin.last_save.saved -and [bool]$back
        Note "rebake_pass" $pass
        $ok = $ok -and $pass
    }
}
catch { Note "error" $_.Exception.Message; $ok = $false }
finally {
    try { if ($script:walker) { Key $script:walker "W" $false } } catch { }
    foreach ($v in @($script:walker, $script:obsv)) {
        if ($null -eq $v) { continue }
        try { $lo = (Invoke-Api $v "session" '{"action":"logout"}').Body | ConvertFrom-Json; Note "logout_$($v.Name)" @{ ended = $lo.ended; reason = $lo.end_reason } } catch { Note "logout_$($v.Name)" "no session" }
        try { Invoke-Api $v "quit" "{}" | Out-Null } catch { }
        if (-not $v.Proc.WaitForExit(60000)) { Note "exit_$($v.Name)" "still running" } else { Note "exit_$($v.Name)" $v.Proc.ExitCode }
    }
    if (-not $NoAttachmentCheck) {
        # the viewer's log is complete now: each leg's two lines are the ones stamped from just before its arrival to nine seconds after it
        $wlog = Join-Path $Out "walker-viewer.log"
        $all = if (Test-Path -LiteralPath $wlog) { @(Get-Content -LiteralPath $wlog | Where-Object { $_ -match 'attachments\.after_crossing' }) } else { @() }
        $drawnLegs = 0
        foreach ($leg in $legs) {
            if (-not $leg.arrived_at) { continue }
            $mine = @($all | Where-Object {
                $t = [datetime]::Parse(($_ -split ' ')[0], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
                $t -ge $leg.arrived_at.AddSeconds(-2) -and $t -le $leg.arrived_at.AddSeconds(9)
            } | ForEach-Object { ($_ -replace '^.*attachments\.after_crossing\s+', '').Trim() })
            $good = $mine.Count -ge 2
            foreach ($l in $mine) {
                $m = [regex]::Match($l, 'worn_roots=(\d+) drawn_roots=(\d+)'); $w = [regex]::Match($l, 'waiting_for_wearer=(\d+)')
                if (-not $m.Success -or -not $w.Success -or [int]$m.Groups[1].Value -lt 1 -or $m.Groups[1].Value -ne $m.Groups[2].Value -or [int]$w.Groups[1].Value -ne 0) { $good = $false }
            }
            $leg.attachments_after_crossing = $mine
            $leg.attachments_drawn = $good
            if ($good) { $drawnLegs++ }
            $leg.arrived_at_utc = $leg.arrived_at   # kept as a time for the frame check below, removed from the result after it
            $leg.arrived_at = $leg.arrived_at.ToString("o")
        }
        Note "attachments_summary" @{ legs = $legs.Count; legs_with_every_worn_object_drawn = $drawnLegs }
        if ($drawnLegs -ne $legs.Count) { $ok = $false }
        # every frame for 3 s after each hand-over, in both viewers' own logs: the walker's own view of itself (own=1) and the observer's view of
        # the walker (own=0). A leg is clean in a view when it has a summary with frames, and in no frame the avatar or a worn piece was missing.
        $frameLegs = 0
        foreach ($view in @(@{ name = "walker"; own = "1" }, @{ name = "observer"; own = "0" })) {
            $vl = Join-Path $Out ($view.name + "-viewer.log")
            $lines = if (Test-Path -LiteralPath $vl) { @(Get-Content -LiteralPath $vl | Where-Object { $_ -match 'handoff\.frames_summary|handoff\.frame\s' }) } else { @() }
            foreach ($leg in $legs) {
                if (-not $leg.arrived_at_utc) { continue }
                $inWindow = @($lines | Where-Object {
                    $t = [datetime]::Parse(($_ -split ' ')[0], [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
                    $t -ge $leg.arrived_at_utc.AddSeconds(-2) -and $t -le $leg.arrived_at_utc.AddSeconds(9)
                })
                $sums = @($inWindow | Where-Object { $_ -match 'handoff\.frames_summary' -and $_ -match ('own=' + $view.own + '(\s|$)') })
                $clean = $sums.Count -ge 1
                foreach ($s in $sums) {
                    $fr = [regex]::Match($s, 'frames=(\d+)'); $na = [regex]::Match($s, 'frames_without_avatar=(\d+)'); $nw = [regex]::Match($s, 'frames_with_fewer_pieces=(\d+)')
                    $we = [regex]::Match($s, 'worn_expected=(\d+)')   # a sample that saw no worn piece at all proves nothing about pieces
                    # one avatar: the body is shown exactly once in every frame, every piece is bound to that skeleton, no joint of a piece is off the body's
                    $nb = [regex]::Match($s, 'frames_not_one_body=(\d+)'); $os = [regex]::Match($s, 'pieces_on_other_skeleton=(\d+)'); $jm = [regex]::Match($s, 'largest_joint_mismatch=([0-9.eE+-]+)')
                    if (-not ($nb.Success -and $os.Success -and $jm.Success) -or [int]$nb.Groups[1].Value -ne 0 -or [int]$os.Groups[1].Value -ne 0 -or [double]::Parse($jm.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture) -gt 0) { $clean = $false }
                    $bo = [regex]::Match($s, 'frames_body_off=(\d+)')   # the body drawn within half a metre of the agent in every frame
                    if (-not $bo.Success -or [int]$bo.Groups[1].Value -ne 0) { $clean = $false }
                    if (-not ($fr.Success -and $na.Success -and $nw.Success -and $we.Success) -or [int]$fr.Groups[1].Value -lt 1 -or [int]$na.Groups[1].Value -ne 0 -or [int]$nw.Groups[1].Value -ne 0) { $clean = $false }
                }
                $leg[$view.name + "_handoff_frames"] = @($sums | ForEach-Object { ($_ -replace '^.*handoff\.frames_summary\s+', '').Trim() })
                $leg[$view.name + "_handoff_clean"] = $clean
                if ($clean) { $frameLegs++ }
                # the per-frame lines of the leg are kept beside the result
                $perFrame = @($inWindow | Where-Object { $_ -match 'handoff\.frame\s' -and $_ -match ('leg=') })
                Set-Content -LiteralPath (Join-Path $Out ("frames-" + $view.name + "-" + $leg.leg + ".txt")) -Value $perFrame -Encoding utf8
            }
        }
        Note "handoff_frames_summary" @{ views_checked = ($legs.Count * 2); views_with_no_missing_frame = $frameLegs }
        if ($frameLegs -ne ($legs.Count * 2)) { $ok = $false }
        foreach ($leg in $legs) { $leg.Remove("arrived_at_utc") }
        # once a second for the whole run (worn.second): the walker in its own view (own=1) and in the observer's view (own=0): the avatar drawn and the worn pieces drawn;
        # a second with the avatar not drawn, or with fewer pieces than the most the view ever had, is a gap
        $secBad = 0; $secAll = 0
        foreach ($view in @(@{ name = "walker"; own = "1" }, @{ name = "observer"; own = "0" })) {
            $vl = Join-Path $Out ($view.name + "-viewer.log")
            $ls = if (Test-Path -LiteralPath $vl) { @(Get-Content -LiteralPath $vl | Where-Object { $_ -match "worn\.second" -and $_ -match (" own=" + $view.own + " ") }) } else { @() }
            $first = -1; for ($k = 0; $k -lt $ls.Count; $k++) { if ($ls[$k] -match "avatar_drawn=1") { $first = $k; break } }
            $ls = if ($first -ge 0) { @($ls[$first..($ls.Count - 1)]) } else { @() }   # from the first second the avatar was drawn (the body takes a moment at login)
            Set-Content -LiteralPath (Join-Path $Out ("worn-seconds-" + $view.name + ".txt")) -Value $ls -Encoding utf8
            $most = 0; foreach ($l in $ls) { $m = [regex]::Match($l, "worn_drawn=(\d+)"); if ($m.Success -and [int]$m.Groups[1].Value -gt $most) { $most = [int]$m.Groups[1].Value } }
            # a gap is a second in which a region the viewer is in touch with HAS the avatar (pos is known) and the avatar is not drawn, or fewer pieces are drawn than the most this view ever had;
            # a second with no region holding the avatar (pos=none: out of every circuit's range, as the official viewer would not show it either) is not one
            $bad = @($ls | Where-Object { $_ -notmatch "pos=none" -and ($_ -match "avatar_drawn=0" -or ([regex]::Match($_, "worn_drawn=(\d+)").Success -and [int][regex]::Match($_, "worn_drawn=(\d+)").Groups[1].Value -lt $most)) })
            $secAll += $ls.Count; $secBad += $bad.Count
            Note ("worn_seconds_" + $view.name) @{ seconds = $ls.Count; most_pieces_drawn = $most; seconds_with_a_gap = $bad.Count; first_gaps = @($bad | Select-Object -First 5 | ForEach-Object { ($_ -replace "^.*worn\.second\s+", "").Substring(0, [math]::Min(300, ($_ -replace "^.*worn\.second\s+", "").Length)) }) }
            if ($ls.Count -lt 5 -or $most -lt 1) { $secBad += 1 }   # a trace that saw nothing proves nothing
        }
        if ($secBad -gt 0) { $ok = $false }
    }
    Start-Sleep -Seconds 3
    Note "presence_after_logout" @{ walker = (Presence $walkerId); observer = (Presence $observerId) }
    Note "pass" ([bool]$ok)
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Out "crossing-live.json") -Encoding utf8
}
exit ($(if ($ok) { 0 } else { 1 }))
