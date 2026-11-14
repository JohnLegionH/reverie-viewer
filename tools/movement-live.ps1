# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# movement-live.ps1 - the reusable live movement test. One command logs a test avatar in with the development export, drives
# REAL key input through the viewer's automation API (the same path a focused keyboard takes) and records, per step, what the
# region sends for the own avatar, the animations it reports, the controls the viewer sends, and the roughness (jerk) of the
# drawn path.
#
# Steps, in order: mouse-look in and out, walking and turning in mouse-look, stand, walk, stop, single tap, walk and turn, double-tap run, Ctrl+R Always Run on and a run, Ctrl+R off and a
# walk, fly up, hover, land.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\movement-live.ps1 -Sandbox <new folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>]
#     [-OtherClientPattern <regex or none>] [-Exe <development export>] [-Coast] [-DryRun]
# -Coast runs the key-release steps instead: walk, run and fly forward for 3 s each and record 3 s after the key comes up.
#
# Settings come from the parameters or, when a parameter is not given, from the environment, never from a committed file:
#   NV_TEST_ACCOUNTS (the accounts file), NV_LIVE_ACCOUNT (the account's alias; default tester1), NV_GRID_INFO_URL (the grid's
#   get_grid_info URL; the login URI is read from it at run time and never printed), NV_LIVE_START (the start location, for
#   example uri:<region>&128&128&25), NV_OTHER_CLIENT_PATTERN (a regex over process executables matching any other client that
#   may use the account; none when there is none).
# Run it under a live-login claim (tools\godot.ps1 refuses to start without a granted claim) and, as the account is logged in,
# an avatar claim on that account. The output goes to the sandbox folder: movement-report.json (everything), movement-report.txt
# (one table), steps.jsonl (the raw motion answers). Every Godot process goes through tools\godot.ps1.
# A step in which the region reported the avatar dropping faster than 3 m/s (it walked off a ledge) is marked FELL and its jerk is not a walk's: start on level ground (-Start).
# Exit: 0 all steps ran and the animation and mouse-look checks passed, 1 a check failed, 3 blocked (preconditions), 5 the viewer
# or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "tester1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$OtherClientPattern = $env:NV_OTHER_CLIENT_PATTERN,
    [string]$Exe = "",
    [int]$StartSeconds = 60,
    [switch]$SecondViewer,      # another account is logged in through another copy of this script at the same time: do not refuse because a viewer is running
    [int]$Observe = 0,           # watch only: no keys, record the motion trace of everyone seen for this many seconds (the second account of a two-account run)
    [int]$StartDrawDistance = 0,   # when above 0: the draw distance is saved in the sandbox settings before the viewer starts (as a saved preference), so the login already carries it
    [int]$DrawDistance = 0,     # when above 0: the draw distance preference (metres) is set once the avatar is in the world, before the steps (the viewer's default stands otherwise)
    [switch]$Quiet,             # no animation polling during the steps: the viewer is asked nothing while it moves, so frame-time figures are not disturbed by the polling (no animation checks)
    [switch]$Coast,             # the key-release steps only: walk, run and fly forward for 3 s each, then 3 s recorded after the key comes up, then land
    [string]$ShotsDir = "",     # pictures of the own view, two a second, during the walk_forward and fly_forward steps (into this existing folder)
    [switch]$DryRun
)
# A live setting that is not set comes from the untracked local settings file (live-env.ps1).
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
if (-not $OtherClientPattern) { $OtherClientPattern = & "$PSScriptRoot\live-env.ps1" NV_OTHER_CLIENT_PATTERN }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
# an error ends the run with where it happened, so a failure is read from the output and not guessed; the avatar is logged out and the viewer quit
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage; Write-Output $_.ScriptStackTrace
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

