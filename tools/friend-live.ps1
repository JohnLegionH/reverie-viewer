# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# friend-live.ps1 - the live check of friend notices, run against the stock upstream OpenSim standalone (tools\stock-server.ps1).
# Two copies of the development export (automation on) log in, one account each, become friends, and the second account logs out and in
# again while the first watches. What the first account must show:
#   1 server     the stock server starts and answers get_grid_info with a login address
#   2 login      both accounts reach the world
#   3 friends    the two are friends and each may see the other online
#   4 offline    when B logs out, A's friend list shows B offline, A is told (a friend notice of kind Offline) and a toast stands
#   5 online     when B logs in again, A's list shows B online, A is told (kind Online) and a toast stands
#   6 toast      (-Full) pressing the online toast opens the instant-message conversation with B
#   7 name menu  (-Full) the name in the chat line offers Offer Teleport, and choosing it sends B a teleport offer
#   8 chat line  (-Full) "<name> is online" is in A's nearby chat while the Preferences switch is on, and not after it is turned off
#   9 return dialog (-Full) Return on an object of A's own opens the modal dialog; it fits the window at interface size 100, 150 and 200 percent; Esc after a second cancels, Esc inside the first second does nothing, Enter presses OK
#   10 logout    both accounts log out and both viewers exit
#   11 server    the server is stopped and reported down
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input is missing); 4 skipped (a lane claim was not granted).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\friend-live.ps1 -Sandbox <new evidence folder> [-Exe <development export>] [-Full]
# Settings are those of tools\stock-smoke.ps1 (NV_STOCK_SETTINGS). Only ids, counts and kinds are written, never names or message text.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$AccountA = $env:NV_STOCK_ACCOUNT_A,
    [string]$AccountB = $env:NV_STOCK_ACCOUNT_B,
    [string]$Region = $env:NV_STOCK_REGION,
    [string]$GridInfoUrl = $env:NV_STOCK_GRID_INFO_URL,
    [string]$ServerControl = $env:NV_STOCK_SERVER_CONTROL,
    [string]$Exe = "",   # default: the development export, host\Viewer.Godot\build\dev\Reverie.console.exe
    [string]$Settings = "",   # a settings file (NAME=VALUE lines) for every setting above that is not given; default NV_STOCK_SETTINGS (tools\stock-settings.ps1)
    [string]$ViewerProcessName = "Reverie*.exe",   # the program name (a wildcard) of a viewer that can log in, for the check that none is already running
    [int]$StartSeconds = 150,   # the wait for each viewer's automation token; the same as the wait for the world after a login
    [switch]$Full,
    [switch]$DryRun,
    [switch]$CheckClaimsOnly
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

# ---------------- inputs (also the whole of -DryRun) ----------------
# a setting not given as a parameter or an environment variable is read from the settings file, if one is named
. (Join-Path $PSScriptRoot "stock-settings.ps1")
$loaded = Read-StockSettings $Settings
$Credentials = Resolve-StockSetting $Credentials "NV_TEST_ACCOUNTS" $loaded
$AccountA = Resolve-StockSetting $AccountA "NV_STOCK_ACCOUNT_A" $loaded
$AccountB = Resolve-StockSetting $AccountB "NV_STOCK_ACCOUNT_B" $loaded
$Region = Resolve-StockSetting $Region "NV_STOCK_REGION" $loaded
$GridInfoUrl = Resolve-StockSetting $GridInfoUrl "NV_STOCK_GRID_INFO_URL" $loaded
$ServerControl = Resolve-StockSetting $ServerControl "NV_STOCK_SERVER_CONTROL" $loaded
Set-StockOptionalEnvironment $loaded
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh evidence folder is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not $AccountA) { $problems.Add("-AccountA (or NV_STOCK_ACCOUNT_A) is required") }
if (-not $AccountB) { $problems.Add("-AccountB (or NV_STOCK_ACCOUNT_B) is required") }
if ($AccountA -and $AccountB -and $AccountA -eq $AccountB) { $problems.Add("-AccountA and -AccountB must be two different accounts") }
if (-not $Region) { $problems.Add("-Region (or NV_STOCK_REGION) is required") }
elseif ($Region -notmatch '^[A-Za-z0-9 _-]{1,64}$') { $problems.Add("-Region must be a plain region name") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_STOCK_GRID_INFO_URL) is required") }
if (-not $ServerControl -or -not (Test-Path -LiteralPath $ServerControl -PathType Leaf)) { $problems.Add("-ServerControl (or NV_STOCK_SERVER_CONTROL) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (build the development export): $Exe") }
if (-not (Test-Path -LiteralPath $godot -PathType Leaf)) { $problems.Add("tools\godot.ps1 not found") }
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Output "BLOCKED: $_" }
    Write-Output ("settings file: " + $(if ($loaded.File) { "read $($loaded.File)" } else { "none found; looked at: $($loaded.Looked)" }) + ". A setting comes from its parameter, its environment variable (NV_...) or a NAME=VALUE line of the settings file.")
    exit 3
}
Write-Output ("preconditions ok: region {0}, accounts {1} and {2}" -f $Region, $AccountA, $AccountB)

