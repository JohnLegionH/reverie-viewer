# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# ao-live.ps1 - the live check of the animation overrider. Two development viewers log in: the walker (-Account, default viewer1)
# with the overrider, and an observer (-Observer, default truly) that stands a few metres away. The walker's inventory gets a small
# animation set laid out the way the Firestorm viewer keeps sets (two cycling stands, a walk and a fly, linked to the first four
# animations its inventory holds; an existing set of that name is used as it is). The walker then turns the overrider on, stands
# for -StandSeconds, walks -Metres, flies -Metres, lands and turns the overrider off, while the observer's viewer is asked every
# -ProbeMs what it has the walker playing.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\ao-live.ps1 -Sandbox <new folder> -OutDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-Observer <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>]
#     [-Exe <development export>] [-SetName <name>] [-CycleSeconds 10] [-StandSeconds 30] [-Metres 10] [-Tag <name prefix>]
#
# Pass: while standing the observer sees one of the set's stands at a time and both of them over the stand, changing at least
# twice, about every -CycleSeconds; it sees the set's walk while the walker walks and the set's fly while it flies forward; and after the overrider
# is turned off it sees none of the set's animations. Settings come from the parameters or the environment as in look-live.ps1.
# Run it under a live-login claim and an avatar claim on each account. Output: <Tag>timeline.json, <Tag>verdict.json and pictures
# in -OutDir (each taken with the observer's camera aimed at the walker, tools\aim-lib.ps1, and judged by tools\aim-check.ps1), a run log
# in the sandbox. Exit: 0 pass, 1 a check failed, 3 blocked, 5 a viewer or login did not come up, 6 the animations passed but a picture
# failed the framing check.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = "viewer1",
    [string]$Observer = "truly",
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [string]$SetName = "Overrider Check",
    [double]$CycleSeconds = 10,
    [int]$StandSeconds = 30,
    [double]$Metres = 10,
    [int]$ProbeMs = 300,
    [int]$StartSeconds = 60,
    [string]$Tag = ""
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
function Call($c, [string]$command, $body = $null) {
    $text = if ($null -eq $body) { "{}" } else { $body | ConvertTo-Json -Compress }
    return Parse (Invoke-Raw $c.port $command $c.token $text).Body
}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note([string]$what) { Write-Output ("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }

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
        $o = Call $c "observe"
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
function Short([string]$id) { return ($id -replace "-", "").Substring(0, 8).ToLowerInvariant() }
function Position($c) { $o = Call $c "observe"; if ($o -and $o.self -and $o.self.region_position) { return @([double]$o.self.region_position[0], [double]$o.self.region_position[1], [double]$o.self.region_position[2]) }; return $null }
function Flat($a, $b) { if (-not $a -or -not $b) { return 0.0 }; return [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }

$exit = 0
$W = $null; $O = $null
$rows = New-Object System.Collections.Generic.List[object]
$run = [System.Diagnostics.Stopwatch]::StartNew()
# one reading: what the observer's viewer has the walker playing, and what the walker's own overrider says
function Probe([string]$phase) {
    $pr = Call $O "avatar_probe"
    $a = if ($pr -and @($pr.avatars).Count -ge 1) { @($pr.avatars)[0] } else { $null }
    $own = Call $W "overrider" @{ action = "state" }
    $ownPlaying = @()
    if ($own -and $own.states) { foreach ($s in @($own.states)) { foreach ($id in @($s.playing)) { $ownPlaying += (Short ([string]$id)) } } }
    $rows.Add([ordered]@{
        t = [math]::Round($run.Elapsed.TotalSeconds, 2); phase = $phase
        seen = $(if ($a) { @($a.presenter.playing | ForEach-Object { [string]$_.id }) } else { @() })
        seen_names = $(if ($a) { @($a.presenter.playing | Where-Object { $_.name } | ForEach-Object { [string]$_.name }) } else { @() })
        others = $(if ($pr) { @($pr.avatars).Count } else { 0 })
        own_state = $(if ($own) { [string]$own.state } else { "" }); own_enabled = $(if ($own) { [bool]$own.enabled } else { $false }); own_playing = $ownPlaying
    })
}
function Probe-For([double]$seconds, [string]$phase) {
    $end = $run.Elapsed.TotalSeconds + $seconds
    while ($run.Elapsed.TotalSeconds -lt $end) { Probe $phase; Start-Sleep -Milliseconds $ProbeMs }
}
function Key([string]$key, [bool]$down) { [void](Invoke-Raw $W.port "keys" $W.token (@{ key = $key; down = $down } | ConvertTo-Json -Compress)) }
# holds a key until the walker has moved -Metres over the ground (or the time runs out); answers the metres moved
function Travel([string]$key, [string]$phase, [double]$limitSeconds) {
    $from = Position $W
    Key $key $true
    $end = $run.Elapsed.TotalSeconds + $limitSeconds
    $moved = 0.0
    while ($run.Elapsed.TotalSeconds -lt $end -and $moved -lt $Metres) { Probe $phase; $moved = Flat $from (Position $W) }
    Key $key $false
    return $moved
}
# a picture of the walker as the observer sees it: the observer's camera is aimed at the walker's whole body from 3 m, a little off the front
function Shot([string]$name) {
    $pr = Call $O "avatar_probe"
    $a = if ($pr -and @($pr.avatars).Count -ge 1) { @($pr.avatars)[0] } else { $null }
    if (-not $a) { Write-Host "picture ${Tag}${name}: the walker is not seen"; $script:aimFailures += "${Tag}${name}"; return }
    $shot = Shoot-Aimed -Port $O.port -Token $O.token -Dir $OutDir -Name "${Tag}$name" -Spec @{ name = [string]$a.name; point = "body"; distance = 3; around_deg = 20; fov_deg = 40 } -SettleMs 300
    Write-Host ("picture {0}{1}: {2}" -f $Tag, $name, $shot.Detail)
    if (-not $shot.Pass) { $script:aimFailures += "${Tag}${name}" }
}

try {
    Write-Output "LIVE OVERRIDER RUN STARTING - no grid restarts, measuring, or other clients on accounts $Account and $Observer until it is DONE"
    $W = Start-Client $Account (Join-Path $Sandbox "walker")
    if (-not $W) { throw "the walker's viewer did not start" }
    if (-not (Wait-World $W)) { throw "the walker did not reach the world" }
    Note "$Account in world"

    # the set: made once, used as it is when it is already there; the inventory is fetched in the background, so the
    # animations to link to may take a while to be known
    $made = $null
    for ($i = 0; $i -lt 60; $i++) {
        $made = Call $W "overrider" @{ action = "make_set"; text = $SetName; seconds = $CycleSeconds }
        if ($made -and $null -ne $made.made -and ($made.made -or $made.reason -ne "animations")) { break }
        Start-Sleep -Seconds 3
    }
    if (-not $made -or $null -eq $made.made) { throw ("the set could not be asked for: " + (Invoke-Raw $W.port "overrider" $W.token '{"action":"state"}').Body) }
    Note ("set: made {0}, reason {1}, animations found {2}, linked {3}" -f $made.made, $made.reason, $made.found, $made.linked)
    if (-not $made.made -and $made.reason -ne "exists") { throw "the set was not made ($($made.reason))" }

    # read the sets again and pick the set that has two stands, a walk and a fly
    [void](Call $W "overrider" @{ action = "reload" })
    $state = $null; $picked = -1
    for ($i = 0; $i -lt 40 -and $picked -lt 0; $i++) {
        Start-Sleep -Seconds 3
        $state = Call $W "overrider" @{ action = "state" }
        if (-not $state -or -not $state.loaded) { continue }
        for ($n = 0; $n -lt [int]$state.sets -and $picked -lt 0; $n++) {
            $s = Call $W "overrider" @{ action = "set"; text = "$n" }
            $byName = @{}
            foreach ($st in @($s.states)) { $byName[[string]$st.state] = $st }
            $resolved = $true
            foreach ($need in @("Standing", "Walking", "Flying")) {
                if (-not $byName.ContainsKey($need)) { $resolved = $false; continue }
                foreach ($step in @($byName[$need].steps)) { foreach ($id in @($step)) { if ([string]$id -eq "00000000-0000-0000-0000-000000000000") { $resolved = $false } } }
            }
            if ($resolved -and @($byName["Standing"].steps).Count -eq 2) { $picked = $n; $state = $s }
        }
        if ($picked -lt 0 -and $state.loaded) { [void](Call $W "overrider" @{ action = "reload" }) }   # links whose animations were not known yet: read again
    }
    if ($picked -lt 0) { throw "no set with two stands, a walk and a fly whose animations are known was read" }
    $byName = @{}
    foreach ($st in @($state.states)) { $byName[[string]$st.state] = $st }
    $stands = @($byName["Standing"].steps | ForEach-Object { Short ([string]@($_)[0]) })
    $walk = Short ([string]@(@($byName["Walking"].steps)[0])[0])
    $fly = Short ([string]@(@($byName["Flying"].steps)[0])[0])
    $setIds = @($stands) + @($walk, $fly)
    Note ("set {0} of {1}: stands {2}, walk {3}, fly {4}; cycle {5} s, cycling {6}" -f $picked, $state.sets, ($stands -join " "), $walk, $fly, $byName["Standing"].cycle_time, $byName["Standing"].cycle)

    $O = Start-Client $Observer (Join-Path $Sandbox "observer")
    if (-not $O) { throw "the observer's viewer did not start" }
    if (-not (Wait-World $O)) { throw "the observer did not reach the world" }
    Note "$Observer in world"
    $wp = Position $W
    [void](Invoke-Raw $O.port "teleport" $O.token (@{ action = "region"; region = $regionName; x = ($wp[0] - 4); y = ($wp[1] - 3); z = ($wp[2] + 1) } | ConvertTo-Json -Compress))
    Start-Sleep -Seconds 8

    Probe-For 3 "before"
    [void](Call $W "overrider" @{ action = "on" })
    Note "overrider on"
    Probe-For ($StandSeconds / 2.0) "stand"
    Shot "stand"
    Probe-For ($StandSeconds / 2.0) "stand"
    $walked = Travel "UP" "walk" 12
    Note ("walked {0:N1} m" -f $walked)
    Shot "after-walk"
    Probe-For 3 "stand-again"
    Key "HOME" $true; Start-Sleep -Milliseconds 300; Key "HOME" $false
    Key "PGUP" $true; Probe-For 1.0 "fly-up"; Key "PGUP" $false
    $flown = Travel "UP" "fly" 10
    Note ("flew {0:N1} m" -f $flown)
    Shot "after-fly"
    Probe-For 2 "hover"
    Key "PGDN" $true; Probe-For 3.5 "down"; Key "PGDN" $false
    Probe-For 5 "landed"
    [void](Call $W "overrider" @{ action = "off" })
    Note "overrider off"
    Probe-For 8 "off"
    Shot "off"

    # ---- the verdict ----
    function Rows([string]$phase) { return @($rows | Where-Object { $_.phase -eq $phase }) }
    function Sees($row, $ids) { foreach ($id in @($row.seen)) { if ($ids -contains $id) { return $true } }; return $false }
    $stand = Rows "stand"
    $settled = @($stand | Where-Object { $_.t -ge ($stand[0].t + 3) })
    $withStand = @($settled | Where-Object { Sees $_ $stands }).Count
    $seenStands = @($stands | Where-Object { $id = $_; @($stand | Where-Object { @($_.seen) -contains $id }).Count -gt 0 })
    $changes = New-Object System.Collections.Generic.List[double]
    $lastStand = $null
    foreach ($r in $stand) {
        $now = @($stands | Where-Object { @($r.seen) -contains $_ })
        if ($now.Count -eq 1) { if ($lastStand -and $now[0] -ne $lastStand) { $changes.Add($r.t) }; $lastStand = $now[0] }
    }
    $gaps = @(); for ($i = 1; $i -lt $changes.Count; $i++) { $gaps += [math]::Round($changes[$i] - $changes[$i - 1], 1) }
    # the longest run of readings in which the observer has both stands at once (a change may show both for a moment)
    $bothLongest = 0; $bothNow = 0
    foreach ($r in $stand) { if (@($stands | Where-Object { @($r.seen) -contains $_ }).Count -ge 2) { $bothNow++; if ($bothNow -gt $bothLongest) { $bothLongest = $bothNow } } else { $bothNow = 0 } }
    $walkRows = Rows "walk"; $flyRows = Rows "fly"
    $offRows = Rows "off"; $offTail = @($offRows | Where-Object { $_.t -ge ($offRows[$offRows.Count - 1].t - 5) })
    $checks = [ordered]@{
        stand_shows_a_set_stand = ($settled.Count -gt 0 -and $withStand -ge [math]::Floor($settled.Count * 0.9))
        both_stands_seen = ($seenStands.Count -eq 2)
        stands_change_on_the_timer = ($changes.Count -ge 2 -and @($gaps | Where-Object { [math]::Abs($_ - $CycleSeconds) -gt 2.5 }).Count -eq 0)
        one_stand_at_a_time = ($bothLongest -le 3)
        walk_shows_the_set_walk = (@($walkRows | Where-Object { @($_.seen) -contains $walk }).Count -gt 0)
        walked_far_enough = ($walked -ge ($Metres - 0.5))
        fly_shows_the_set_fly = (@($flyRows | Where-Object { @($_.seen) -contains $fly }).Count -gt 0)
        flew_far_enough = ($flown -ge ($Metres - 0.5))
        before_shows_no_set_animation = (@((Rows "before") | Where-Object { Sees $_ $setIds }).Count -eq 0)
        off_shows_no_set_animation = ($offTail.Count -gt 0 -and @($offTail | Where-Object { Sees $_ $setIds }).Count -eq 0)
        off_shows_the_default_stand = (@($offTail | Where-Object { @($_.seen_names) -contains "stand" }).Count -gt 0)
    }
    $verdict = [ordered]@{
        pass = (@($checks.Values | Where-Object { -not $_ }).Count -eq 0); checks = $checks
        set = [ordered]@{ stands = $stands; walk = $walk; fly = $fly; cycle_seconds = $CycleSeconds }
        stand_rows = $stand.Count; stand_rows_settled = $settled.Count; stand_rows_with_a_set_stand = $withStand
        stand_change_times = @($changes); stand_change_gaps = $gaps; longest_run_with_both_stands = $bothLongest
        walk_rows = $walkRows.Count; walk_rows_with_the_set_walk = @($walkRows | Where-Object { @($_.seen) -contains $walk }).Count; walked_metres = [math]::Round($walked, 1)
        fly_rows = $flyRows.Count; fly_rows_with_the_set_fly = @($flyRows | Where-Object { @($_.seen) -contains $fly }).Count; flown_metres = [math]::Round($flown, 1)
        off_tail_rows = $offTail.Count; others_seen_by_observer = @($rows | ForEach-Object { $_.others } | Sort-Object -Unique)
    }
    ($rows | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}timeline.json")
    ($verdict | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}verdict.json")
    foreach ($k in $checks.Keys) { Note ("{0}: {1}" -f $k, $checks[$k]) }
    Note ("stand changes at {0}; gaps {1}" -f (($changes | ForEach-Object { "{0:N1}" -f $_ }) -join " "), ($gaps -join " "))
    if (-not $verdict.pass) { $exit = 1 }
} catch {
    Write-Output ("ERROR " + $_.Exception.Message); $exit = 5
    if ($rows.Count -gt 0) { ($rows | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}timeline-partial.json") }
} finally {
    Stop-Client $O; Stop-Client $W
    Start-Sleep -Seconds 5
    foreach ($p in $script:procs) { try { if (-not $p.HasExited) { Wait-Process -Id $p.Id -Timeout 30 -ErrorAction SilentlyContinue } } catch { } }
    Write-Output "LIVE OVERRIDER RUN DONE - other work may run again"
}
if ($exit -eq 0 -and $script:aimFailures.Count -gt 0) { Write-Output ("FRAMING FAILED for: " + ($script:aimFailures -join ", ")); $exit = 6 }
exit $exit
