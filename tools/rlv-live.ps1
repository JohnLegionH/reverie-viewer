# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
# =====================================================================================
# rlv-live.ps1 - the live check of RLV. Starts the development export, logs a test avatar in, and through the automation interface:
#   1 off       RLV is off and not running at first
#   2 switch    switching it on keeps the choice, says it takes effect at the next login, and does not start it
#   3 relog     after logging out and in again it runs, and its Restrictions window says no object restricts the user
#   4 wear      the RLV test object (samples/rlv-test in a box, given by its inventory item) is worn
#   5 lock      "lock": the object holds detach, it is locked (Take Off and Detach greyed), and detaching it is refused
#   6 teleport  "notp": a teleport to a location is refused with the RLV notice
#   7 redirect  "redir": a line said in local chat does not go out there; the object hears it on its channel and says it back
#   8 version   "version": the reply names this viewer in RLVa's form
#   9 sit       "sit <seat>": the user sits on the seat object; "stand": the user stands up
#  10 window    the Restrictions window lists the object's behaviours, each enforced, with the time set
#  11 swallow   the object's command lines were kept out of chat
#  12 clear     "clear": every restriction goes, the object can be taken off, and it is taken off
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\rlv-live.ps1 -Sandbox <new folder> -GridInfoUrl <url> -Start <start>
#     -ItemId <the test object's inventory item> -SeatId <the seat object's id> [-Credentials <accounts file>] [-Account <alias>] [-Exe <development export>]
#
# Run it under the claims the target needs (an avatar claim on the account, and live-login on a shared grid). Wearing and taking off
# need NV_OUTFIT_SCRATCH_OK=1 (the automation changes what is worn only on a scratch account). Output: rlv-report.json in the sandbox
# (ids, counts and states only; no chat text). Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ItemId,
    [Parameter(Mandatory)][string]$SeatId,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 90,
    [int]$CommandChannel = 7171
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }
$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
if ($env:NV_OUTFIT_SCRATCH_OK -ne "1") { $problems.Add("NV_OUTFIT_SCRATCH_OK=1 is required to wear and take off the test object") }
$item = [Guid]::Empty; $seat = [Guid]::Empty
if (-not [Guid]::TryParse($ItemId, [ref]$item)) { $problems.Add("-ItemId is not an id") }
if (-not [Guid]::TryParse($SeatId, [ref]$seat)) { $problems.Add("-SeatId is not an id") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

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
function Api([string]$command, [hashtable]$b) { Parse (Invoke-Raw $port $command $token ($b | ConvertTo-Json -Compress)).Body }
function Rlv { Api "addons" @{ action = "rlv" } }
function Say([string]$text) { Api "chat" @{ action = "say"; text = $text } }
function Word([string]$word) { Say "/$CommandChannel $word" }
# the nearby lines holding a token (never their text); always an array, also when empty
function Lines([string]$token) { $c = Api "chat" @{ action = "find"; token = $token }; if ($c -and $c.nearby) { return ,@($c.nearby) } else { return ,@() } }
function Count([string]$token) { (Lines $token).Count }
# polls a script block until it returns something true, at most $seconds; answers the last result
function Wait-Until([scriptblock]$test, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    do { $r = & $test; if ($r) { return $r }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $until)
    return $null
}
function Login {
    $l = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
    Note "login requested: $($l.Status)"
    return Wait-Until { $x = Parse (Invoke-Raw $port "observe" $token).Body; if ($x -and $x.stage -eq "InWorld" -and $x.self) { $x } } 150
}
function Logout {
    [void](Invoke-Raw $port "session" $token '{"action":"logout"}')
    return Wait-Until { $x = Parse (Invoke-Raw $port "observe" $token).Body; if ($x -and $x.stage -ne "InWorld") { $x } } 60
}

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output "LIVE RLV RUN STARTING - no other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan -- --automation"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
Note "viewer started through godot.ps1, pid $($p.Id)"
$tokDir = Join-Path $local "ReverieViewer\Automation"
$tf = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$port = [int]$j.port; $token = [string]$j.token

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-44} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; item = $item; seat = $seat; checks = $null; completed = $false; states = [ordered]@{} }
$stamp = (Get-Date).ToUniversalTime().ToString("HHmmss")