# ---------------- the lane claims: run this script again under them ----------------
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_FRIEND_LIVE_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "friend-live") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials,
        "-AccountA", $AccountA, "-AccountB", $AccountB, "-Region", $Region, "-GridInfoUrl", $GridInfoUrl, "-ServerControl", $ServerControl,
        "-Exe", $Exe, "-StartSeconds", "$StartSeconds", "-ViewerProcessName", $ViewerProcessName)
    if ($loaded.File) { $inner += @("-Settings", $loaded.File) }
    if ($CheckClaimsOnly) { $inner += "-CheckClaimsOnly" }
    if ($Full) { $inner += "-Full" }
    # the innermost claim is the one the viewer guard looks at, and the guard accepts build, measure, live-test and live-login, not avatar
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $cmd = @("bash", $wrapper) + $order + @("--resource", $AccountA, $lane, "avatar", "friend live", "--",
            "bash", $wrapper) + $order + @("--resource", $AccountB, $lane, "avatar", "friend live", "--",
            "bash", $wrapper) + $order + @($lane, "live-login", "friend live", "--") + $inner
    if ($DryRun) { Write-Output ("would run under claims: " + ($cmd -join " ")); Write-Output "dry run: nothing started, nothing logged in"; exit 0 }
    $env:NV_FRIEND_LIVE_CLAIMED = "1"
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
if ($CheckClaimsOnly) { Write-Output "claims accepted: nothing started"; exit 0 }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like $ViewerProcessName -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { Write-Output "BLOCKED: a viewer that can log in is already running ($($viewers.Count)): one account in one session at a time"; exit 3 }

