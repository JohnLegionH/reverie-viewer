# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# avatar-live.ps1 - the live look at another avatar's outfit. Two development viewers log in: the observed test avatar (-Observed, default
# truly) stands where it logs in, and the observer (-Account, default viewer1) logs in at the same start location. The observer's
# avatar_probe lists the observed avatar's bakes, hidden body slots and worn pieces with their face textures (as sent and as drawn); the
# observer's camera is then put at -Distances metres from it on each side (the aim command, tools\aim-lib.ps1: the camera is aimed at the
# avatar's -Point, whatever the observer's own avatar does) and a picture is taken; each picture is judged by tools\aim-check.ps1 (the
# avatar must fill enough of the frame and sit in the middle).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\avatar-live.ps1 -Sandbox <new folder> -OutDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-Observed <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>]
#     [-Exe <development export>] [-Distances "3,15"] [-Sides "front,back,side"] [-Tag <name prefix>] [-GroundAt "x,y,z"] [-ProbeOnly]
#     [-Point head|chest|body|feet] [-ViewDistance 3] [-FovDeg 40] [-AboveDeg 0] [-MinFill 0] [-Shots "head:1.5:front:40,body:3:front,body:3:side"] [-ObserverGap 3]
#     [-ObservedOutFirst [-WatchLogoutSeconds 15]]
#   -ObservedOutFirst: at the end the observed avatar logs out first while the observer keeps watching (run the observer with NV_WORN_FRAMES=1 to
#   log the removal of each of the observed avatar's pieces and of its body).
#   -Shots lists the pictures as point:metres:side (side = front, back or side) in place of -Sides and -Distances; -ObserverGap puts the
#   observer's own avatar that many metres from the observed one before the pictures (the camera does not depend on where it stands).
#
# Settings come from the parameters or the environment as in look-live.ps1. Run it under a live-login claim and an avatar claim on each
# account. Output: <Tag>probe.json, the pictures and their <name>.aim.json reports in -OutDir, a run log in the sandbox.
# Exit: 0 done, 3 blocked, 5 a viewer or login did not come up, 6 a picture failed the framing check (-MinFill 0 asks for 0.5 of the frame
# up to 6 m and 0.12 further away; a head asks for 0.25 up to 6 m).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = "viewer1",
    [string]$Observed = "truly",
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [string]$Distances = "3,15",
    [string]$Sides = "front,back,side",
    [string]$Tag = "",
    [string]$SkyChoice = "midday",   # the time-of-day choice the pictures are taken under (as the sky command takes it, e.g. "legacy noon")
    [int]$StartSeconds = 60,
    [string]$GroundAt = "",
    [switch]$Late,
    [switch]$Sequence,
    [switch]$Weights,
    [switch]$Defaults,
    [int]$StandSeconds = 20,
    [int]$ProbeMs = 250,
    [int]$HoverSeconds = 8,
    [int]$SequenceAt = 12,
    [switch]$Rebake,
    [int]$RebakeSeconds = 30,
    [double]$CameraOffsetDeg = 25,   # the angle around the avatar, from the way it faces, the sequence pictures are taken from
    [string]$Point = "body",   # the part of the avatar the camera looks at: head, chest or body
    [double]$ViewDistance = 3,   # the distance of the sequence pictures
    [double]$FovDeg = 40,
    [double]$AboveDeg = 0,
    [double]$MinFill = 0,
    [string]$Shots = "",
    [double]$ObserverGap = 0,
    [int]$ForSeconds = 120,
    [int]$EverySeconds = 5,
    [switch]$MoveObserved,
    [switch]$ProbeOnly,
    [switch]$ObservedOutFirst,   # at the end the observed avatar logs out first and the observer keeps watching -WatchLogoutSeconds before it logs out
    [ValidateRange(0, 300)][int]$WatchLogoutSeconds = 15,
    [string]$SaveBake = "",   # a bake slot (e.g. head): the final avatar_probe also saves the picture the observer holds of the observed avatar's bake in that slot, under its log folder
    [string]$DryRunDir = ""   # a folder: the observed viewer also bakes its worn outfit here (NV_BAKE_DRYRUN_DIR) and writes each bake as a picture there, uploading and sending nothing
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
. (Join-Path $PSScriptRoot "aim-lib.ps1")
$script:aimFailures = @()
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }
$script:procs = @()

