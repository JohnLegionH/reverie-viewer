# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# agent-update-live.ps1 - the agent updates the viewer sends, seen against a stock upstream OpenSim standalone and compared with the
# official viewer's send rule (llviewermessage.cpp send_agent_update). One copy of the development export, one account, the agent
# update trace on (automation: neighbour_debug agent_trace_on). The script starts the stock server (tools\stock-server.ps1) and stops
# it on every way out.
#   1 server     the stock server starts and answers get_grid_info with a login address
#   2 login      the account logs in; the trace writes agent.update.sent lines to the session log (NV_LOG_DIR in the sandbox)
#   3 still      30 s standing still: how many updates, and the spacing (the rule: one at least every second, none more often
#                than every 8 ms, and only on a change)
#   4 orbit      the camera is orbited without turning the body: the head rotation sent stays the body's (outside mouse-look the
#                official head rotation is the avatar's), and the camera turn is what causes the updates
#   5 teleport   a teleport inside the region: no update is sent between its start and its end
#   6 typing     text typed in the local chat bar: the render state byte carries the typing bit while it lasts and clears after it
#   6b burst     the development frame burst (NV_FRAME_BURST=1) writes only under the run's log folder; its frames and bytes are reported
#   7 logout     the viewer logs out and exits; the server is stopped
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input is missing); 4 skipped (a lane claim was not granted).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\agent-update-live.ps1 -Sandbox <new evidence folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-Region <name>] [-GridInfoUrl <url>] [-ServerControl <bash script>]
#     [-Exe <development export>] [-DryRun]
# Settings come from the parameters or the same environment variables stock-smoke.ps1 reads (NV_TEST_ACCOUNTS, NV_STOCK_ACCOUNT_A,
# NV_STOCK_REGION, NV_STOCK_GRID_INFO_URL, NV_STOCK_SERVER_CONTROL, NV_CLAIM_WRAPPER). The accounts file is passed by path and never
# opened here. Only ids, counts and numbers are written, never message text.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $env:NV_STOCK_ACCOUNT_A,
    [string]$Region = $env:NV_STOCK_REGION,
    [string]$GridInfoUrl = $env:NV_STOCK_GRID_INFO_URL,
    [string]$ServerControl = $env:NV_STOCK_SERVER_CONTROL,
    [string]$Exe = "",
    [int]$StartSeconds = 120,
    [int]$StillSeconds = 30,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$godot = Join-Path $PSScriptRoot "godot.ps1"
$serverScript = Join-Path $PSScriptRoot "stock-server.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

function Find-Bash {
    foreach ($candidate in @((Get-Command bash.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
                             (Join-Path $env:ProgramFiles "Git\bin\bash.exe"), (Join-Path $env:ProgramFiles "Git\usr\bin\bash.exe"))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}
function ToUnixPath([string]$p) { $u = $p -replace '\\', '/'; if ($u -match '^([A-Za-z]):(.*)$') { $u = "/" + $Matches[1].ToLower() + $Matches[2] }; return $u }

$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh evidence folder is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not $Account) { $problems.Add("-Account (or NV_STOCK_ACCOUNT_A) is required") }
if (-not $Region -or $Region -notmatch '^[A-Za-z0-9 _-]{1,64}$') { $problems.Add("-Region (or NV_STOCK_REGION) must be a plain region name") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_STOCK_GRID_INFO_URL) is required") }
if (-not $ServerControl -or -not (Test-Path -LiteralPath $ServerControl -PathType Leaf)) { $problems.Add("-ServerControl (or NV_STOCK_SERVER_CONTROL) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (build the development export): $Exe") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

# the lane claims: this script runs again under an avatar claim for the account and a build claim on this checkout
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_AGENT_LIVE_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "agent-update-live") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials,
        "-Account", $Account, "-Region", $Region, "-GridInfoUrl", $GridInfoUrl, "-ServerControl", $ServerControl, "-Exe", $Exe,
        "-StartSeconds", "$StartSeconds", "-StillSeconds", "$StillSeconds")
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $cmd = @("bash", $wrapper) + $order + @("--resource", $Account, $lane, "avatar", "agent update live", "--",
            "bash", $wrapper) + $order + @("--resource", (ToUnixPath $repoRoot), $lane, "build", "agent update live", "--") + $inner
    if ($DryRun) { Write-Output ("would run under claims: " + ($cmd -join " ")); Write-Output "dry run: nothing started, nothing logged in"; exit 0 }
    $env:NV_AGENT_LIVE_CLAIMED = "1"
    if (-not $env:CLAIM_WAIT) { $env:CLAIM_WAIT = "30" }
    $bash = Find-Bash
    if (-not $bash) { Write-Output "BLOCKED: bash not found, so the claim wrapper cannot run"; exit 3 }
    & $bash @($cmd[1..($cmd.Count - 1)])
    $code = $LASTEXITCODE
    if ($code -eq 125) { Write-Output "SKIPPED: a claim was not granted within CLAIM_WAIT minutes; nothing was started"; exit 4 }
    exit $code
}
if ($DryRun) { Write-Output "dry run: nothing started, nothing logged in"; exit 0 }

. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { Write-Output "BLOCKED: a viewer that can log in is already running ($($viewers.Count)): one account in one session at a time"; exit 3 }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$logDir = Join-Path $Sandbox "viewer-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$textLog = Join-Path $Sandbox "agent-live.txt"
$report = [ordered]@{ started_utc = (Get-Date).ToUniversalTime().ToString("o"); steps = @() }
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
function Record([string]$n, [string]$name, [bool]$pass, [string]$detail) {
    $script:report.steps += [ordered]@{ step = $n; name = $name; pass = $pass; detail = $detail }
    Say ("STEP {0} {1}: {2}  {3}" -f $n, $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail)
}
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
function Save-Json([string]$name, $obj) { $obj | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox $name) -Encoding utf8 }
function Server([string]$action) {
    $o = Join-Path $Sandbox "server-$action.out"; $e = Join-Path $Sandbox "server-$action.err"
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $p = Start-Process -FilePath "powershell" -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$serverScript`"", "-Action", $action, "-Control", "`"$ServerControl`"",
        "-GridInfoUrl", "`"$GridInfoUrl`"", "-Lane", $lane, "-LogFolder", "`"$Sandbox`"")
    $null = $p.Handle
    $p.WaitForExit()
    foreach ($f in @($o, $e)) { if (Test-Path -LiteralPath $f) { Get-Content -LiteralPath $f -ErrorAction SilentlyContinue | ForEach-Object { Add-Content -LiteralPath $textLog -Value "$_" -Encoding utf8 } } }
    return $p.ExitCode
}

