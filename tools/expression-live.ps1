# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# expression-live.ps1 - the live check of facial expressions. One development viewer logs in as -Account (a test account's alias), asks the
# region to play an expression animation on its own avatar (-Expression, default express_smile), waits for it to fade in, hold and
# fade out, asks the region to stop it, and reads the weights its face took at every change. Each reading is compared with the
# official expression curve: cubic_step over the 0.3 s fade-in, full weight until 1.4 s, cubic_step down over the 1.4 s fade-out
# (llemote.h and llmotioncontroller.cpp), the closed mouth taking the complement. Nothing is worn, taken off, made or deleted.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\expression-live.ps1 -Sandbox <new folder> -OutDir <existing folder> -Account <alias>
#     [-Credentials <accounts file>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#     [-Expression express_smile] [-Tag <name prefix>]
#
# Pass: the face took at least 10 readings, the expression reached full weight and ended at none, every reading is within 0.001 of the
# official curve at its time since the start, the closed mouth is its complement, and the avatar's own body carried the change.
# Run it under a live-login claim and an avatar claim on the account. Output: <Tag>trace.json and <Tag>verdict.json in -OutDir, a run
# log in the sandbox. Exit: 0 pass, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [Parameter(Mandatory)][string]$Account,
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [string]$Expression = "express_smile",
    [int]$StartSeconds = 60,
    [string]$Tag = ""
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
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

function Start-Client([string]$alias, [string]$dir) {
    $appdata = Join-Path $dir "appdata"; $local = Join-Path $dir "localappdata"; $log = Join-Path $dir "run.log"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $argLine = "--rendering-driver vulkan -- --automation"
    $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0",
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
function Cubic([double]$x) { $x = [math]::Min([math]::Max($x, 0.0), 1.0); return $x * $x * (3.0 - 2.0 * $x) }
# the official expression weight at $t seconds after its start (fade-in 0.3 s, stops itself at 1.4 s, fade-out 1.4 s)
function Official([double]$t) { if ($t -le 0.3) { return Cubic ($t / 0.3) }; if ($t -le 1.4) { return 1.0 }; return Cubic (1.0 - ($t - 1.4) / 1.4) }

$exit = 0
$V = $null
try {
    Write-Output "LIVE EXPRESSION RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
    $V = Start-Client $Account (Join-Path $Sandbox "viewer")
    if (-not $V) { throw "the viewer did not start" }
    if (-not (Wait-World $V)) { throw "the viewer did not reach the world" }
    Note "$Account in world"
    $param = $null
    $played = Call $V "expression" @{ action = "play"; name = $Expression }
    if (-not $played -or -not $played.sent) { throw ("the expression could not be played: " + (Invoke-Raw $V.port "expression" $V.token (@{ action = "play"; name = $Expression } | ConvertTo-Json -Compress)).Body) }
    Note ("asked the region to play {0} ({1})" -f $Expression, $played.id)
    Start-Sleep -Seconds 5
    $trace = Call $V "expression" @{ action = "trace" }
    [void](Call $V "expression" @{ action = "stop"; name = $Expression })
    Note "asked the region to stop it"
    if (-not $trace) { throw "no trace" }
    $readings = @($trace.trace)
    $start = $trace.start
    $names = @(); foreach ($r in $readings) { foreach ($p in $r.w.PSObject.Properties) { if ($p.Name -ne "Express_Closed_Mouth" -and $names -notcontains $p.Name) { $names += $p.Name } } }
    $param = if ($names.Count -eq 1) { $names[0] } else { $null }
    $rows = @(); $worst = 0.0; $worstMouth = 0.0; $peak = 0.0; $last = $null
    foreach ($r in $readings) {
        $t = [double]$r.t - [double]$start
        $w = if ($param -and $r.w.PSObject.Properties[$param]) { [double]$r.w.$param } else { 0.0 }
        $m = if ($r.w.PSObject.Properties["Express_Closed_Mouth"]) { [double]$r.w.Express_Closed_Mouth } else { 1.0 }
        $done = $t -gt 2.8
        $want = if ($done) { 0.0 } else { Official $t }
        $worst = [math]::Max($worst, [math]::Abs($w - $want)); $worstMouth = [math]::Max($worstMouth, [math]::Abs($m - (1.0 - $want)))
        $peak = [math]::Max($peak, $w); $last = $w
        $rows += [ordered]@{ t = [math]::Round($t, 4); weight = [math]::Round($w, 6); official = [math]::Round($want, 6); closed_mouth = [math]::Round($m, 6) }
    }
    $checks = [ordered]@{
        one_expression_parameter = ($null -ne $param)
        enough_readings = ($rows.Count -ge 10)
        reached_full_weight = ($peak -ge 0.999)
        ended_at_none = ($null -ne $last -and $last -le 0.001 -and -not $trace.active)
        follows_the_official_curve = ($rows.Count -gt 0 -and $worst -le 0.001)
        closed_mouth_is_the_complement = ($rows.Count -gt 0 -and $worstMouth -le 0.001)
        own_body_carries_it = [bool]$trace.own_body
    }
    $verdict = [ordered]@{ pass = (@($checks.Values | Where-Object { -not $_ }).Count -eq 0); checks = $checks; expression = $Expression; parameter = $param
        readings = $rows.Count; largest_difference = [math]::Round($worst, 6); largest_closed_mouth_difference = [math]::Round($worstMouth, 6); peak = $peak }
    ($rows | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}trace.json")
    ($verdict | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $OutDir "${Tag}verdict.json")
    foreach ($k in $checks.Keys) { Note ("{0}: {1}" -f $k, $checks[$k]) }
    Note ("{0} readings, largest difference from the official curve {1:N6}, closed mouth {2:N6}" -f $rows.Count, $worst, $worstMouth)
    if (-not $verdict.pass) { $exit = 1 }
} catch {
    Write-Output ("ERROR " + $_.Exception.Message); $exit = 5
} finally {
    Stop-Client $V
    Start-Sleep -Seconds 5
    foreach ($p in $script:procs) { try { if (-not $p.HasExited) { Wait-Process -Id $p.Id -Timeout 30 -ErrorAction SilentlyContinue } } catch { } }
    Write-Output "LIVE EXPRESSION RUN DONE - other work may run again"
}
exit $exit