function Invoke-Raw([int]$port, [string]$command, [string]$token, [string]$body = "{}") {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $port)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($body)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: 127.0.0.1:$port`r`nAuthorization: Bearer $token`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $r = New-Object System.IO.StreamReader($s)
        $all = $r.ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function Parse([string]$json) { try { return $json | ConvertFrom-Json } catch { return $null } }
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note([string]$what) { Write-Output ("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }

# the observer's avatar_probe of the first other avatar, with the time it was read
function Read-Avatar($c) {
    $raw = (Invoke-Raw $c.port "avatar_probe" $c.token).Body
    $pr = Parse $raw
    if ($pr -and @($pr.avatars).Count -ge 1) { return @($pr.avatars)[0] }
    return $null
}
# aims the observer's camera at the avatar (the aim command) and takes a picture that must pass the framing check; the avatar is found by
# the name the observer's probe gave. -around is measured from the way the avatar faces: 0 in front of it, 90 on its left, 180 behind it.
function Shoot-Avatar($c, $a0, [string]$name, [double]$distance = -1, [double]$around = [double]::NaN, [string]$point = "", [double]$fov = 0) {
    if ($fov -le 0) { $fov = $FovDeg }
    if (-not $point) { $point = $Point }
    if ($distance -le 0) { $distance = $ViewDistance }
    if ([double]::IsNaN($around)) { $around = $CameraOffsetDeg }
    $fill = if ($MinFill -gt 0) { $MinFill } elseif ($distance -le 6) { $(if ($point -eq "head" -or $point -eq "feet") { 0.25 } else { 0.5 }) } else { 0.12 }
    $spec = @{ name = [string]$a0.name; point = $point; distance = $distance; around_deg = $around; above_deg = $AboveDeg; fov_deg = $fov; min_fill = $fill }
    $shot = Shoot-Aimed -Port $c.port -Token $c.token -Dir $OutDir -Name $name -Spec $spec
    Write-Host ("{0,7:N1} picture {1}: {2}" -f $clock.Elapsed.TotalSeconds, $name, $shot.Detail)
    if (-not $shot.Pass) { $script:aimFailures += $name }
    return $shot
}
$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) { $problems.Add("-OutDir must be an existing folder") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$regionName = if ($Start -match 'uri:([^&]+)') { [uri]::UnescapeDataString($Matches[1]) } else { "" }

function Start-Client([string]$alias, [string]$dir) {
    $appdata = Join-Path $dir "appdata"; $local = Join-Path $dir "localappdata"; $log = Join-Path $dir "run.log"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $argLine = "--rendering-driver vulkan -- --automation"
    $env:NV_LOG_DIR = Join-Path $dir "viewer-logs"
    $env:NV_BAKE_DRYRUN_DIR = if ($DryRunDir -and $alias -eq $Observed) { $DryRunDir } else { $null }
    $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$dir`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
    $script:procs += $p
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = $null
    for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
    if (-not $tf) { Note "$alias : no automation token file"; return $null }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    $c = @{ alias = $alias; port = [int]$j.port; token = [string]$j.token }
    $r = Invoke-Raw $c.port "session" $c.token (@{ action = "login"; accounts_file = $Credentials; account = $alias; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
    Note "$alias : login requested, $($r.Status)"
    return $c
}
function Wait-World($c) {
    $stable = 0; $last = -1
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $o = Parse (Invoke-Raw $c.port "observe" $c.token).Body
        if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
            if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
            $last = $o.world.objects
            if ($stable -ge 3) { return $o }
        }
    }
    return $null
}
function Stop-Client($c) {
    if (-not $c) { return }
    try { [void](Invoke-Raw $c.port "session" $c.token '{"action":"logout"}'); Start-Sleep -Seconds 3; [void](Invoke-Raw $c.port "quit" $c.token) } catch { }
}

