# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# grids-live.ps1 - the live check of a grid unlike the one the viewer is developed against: a region with no inventory API, and a
# region larger than 256 m with a 256 m neighbour. It starts the stock server (tools\stock-server.ps1), runs one development export
# (automation on) logged in with one account, and stops the server on every way out. Each step reports PASS or FAIL with figures.
#   1 server      the server starts and answers get_grid_info with a login address
#   2 login       the account arrives in the small region
#   3 inventory   the inventory check by real mouse and key events (inventory_view noais): the region has no inventory API; the window
#                 opened with its key, every folder opened, a wearable worn and taken off, a folder made, an item moved into it, the
#                 folder renamed, the look saved as an outfit and worn, and everything put back (made folders to the Trash, never purged)
#   4 large       a teleport into the large region: the session, the map and the mini-map give its true size
#   5 walk        the avatar walks west across the large region (W held, through the engine) until it crosses or stops at its edge;
#                 then, from the small region's east side, it walks east across the border into the large region, whose size it then holds
#   6 logout      the account logs out and the viewer exits
#   7 server      the server is stopped
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input is missing); 4 skipped (a lane claim was not granted).
#
#   powershell -NoProfile -File <repo>\tools\grids-live.ps1 -Sandbox <new evidence folder> [-Account <alias>] [-Region <small region>]
#     [-LargeRegion <large region>] [-LargeSize 512] [-TestName "Test Folder"] [-Exe <development export>]
#
# Settings as tools\stock-smoke.ps1 reads them (parameters, then NV_* environment variables, then the settings file NV_STOCK_SETTINGS):
# NV_TEST_ACCOUNTS, NV_STOCK_ACCOUNT_A (the account), NV_STOCK_REGION (the small region), NV_STOCK_GRID_INFO_URL, NV_STOCK_SERVER_CONTROL.
# The large region (default "Stock Large Region") must lie east of the small one and touch it. Claims as the smoke takes them: an avatar
# claim for the account and a build claim on this checkout, through NV_CLAIM_WRAPPER. Only ids, counts and flags are written.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $env:NV_STOCK_ACCOUNT_A,
    [string]$Region = $env:NV_STOCK_REGION,
    [string]$LargeRegion = "Stock Large Region",
    [int]$LargeSize = 512,
    [string]$TestName = "Test Folder",
    [string]$GridInfoUrl = $env:NV_STOCK_GRID_INFO_URL,
    [string]$ServerControl = $env:NV_STOCK_SERVER_CONTROL,
    [string]$Exe = "",
    [string]$Settings = "",
    [int]$StartSeconds = 150
)
$ErrorActionPreference = "Stop"
$godot = Join-Path $PSScriptRoot "godot.ps1"
$serverScript = Join-Path $PSScriptRoot "stock-server.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }
function ToUnixPath([string]$p) { $u = $p -replace '\\', '/'; if ($u -match '^([A-Za-z]):(.*)$') { $u = "/" + $Matches[1].ToLower() + $Matches[2] }; return $u }

. (Join-Path $PSScriptRoot "stock-settings.ps1")
$loaded = Read-StockSettings $Settings
$Credentials = Resolve-StockSetting $Credentials "NV_TEST_ACCOUNTS" $loaded
$Account = Resolve-StockSetting $Account "NV_STOCK_ACCOUNT_A" $loaded
$Region = Resolve-StockSetting $Region "NV_STOCK_REGION" $loaded
$GridInfoUrl = Resolve-StockSetting $GridInfoUrl "NV_STOCK_GRID_INFO_URL" $loaded
$ServerControl = Resolve-StockSetting $ServerControl "NV_STOCK_SERVER_CONTROL" $loaded
Set-StockOptionalEnvironment $loaded
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh evidence folder is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not $Account) { $problems.Add("-Account (or NV_STOCK_ACCOUNT_A) is required") }
foreach ($r in @($Region, $LargeRegion)) { if (-not $r -or $r -notmatch '^[A-Za-z0-9 _-]{1,64}$') { $problems.Add("region names must be plain names") } }
if ($TestName -notmatch '^[A-Za-z0-9 ]{1,40}$') { $problems.Add("-TestName must be letters, digits and spaces") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_STOCK_GRID_INFO_URL) is required") }
if (-not $ServerControl -or -not (Test-Path -LiteralPath $ServerControl -PathType Leaf)) { $problems.Add("-ServerControl (or NV_STOCK_SERVER_CONTROL) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (build the development export): $Exe") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

# ---------------- the lane claims: run this script again under them ----------------
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_GRIDS_LIVE_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "grids-live") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials, "-Account", $Account,
        "-Region", $Region, "-LargeRegion", $LargeRegion, "-LargeSize", "$LargeSize", "-TestName", $TestName, "-GridInfoUrl", $GridInfoUrl,
        "-ServerControl", $ServerControl, "-Exe", $Exe, "-StartSeconds", "$StartSeconds")
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $cmd = @($wrapper) + $order + @("--resource", $Account, $lane, "avatar", "grids live", "--",
            "bash", $wrapper) + $order + @("--resource", (ToUnixPath $repoRoot), $lane, "build", "grids live", "--") + $inner
    $env:NV_GRIDS_LIVE_CLAIMED = "1"
    if (-not $env:CLAIM_WAIT) { $env:CLAIM_WAIT = "30" }
    & bash @cmd
    $code = $LASTEXITCODE
    if ($code -eq 125) { Write-Output "SKIPPED: a claim was not granted; nothing was started"; exit 4 }
    exit $code
}
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { Write-Output "BLOCKED: a viewer that can log in is already running"; exit 3 }

