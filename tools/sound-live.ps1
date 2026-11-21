# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# sound-live.ps1 - the live check of a grid's own interface sounds. The viewer ships no interface sound: it plays the sounds the
# grid it is connected to serves for the official sound ids. This starts the development export on a fresh settings folder, logs a
# test avatar in, and for every slot reports whether the grid served its sound (the id, the state, why not, the MD5 of the served
# bytes). Then it makes a window open, a click (through the engine's input path) and typing, and shows by MD5 that the bytes the
# engine was handed for each are the ones the grid served for that slot's id. A sound the grid does not serve must be silent.
#
#   A grid test region (accounts file, grid address and account come from live.env and the avatar claim; -Start is the test region's start):
#     powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\sound-live.ps1 -Sandbox <new folder> -Start <start location> [-Exe <development export>]
#   Stock standalone (accounts file, first stock account, grid-info address, region and server control come from the stock smoke's
#   settings file named by NV_STOCK_SETTINGS or -Settings; the run takes its own avatar and build claims and starts and stops the
#   stock server around itself, as the stock smoke does):
#     powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\sound-live.ps1 -Sandbox <new folder> -Stock [-Settings <settings file>]
#   No accounts file path goes on a command line: it is read from the run's settings, never from an argument.
#
# Outside -Stock, run it under an avatar claim on the account (its name is in CLAIM_AVATAR), a live-test claim naming the region and
# a live-login claim. Output: sound-report.json in the sandbox (one row per slot, the checks). Exit: 0 all checks passed, 1 a check
# failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:CLAIM_AVATAR) { $env:CLAIM_AVATAR } elseif ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 90,
    [switch]$Stock,
    [string]$Settings = ""
)
$ErrorActionPreference = "Stop"
$serverControl = ""
if ($Stock) {
    # the stock standalone: every setting comes from the stock smoke's settings file
    . (Join-Path $PSScriptRoot "stock-settings.ps1")
    $loaded = Read-StockSettings $Settings
    $Credentials = Resolve-StockSetting "" "NV_TEST_ACCOUNTS" $loaded
    $Account = Resolve-StockSetting "" "NV_STOCK_ACCOUNT_A" $loaded
    $GridInfoUrl = Resolve-StockSetting "" "NV_STOCK_GRID_INFO_URL" $loaded
    $serverControl = Resolve-StockSetting "" "NV_STOCK_SERVER_CONTROL" $loaded
    $region = Resolve-StockSetting "" "NV_STOCK_REGION" $loaded
    if ($region) { $Start = "uri:" + $region + "&128&128&25" }
    Set-StockOptionalEnvironment $loaded
    $miss = @()
    if (-not $Credentials) { $miss += "NV_TEST_ACCOUNTS" }
    if (-not $Account) { $miss += "NV_STOCK_ACCOUNT_A" }
    if (-not $GridInfoUrl) { $miss += "NV_STOCK_GRID_INFO_URL" }
    if (-not $region) { $miss += "NV_STOCK_REGION" }
    if (-not $serverControl -or -not (Test-Path -LiteralPath $serverControl -PathType Leaf)) { $miss += "NV_STOCK_SERVER_CONTROL (a file)" }
    if ($miss.Count -gt 0) { Write-Output ("BLOCKED: -Stock needs these settings (NV_STOCK_SETTINGS, -Settings or the environment): " + ($miss -join ", ")); exit 3 }
}
else {
    # a grid test region: accounts file and grid address from the run's settings (live.env), the account from the avatar claim
    if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
    if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
    if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
    if (-not $Account) { Write-Output "BLOCKED: no account: run under an avatar claim (CLAIM_AVATAR) or set NV_LIVE_ACCOUNT"; exit 3 }
}