# ---------------- the steps: a timeline of key events, then a wait ----------------
# Each step: Name, Keys (At seconds from the step's start, Key, Down, Control), Seconds (how long the step records),
# Expect (animation names at least one of which the region must report for some of the step; empty: no check), Until (a
# condition ended early: the landing).
function K([double]$at, [string]$key, [bool]$down, [bool]$control = $false) { @{ At = $at; Key = $key; Down = $down; Control = $control } }
# a mouse-look event: the automation API's mouselook command (action enter, leave, look with dx and dy pixels, state), at a time in the step
function ML([double]$at, [string]$action, [double]$dx = 0, [double]$dy = 0) { @{ At = $at; Mouse = $action; Dx = $dx; Dy = $dy } }
$lookRight = @(); for ($i = 0; $i -lt 12; $i++) { $lookRight += ,(ML (1.0 + $i * 0.1) "look" 12 0) }                 # 144 pixels to the right in 1.2 s
$walkLook = @(); for ($i = 0; $i -lt 20; $i++) { $walkLook += ,(ML (1.5 + $i * 0.1) "look" 15 -3) }                  # 300 pixels right and 60 up while walking
$steps = @(
    # mouse-look in and out, then walking and turning in it; MlCheck: the answers to these events are checked (see the end of the run)
    @{ Name = "mouselook_in_out"; Seconds = 5; Keys = @((ML 0.5 "enter"), (ML 0.9 "state")) + $lookRight + @((ML 2.6 "state"), (ML 3.0 "leave"), (ML 3.4 "state")); Expect = @(); MlCheck = "in_out"; LookPixels = 144 },
    @{ Name = "mouselook_walk_and_turn"; Seconds = 8; Keys = @((ML 0.3 "enter"), (K 0.8 "W" $true)) + $walkLook + @((ML 3.7 "state"), (K 4.5 "W" $false), (ML 5.5 "leave"), (ML 6.0 "state")); Expect = @("walk", "female_walk"); MlCheck = "walk"; LookPixels = 300 },
    @{ Name = "stand"; Seconds = 3; Keys = @(); Expect = @() },
    @{ Name = "walk_forward"; Seconds = 6; Keys = @((K 0.5 "W" $true), (K 4.5 "W" $false)); Expect = @("walk", "female_walk") },
    @{ Name = "single_tap"; Seconds = 3; Keys = @((K 0.3 "W" $true), (K 0.42 "W" $false)); Expect = @() },
    @{ Name = "walk_and_turn"; Seconds = 7; Keys = @((K 0.5 "W" $true), (K 2.0 "D" $true), (K 3.5 "D" $false), (K 5.5 "W" $false)); Expect = @("walk", "female_walk") },
    @{ Name = "double_tap_run"; Seconds = 7; Keys = @((K 0.5 "W" $true), (K 0.58 "W" $false), (K 0.66 "W" $true), (K 5.5 "W" $false)); Expect = @("run") },
    @{ Name = "always_run_on"; Seconds = 7; Keys = @((K 0.3 "R" $true $true), (K 0.4 "R" $false $true), (K 1.0 "W" $true), (K 5.5 "W" $false)); Expect = @("run") },
    @{ Name = "always_run_off_walk"; Seconds = 6; Keys = @((K 0.3 "R" $true $true), (K 0.4 "R" $false $true), (K 1.0 "W" $true), (K 4.5 "W" $false)); Expect = @("walk", "female_walk") },
    @{ Name = "fly_up"; Seconds = 5; Keys = @((K 0.3 "F" $true), (K 0.4 "F" $false), (K 0.8 "E" $true), (K 3.3 "E" $false)); Expect = @("fly", "fly_slow", "hover_up") },
    @{ Name = "hover"; Seconds = 4; Keys = @(); Expect = @("hover", "fly", "fly_slow") },
    @{ Name = "fly_forward"; Seconds = 8; Keys = @((K 0.5 "W" $true), (K 5.5 "W" $false)); Expect = @("fly", "fly_slow") },
    @{ Name = "fly_turn"; Seconds = 9; Keys = @((K 0.3 "W" $true), (K 1.0 "D" $true), (K 3.0 "D" $false), (K 3.6 "A" $true), (K 5.6 "A" $false), (K 6.5 "W" $false)); Expect = @("fly", "fly_slow") },
    @{ Name = "land"; Seconds = 14; Keys = @((K 0.5 "C" $true), (K 12.0 "C" $false)); Expect = @(); LandWhen = $true }
)