$exit = 0
$T = $null; $O = $null
try {
    Write-Output "LIVE AVATAR RUN STARTING - no grid restarts, measuring, or other clients on accounts $Account and $Observed until it is DONE"
    if ($Sequence) {
        # the sequence of a person who is already in the world when the observed avatar logs in: the observer logs in first, the observed avatar then logs in,
        # is moved -SequenceAt metres away at the observer's height and flies, and the observer's avatar_probe is read every -EverySeconds for -ForSeconds
        $O = Start-Client $Account (Join-Path $Sandbox "observer")
        if (-not $O) { throw "observer viewer did not start" }
        if (-not (Wait-World $O)) { throw "observer did not reach the world" }
        Note "$Account in world"
        [void](Invoke-Raw $O.port "sky" $O.token (@{ choice = $SkyChoice } | ConvertTo-Json -Compress))   # the pictures are compared by day
        $T0 = Get-Date
        $T = Start-Client $Observed (Join-Path $Sandbox "observed")
        if (-not $T) { throw "observed viewer did not start" }
        if (-not (Wait-World $T)) { throw "observed avatar did not reach the world" }
        Note "$Observed in world"
        $ob = Parse (Invoke-Raw $O.port "observe" $O.token).Body
        $ox = [double]$ob.self.region_position[0]; $oy = [double]$ob.self.region_position[1]; $oz = [double]$ob.self.region_position[2]
        if ($SequenceAt -gt 0) {
            [void](Invoke-Raw $T.port "teleport" $T.token (@{ action = "region"; region = $regionName; x = ($ox + $SequenceAt); y = $oy; z = ($oz + 1) } | ConvertTo-Json -Compress))
            Start-Sleep -Seconds 8
        }
        if ($Defaults) {
            # what the observed avatar's built-in motions give, as the observer sees it, read every -ProbeMs: standing for -StandSeconds (chest, neck, head, eye joints,
            # blink weights, hand pose), a picture of it at -SequenceAt metres, then moved to 3 m for a second picture; then it flies forward, turns left, turns right,
            # hovers and lands (the pelvis turn and the flying roll). Pictures and <Tag>defaults.json go to -OutDir.
            $drows = New-Object System.Collections.Generic.List[object]
            $dclock = [System.Diagnostics.Stopwatch]::StartNew()
            function Probe-Defaults([double]$seconds, [string]$phase) {
                $end = $dclock.Elapsed.TotalSeconds + $seconds
                while ($dclock.Elapsed.TotalSeconds -lt $end) {
                    $a = Read-Avatar $O
                    $drows.Add([ordered]@{ t = [math]::Round($dclock.Elapsed.TotalSeconds, 3); phase = $phase; position = $(if ($a) { $a.position } else { $null }); playing = $(if ($a) { @($a.presenter.playing | ForEach-Object { $_.name }) } else { $null }); defaults = $(if ($a) { $a.presenter.defaults } else { $null }) })
                    Start-Sleep -Milliseconds $ProbeMs
                }
            }
            function Hold([string]$key, [double]$seconds, [string]$phase) {
                [void](Invoke-Raw $T.port "keys" $T.token (@{ key = $key; down = $true } | ConvertTo-Json -Compress)); Probe-Defaults $seconds $phase
                [void](Invoke-Raw $T.port "keys" $T.token (@{ key = $key; down = $false } | ConvertTo-Json -Compress))
            }
            Probe-Defaults $StandSeconds "stand"
            $a0 = Read-Avatar $O
            if ($a0) { Shoot-Avatar $O $a0 "${Tag}stand-$($SequenceAt)m" }
            [void](Invoke-Raw $T.port "teleport" $T.token (@{ action = "region"; region = $regionName; x = ($ox + 3); y = $oy; z = ($oz + 1) } | ConvertTo-Json -Compress))
            Start-Sleep -Seconds 6
            $a1 = Read-Avatar $O
            if ($a1) { Shoot-Avatar $O $a1 "${Tag}stand-3m" }
            Hold "HOME" 0.3 "fly-toggle"
            Hold "PGUP" 1.1 "up"
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = "UP"; down = $true } | ConvertTo-Json -Compress))
            Probe-Defaults 2 "forward"
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = "LEFT"; down = $true } | ConvertTo-Json -Compress))
            Probe-Defaults 1 "left-turn"
            $a2 = Read-Avatar $O
            if ($a2) { Shoot-Avatar $O $a2 "${Tag}fly-turn" }
            Probe-Defaults 1 "left-turn"
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = "LEFT"; down = $false } | ConvertTo-Json -Compress))
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = "RIGHT"; down = $true } | ConvertTo-Json -Compress))
            Probe-Defaults 2 "right-turn"
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = "RIGHT"; down = $false } | ConvertTo-Json -Compress))
            Probe-Defaults 1 "straight"
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = "UP"; down = $false } | ConvertTo-Json -Compress))
            Probe-Defaults 2 "hover"
            Hold "PGDN" 2.5 "down"
            Probe-Defaults 5 "landed"
            ($drows | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}defaults.json")
            Note ("defaults timeline: {0} rows" -f $drows.Count)
            return
        }
        if ($Weights) {
            # the animation weights of the observed avatar as the observer sees them, read every -ProbeMs through: standing, taking off (fly, up),
            # hovering for -HoverSeconds, landing (down) and standing again. Pictures of it hovering, at 12 m and at 3 m, go to -OutDir.
            $wrows = New-Object System.Collections.Generic.List[object]
            $wclock = [System.Diagnostics.Stopwatch]::StartNew()
            function Probe-For([double]$seconds, [string]$phase) {
                $end = $wclock.Elapsed.TotalSeconds + $seconds
                while ($wclock.Elapsed.TotalSeconds -lt $end) {
                    $a = Read-Avatar $O
                    $wrows.Add([ordered]@{ t = [math]::Round($wclock.Elapsed.TotalSeconds, 3); phase = $phase; weights = $(if ($a) { $a.presenter.weights } else { $null }); mixed = $(if ($a) { $a.presenter.mixed } else { $null }); ready = $(if ($a) { @($a.presenter.playing | ForEach-Object { $_.ready }) } else { $null }); drawn = $(if ($a) { $a.presenter.drives } else { $null }); playing = $(if ($a) { @($a.presenter.playing | ForEach-Object { $_.name }) } else { $null }) })
                    Start-Sleep -Milliseconds $ProbeMs
                }
            }
            function Press([string]$key, [int]$ms, [string]$phase) {
                [void](Invoke-Raw $T.port "keys" $T.token (@{ key = $key; down = $true } | ConvertTo-Json -Compress)); Probe-For ($ms / 1000.0) $phase
                [void](Invoke-Raw $T.port "keys" $T.token (@{ key = $key; down = $false } | ConvertTo-Json -Compress))
            }
            Probe-For 3 "stand"
            Press "HOME" 300 "fly-toggle"
            Press "PGUP" 1100 "up"
            Probe-For 3 "hover"
            $a0 = Read-Avatar $O
            if ($a0) { Shoot-Avatar $O $a0 "${Tag}hover" }
            Probe-For ($HoverSeconds - 3) "hover"
            Press "PGDN" 2500 "down"
            Probe-For 5 "landed"
            ($wrows | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}weights.json")
            Note ("weights timeline: {0} rows" -f $wrows.Count)
            return
        }
        foreach ($k in @(@("HOME", 300), @("PGUP", 1100))) {
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = $k[0]; down = $true } | ConvertTo-Json -Compress)); Start-Sleep -Milliseconds $k[1]
            [void](Invoke-Raw $T.port "keys" $T.token (@{ key = $k[0]; down = $false } | ConvertTo-Json -Compress)); Start-Sleep -Milliseconds 300
        }
        $tb = Parse (Invoke-Raw $T.port "observe" $T.token).Body
        Note ("observed avatar at {0:N1},{1:N1},{2:N1}; observer at {3:N1},{4:N1},{5:N1}" -f $tb.self.region_position[0], $tb.self.region_position[1], $tb.self.region_position[2], $ox, $oy, $oz)
        $t0 = Get-Date; $n = 0
        $rows = New-Object System.Collections.Generic.List[object]
        while (((Get-Date) - $t0).TotalSeconds -lt $ForSeconds) {
            $sec = [int]((Get-Date) - $t0).TotalSeconds
            $raw = (Invoke-Raw $O.port "avatar_probe" $O.token).Body
            $pr = Parse $raw
            $a0 = if ($pr -and @($pr.avatars).Count -ge 1) { @($pr.avatars)[0] } else { $null }
            $rows.Add([ordered]@{ seconds = $sec; probe = $a0 })
            if ($a0) {
                $pz = $a0.presenter
                Note ("+{0}s appearances={1} bakes_in_slots={2} hidden={3} worn={4} clip={5} playing={6}" -f $sec, @($a0.appearances).Count, @($pz.slots | Where-Object { $_.on_gpu }).Count, ($a0.hidden_body_slots -join '+'), ($pz.worn -join '+'), $pz.clip, ((@($pz.playing) | ForEach-Object { "$($_.name)$(if ($_.ready) { '' } else { '?' })" }) -join ','))
            } else { Note "+${sec}s other avatar not seen" }
            if ($a0 -and ($n % 3 -eq 0)) {
                [void](Shoot-Avatar $O $a0 ("${Tag}seq-{0:D3}" -f $sec))
            }
            $n++
            Start-Sleep -Seconds $EverySeconds
        }
        ($rows | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}sequence.json")
        if ($Rebake) {
            # the observed avatar takes one worn piece off and puts it back (the grid rebakes it each time); the observer is probed for -RebakeSeconds after each
            # change. Needs NV_OUTFIT_SCRATCH_OK=1 in the environment (a scratch test account only).
            [void](Invoke-Raw $T.port "inventory" $T.token '{"action":"open"}'); Start-Sleep -Seconds 6
            $of = Parse (Invoke-Raw $T.port "inventory" $T.token '{"action":"outfit"}').Body
            $targets = @($of.targets)
            Note ("observed avatar wears {0} items; cof loaded {1}" -f $targets.Count, $of.cof_loaded)
            $rb = New-Object System.Collections.Generic.List[object]
            if ($targets.Count -gt 0) {
                $pick = $targets[$targets.Count - 1]
                foreach ($step in @(@("takeoff", "off"), @("add", "back"))) {
                    $r = Parse (Invoke-Raw $T.port "inventory" $T.token (@{ action = $step[0]; id = $pick } | ConvertTo-Json -Compress)).Body
                    Note ("{0}: submitted={1} code={2}" -f $step[0], $r.submitted, $r.code)
                    $t1 = Get-Date
                    while (((Get-Date) - $t1).TotalSeconds -lt $RebakeSeconds) {
                        $pr = Parse (Invoke-Raw $O.port "avatar_probe" $O.token).Body
                        $a1 = if ($pr -and @($pr.avatars).Count -ge 1) { @($pr.avatars)[0] } else { $null }
                        $sec = [int]((Get-Date) - $t1).TotalSeconds
                        $rb.Add([ordered]@{ step = $step[1]; seconds = $sec; probe = $a1 })
                        if ($a1) { Note ("  {0} +{1}s appearances={2} hidden={3} bakes={4} pieces={5}" -f $step[1], $sec, @($a1.appearances).Count, ($a1.hidden_body_slots -join '+'), @($a1.bakes).Count, @($a1.pieces).Count) }
                        Start-Sleep -Seconds $EverySeconds
                    }
                    $seenNow = Read-Avatar $O
                    if ($seenNow) { [void](Shoot-Avatar $O $seenNow ("${Tag}rebake-" + $step[1])) } else { Note ("rebake-" + $step[1] + ": the avatar is not seen"); $script:aimFailures += ("${Tag}rebake-" + $step[1]) }
                }
            }
            ($rb | ConvertTo-Json -Depth 12) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}rebake.json")
        }
        return
    }
    if ($Late) {
        # the observer is in the world first; the observed avatar then arrives (a login, then a teleport onto the ground), and the observer's view of it is
        # recorded at intervals from the moment its first piece is listed: the sequence of what is drawn while its outfit and bakes arrive
        $O = Start-Client $Account (Join-Path $Sandbox "observer")
        if (-not $O) { throw "observer viewer did not start" }
        if (-not (Wait-World $O)) { throw "observer did not reach the world" }
        Note "$Account in world"
        $T = Start-Client $Observed (Join-Path $Sandbox "observed")
        if (-not $T) { throw "observed viewer did not start" }
        if (-not (Wait-World $T)) { throw "observed avatar did not reach the world" }
        Note "$Observed in world"
        if ($GroundAt) {
            $g = $GroundAt -split ','
            [void](Invoke-Raw $T.port "teleport" $T.token (@{ action = "region"; region = $regionName; x = [double]$g[0]; y = [double]$g[1]; z = [double]$g[2] } | ConvertTo-Json -Compress))
        }
        $t0 = Get-Date
        $series = @()
        for ($i = 0; $i -lt 12; $i++) {
            $pr = Parse (Invoke-Raw $O.port "avatar_probe" $O.token).Body
            $a0 = if ($pr -and @($pr.avatars).Count -ge 1) { @($pr.avatars)[0] } else { $null }
            $sec = [int]((Get-Date) - $t0).TotalSeconds
            $row = [ordered]@{ seconds = $sec; seen = [bool]$a0; bakes = $(if ($a0) { @($a0.bakes).Count } else { 0 }); pieces = $(if ($a0) { @($a0.pieces).Count } else { 0 }); body_drawn = $(if ($a0) { $a0.body_drawn } else { $false }); hidden = $(if ($a0) { ($a0.hidden_body_slots -join '+') } else { "" }) }
            $series += $row
            Note ("late +{0}s: seen={1} bakes={2} pieces={3} body={4} hidden={5}" -f $row.seconds, $row.seen, $row.bakes, $row.pieces, $row.body_drawn, $row.hidden)
            if ($a0 -and $i -ge 1) {
                [void](Shoot-Avatar $O $a0 ("${Tag}late-{0:D2}" -f $i))
            }
            Start-Sleep -Seconds 4
        }
        $series | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}late-series.json")
        return
    }
    $T = Start-Client $Observed (Join-Path $Sandbox "observed")
    if (-not $T) { throw "observed viewer did not start" }
    $tw = Wait-World $T
    if (-not $tw) { throw "observed avatar did not reach the world" }
    Note "$Observed in world"
    if ($GroundAt) {
        # the observed avatar logs in where it was last (it may hover well above the ground): stand it on the ground at -GroundAt "x,y,z" so the observer can look at it level
        $g = $GroundAt -split ','
        [void](Invoke-Raw $T.port "teleport" $T.token (@{ action = "region"; region = $regionName; x = [double]$g[0]; y = [double]$g[1]; z = [double]$g[2] } | ConvertTo-Json -Compress))
        for ($i = 0; $i -lt 30; $i++) {
            Start-Sleep -Seconds 2
            $ob = Parse (Invoke-Raw $T.port "observe" $T.token).Body
            if ($ob -and $ob.self -and $ob.self.region_position -and [math]::Abs($ob.self.region_position[0] - [double]$g[0]) -lt 2 -and [math]::Abs($ob.self.region_position[1] - [double]$g[1]) -lt 2) { break }
        }
        Note ("observed avatar now at {0:N1},{1:N1},{2:N1}" -f $ob.self.region_position[0], $ob.self.region_position[1], $ob.self.region_position[2])
        Start-Sleep -Seconds 5
    }
    $O = Start-Client $Account (Join-Path $Sandbox "observer")
    if (-not $O) { throw "observer viewer did not start" }
    $ow = Wait-World $O
    if (-not $ow) { throw "observer did not reach the world" }
    Note "$Account in world"

    if ($SkyChoice -ne "midday") { [void](Invoke-Raw $O.port "sky" $O.token (@{ choice = $SkyChoice } | ConvertTo-Json -Compress)) }   # a time of day other than the default one
    # wait for the observed avatar's outfit to arrive: a worn piece listed, up to 90 s
    $probe = $null
    for ($i = 0; $i -lt 45; $i++) {
        $probe = Parse (Invoke-Raw $O.port "avatar_probe" $O.token).Body
        if ($probe -and @($probe.avatars).Count -ge 1 -and @(@($probe.avatars)[0].pieces).Count -ge 1) { break }
        Start-Sleep -Seconds 2
    }
    Start-Sleep -Seconds 15   # textures and bakes settle
    if ($ObserverGap -gt 0) {
        # the observer's own avatar goes to -ObserverGap metres from the observed one (east of it)
        $tp = (Parse (Invoke-Raw $T.port "observe" $T.token).Body).self.region_position
        [void](Invoke-Raw $O.port "teleport" $O.token (@{ action = "region"; region = $regionName; x = ([double]$tp[0] + $ObserverGap); y = [double]$tp[1]; z = [double]$tp[2] } | ConvertTo-Json -Compress))
        $gap = $null
        for ($i = 0; $i -lt 20; $i++) {
            Start-Sleep -Seconds 2
            $q = (Parse (Invoke-Raw $O.port "observe" $O.token).Body).self.region_position
            if ($q) { $gap = [math]::Sqrt([math]::Pow($q[0] - $tp[0], 2) + [math]::Pow($q[1] - $tp[1], 2)); if ([math]::Abs($gap - $ObserverGap) -lt 1.0) { break } }
        }
        Note ("observer stands {0:N1} m from the observed avatar" -f $gap)
        Start-Sleep -Seconds 5
    }
    $probe = Parse (Invoke-Raw $O.port "avatar_probe" $O.token).Body
    $json = (Invoke-Raw $O.port "avatar_probe" $O.token $(if ($SaveBake) { (@{ save = $SaveBake } | ConvertTo-Json -Compress) } else { "{}" })).Body
    [System.IO.File]::WriteAllText((Join-Path $OutDir "${Tag}probe.json"), $json)
    $found = if ($probe) { @($probe.avatars).Count } else { 0 }
    Note "probe: $found other avatar(s) seen"
    if ($found -lt 1) { throw "the observer sees no other avatar" }
    $aroundOf = @{ front = 0; back = 180; side = 90 }
    if ($MoveObserved -and -not $ProbeOnly) {
        # the observer stays where it logged in; the observed avatar is teleported to each place in turn, so both stand at the same height
        $a = @($probe.avatars)[0]
        $yaw = [double]$a.yaw_deg
        $ob = Parse (Invoke-Raw $O.port "observe" $O.token).Body
        $ox = [double]$ob.self.region_position[0]; $oy = [double]$ob.self.region_position[1]; $oz = [double]$ob.self.region_position[2]
        Note ("observer at {0:N1},{1:N1},{2:N1}; observed facing {3:N0} deg" -f $ox, $oy, $oz, $yaw)
        foreach ($side in ($Sides -split ',')) {
            # front: the observer is in the direction the observed avatar faces; back: behind it; side: to its left
            $dir = switch ($side) { "front" { $yaw } "back" { $yaw + 180 } default { $yaw + 90 } }
            foreach ($d in ($Distances -split ',')) {
                $dist = [double]$d
                $x = $ox - $dist * [math]::Cos($dir * [math]::PI / 180); $y = $oy - $dist * [math]::Sin($dir * [math]::PI / 180)
                $ok = $false
                for ($try = 0; $try -lt 2 -and -not $ok; $try++) {
                    [void](Invoke-Raw $T.port "teleport" $T.token (@{ action = "region"; region = $regionName; x = $x; y = $y; z = $oz } | ConvertTo-Json -Compress))
                    for ($i = 0; $i -lt 20; $i++) {
                        Start-Sleep -Seconds 2
                        $tb = Parse (Invoke-Raw $T.port "observe" $T.token).Body
                        if ($tb -and $tb.self -and $tb.self.region_position) {
                            $p = $tb.self.region_position
                            if ([math]::Sqrt([math]::Pow($p[0] - $x, 2) + [math]::Pow($p[1] - $y, 2)) -lt 1.5) { $ok = $true; break }
                        }
                    }
                }
                Note ("{0} {1} m: observed avatar {2} at {3:N1},{4:N1}" -f $side, $dist, $(if ($ok) { "arrived" } else { "did not arrive" }), $x, $y)
                if ($ok) {
                    Start-Sleep -Seconds 10
                    $pr = Parse (Invoke-Raw $O.port "avatar_probe" $O.token).Body
                    $a1 = @($pr.avatars)[0]
                    Note ("  seen at {0:N1},{1:N1},{2:N1} facing {3:N0}; hidden body slots: {4}; body drawn: {5}" -f $a1.position[0], $a1.position[1], $a1.position[2], $a1.yaw_deg, ($a1.hidden_body_slots -join '+'), $a1.body_drawn)
                    [void](Shoot-Avatar $O $a1 ("${Tag}${side}-${d}m") $dist ([double]$aroundOf[$side.Trim()]))
                }
            }
        }
    } elseif (-not $ProbeOnly) {
        # the camera goes to each side of the avatar at each distance (or to the places -Shots lists); the observer's own avatar stays where it is
        $plan = @()
        if ($Shots) { foreach ($e in ($Shots -split ',')) { $q = $e.Trim() -split ':'; $plan += ,@($q[0], [double]$q[1], $q[2], $(if ($q.Count -ge 4) { [double]$q[3] } else { 0.0 })) } }
        else { foreach ($side in ($Sides -split ',')) { foreach ($d in ($Distances -split ',')) { $plan += ,@($Point, [double]$d, $side.Trim(), 0.0) } } }
        foreach ($e in $plan) {
            $label = if ($Shots) { "{0}${Tag}{1}-{2}-{3}m" -f "", $e[0], $e[2], ([string]$e[1] -replace '\.', 'p') } else { "${Tag}$($e[2])-$([string]$e[1] -replace '\.', 'p')m" }
            $a1 = Read-Avatar $O
            if (-not $a1) { Note "${label}: the avatar is not seen"; $script:aimFailures += $label; continue }
            [void](Shoot-Avatar $O $a1 $label ([double]$e[1]) ([double]$aroundOf[[string]$e[2]]) ([string]$e[0]) ([double]$e[3]))
            if ($SaveBake) { [void](Invoke-Raw $O.port "avatar_probe" $O.token (@{ save = $SaveBake } | ConvertTo-Json -Compress)) }   # the bake as the engine holds it now (at the level this distance gave it)
        }
    }
} catch {
    Write-Output ("ERROR " + $_.Exception.Message); $exit = 5
} finally {
    if ($ObservedOutFirst) {
        # the observed avatar logs out while the observer still watches (its log then holds the removals of the observed avatar's pieces and body)
        Stop-Client $T; Start-Sleep -Seconds $WatchLogoutSeconds; Stop-Client $O
    } else { Stop-Client $O; Stop-Client $T }
    Start-Sleep -Seconds 5
    foreach ($p in $script:procs) { try { if (-not $p.HasExited) { Wait-Process -Id $p.Id -Timeout 30 -ErrorAction SilentlyContinue } } catch { } }
    Write-Output "LIVE AVATAR RUN DONE - other work may run again"
}
if ($exit -eq 0 -and $script:aimFailures.Count -gt 0) { Write-Output ("FRAMING FAILED for: " + ($script:aimFailures -join ", ")); $exit = 6 }
exit $exit