# ---------------- helpers ----------------
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$report = [ordered]@{ started_utc = (Get-Date).ToUniversalTime().ToString("o"); steps = @() }
$textLog = Join-Path $Sandbox "grids.txt"
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
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
function State($v) { Api $v "session" '{"action":"state"}' }
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }
    return $null
}
function Save-Json([string]$name, $obj) { $obj | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox $name) -Encoding utf8 }
function Shot($v, [string]$name) { try { [void](Api $v "screenshot" @{ dir = $Sandbox; name = $name }) } catch { } }
function Key($v, [string]$k, [bool]$down) { [void](Api $v "input" @{ kind = "key"; key = $k; down = $down; shift = $false; alt = $false; ctrl = $false }) }
function Heading($v) { $o = Api $v "observe" '{}'; if ($o -and $o.self) { [double]$o.self.region_heading_deg } else { $null } }
function Face($v, [double]$deg) {
    for ($k = 0; $k -lt 40; $k++) {
        $h = Heading $v
        if ($null -eq $h) { return $false }
        $d = (($deg - $h + 540) % 360) - 180
        if ([math]::Abs($d) -lt 10) { return $true }
        $turn = if ($d -gt 0) { "A" } else { "D" }
        Key $v $turn $true; Start-Sleep -Milliseconds ([math]::Min(600, [math]::Max(120, [math]::Abs($d) * 4))); Key $v $turn $false; Start-Sleep -Milliseconds 200
    }
    return $false
}
function Server([string]$action) {
    $o = Join-Path $Sandbox "server-$action.out"; $e = Join-Path $Sandbox "server-$action.err"
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $p = Start-Process -FilePath "powershell" -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e -ArgumentList @(
        "-NoProfile", "-File", "`"$serverScript`"", "-Action", $action, "-Control", "`"$ServerControl`"",
        "-GridInfoUrl", "`"$GridInfoUrl`"", "-Lane", $lane, "-LogFolder", "`"$Sandbox`"")
    $null = $p.Handle
    $p.WaitForExit()
    return $p.ExitCode
}
function Stop-Tree([int]$rootId) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($rootId)
    for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $all) { if ([int]$c.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$c.ProcessId)) { $ids.Add([int]$c.ProcessId) } } }
    for ($i = $ids.Count - 1; $i -ge 0; $i--) { Stop-Process -Id $ids[$i] -Force -ErrorAction SilentlyContinue }
}
$allPass = $true
function Step([string]$n, [string]$name, [scriptblock]$body) {
    $r = $null
    try { $r = & $body }
    catch { $r = @{ pass = $false; detail = ("error at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) } }
    $script:report.steps += [ordered]@{ step = $n; name = $name; pass = [bool]$r.pass; detail = [string]$r.detail }
    Say ("STEP {0} {1}: {2}  {3}" -f $n, $name, $(if ($r.pass) { "PASS" } else { "FAIL" }), $r.detail)
    if (-not $r.pass) { $script:allPass = $false }
}

$V = $null; $vp = $null; $loginUri = $null; $serverStarted = $false
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
    if ($loginUri) {
        $local = Join-Path $Sandbox "localappdata"; $appdata = Join-Path $Sandbox "appdata"
        New-Item -ItemType Directory -Force -Path $appdata | Out-Null
        $vlog = Join-Path $Sandbox "viewer.log"
        $vp = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
            "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "1800", "-HangSnapshotSeconds", "0",
            "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$vlog`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
        $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
        if (-not $tf) { throw "the viewer has no token file" }
        Start-Sleep -Milliseconds 300
        $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
        $V = @{ Port = [int]$j.port; Token = [string]$j.token }

        Step "2" "login" {
            $l = Invoke-Api $V "session" @{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $script:loginUri; start = ("uri:" + $Region + "&128&128&25") }
            $st = Wait-Until { $s = State $V; if ($s -and $s.has_session -and $s.stage -eq "InWorld") { $s } } 150
            @{ pass = ($l.Status -eq 200 -and [bool]$st); detail = "login status $($l.Status); in world $([bool]$st); region size $($st.region.size_x)x$($st.region.size_y)" }
        }

        Step "3" "inventory without the inventory API (real input)" {
            [void](Wait-Until { $i = Api $V "inventory" @{ action = "status" }; if ($i -and $i.skeleton) { $i } } 60)
            $start = Api $V "inventory_view" @{ action = "noais"; folder = $TestName }
            if (-not $start.started) { return @{ pass = $false; detail = "the check did not start: $($start | ConvertTo-Json -Compress)" } }
            # the check runs inside the viewer for minutes; its answer is read until it is done
            $r = Wait-Until { $x = Api $V "inventory_view" @{ action = "noais_result" }; if ($x -and -not $x.running) { $x } } 900
            if (-not $r) { return @{ pass = $false; detail = "the check did not finish in 900 s" } }
            Save-Json "inventory.json" $r
            Shot $V "inventory"
            $lines = @($r.steps | ForEach-Object { "{0}={1}" -f $_.name, $(if ($_.ok) { "ok" } else { "FAIL " + ($_.detail | ConvertTo-Json -Compress -Depth 4) }) })
            @{ pass = [bool]$r.ok; detail = ($lines -join "; ") }
        }

        Step "4" "large region size (session, map, mini-map)" {
            $t = Api $V "teleport" @{ action = "region"; region = $LargeRegion; x = ($LargeSize - 24); y = ($LargeSize / 2); z = 25 }
            $st = Wait-Until { $s = State $V; if ($s -and $s.region -and $s.region.name -eq $LargeRegion -and $s.stage -eq "InWorld") { $s } } 90
            Start-Sleep -Seconds 5
            [void](Api $V "map" @{ action = "open" })
            $m = Wait-Until { $x = Api $V "map" @{ action = "state" }; if ($x -and @($x.region_sizes | Where-Object { $_.size_x -eq $LargeSize }).Count -gt 0) { $x } } 30
            if (-not $m) { $m = Api $V "map" @{ action = "state" } }
            $st = State $V
            $obs = Api $V "observe" '{}'
            Save-Json "large-map.json" $m; Save-Json "large-observe.json" $obs
            Shot $V "large-region"
            $mapSize = @($m.region_sizes | Where-Object { $_.size_x -eq $LargeSize -and $_.size_y -eq $LargeSize }).Count -gt 0
            $mini = if ($m.minimap) { [double]$m.minimap.outline_m } else { 0 }
            $ok = $st.region.name -eq $LargeRegion -and $st.region.size_x -eq $LargeSize -and $st.region.size_y -eq $LargeSize -and $mapSize -and [math]::Abs($mini - $LargeSize) -le 2
            @{ pass = $ok; detail = "teleport sent $([bool]$t.sent); region $($st.region.size_x)x$($st.region.size_y); map shows a ${LargeSize} m region $mapSize; mini-map outline $mini m; terrain $($obs.terrain | ConvertTo-Json -Compress -Depth 3)" }
        }

        Step "5" "walk across the large region, and cross from its neighbour into it" {
            [void](Api $V "input" @{ kind = "release_focus" })
            # a: west across the large region from its east side (W held through the engine), until it crosses or stops moving
            $faced = Face $V 180
            $p0 = @((Api $V "observe" '{}').self.region_position)
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            $script:samples = @()
            $script:lastX = $null; $script:stillSince = $null
            Key $V "W" $true
            $west = $null
            try {
                $west = Wait-Until {
                    $s = State $V; $o = Api $V "observe" '{}'
                    if ($o -and $o.self) {
                        $x = [double]$o.self.region_position[0]
                        $script:samples += [pscustomobject]@{ t = [math]::Round($clock.Elapsed.TotalSeconds, 1); large = ($s.region.name -eq $LargeRegion); x = $x; z = $o.self.region_position[2]; vx = $o.self.region_velocity[0]; keys = ($o.self.keys_held -join '') }
                        if ($null -ne $script:lastX -and [math]::Abs($x - $script:lastX) -lt 0.05) { if (-not $script:stillSince) { $script:stillSince = $clock.Elapsed.TotalSeconds } } else { $script:stillSince = $null }
                        $script:lastX = $x
                    }
                    if ($s -and $s.region -and $s.region.name -eq $Region) { "crossed" }
                    elseif ($script:stillSince -and $clock.Elapsed.TotalSeconds - $script:stillSince -gt 8) { "stopped" }
                } 300
            } finally { Key $V "W" $false }
            $p1 = @((Api $V "observe" '{}').self.region_position)
            $walked = [math]::Abs([double]$p0[0] - [double]$p1[0])
            Save-Json "walk-west.json" @{ faced = $faced; start = $p0; end = $p1; seconds = $clock.Elapsed.TotalSeconds; outcome = $west; samples = $script:samples }
            Shot $V "walk-west-end"
            # b: from the small region's east side east across the border into the large region
            $t = Api $V "teleport" @{ action = "region"; region = $Region; x = 236; y = 128; z = 25 }
            [void](Wait-Until { $s = State $V; if ($s -and $s.region -and $s.region.name -eq $Region -and $s.stage -eq "InWorld") { $s } } 90)
            Start-Sleep -Seconds 6
            [void](Api $V "input" @{ kind = "release_focus" })
            $facedEast = Face $V 0
            $clock2 = [System.Diagnostics.Stopwatch]::StartNew()
            Key $V "W" $true
            $east = $null
            try { $east = Wait-Until { $s = State $V; if ($s -and $s.region -and $s.region.name -eq $LargeRegion) { $s } } 90 } finally { Key $V "W" $false }
            Start-Sleep -Seconds 5
            $st = State $V
            $m = Api $V "map" @{ action = "state" }
            $mini = if ($m.minimap) { [double]$m.minimap.outline_m } else { 0 }
            Shot $V "after-crossing"
            Save-Json "walk-east.json" @{ faced = $facedEast; seconds = $clock2.Elapsed.TotalSeconds; crossed = [bool]$east; region = $st.region; minimap = $mini }
            $acrossLarge = $walked -ge ($LargeSize - 40)
            $ok = $faced -and $acrossLarge -and $facedEast -and [bool]$east -and $st.region.size_x -eq $LargeSize -and [math]::Abs($mini - $LargeSize) -le 2
            @{ pass = $ok; detail = ("west: walked {0:N0} m in {1:N0} s, outcome {2}; east: crossed from {3} into {4} {5} after {6:N1} s, now {7}x{8}, mini-map outline {9} m" -f $walked, $clock.Elapsed.TotalSeconds, $west, $Region, $LargeRegion, [bool]$east, $clock2.Elapsed.TotalSeconds, $st.region.size_x, $st.region.size_y, $mini) }
        }

        Step "6" "logout" {
            [void](Api $V "session" @{ action = "logout" })
            $ended = Wait-Until { $s = State $V; if ($s -and (-not $s.has_session -or $s.ended)) { $true } } 60
            [void](Invoke-Api $V "quit" '{}')
            $exited = $vp.WaitForExit(60000)
            @{ pass = ([bool]$ended -and $exited); detail = "ended $([bool]$ended); viewer exited $exited" }
        }
    }
}
finally {
    if ($vp -and -not $vp.HasExited) { Stop-Tree $vp.Id; Say "the viewer was stopped by this run" }
    if ($serverStarted) {
        $code = Server "stop"
        $report.server_stop = $code
        Say ("STEP 7 server stopped: {0}  stop exit {1}" -f $(if ($code -eq 0) { "PASS" } else { "FAIL" }), $code)
        if ($code -ne 0) { $allPass = $false }
    }
    Save-Json "grids.json" $report
}
if ($allPass) { exit 0 } else { exit 1 }