# the coast after a key comes up: each step holds forward for 3 s and records for 3 s more (and a little) after the release
if ($Coast) {
    $steps = @(
        @{ Name = "stand"; Seconds = 2; Keys = @(); Expect = @() },
        @{ Name = "coast_walk"; Seconds = 6.8; Keys = @((K 0.5 "W" $true), (K 3.5 "W" $false)); Expect = @("walk", "female_walk") },
        @{ Name = "coast_run"; Seconds = 7.3; Keys = @((K 0.3 "R" $true $true), (K 0.4 "R" $false $true), (K 1.0 "W" $true), (K 4.0 "W" $false)); Expect = @("run") },
        @{ Name = "always_run_off"; Seconds = 1.5; Keys = @((K 0.3 "R" $true $true), (K 0.4 "R" $false $true)); Expect = @() },
        @{ Name = "fly_up"; Seconds = 4; Keys = @((K 0.3 "F" $true), (K 0.4 "F" $false), (K 0.8 "E" $true), (K 1.8 "E" $false)); Expect = @("fly", "fly_slow", "hover_up") },
        @{ Name = "coast_fly"; Seconds = 6.8; Keys = @((K 0.5 "W" $true), (K 3.5 "W" $false)); Expect = @("fly", "fly_slow") },
        @{ Name = "land"; Seconds = 14; Keys = @((K 0.5 "C" $true), (K 12.0 "C" $false)); Expect = @(); LandWhen = $true }
    )
}
if ($Observe -gt 0) { $steps = @(@{ Name = "observe"; Seconds = $Observe; Keys = @(); Expect = @() }) }