if (Login) {
    Note "in world"
    Start-Sleep -Seconds 3
    # 1 off
    $r = Rlv; $report.states.first = $r
    Check "1 off at first" ($r -and -not $r.enabled -and -not $r.running) "enabled=$($r.enabled) running=$($r.running)"
    # 2 switch
    [void](Api "addons" @{ action = "on"; id = "rlv" }); $r = Rlv; $report.states.switched = $r
    Check "2 switch takes effect at next login" ($r -and $r.enabled -and -not $r.running -and $r.waits_for_login -and $r.note -eq "Takes effect at next login.") "enabled=$($r.enabled) running=$($r.running) note=$($r.note)"
    # 3 relog
    [void](Logout)
    if (Login) {
        Note "in world again"
        $r = Wait-Until { $x = Rlv; if ($x -and $x.running) { $x } } 20; $report.states.relogged = $r
        Check "3 on after the next login" ($r -and $r.running -and -not $r.waits_for_login) "running=$($r.running)"
        Check "3 window says nothing restricts" ($r -and (@($r.lines) -contains "RLV is on. No object is restricting you.")) "lines=$(@($r.lines).Count)"
        # 4 wear
        # the item arrives with its folder: the inventory window is opened and the attach asked again until it is sent
        [void](Api "inventory" @{ action = "open" })
        $w = Wait-Until { $x = Api "inventory" @{ action = "attach"; id = "$item" }; if ($x -and $x.submitted) { $x } else { Start-Sleep -Seconds 2; $null } } 60
        $r = Wait-Until { $x = Rlv; if ($x -and @($x.worn | Where-Object { $_.item -eq "$item" }).Count -gt 0) { $x } } 45
        $objectId = if ($r) { @($r.worn | Where-Object { $_.item -eq "$item" })[0].object } else { $null }
        Check "4 test object worn" ($null -ne $objectId) "attach submitted=$($w.submitted) object=$objectId"
        Start-Sleep -Seconds 3
        # 5 lock
        [void](Word "lock")
        $r = Wait-Until { $x = Rlv; if ($x -and @($x.locked) -contains "$item") { $x } } 20; $report.states.locked = $r
        Check "5 lock holds detach and greys Take Off" ($null -ne $r -and @($r.held | Where-Object { $_.behaviour -eq "detach" -and $_.enforced }).Count -eq 1) "locked=$(@($r.locked).Count)"
        $d = Api "inventory" @{ action = "detach"; id = "$item" }
        Check "5 detaching it is refused" ($d -and -not $d.submitted -and $d.code -eq "rlv.detach") "submitted=$($d.submitted) code=$($d.code)"
        # diagnostics (not a check): a word the object answers with a plain owner-say line, and every nearby line by style and mark
        [void](Word "bogus")
        [void](Wait-Until { if ((Count "unknown word") -gt 0) { $true } } 15)
        $all = Lines " "
        $report.states.diag = [ordered]@{ unknown_word = (Count "unknown word"); lines = @($all | ForEach-Object { "$($_.style)/$($_.mark)" }) }
        Note "diag: unknown word lines=$($report.states.diag.unknown_word); nearby styles=$($report.states.diag.lines -join ',')"
        # 6 teleport
        [void](Word "notp")
        [void](Wait-Until { $x = Rlv; if ($x -and @($x.held | Where-Object { $_.behaviour -eq "tploc" }).Count -eq 1) { $x } } 20)
        $before = @((Rlv).refusals).Count
        $t = Api "teleport" @{ action = "region"; region = "Elsewhere"; x = 100; y = 100; z = 30 }
        $r = Wait-Until { $x = Rlv; if ($x -and @($x.refusals).Count -gt $before) { $x } } 10
        Check "6 teleport refused with the RLV notice" ($r -and @($r.refusals)[-1] -eq "rlv.teleport") "refusals=$(@($r.refusals) -join ',')"
        # 7 redirect
        [void](Word "redir")
        [void](Wait-Until { $x = Rlv; if ($x -and @($x.held | Where-Object { $_.behaviour -eq "redirchat" }).Count -eq 1) { $x } } 20)
        $said = "rlv-check-$stamp"
        [void](Say $said)
        $heard = Wait-Until { $l = Lines $said; if (@($l | Where-Object { $_.style -ne "Own" }).Count -gt 0) { $l } } 20
        $own = @((Lines $said) | Where-Object { $_.style -eq "Own" }).Count
        $report.states.chat_after_redirect = [ordered]@{ ready = (Count "RLV test object ready"); redirected = (Count "redirected:"); reply = (Count "reply:"); unknown = (Count "unknown word") }
        Check "7 redirected: not said publicly, heard on the channel" ($null -ne $heard -and $own -eq 0) "object lines=$(if ($heard) { @($heard).Count } else { 0 }) own lines=$own ready=$($report.states.chat_after_redirect.ready) redirected=$($report.states.chat_after_redirect.redirected)"
        # 8 version
        [void](Word "version")
        $v = Wait-Until { if ((Count "reply: RestrainedLove viewer v3.4.3 (") -gt 0) { $true } } 20
        Check "8 version reply in RLVa's form" ($true -eq $v) "replies=$(Count "reply:")"
        # 9 sit and stand
        [void](Word "sit $seat")
        $s = Wait-Until { $x = Rlv; if ($x -and $x.seated) { $x } } 20
        Check "9 forced sit" ($null -ne $s) "seated=$($s.seated)"
        [void](Word "stand")
        $s2 = Wait-Until { $x = Rlv; if ($x -and $x.seated -eq $false) { $x } } 20
        Check "9 forced stand" ($null -ne $s2) "seated=$($s2.seated)"
        # 10 window
        $r = Rlv; $report.states.window = $r
        $lines = @($r.lines)
        $want = @("detach", "tploc", "tplm", "redirchat:7272")
        $missing = @($want | Where-Object { $w = $_; -not ($lines | Where-Object { $_ -match "^\s+$([regex]::Escape($w)) \S enforced \S set at [0-9]{2}:[0-9]{2}:[0-9]{2}$" }) })
        Check "10 window lists each behaviour, enforced, with its time" ($lines -contains "RLV is on. These objects are restricting you." -and $missing.Count -eq 0) "missing=$($missing -join ',')"
        # 11 swallow
        Check "11 command lines kept out of chat" ($r.swallowed -ge 6 -and (Count "@detach") -eq 0) "swallowed=$($r.swallowed) shown=$(Count "@detach")"
        # 12 clear
        [void](Word "clear")
        $r = Wait-Until { $x = Rlv; if ($x -and @($x.held).Count -eq 0) { $x } } 20; $report.states.cleared = $r
        Check "12 clear releases everything" ($r -and @($r.locked).Count -eq 0 -and (@($r.lines) -contains "RLV is on. No object is restricting you.")) "held=$(@($r.held).Count)"
        $d = Api "inventory" @{ action = "detach"; id = "$item" }
        $gone = Wait-Until { $x = Rlv; if ($x -and @($x.worn | Where-Object { $_.item -eq "$item" }).Count -eq 0) { $x } } 30
        Check "12 then it can be taken off" ($d.submitted -and $null -ne $gone) "submitted=$($d.submitted)"
        $report.completed = $true
    } else { Check "3 second login" $false "the second login did not reach the world" }
} else { Check "login" $false "the login did not reach the world" }

$report.checks = $checks
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "rlv-report.json") -Encoding utf8
[void](Logout)
try { [void](Invoke-Raw $port "quit" $token) } catch { }
$null = $p.WaitForExit(60000)
Write-Output "LIVE RLV RUN DONE"
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note "checks: $($checks.Count - $failed) of $($checks.Count) passed"
if (-not $report.completed -and $checks.Count -le 1) { exit 5 }
exit $(if ($failed -eq 0) { 0 } else { 1 })