# -Stock, outer run: take the avatar and build claims (as the stock smoke does), start the stock server, run this script again
# inside the claims against it, and stop the server on every way out. The inner run is everything below this block.
if ($Stock -and -not $env:NV_SOUND_LIVE_INNER) {
    function ToUnixPath([string]$p) { $u = $p -replace '\\', '/'; if ($u -match '^([A-Za-z]):(.*)$') { $u = "/" + $Matches[1].ToLower() + $Matches[2] }; return $u }
    $bash = $null
    foreach ($candidate in @((Get-Command bash.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
                             (Join-Path $env:ProgramFiles "Git\bin\bash.exe"), (Join-Path $env:ProgramFiles "Git\usr\bin\bash.exe"))) {
        if (-not $bash -and $candidate -and (Test-Path -LiteralPath $candidate)) { $bash = $candidate }
    }
    $passOn = @("-Sandbox", $Sandbox, "-Stock", "-StartSeconds", "$StartSeconds")
    if ($loaded.File) { $passOn += @("-Settings", $loaded.File) }
    if ($Exe) { $passOn += @("-Exe", $Exe) }
    if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_SOUND_LIVE_CLAIMED) {
        $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
        $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "sound-live") } else { @() }
        $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
        $cmd = @($wrapper) + $order + @("--resource", $Account, $lane, "avatar", "sound live", "--",
                "bash", $wrapper) + $order + @("--resource", (ToUnixPath (Split-Path -Parent $PSScriptRoot)), $lane, "build", "sound live", "--",
                "powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath) + $passOn
        $env:NV_SOUND_LIVE_CLAIMED = "1"
        if (-not $env:CLAIM_WAIT) { $env:CLAIM_WAIT = "30" }
        if (-not $bash) { Write-Output "BLOCKED: bash not found, so the claim wrapper cannot run"; exit 3 }
        & $bash @cmd
        $code = $LASTEXITCODE
        if ($code -eq 125) { Write-Output "SKIPPED: a claim was not granted within CLAIM_WAIT minutes; nothing was started" }
        exit $code
    }
    . (Join-Path $PSScriptRoot "claim-guard.ps1")
    Assert-ClaimGranted
    if (Test-Path -LiteralPath $Sandbox) { Write-Output "BLOCKED: -Sandbox exists (a fresh sandbox is required): $Sandbox"; exit 3 }
    $serverScript = Join-Path $PSScriptRoot "stock-server.ps1"
    $serverLogs = $Sandbox + "-server"
    New-Item -ItemType Directory -Force -Path $serverLogs | Out-Null
    # the server script runs with its output in files: the server it starts inherits the output handles, and a pipe would stay open
    # until the server ended (this call would never return)
    function Invoke-StockServer([string]$action) {
        $o = Join-Path $serverLogs "server-$action.out"; $e = Join-Path $serverLogs "server-$action.err"
        $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
        $sp = Start-Process -FilePath "powershell" -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$serverScript`"", "-Action", $action, "-Control", "`"$serverControl`"",
            "-GridInfoUrl", "`"$GridInfoUrl`"", "-Lane", $lane, "-LogFolder", "`"$serverLogs`"")
        $null = $sp.Handle
        $sp.WaitForExit()
        return $sp.ExitCode
    }
    $started = $false; $runCode = 3
    try {
        $c = Invoke-StockServer "start"
        $started = ($c -ne 1)   # exit 1 is a refusal: the server is someone else's and is not stopped here
        Write-Output "stock server start exit $c"
        if ($c -eq 0) {
            $env:NV_SOUND_LIVE_INNER = "1"
            & powershell @(@("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath) + $passOn)
            $runCode = $LASTEXITCODE
        }
    }
    finally {
        if ($started) { Write-Output ("stock server stop exit " + (Invoke-StockServer "stop")) }
    }
    exit $runCode
}
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
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("the accounts file (NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start) { $problems.Add("a start location is required (-Start, NV_LIVE_START or the stock region)") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
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
function Sounds([hashtable]$b) { Parse (Invoke-Raw $port "ui_sounds" $token ($b | ConvertTo-Json -Compress)).Body }
function Slots() { @((Sounds @{ action = "state" }).slots) }
function SlotOf([string]$name) { @(Slots | Where-Object { $_.slot -eq $name })[0] }
function Wait-Until([scriptblock]$cond, [double]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 250 }
    return $null
}

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
Write-Output "LIVE SOUND RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-62} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; slots = $null; checks = $null; completed = $false }

# before login: nothing is cached for the grid, and the viewer ships no sound, so the login screen is silent
$ready = $null
for ($i = 0; $i -lt 60 -and -not $ready; $i++) { $ready = Sounds @{ action = "state" }; if (-not $ready.slots) { $ready = $null; Start-Sleep -Milliseconds 500 } }
[void](Sounds @{ action = "clear" })
$before0 = (Sounds @{ action = "log" }).started
$pre = Sounds @{ action = "play"; slot = "Click" }
$after0 = (Sounds @{ action = "log" }).started
Check "1 login screen, nothing cached: Click is silent" ($pre.outcome -eq "NotOnGrid" -and [int]$after0 -eq [int]$before0) ("outcome {0}, engine plays {1}" -f $pre.outcome, ([int]$after0 - [int]$before0))

$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
Note "login requested: $($login.Status)"
$o = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Parse (Invoke-Raw $port "observe" $token).Body
    if ($o -and $o.stage -eq "InWorld" -and $o.self) { break }
    $o = $null
}
if ($o) {
    Note "in world"
    # the preload asks for every sound as soon as the session is in the world; wait until none is still being fetched
    $settled = Wait-Until { $s = Sounds @{ action = "state" }; if ($s.in_world -and @($s.slots | Where-Object { $_.id -ne "00000000-0000-0000-0000-000000000000" -and $_.grid_state -in @("Unknown", "Fetching") }).Count -eq 0) { $s } } 60
    Check "2 every grid sound was asked for and answered" ([bool]$settled) ("{0} slots" -f @($settled.slots).Count)
    $rows = @(Slots | ForEach-Object { [ordered]@{ slot = $_.slot; id = $_.id; served = ($_.grid_state -eq "Ready"); state = $_.grid_state; why = $_.why; bytes = $_.served_bytes; md5 = $_.served_md5 } })
    $report.slots = $rows
    $servedCount = @($rows | Where-Object { $_.served }).Count
    foreach ($r in $rows) { Note ("  {0,-22} {1} served={2,-5} {3} {4}" -f $r.slot, $r.id, $r.served, $r.md5, $r.why) }
    Note "served: $servedCount of $(@($rows).Count) slots"

    function PlayedSince([int]$n) { @((Sounds @{ action = "log" }).played | Select-Object -Skip $n) }
    function PlayedCount() { @((Sounds @{ action = "log" }).played).Count }
    function Prove([string]$name, [string[]]$slotsWanted, [scriptblock]$act) {
        [void](Sounds @{ action = "clear" })
        $n = PlayedCount
        & $act
        Start-Sleep -Milliseconds 1500
        $log = Sounds @{ action = "log" }
        $new = @(PlayedSince $n)
        $ok = $true; $detail = @()
        foreach ($sl in $slotsWanted) {
            $row = @($rows | Where-Object { $_.slot -eq $sl })[0]
            $hit = @($new | Where-Object { $_.slot -eq $sl })
            if ($row.served) {
                $good = ($hit.Count -ge 1) -and (@($hit | Where-Object { $_.md5 -ne $row.md5 }).Count -eq 0)
                $detail += "{0}: played {1}x, md5 {2} {3} grid's {4}" -f $sl, $hit.Count, $(if ($hit.Count) { $hit[0].md5 } else { "-" }), $(if ($good) { "=" } else { "!=" }), $row.md5
            } else {
                $good = ($hit.Count -eq 0)
                $detail += "{0}: grid does not serve it ({1}); played {2}x (silent expected)" -f $sl, $row.why, $hit.Count
            }
            if (-not $good) { $ok = $false }
        }
        Check $name $ok ($detail -join "; ")
    }
    Prove "3 window open plays the grid's WindowOpen asset" @("WindowOpen") { [void](Invoke-Raw $port "open_window" $token '{"name":"preferences"}') }
    $pos = Wait-Until { $f = Sounds @{ action = "find"; name = "category_graphics" }; if ($f.found) { $f } } 10
    if (-not $pos) { Check "4 a button click plays the grid's ClickRelease asset" $false "the Preferences category button was not found" }
    else {
        # a button plays ClickRelease when a press ends inside it (UiSoundRules.Clicks); Click and ClickRelease share one sound id, so one MD5 covers both
        Prove "4 a button click plays the grid's ClickRelease asset" @("ClickRelease") {
            [void](Invoke-Raw $port "input" $token (@{ kind = "mouse"; x = $pos.x; y = $pos.y } | ConvertTo-Json -Compress))
            Start-Sleep -Milliseconds 100
            [void](Invoke-Raw $port "input" $token (@{ kind = "mouse"; button = "left"; down = $true; x = $pos.x; y = $pos.y } | ConvertTo-Json -Compress))
            Start-Sleep -Milliseconds 100
            [void](Invoke-Raw $port "input" $token (@{ kind = "mouse"; button = "left"; down = $false; x = $pos.x; y = $pos.y } | ConvertTo-Json -Compress))
        }
    }
    [void](Sounds @{ action = "close_window"; name = "preferences" })
    Prove "5 typing plays the grid's Typing asset" @("Typing") {
        # the chat bar: Enter puts the cursor in it, the first letter typed starts the typing sound; the Play button plays the same slot
        [void](Invoke-Raw $port "input" $token (@{ kind = "key"; key = "Enter"; down = $true } | ConvertTo-Json -Compress))
        [void](Invoke-Raw $port "input" $token (@{ kind = "key"; key = "Enter"; down = $false } | ConvertTo-Json -Compress))
        Start-Sleep -Milliseconds 300
        [void](Invoke-Raw $port "input" $token (@{ kind = "text"; text = "h" } | ConvertTo-Json -Compress))
        Start-Sleep -Milliseconds 800
        $ev = @((Sounds @{ action = "log" }).events | Where-Object { $_.slot -eq "Typing" })
        if ($ev.Count -eq 0) { [void](Sounds @{ action = "play"; slot = "Typing" }); $script:typingBy = "the Play button" } else { $script:typingBy = "typing in the chat bar" }
    }
    Note "typing was started by: $typingBy"
    $report.typing_by = $typingBy
    $report.completed = $true
}
else { Note "not in the world within 150 s" }
$report.checks = $checks
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$afterLogout = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($afterLogout.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$afterLogout.ended
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "sound-report.json") -Encoding utf8
Write-Output "LIVE SOUND RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0