# the session log of this run: its lines from <from> on, as objects (kind, time, and the fields of an agent update)
function Session-Lines() {
    $f = Get-ChildItem -LiteralPath $logDir -Filter "*.log" -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $f) { return @() }
    $fs = [System.IO.File]::Open($f.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try { return @((New-Object System.IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" | Where-Object { $_ }) } finally { $fs.Close() }
}
function Session-Raw() { Session-Lines }
function Updates-Since([int]$from) {
    $all = Session-Lines
    $out = @()
    for ($i = $from; $i -lt $all.Count; $i++) {
        $l = $all[$i]
        if ($l -match ' agent\.update\.sent .*connected=true') {
            $t = [int64]([regex]::Match($l, 't_ms=(\d+)').Groups[1].Value)
            $out += [pscustomobject]@{ Kind = "sent"; T = $t; State = [int]([regex]::Match($l, 'state=(\d+)').Groups[1].Value);
                Flags = [int]([regex]::Match($l, 'flags=(\d+)').Groups[1].Value); Dot = [double]([regex]::Match($l, 'head_body_dot=([\d.]+)').Groups[1].Value);
                Controls = [int64]([regex]::Match($l, 'controls=(\d+)').Groups[1].Value) }
        }
        elseif ($l -match ' agent\.updates .*sending=(true|false)') { $out += [pscustomobject]@{ Kind = "gate"; Open = ($Matches[1] -eq "true") } }
    }
    return $out
}

$A = $null; $allPass = $true; $loginUri = $null; $serverStarted = $false
function Step([string]$n, [string]$name, [scriptblock]$body) {
    $r = $null
    try { $r = & $body }
    catch { $r = @{ pass = $false; detail = ("error at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) } }
    Record $n $name ([bool]$r.pass) ([string]$r.detail)
    if (-not $r.pass) { $script:allPass = $false }
}

try {
    Step "1" "server" {
        $code = Server "start"
        $script:serverStarted = ($code -ne 1)
        if ($code -eq 0) {
            try { $req = [System.Net.WebRequest]::Create($GridInfoUrl); $req.Timeout = 10000; $resp = $req.GetResponse()
                  try { $script:loginUri = ([xml](New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd()).gridinfo.login } finally { $resp.Close() } } catch { }
        }
        @{ pass = ($code -eq 0 -and [bool]$script:loginUri); detail = "start exit $code; login address answered $([bool]$script:loginUri)" }
    }
    if ($script:loginUri) {
        $env:NV_LOG_DIR = $logDir
        $env:NV_FRAME_BURST = "1"       # development exports only; a burst runs only when asked for (the automation command) or at a region crossing
        $env:NV_TEST_WINDOW_FULL = "1"  # a full-size window, so the burst's frames have the size a person's window gives
        $local = Join-Path $Sandbox "localappdata"; $appdata = Join-Path $Sandbox "appdata"
        New-Item -ItemType Directory -Force -Path $appdata | Out-Null
        $vlog = Join-Path $Sandbox "viewer.log"
        $vp = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
            "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "1500", "-HangSnapshotSeconds", "0",
            "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$vlog`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
        $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
        if (-not $tf) { throw "the viewer has no token file" }
        Start-Sleep -Milliseconds 300
        $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
        $A = @{ Proc = $vp; Port = [int]$j.port; Token = [string]$j.token }

        Step "2" "login" {
            $l = Invoke-Api $A "session" @{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $script:loginUri; start = ("uri:" + $Region + "&128&128&25") }
            if ($l.Status -ne 200) { return @{ pass = $false; detail = "login request answered $($l.Status)" } }
            $st = Wait-Until { $s = Api $A "session" '{"action":"state"}'; if ($s -and $s.has_session -and -not $s.ended -and $s.stage -eq "InWorld") { $s } } 150
            if (-not $st) { return @{ pass = $false; detail = "the world was not reached" } }
            $null = Api $A "neighbour_debug" '{"action":"agent_trace_on"}'
            $ok = Wait-Until { @(Updates-Since 0 | Where-Object { $_.Kind -eq "sent" }).Count -gt 0 } 20
            @{ pass = [bool]$ok; detail = "in world; trace writes agent.update.sent lines: $([bool]$ok)" }
        }

        Step "3" "still" {
            $mark = (Session-Lines).Count
            Start-Sleep -Seconds $StillSeconds
            $u = @(Updates-Since $mark | Where-Object { $_.Kind -eq "sent" })
            if ($u.Count -lt 2) { return @{ pass = $false; detail = "only $($u.Count) updates in $StillSeconds s" } }
            $gaps = @(); for ($i = 1; $i -lt $u.Count; $i++) { $gaps += ($u[$i].T - $u[$i - 1].T) }
            $min = ($gaps | Measure-Object -Minimum).Minimum; $max = ($gaps | Measure-Object -Maximum).Maximum; $avg = [math]::Round(($gaps | Measure-Object -Average).Average, 0)
            Save-Json "still-gaps.json" @{ count = $u.Count; gaps_ms = $gaps }
            # the rule: one at least every second (a little over, as the next frame after it), never closer than 8 ms
            @{ pass = ($u.Count -ge ($StillSeconds - 3) -and $u.Count -le ($StillSeconds + 3) -and $min -ge 8 -and $max -le 1100); detail = "$($u.Count) updates in $StillSeconds s; spacing ms min $min, mean $avg, max $max" }
        }

        Step "4" "orbit" {
            $mark = (Session-Lines).Count
            foreach ($yaw in 20, 40, 60, 80, 100, 120, 140) { $null = Api $A "orbit" @{ yaw_deg = $yaw; pitch_deg = 10 }; Start-Sleep -Milliseconds 400 }
            Start-Sleep -Seconds 1
            $u = @(Updates-Since $mark | Where-Object { $_.Kind -eq "sent" })
            if ($u.Count -eq 0) { return @{ pass = $false; detail = "no update while the camera orbited" } }
            $minDot = ($u | Measure-Object -Property Dot -Minimum).Minimum
            @{ pass = ($minDot -ge 0.99999); detail = "$($u.Count) updates during a 120 degree orbit; head/body rotation dot product min $minDot (1 = the head follows the body, as the official viewer sends outside mouse-look)" }
        }

        Step "5" "teleport" {
            $mark = (Session-Lines).Count
            $t = Api $A "teleport" @{ action = "region"; region = $Region; x = 100; y = 100; z = 25 }
            $done = Wait-Until { $s = Api $A "teleport" '{"action":"status"}'; if ($s -and $s.phase -in @("Idle", "idle", "None")) { $s } } 30
            Start-Sleep -Seconds 2
            $seq = @(Updates-Since $mark)
            $held = $false; $inside = 0; $closed = 0; $opened = 0
            foreach ($e in $seq) {
                if ($e.Kind -eq "gate") { if ($e.Open) { $held = $false; $opened++ } else { $held = $true; $closed++ } }
                elseif ($held) { $inside++ }
            }
            @{ pass = ($closed -ge 1 -and $opened -ge 1 -and $inside -eq 0); detail = "gate closed $closed, reopened $opened; updates sent while the teleport was in progress: $inside; updates around it: $(@($seq | Where-Object { $_.Kind -eq 'sent' }).Count)" }
        }

        Step "6" "typing" {
            $null = Api $A "typing" '{"action":"focus"}'
            $mark = (Session-Lines).Count
            $null = Api $A "typing" '{"action":"text","text":"typing for the trace"}'
            Start-Sleep -Seconds 3
            $during = @(Updates-Since $mark | Where-Object { $_.Kind -eq "sent" })
            $null = Api $A "typing" '{"action":"text","text":""}'
            Start-Sleep -Seconds 2
            $all = @(Updates-Since $mark | Where-Object { $_.Kind -eq "sent" })
            $typingSeen = @($during | Where-Object { ($_.State -band 4) -ne 0 }).Count
            $last = $all | Select-Object -Last 1
            $null = Api $A "typing" '{"action":"release"}'
            @{ pass = ($typingSeen -ge 1 -and $last -and ($last.State -band 4) -eq 0); detail = "updates while typing $($during.Count), with the typing bit (state 4): $typingSeen; the last update after the text was cleared has state $($last.State)" }
        }

        Step "6b" "burst" {
            $eng = Api $A "engine" '{}'
            $r = Invoke-Api $A "frame_burst" '{}'
            if ($r.Status -ne 200) { return @{ pass = $false; detail = "frame_burst answered $($r.Status): $($r.Body)" } }
            $saved = Wait-Until { @(Session-Raw | Where-Object { $_ -match ' frame_burst\.saved ' }).Count -gt 0 } 30
            $files = @(Get-ChildItem -LiteralPath (Join-Path $logDir "frame-burst") -Recurse -Filter "frame-*.png" -ErrorAction SilentlyContinue)
            $bytes = ($files | Measure-Object -Property Length -Sum).Sum
            $outside = @(Get-ChildItem -LiteralPath $Sandbox -Recurse -Filter "frame-*.png" -ErrorAction SilentlyContinue | Where-Object { -not $_.FullName.StartsWith($logDir, [System.StringComparison]::OrdinalIgnoreCase) }).Count
            $win = if ($eng -and $eng.window) { ($eng.window | ConvertTo-Json -Compress) } else { "unknown" }
            Save-Json "burst.json" @{ frames = $files.Count; bytes = $bytes; window = $win }
            @{ pass = ([bool]$saved -and $files.Count -gt 0 -and $outside -eq 0); detail = "frames saved $($files.Count), $([math]::Round($bytes / 1MB, 1)) MiB in all, $(if ($files.Count) { [math]::Round($bytes / $files.Count / 1KB, 0) } else { 0 }) KiB a frame; window $win; saved line seen $([bool]$saved)" }
        }

        Step "7" "logout" {
            $null = Api $A "session" '{"action":"logout"}'
            $ended = Wait-Until { $s = Api $A "session" '{"action":"state"}'; if ($s -and (-not $s.has_session -or $s.ended -or $s.stage -eq "Idle")) { $s } } 60
            $null = Invoke-Api $A "quit" '{}'
            $exited = Wait-Until { $A.Proc.HasExited } 60
            @{ pass = ([bool]$ended -and [bool]$exited); detail = "session ended $([bool]$ended); viewer exited $([bool]$exited)" }
        }
    }
}
finally {
    if ($A -and $A.Proc -and -not $A.Proc.HasExited) {
        try { $null = Api $A "quit" '{}' } catch { }
        $null = $A.Proc.WaitForExit(30000)
    }
    if ($serverStarted) {
        $code = Server "stop"
        Say ("server stop exit $code")
    }
    Save-Json "agent-live.json" $report
}
if ($allPass) { exit 0 } else { exit 1 }