# ---------------- preconditions (also the whole of -DryRun) ----------------
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (run tools\s1.ps1 for the development export): $Exe") }
if (-not (Test-Path -LiteralPath $godot -PathType Leaf)) { $problems.Add("tools\godot.ps1 not found") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
if (-not $OtherClientPattern) { $problems.Add("-OtherClientPattern (or NV_OTHER_CLIENT_PATTERN) is required: a regex of other clients that may use the account, or none") }
elseif ($OtherClientPattern -ne "none") {
    . (Join-Path $PSScriptRoot "other-clients.ps1")
    $agent = @(Find-OtherClients -Pattern $OtherClientPattern)   # by executable, never by command-line text
    if ($agent.Count -gt 0) { $problems.Add("another test client is running ($($agent.Count) process(es)): the test account may be in use") }
}
# a viewer that can log in is one started with --automation (a render or test run has no login); one session per account at a time
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0 -and -not $SecondViewer) { $problems.Add("a viewer that can log in is already running ($($viewers.Count)): one account in one session at a time") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
Write-Output ("preconditions ok: account {0}, {1} steps" -f $Account, $steps.Count)
if ($DryRun) { Write-Output "dry run: nothing started, nothing logged in"; exit 0 }

# ---------------- helpers ----------------
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

# The drawn path's roughness: positions resampled to 60 per second (linear), then speed, acceleration and jerk by differences.
# Returns RMS jerk, peak jerk (m/s^3), peak acceleration (m/s^2) over [from, to] (seconds on the viewer's clock).
function Jerk($pts, [double]$from, [double]$to) {
    $r = @{ rms = $null; peak = $null; peak_acc = $null; samples = 0 }
    $p = @($pts | Where-Object { $_.t -ge $from - 0.5 -and $_.t -le $to + 0.5 })
    if ($p.Count -lt 8) { return $r }
    $dt = 1.0 / 60
    $res = New-Object System.Collections.Generic.List[object]
    $i = 0
    for ($t = [math]::Max($from, $p[0].t); $t -le [math]::Min($to, $p[$p.Count - 1].t); $t += $dt) {
        while ($i + 1 -lt $p.Count - 1 -and $p[$i + 1].t -lt $t) { $i++ }
        $a = $p[$i]; $b = $p[$i + 1]
        $f = if ($b.t -gt $a.t) { [math]::Min(1, [math]::Max(0, ($t - $a.t) / ($b.t - $a.t))) } else { 0 }
        $res.Add(@(($a.x + ($b.x - $a.x) * $f), ($a.y + ($b.y - $a.y) * $f), ($a.z + ($b.z - $a.z) * $f)))
    }
    if ($res.Count -lt 8) { return $r }
    function D($s) { $o = New-Object System.Collections.Generic.List[object]; for ($k = 1; $k -lt $s.Count; $k++) { $o.Add(@((($s[$k][0] - $s[$k - 1][0]) / $dt), (($s[$k][1] - $s[$k - 1][1]) / $dt), (($s[$k][2] - $s[$k - 1][2]) / $dt))) }; return $o }
    $v = D $res; $a2 = D $v; $j = D $a2
    $sum = 0.0; $peak = 0.0; $pa = 0.0
    foreach ($x in $j) { $m = [math]::Sqrt($x[0] * $x[0] + $x[1] * $x[1] + $x[2] * $x[2]); $sum += $m * $m; if ($m -gt $peak) { $peak = $m } }
    foreach ($x in $a2) { $m = [math]::Sqrt($x[0] * $x[0] + $x[1] * $x[1] + $x[2] * $x[2]); if ($m -gt $pa) { $pa = $m } }
    $r.rms = [math]::Round([math]::Sqrt($sum / $j.Count), 1); $r.peak = [math]::Round($peak, 1); $r.peak_acc = [math]::Round($pa, 2); $r.samples = $res.Count
    return $r
}

# ---------------- run ----------------
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
if ($StartDrawDistance -gt 0) { $sd = Join-Path $appdata "ReverieViewer"; New-Item -ItemType Directory -Force -Path $sd | Out-Null; Set-Content -LiteralPath (Join-Path $sd "settings.json") -Value (@{ draw_distance = $StartDrawDistance; graphics_defaults_version = 1 } | ConvertTo-Json -Compress) -Encoding UTF8 }
$stepsFile = Join-Path $Sandbox "steps.jsonl"
Write-Output ""
Write-Output "LIVE MOVEMENT RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan -- --automation"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
Note "viewer started through godot.ps1, pid $($p.Id)"
$tokDir = Join-Path $local "ReverieViewer\Automation"
$tf = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$port = [int]$j.port; $token = [string]$j.token

$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
$stable = 0; $last = -1; $ended = $false; $o = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Parse (Invoke-Raw $port "observe" $token).Body
    if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
        if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
        $last = $o.world.objects
        if ($stable -ge 3) { break }
    }
    $st = Parse (Invoke-Raw $port "session" $token '{"action":"state"}').Body
    if ($st -and $st.ended) { Note "session ended during settle: $($st.end_reason) / $($st.status)"; $ended = $true; break }
}
$report = [ordered]@{ account = $Account; steps = @(); animation_checks = @(); mouselook_checks = @(); completed = $false }
if (-not $ended -and $o -and $o.self) {
    Note "in world, settled (objects $last)"
    if ($DrawDistance -gt 0) {
        $dd = Invoke-Raw $port "prefs" $token (@{ action = "set"; key = "draw_distance"; value = $DrawDistance } | ConvertTo-Json -Compress)
        Note ("draw distance set to $DrawDistance m (status " + $dd.Status + ")")
        Start-Sleep -Seconds 4
    }
    Start-Sleep -Seconds 5
    [void](Invoke-Raw $port "motion" $token '{"action":"start"}')
    foreach ($step in $steps) {
        [void](Invoke-Raw $port "motion" $token '{"action":"take"}')   # drop what came before this step
        $events = @($step.Keys | Sort-Object { $_.At })
        $ei = 0
        $polls = New-Object System.Collections.Generic.List[object]
        $keyTimes = New-Object System.Collections.Generic.List[double]
        $mlReplies = New-Object System.Collections.Generic.List[object]
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $lastPoll = -1.0; $lastShot = -1.0; $landed = $false
        while ($sw.Elapsed.TotalSeconds -lt $step.Seconds -and -not $landed) {
            $now = $sw.Elapsed.TotalSeconds
            while ($ei -lt $events.Count -and $events[$ei].At -le $now) {
                $e = $events[$ei]; $ei++
                if ($e.Mouse) {
                    $mr = Invoke-Raw $port "mouselook" $token (@{ action = $e.Mouse; dx = $e.Dx; dy = $e.Dy } | ConvertTo-Json -Compress)
                    $mlReplies.Add([pscustomobject]@{ t = [math]::Round($sw.Elapsed.TotalSeconds, 2); action = $e.Mouse; status = $mr.Status; reply = (Parse $mr.Body) })
                    continue
                }
                [void](Invoke-Raw $port "keys" $token (@{ key = $e.Key; down = $e.Down; control = $e.Control } | ConvertTo-Json -Compress))
                $keyTimes.Add([math]::Round($sw.Elapsed.TotalSeconds, 3))
            }
            # a poll takes up to a tenth of a second: none is started when a key event is due within the next 0.4 s, so the gaps
            # between key events (a double-tap's 0.15 s) are the ones the step says
            $nextAt = if ($ei -lt $events.Count) { $events[$ei].At } else { [double]::PositiveInfinity }
            if (-not $Quiet -and $now - $lastPoll -ge 0.25 -and ($nextAt - $now) -gt 0.4) {
                $lastPoll = $now
                $raw = (Invoke-Raw $port "observe" $token).Body
                $ob = Parse $raw
                if ($step.Name -eq "walk_forward" -and $now -gt 2.5 -and -not $script:sampled) { Set-Content -LiteralPath (Join-Path $Sandbox "observe-sample.json") -Value $raw -Encoding utf8; $script:sampled = $true }   # one raw answer kept, for reading the fields
                if ($ob -and $ob.self) {
                    $me = @($ob.presentation.avatars.each | Where-Object { $_.self }) | Select-Object -First 1   # the walk cycle's rate and the clip's planted-foot speed
                    $polls.Add([pscustomobject]@{ t = [math]::Round($now, 2); playing = @($ob.self.playing); controls = $ob.self.controls; flying = $ob.self.flying; rate = $me.walk.rate; foot = $me.walk.foot_mean_ms; walk_active = $me.walk.active; why = $me.walk.why_not; roll = $me.defaults.fly_roll })
                }
                if ($step.LandWhen -and $now -gt 3 -and $ob -and $ob.self -and -not $ob.self.flying -and (@($ob.self.playing) | Where-Object { $_ -in @("fly", "fly_slow", "hover", "hover_up", "hover_down") }).Count -eq 0) { $landed = $true }
            } else { Start-Sleep -Milliseconds 15 }
            if ($ShotsDir -and $step.Name -in @("walk_forward", "fly_forward", "fly_turn") -and $now -gt 0.8 -and ($now - $lastShot) -ge 0.5) {
                $lastShot = $now
                [void](Invoke-Raw $port "screenshot" $token (@{ dir = $ShotsDir; name = ("{0}_{1:000}" -f $step.Name, [int]($now * 10)) } | ConvertTo-Json -Compress))
            }
        }
        # a landing ends the step early: release the down key, then take the motion record
        if ($landed) { [void](Invoke-Raw $port "keys" $token (@{ key = "C"; down = $false } | ConvertTo-Json -Compress)) }
        $take = Invoke-Raw $port "motion" $token '{"action":"take"}'
        Add-Content -LiteralPath $stepsFile -Value ('{"step":"' + $step.Name + '","motion":' + $take.Body + '}') -Encoding utf8
        $m = Parse $take.Body
        # self samples: [at, id, self, dx, dy, dz, sx, sy, sz, vx, vy, vz, updates]
        $self = @($m.samples | Where-Object { $_[2] -eq $true })
        $drawn = @($self | ForEach-Object { [pscustomobject]@{ t = [double]$_[0]; x = [double]$_[3]; y = [double]$_[4]; z = [double]$_[5] } })
        $stated = New-Object System.Collections.Generic.List[object]
        $prevU = -1
        foreach ($s in $self) { if ($s[12] -ne $prevU) { $stated.Add([pscustomobject]@{ t = [double]$s[0]; x = [double]$s[6]; y = [double]$s[7]; z = [double]$s[8]; vx = [double]$s[9]; vy = [double]$s[10]; vz = [double]$s[11] }); $prevU = $s[12] } }
        $t0 = if ($drawn.Count) { $drawn[0].t } else { 0 }; $t1 = if ($drawn.Count) { $drawn[$drawn.Count - 1].t } else { 0 }
        $jerk = Jerk $drawn ($t0 + 0.4) $t1
        $ctrl = New-Object System.Collections.Generic.List[object]; $prevC = $null
        foreach ($e in @($m.sent)) { $v = "$($e[1]):$($e[2])"; if ($v -ne $prevC) { $ctrl.Add([pscustomobject]@{ t = [math]::Round([double]$e[0] - $t0, 2); what = $v }); $prevC = $v } }
        $anim = New-Object System.Collections.Generic.List[object]; $prevA = $null
        foreach ($pl in $polls) { $v = ($pl.playing -join "+"); if ($v -ne $prevA) { $anim.Add([pscustomobject]@{ t = $pl.t; playing = $v }); $prevA = $v } }
        $rates = @($polls | Where-Object { $null -ne $_.rate } | ForEach-Object { [double]$_.rate })
        $feet = @($polls | Where-Object { $_.foot } | ForEach-Object { [double]$_.foot } | Select-Object -Unique)
        $whyNot = @($polls | Where-Object { $_.why } | ForEach-Object { $_.why } | Select-Object -Unique)
        $activeSeen = @($polls | Where-Object { $_.walk_active -eq $true }).Count
        $maxSpeed = 0.0
        foreach ($s in $stated) { $sp = [math]::Sqrt($s.vx * $s.vx + $s.vy * $s.vy); if ($sp -gt $maxSpeed) { $maxSpeed = $sp } }
        $seen = @($polls | ForEach-Object { $_.playing } | Select-Object -Unique)
        $check = $null
        if ($step.Expect.Count -gt 0 -and -not $Quiet) {
            $ok = @($step.Expect | Where-Object { $seen -contains $_ }).Count -gt 0
            $check = [ordered]@{ step = $step.Name; expected_any_of = $step.Expect; seen = $seen; pass = $ok }
            $report.animation_checks += $check
        }
        # a fall: the region reported the avatar dropping faster than 3 m/s (it walked off a ledge); the jerk of a fall and its landing is
        # not a walk's, so such a step says so and its figure is not compared with the model's
        $minVz = 0.0; $zs = @($stated | ForEach-Object { $_.z })
        foreach ($s in $stated) { if ($s.vz -lt $minVz) { $minVz = $s.vz } }
        $drop = if ($zs.Count) { [math]::Round((($zs | Measure-Object -Maximum).Maximum - ($zs | Measure-Object -Minimum).Minimum), 2) } else { 0 }
        $flew = @($polls | Where-Object { $_.flying -eq $true }).Count -gt 0   # a flying avatar descends fast on purpose (the land step): that is not a fall
        $fell = (($minVz -lt -3.0) -and -not $flew) -or ($seen -contains "falldown")
        # mouse-look: what the viewer answered to this step's mouse-look events, checked against the official behaviour
        $mlCheck = $null
        if ($step.MlCheck) {
            $ok = $true; $why = New-Object System.Collections.Generic.List[string]
            $enter = $mlReplies | Where-Object { $_.action -eq "enter" } | Select-Object -First 1
            $leave = $mlReplies | Where-Object { $_.action -eq "leave" } | Select-Object -First 1
            $states = @($mlReplies | Where-Object { $_.action -eq "state" })
            $inState = $states | Where-Object { $_.reply.active -eq $true } | Select-Object -Last 1
            $outState = $states | Where-Object { $_.reply.active -eq $false } | Select-Object -Last 1
            $bit = 0x40000
            if (-not $enter -or $enter.reply.active -ne $true) { $ok = $false; $why.Add("enter did not make mouse-look active") }
            elseif ($enter.reply.own_avatar_drawn -ne $false) { $ok = $false; $why.Add("the own avatar was still drawn in mouse-look") }
            if (-not $inState) { $ok = $false; $why.Add("no state answer in mouse-look") }
            else {
                if (([Convert]::ToUInt32($inState.reply.controls, 16) -band $bit) -eq 0) { $ok = $false; $why.Add("the mouse-look bit was not in the controls: $($inState.reply.controls)") }
                $turned = [double]$inState.reply.heading_deg - [double]$enter.reply.heading_deg
                $turned = (($turned + 540) % 360) - 180
                $want = - $step.LookPixels * 0.002375 * 180 / [math]::PI
                if ([math]::Abs($turned - $want) -gt 2.0 -and $step.MlCheck -eq "in_out") { $ok = $false; $why.Add("heading turned $([math]::Round($turned,2)) degrees, expected $([math]::Round($want,2))") }
                if ($step.MlCheck -eq "walk" -and [math]::Abs($turned) -lt 5) { $ok = $false; $why.Add("the walk did not turn the heading: $([math]::Round($turned,2)) degrees") }
            }
            if (-not $leave -or $leave.reply.active -ne $false) { $ok = $false; $why.Add("leave did not end mouse-look") }
            if (-not $outState) { $ok = $false; $why.Add("no state answer after leaving") }
            else {
                if ($outState.reply.own_avatar_drawn -ne $true) { $ok = $false; $why.Add("the own avatar was not drawn again") }
                if (([Convert]::ToUInt32($outState.reply.controls, 16) -band $bit) -ne 0) { $ok = $false; $why.Add("the mouse-look bit stayed in the controls: $($outState.reply.controls)") }
                if ([math]::Abs([double]$outState.reply.pitch_deg) -gt 0.001) { $ok = $false; $why.Add("the pitch stayed after leaving: $($outState.reply.pitch_deg)") }
            }
            $mlCheck = [ordered]@{ step = $step.Name; pass = $ok; failures = @($why) }
            $report.mouselook_checks += $mlCheck
        }
        $report.steps += [ordered]@{
            fell = $fell; lowest_vertical_speed = [math]::Round($minVz, 2); region_drop_m = $drop; mouselook_answers = $mlReplies.ToArray();
            step = $step.Name; key_calls_at_s = @($keyTimes); seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1); keys = @($events | ForEach-Object { "$($_.At)s $(if ($_.Control) { 'Ctrl+' })$($_.Key) $(if ($_.Down) { 'down' } else { 'up' })" })
            region_updates = $stated.Count; region_max_speed = [math]::Round($maxSpeed, 2); region_updates_list = @($stated | Select-Object -First 40 | ForEach-Object { "{0:N2}s ({1:N2}, {2:N2}, {3:N2}) v=({4:N2}, {5:N2}, {6:N2})" -f ($_.t - $t0), $_.x, $_.y, $_.z, $_.vx, $_.vy, $_.vz })
            fly_roll = $(if (@($polls | Where-Object { $null -ne $_.roll }).Count) { $rolls = @($polls | Where-Object { $null -ne $_.roll } | ForEach-Object { [double]$_.roll }); [ordered]@{ min = [math]::Round(($rolls | Measure-Object -Minimum).Minimum, 3); max = [math]::Round(($rolls | Measure-Object -Maximum).Maximum, 3); samples = $rolls.Count } } else { $null })
            animations = $anim; walk_rate = $(if ($rates.Count) { [ordered]@{ min = [math]::Round(($rates | Measure-Object -Minimum).Minimum, 2); max = [math]::Round(($rates | Measure-Object -Maximum).Maximum, 2); last = [math]::Round($rates[-1], 2); foot_mean_ms = $feet; polls_active = $activeSeen; why_not = $whyNot } } else { $null }); controls_sent = $ctrl; drawn_samples = $drawn.Count; drawn_jerk = $jerk; dropped = $m.dropped
        }
        Note ("step {0}: region updates {1}, max speed {2:N2}, animations [{3}], controls changes {4}, drawn jerk rms {5}" -f $step.Name, $stated.Count, $maxSpeed, (($anim | ForEach-Object { $_.playing }) -join " > "), $ctrl.Count, $jerk.rms)
    }
    [void](Invoke-Raw $port "motion" $token '{"action":"stop"}')
    $report.completed = $true
}
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended) reason=$($after.end_reason)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report.token_in_log = ((Get-Content -Raw -LiteralPath $log) -match [regex]::Escape($token))
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "movement-report.json") -Encoding utf8
$txt = New-Object System.Collections.Generic.List[string]
foreach ($s in $report.steps) {
    $txt.Add(("{0,-24} region updates {1,3}  max speed {2,5}  drawn jerk rms {3,8}  peak {4,8}  animations: {5}{6}" -f $s.step, $s.region_updates, $s.region_max_speed, $s.drawn_jerk.rms, $s.drawn_jerk.peak, (($s.animations | ForEach-Object { $_.playing }) -join " > "), $(if ($s.fly_roll -and ($s.fly_roll.min -ne 0 -or $s.fly_roll.max -ne 0)) { "  fly roll " + $s.fly_roll.min + " to " + $s.fly_roll.max + " rad" } elseif ($s.fell) { "  FELL: dropped $($s.region_drop_m) m, lowest vertical speed $($s.lowest_vertical_speed) m/s (a fall, not a walk: its jerk is not compared)" } else { "" })))
}
foreach ($c in $report.animation_checks) { $txt.Add(("check {0,-22} expects any of [{1}] saw [{2}] -> {3}" -f $c.step, ($c.expected_any_of -join ","), ($c.seen -join ","), $(if ($c.pass) { "PASS" } else { "FAIL" }))) }
foreach ($c in $report.mouselook_checks) { $txt.Add(("mouse-look check {0,-22} -> {1} {2}" -f $c.step, $(if ($c.pass) { "PASS" } else { "FAIL" }), ($c.failures -join "; "))) }
Set-Content -LiteralPath (Join-Path $Sandbox "movement-report.txt") -Value $txt -Encoding utf8
$txt | ForEach-Object { Write-Output $_ }
Write-Output ""
Write-Output "LIVE MOVEMENT RUN DONE - other work may run again"
if (-not $report.completed) { exit 5 }
if (@($report.animation_checks | Where-Object { -not $_.pass }).Count -gt 0 -or @($report.mouselook_checks | Where-Object { -not $_.pass }).Count -gt 0) { exit 1 }
exit 0