# ---------------- helpers ----------------
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$report = [ordered]@{ started_utc = (Get-Date).ToUniversalTime().ToString("o"); steps = @() }
$textLog = Join-Path $Sandbox "smoke.txt"
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
function State($v) { Api $v "session" '{"action":"state"}' }
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }
    return $null
}
function Dist2D($p, $q) { [math]::Round([math]::Sqrt([math]::Pow($p[0] - $q[0], 2) + [math]::Pow($p[1] - $q[1], 2)), 2) }
function Save-Json([string]$name, $obj) { $obj | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox $name) -Encoding utf8 }
function Shot($v, [string]$name) { try { [void](Api $v "screenshot" @{ dir = $Sandbox; name = $name }) } catch { } }
function Start-Viewer([string]$label) {
    $local = Join-Path $Sandbox "localappdata-$label"
    $appdata = Join-Path $Sandbox "appdata-$label"
    New-Item -ItemType Directory -Force -Path $appdata | Out-Null
    $vlog = Join-Path $Sandbox "viewer-$label.log"
    $vp = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "1500", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$vlog`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
    $script:startedViewers += $vp   # recorded at once, so the end stops it even when this start fails
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
    if (-not $tf) { throw "viewer $label has no token file" }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    return @{ Label = $label; Proc = $vp; Log = $vlog; Port = [int]$j.port; Token = [string]$j.token }
}
# the login, and the seconds until the world is up (the world stage), or $null
function Login($v, [string]$account, [string]$start) {
    $l = Invoke-Api $v "session" @{ action = "login"; accounts_file = $Credentials; account = $account; login_uri = $script:loginUri; start = $start }
    if ($l.Status -ne 200) { return $null }
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $st = Wait-Until { $s = State $v; if ($s -and $s.has_session -and -not $s.ended -and $s.stage -eq "InWorld") { $s } } 150
    if ($st) { return $clock.Elapsed.TotalSeconds } else { return $null }
}
function Others($obs) { @($obs.presentation.avatars.each | Where-Object { -not $_.self }) }
# the server script runs with its output in files: the server it starts inherits the output handles, and a pipe would stay open
# until the server ended (this call would never return)
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

$A = $null; $B = $null; $allPass = $true; $loginUri = $null; $serverStarted = $false; $startedViewers = @()
# Stops a process this run started and every process under it (godot.ps1, the viewer, its crash watch), children first, by PID.
function Stop-Tree([int]$rootId) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($rootId)
    for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $all) { if ([int]$c.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$c.ProcessId)) { $ids.Add([int]$c.ProcessId) } } }
    for ($i = $ids.Count - 1; $i -ge 0; $i--) { Stop-Process -Id $ids[$i] -Force -ErrorAction SilentlyContinue }
    return $ids.ToArray()
}
function Step([string]$n, [string]$name, [scriptblock]$body) {
    $r = $null
    try { $r = & $body }
    catch { $r = @{ pass = $false; detail = ("error at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) } }
    Record $n $name ([bool]$r.pass) ([string]$r.detail)
    if (-not $r.pass) { $script:allPass = $false }
}


# a friend list row for an id, or $null
function Row($v, [string]$id) { @((Api $v "friends" @{ action = "list" }).friends | Where-Object { $_.id -eq $id }) | Select-Object -First 1 }
# how many friend notices of one kind the viewer has recorded for one friend (the model's record, whatever the toast did)
function Notices($v, [string]$id, [string]$kind) { @((Api $v "friends" @{ action = "notices" }).notices | Where-Object { $_.friend -eq $id -and $_.kind -eq $kind }).Count }
function Toasts($v) { [int](Api $v "dialog" @{}).toasts }
# B leaves or comes back while A watches: A's list, A's notice record for B, and the most toasts A showed while waiting
function Watch([string]$wantKind, [bool]$wantOnline, [scriptblock]$trigger) {
    $before = Notices $A $script:ids.B $wantKind
    & $trigger
    $script:peak = 0
    $row = Wait-Until { $t = Toasts $A; if ($t -gt $script:peak) { $script:peak = $t }; $r = Row $A $script:ids.B; if ($r -and ([bool]$r.online -eq $wantOnline)) { $r } } 90
    $note = Wait-Until { $t = Toasts $A; if ($t -gt $script:peak) { $script:peak = $t }; if ((Notices $A $script:ids.B $wantKind) -gt $before) { $true } } 10
    return @{ list = [bool]$row; notice = [bool]$note; toasts = $script:peak }
}

try {
    # 1 the server
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
        $A = Start-Viewer "A"
        $B = Start-Viewer "B"
        $ids = @{}
        $start = "uri:" + $Region + "&128&128&25"

        # 2 both accounts reach the world
        Step "2" "login" {
            $sa = Login $A $AccountA $start
            $sb = Login $B $AccountB $start
            if ($null -ne $sa -and $null -ne $sb) { $script:ids.A = [string](Api $A "friends" @{ action = "list" }).self; $script:ids.B = [string](Api $B "friends" @{ action = "list" }).self }
            @{ pass = ($null -ne $sa -and $null -ne $sb); detail = "A in world $($null -ne $sa), B in world $($null -ne $sb)" }
        }

        # 3 friends who may see each other online (a friendship is offered and accepted when they are not friends yet)
        Step "3" "friends" {
            if (-not (Row $A $script:ids.B)) {
                [void](Api $A "friends" @{ action = "offer"; agent = $script:ids.B })
                [void](Wait-Until { if (@((Api $B "friends" @{ action = "list" }).offers | Where-Object { $_.from -eq $script:ids.A }).Count -gt 0) { $true } } 30)
                [void](Api $B "friends" @{ action = "accept"; agent = $script:ids.A })
                [void](Wait-Until { if ((Row $A $script:ids.B) -and (Row $B $script:ids.A)) { $true } } 30)
            }
            foreach ($pair in @(@($A, $script:ids.B), @($B, $script:ids.A))) {
                $r = Row $pair[0] $pair[1]
                if ($r -and -not ([int]$r.granted -band 1)) { [void](Api $pair[0] "friends" @{ action = "grant_map"; agent = $pair[1] }) }
            }
            $ok = Wait-Until { $ra = Row $A $script:ids.B; $rb = Row $B $script:ids.A; if ($ra -and $rb -and ([int]$ra.received -band 1) -and ([int]$rb.received -band 1) -and $ra.online -and $rb.online) { $true } } 40
            $ra = Row $A $script:ids.B; $rb = Row $B $script:ids.A
            @{ pass = [bool]$ok; detail = "A lists B (online $($ra.online), gave $($ra.granted), got $($ra.received)); B lists A (online $($rb.online), gave $($rb.granted), got $($rb.received))" }
        }

        # 4 B logs out: A's list, A's notice record and a toast
        Step "4" "offline notice" {
            $w = Watch "Offline" $false { [void](Api $B "session" '{"action":"logout"}') }
            @{ pass = ($w.list -and $w.notice -and $w.toasts -ge 1); detail = "A's list shows B offline $($w.list); A told (kind Offline) $($w.notice); most toasts up while waiting $($w.toasts)" }
        }

        # 5 B logs in again: the same
        Step "5" "online notice" {
            $w = Watch "Online" $true { [void](Login $B $AccountB $start) }
            @{ pass = ($w.list -and $w.notice -and $w.toasts -ge 1); detail = "A's list shows B online $($w.list); A told (kind Online) $($w.notice); most toasts up while waiting $($w.toasts)" }
        }

        if ($Full) {
            # 6 pressing the online toast opens the conversation with B
            Step "6" "toast opens an IM" {
                $r = Api $A "dialog" @{ action = "toast_click"; token = $script:ids.B }
                @{ pass = ($r.clicked -and $r.im_active); detail = "a toast for B was pressed $($r.clicked); the conversation with B is the active one $($r.im_active)" }
            }
            # 7 the name in the chat line offers Offer Teleport; choosing it sends B an offer
            Step "7" "name menu" {
                $m = Api $A "chat" @{ action = "name_menu"; agent = $script:ids.B }
                $has = @($m.entries) -contains "offer_teleport"
                $before = @((Api $B "dialog" @{}).waiting).Count
                $c = Api $A "chat" @{ action = "name_menu"; agent = $script:ids.B; text = "offer_teleport" }
                $got = Wait-Until { if (@((Api $B "dialog" @{}).waiting).Count -gt $before) { $true } } 30
                @{ pass = ($has -and [bool]$got); detail = "the name's menu offers Offer Teleport $has (entries $(@($m.entries).Count)); B was sent an offer $([bool]$got)" }
            }
            # 8 the nearby-chat line: present while the Preferences switch is on, absent after it is turned off
            Step "8" "chat line and its switch" {
                $on = @((Api $A "chat" @{ action = "find"; token = " is online" }).nearby).Count
                [void](Api $A "prefs" @{ action = "set"; key = "friend_online_in_chat"; value = $false })
                [void](Api $B "session" '{"action":"logout"}')
                [void](Wait-Until { $r = Row $A $script:ids.B; if ($r -and -not $r.online) { $true } } 60)
                [void](Login $B $AccountB $start)
                [void](Wait-Until { $r = Row $A $script:ids.B; if ($r -and $r.online) { $true } } 60)
                Start-Sleep -Seconds 3
                $off = @((Api $A "chat" @{ action = "find"; token = " is online" }).nearby).Count
                [void](Api $A "prefs" @{ action = "set"; key = "friend_online_in_chat"; value = $true })
                @{ pass = ($on -ge 1 -and $off -eq $on); detail = "lines holding the word with the switch on $on; after B came online again with it off $off" }
            }

            # 9 the Return dialog, on an object of A's own: the question opens as a modal dialog that fits the window at each interface size;
            # Esc after its first second cancels it (the object stays); Esc inside the first second does nothing; Enter presses OK (the object is returned)
            Step "9" "return dialog" {
                # the view must be clear: B leaves (its avatar stood near the box) and the windows the earlier steps opened are closed
                [void](Api $B "session" '{"action":"logout"}')
                foreach ($w in @("conversations", "nearby_chat", "notifications", "build", "build_tools", "edit", "object_edit")) { [void](Api $A "windows" @{ action = "close"; name = $w }) }
                Save-Json "windows-before-return.json" (Api $A "windows" @{ action = "state" })
                for ($k = 0; $k -lt 6; $k++) { [void](Api $A "input" @{ kind = "key"; key = "ESC"; down = $true }); [void](Api $A "input" @{ kind = "key"; key = "ESC"; down = $false }); Start-Sleep -Milliseconds 200 }
                Start-Sleep -Seconds 3
                $me = @((Api $A "observe" '{}').self.region_position)
                $spot = @{ x = [double]$me[0] + 3.0; y = [double]$me[1]; z = [double]$me[2] - 0.9 }
                [void](Api $A "interact" @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 6 }); Start-Sleep -Milliseconds 1500
                $before = @((Api $A "build" @{ action = "state" }).own_objects | ForEach-Object { $_.id })
                [void](Api $A "build" @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z })
                $box = Wait-Until { $b = Api $A "build" @{ action = "state" }; @($b.own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
                if (-not $box) { return @{ pass = $false; detail = "no box was made" } }
                $script:box = $box
                $detail = @()
                $allOk = $true
                foreach ($pct in @(100, 150, 200)) {
                    [void](Api $A "interface_size" @{ percent = $pct }); Start-Sleep -Milliseconds 700
                    [void](Api $A "interact" @{ action = "aim"; x = [double]$box.x; y = [double]$box.y; z = [double]$box.z; distance = 2.2 }); Start-Sleep -Milliseconds 1200
                    [void](Api $A "screenshot" @{ dir = $Sandbox; name = "before-click-$pct" })
                    $rc = Api $A "interact" @{ action = "rightclick"; x = [double]$box.x; y = [double]$box.y; z = ([double]$box.z + [double]$box.sz / 2) }
                    Save-Json "return-rightclick-$pct.json" @{ box = $box; rc = $rc }
                    $ret = @($rc.entries | Where-Object { $_.Id -eq "return" }) | Select-Object -First 1
                    $ch = if ($ret -and $ret.Enabled) { (Api $A "interact" @{ action = "choose"; entry = "return" }).chosen } else { $false }
                    $up = Wait-Until { $d = Api $A "dialog" @{}; if ($d.modal) { $d } } 10
                    if (-not $up) { $detail += "at $pct percent: entry $([bool]$ret), chosen $ch, no dialog"; $allOk = $false; continue }
                    $bodyKey = [string]$up.modal_body; $def = $up.modal_default
                    $guard = $true
                    if ($pct -eq 200) {
                        # Esc at once (inside the first second) must not answer it
                        [void](Api $A "input" @{ kind = "key"; key = "ESC"; down = $true }); [void](Api $A "input" @{ kind = "key"; key = "ESC"; down = $false })
                        $guard = [bool](Api $A "dialog" @{}).modal
                    }
                    Start-Sleep -Milliseconds 1300
                    $m = Api $A "ui" @{ action = "find"; name = "message" }; $b0 = Api $A "ui" @{ action = "find"; name = "button_0" }; $b1 = Api $A "ui" @{ action = "find"; name = "button_1" }
                    [void](Api $A "screenshot" @{ dir = $Sandbox; name = "return-dialog-$pct" })
                    $inView = ($m.reach -eq "visible") -and ($b0.reach -eq "visible") -and ($b1.reach -eq "visible")
                    $step = "at $pct percent: text $($m.reach), OK $($b0.reach), Cancel $($b1.reach), body $bodyKey, default button $def"
                    if ($bodyKey -ne "confirm.return.body" -or $def -ne 0 -or -not $inView -or -not $guard) { $allOk = $false }
                    if ($pct -ne 200) {
                        # Esc after the first second cancels: the dialog closes and the object stays
                        [void](Api $A "input" @{ kind = "key"; key = "ESC"; down = $true }); [void](Api $A "input" @{ kind = "key"; key = "ESC"; down = $false })
                        $closed = [bool](Wait-Until { if (-not (Api $A "dialog" @{}).modal) { $true } } 5)
                        $stays = @((Api $A "build" @{ action = "state" }).own_objects | Where-Object { $_.id -eq $box.id }).Count -eq 1
                        $step += "; Esc closed it $closed, object stays $stays"
                        if (-not ($closed -and $stays)) { $allOk = $false }
                    } else {
                        $step += "; Esc inside the first second left it up $guard"
                        [void](Api $A "input" @{ kind = "key"; key = "ENTER"; down = $true }); [void](Api $A "input" @{ kind = "key"; key = "ENTER"; down = $false })
                        $gone = [bool](Wait-Until { $b = Api $A "build" @{ action = "state" }; if (@($b.own_objects | Where-Object { $_.id -eq $box.id }).Count -eq 0) { $true } } 20)
                        $closed = -not [bool](Api $A "dialog" @{}).modal
                        $step += "; Enter returned the object $gone and closed the dialog $closed"
                        if (-not ($gone -and $closed)) { $allOk = $false }
                    }
                    $detail += $step
                }
                [void](Api $A "interface_size" @{ percent = 100 })
                @{ pass = $allOk; detail = ($detail -join "; ") }
            }
        }
    }
}
catch { Say ("ERROR: " + $_.Exception.Message); $allPass = $false }
finally {
    $exA = "not started"; $exB = "not started"
    foreach ($v in @($B, $A)) {
        if ($null -eq $v) { continue }
        try { Invoke-Api $v "session" '{"action":"logout"}' | Out-Null } catch { }
        try { Invoke-Api $v "quit" "{}" | Out-Null } catch { }
        $exited = $v.Proc.WaitForExit(60000)
        $code = if ($exited) { $v.Proc.ExitCode } else { "still running" }
        if ($v.Label -eq "A") { $exA = $code } else { $exB = $code }
    }
    $pass10 = ($null -ne $A) -and ($null -ne $B) -and ($exA -eq 0) -and ($exB -eq 0)
    Record "10" "logout" ([bool]$pass10) ("viewer A exit {0}, viewer B exit {1}" -f $exA, $exB)
    $allPass = $allPass -and [bool]$pass10
    foreach ($vp in $startedViewers) {
        if ($vp.HasExited) { continue }
        $stopped = Stop-Tree $vp.Id
        Say ("stopped a viewer this run started that was still running: PIDs {0}" -f ($stopped -join ", "))
        $allPass = $false
    }
    if ($serverStarted) {
        $stopCode = Server "stop"
        $statusCode = Server "status"
        $pass11 = ($stopCode -eq 0) -and ($statusCode -ne 0)
        Record "11" "server stopped" $pass11 "stop exit $stopCode, status exit $statusCode (1 means down)"
    } else { $pass11 = $false; Record "11" "server stopped" $false "the server was not started by this run (refused), so it was left alone" }
    $allPass = $allPass -and $pass11
    $report.finished_utc = (Get-Date).ToUniversalTime().ToString("o")
    $report.pass = [bool]$allPass
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "friend-live.json") -Encoding utf8
    Say ("FRIEND LIVE " + $(if ($allPass) { "PASS" } else { "FAIL" }))
}
if ($allPass) { exit 0 } else { exit 1 }
