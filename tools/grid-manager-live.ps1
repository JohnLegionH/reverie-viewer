# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# grid-manager-live.ps1 - the login screen's grid manager and saved names, proved live through real input against the stock
# OpenSim standalone (tools\stock-server.ps1). The development export starts with no --grid and an empty settings folder; every step on
# the login screen is a mouse click or a key press through the engine's input path (the automation's input command), never a call of
# the action itself. Steps, each PASS or FAIL with figures:
#   1 server    the stock server starts and answers get_grid_info
#   2 empty     the viewer starts with no grid on its list
#   3 add       the grid chooser is clicked, "Add a grid..." picked with the arrow keys and Enter, the server's address typed, Look up
#               clicked: the grid's name is shown (and the http warning for a plain-http address); Add grid clicked: the grid is listed and chosen
#   4 login     the name and password typed, Remember name ticked by a click, Enter pressed in the password field: the account reaches the world
#   5 again     logged out: back on the login screen the name saved for that grid is in the name field and in the saved names
#   6 remove    "Manage grids..." picked the same way, the grid's row clicked, Remove clicked: the list is empty
#   7 server    the viewer quits and the server is stopped
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input is missing); 4 skipped (a lane claim was not granted).
#
#   powershell -NoProfile -File <repo>\tools\grid-manager-live.ps1 -Sandbox <new evidence folder> [-Settings <stock settings file>]
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <url>] [-ServerControl <bash script>] [-Exe <development export>] [-DryRun]
#
# Settings come from the parameters, the environment or the stock smoke's settings file (tools\stock-settings.ps1): NV_TEST_ACCOUNTS,
# NV_STOCK_ACCOUNT_A (the account), NV_STOCK_GRID_INFO_URL, NV_STOCK_SERVER_CONTROL. The accounts file is read only by the viewer.
# Claims: with NV_CLAIM_WRAPPER set, the script runs itself under an avatar claim on the account and, inside it, a build claim on this
# checkout (the kind the viewer guard accepts). Output: the sandbox folder (live.txt, live.json, the viewer's log). No message text and
# no password is written; names of test accounts only as the viewer shows them.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $env:NV_STOCK_ACCOUNT_A,
    [string]$GridInfoUrl = $env:NV_STOCK_GRID_INFO_URL,
    [string]$ServerControl = $env:NV_STOCK_SERVER_CONTROL,
    [string]$Exe = "",
    [string]$Settings = "",
    [int]$StartSeconds = 150,
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

# ---------------- inputs ----------------
. (Join-Path $PSScriptRoot "stock-settings.ps1")
$loaded = Read-StockSettings $Settings
$Credentials = Resolve-StockSetting $Credentials "NV_TEST_ACCOUNTS" $loaded
$Account = Resolve-StockSetting $Account "NV_STOCK_ACCOUNT_A" $loaded
$GridInfoUrl = Resolve-StockSetting $GridInfoUrl "NV_STOCK_GRID_INFO_URL" $loaded
$ServerControl = Resolve-StockSetting $ServerControl "NV_STOCK_SERVER_CONTROL" $loaded
Set-StockOptionalEnvironment $loaded
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh evidence folder is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not $Account) { $problems.Add("-Account (or NV_STOCK_ACCOUNT_A) is required") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_STOCK_GRID_INFO_URL) is required") }
if (-not $ServerControl -or -not (Test-Path -LiteralPath $ServerControl -PathType Leaf)) { $problems.Add("-ServerControl (or NV_STOCK_SERVER_CONTROL) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (build the development export): $Exe") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
# the address a person types: the grid-info address without get_grid_info
$typedAddress = $GridInfoUrl -replace 'get_grid_info/?$', ''
Write-Output ("preconditions ok: account {0}" -f $Account)

# ---------------- the lane claims: run this script again under them ----------------
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_GRID_LIVE_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "grid-manager-live") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials, "-Account", $Account,
        "-GridInfoUrl", $GridInfoUrl, "-ServerControl", $ServerControl, "-Exe", $Exe, "-StartSeconds", "$StartSeconds")
    if ($loaded.File) { $inner += @("-Settings", $loaded.File) }
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $cmd = @("bash", $wrapper) + $order + @("--resource", $Account, $lane, "avatar", "grid manager live", "--",
            "bash", $wrapper) + $order + @("--resource", (ToUnixPath $repoRoot), $lane, "build", "grid manager live", "--") + $inner
    if ($DryRun) { Write-Output ("would run under claims: " + ($cmd -join " ")); exit 0 }
    $env:NV_GRID_LIVE_CLAIMED = "1"
    if (-not $env:CLAIM_WAIT) { $env:CLAIM_WAIT = "30" }
    $bash = Find-Bash
    if (-not $bash) { Write-Output "BLOCKED: bash not found, so the claim wrapper cannot run"; exit 3 }
    & $bash @($cmd[1..($cmd.Count - 1)])
    $code = $LASTEXITCODE
    if ($code -eq 125) { Write-Output "SKIPPED: a claim was not granted within CLAIM_WAIT minutes; nothing was started"; exit 4 }
    exit $code
}
if ($DryRun) { Write-Output "dry run: nothing started"; exit 0 }

. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { Write-Output "BLOCKED: a viewer that can log in is already running ($($viewers.Count)): one account in one session at a time"; exit 3 }

# ---------------- helpers ----------------
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$report = [ordered]@{ started_utc = (Get-Date).ToUniversalTime().ToString("o"); steps = @() }
$textLog = Join-Path $Sandbox "live.txt"
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
function Layout($v) { Api $v "login_screen" '{"action":"layout"}' }
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }
    return $null
}
# a click as a person makes it: a move to the control's centre, the left button down, then up, through the engine's input path
function Click($v, [string]$id) {
    $l = Layout $v
    $c = $l.controls.$id
    if (-not $c) { throw "no control '$id' on the screen" }
    [void](Api $v "input" @{ kind = "mouse"; x = $c.x; y = $c.y })
    [void](Api $v "input" @{ kind = "mouse"; button = "left"; down = $true; x = $c.x; y = $c.y })
    [void](Api $v "input" @{ kind = "mouse"; button = "left"; down = $false; x = $c.x; y = $c.y })
    Start-Sleep -Milliseconds 250
}
function Key($v, [string]$key) {
    [void](Api $v "input" @{ kind = "key"; key = $key; down = $true })
    [void](Api $v "input" @{ kind = "key"; key = $key; down = $false })
    Start-Sleep -Milliseconds 150
}
# opens the grid chooser with a click and picks the item at $index with the arrow keys and Enter
function Pick-GridItem($v, [string]$which) {
    Click $v "grid"
    $open = Wait-Until { $l = Layout $v; if ($l.grid_popup_open) { $l } } 5
    if (-not $open) { throw "the grid chooser did not open" }
    $target = [int]$open.$which
    for ($i = 0; $i -lt 10; $i++) {
        $l = Layout $v
        if ([int]$l.grid_popup_focused -eq $target) { break }
        Key $v "DOWN"
    }
    if ([int](Layout $v).grid_popup_focused -ne $target) { throw "the arrow keys did not reach item $target" }
    Key $v "ENTER"
}
function Start-Viewer([string]$label) {
    $local = Join-Path $Sandbox "localappdata-$label"
    $appdata = Join-Path $Sandbox "appdata-$label"
    New-Item -ItemType Directory -Force -Path $appdata | Out-Null
    $vlog = Join-Path $Sandbox "viewer-$label.log"
    $vp = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$vlog`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
    $script:startedViewers += $vp
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
    if (-not $tf) { throw "viewer $label has no token file" }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $v = @{ Label = $label; Proc = $vp; Log = $vlog; Port = [int]$j.port; Token = [string]$j.token }
    if (-not (Wait-Until { $l = Layout $v; if ($l -and $l.controls) { $true } } 60)) { throw "the login screen did not come up" }
    return $v
}
function Server([string]$action) {
    $o = Join-Path $Sandbox "server-$action.out"; $e = Join-Path $Sandbox "server-$action.err"
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $p = Start-Process -FilePath "powershell" -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e -ArgumentList @(
        "-NoProfile", "-File", "`"$serverScript`"", "-Action", $action, "-Control", "`"$ServerControl`"",
        "-GridInfoUrl", "`"$GridInfoUrl`"", "-Lane", $lane, "-LogFolder", "`"$Sandbox`"")
    $null = $p.Handle
    $p.WaitForExit()
    foreach ($f in @($o, $e)) { if (Test-Path -LiteralPath $f) { Get-Content -LiteralPath $f -ErrorAction SilentlyContinue | ForEach-Object { Add-Content -LiteralPath $textLog -Value "$_" -Encoding utf8 } } }
    return $p.ExitCode
}
function Stop-Tree([int]$rootId) {
    $all = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($rootId)
    for ($i = 0; $i -lt $ids.Count; $i++) { foreach ($c in $all) { if ([int]$c.ParentProcessId -eq $ids[$i] -and -not $ids.Contains([int]$c.ProcessId)) { $ids.Add([int]$c.ProcessId) } } }
    for ($i = $ids.Count - 1; $i -ge 0; $i--) { Stop-Process -Id $ids[$i] -Force -ErrorAction SilentlyContinue }
}
$allPass = $true; $serverStarted = $false; $startedViewers = @(); $V = $null
function Step([string]$n, [string]$name, [scriptblock]$body) {
    $r = $null
    try { $r = & $body }
    catch { $r = @{ pass = $false; detail = ("error at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) } }
    Record $n $name ([bool]$r.pass) ([string]$r.detail)
    if (-not $r.pass) { $script:allPass = $false }
    return [bool]$r.pass
}

try {
    $up = Step "1" "server" {
        $code = Server "start"
        $script:serverStarted = ($code -ne 1)
        @{ pass = ($code -eq 0); detail = "start exit $code" }
    }
    if ($up) {
        $V = Start-Viewer "A"
        $ok = Step "2" "empty" {
            $l = Layout $V
            @{ pass = (@($l.grids).Count -eq 0 -and -not $l.grid); detail = "grids listed $(@($l.grids).Count), chosen '$($l.grid)'" }
        }
        if ($ok) { $ok = Step "3" "add" {
            Pick-GridItem $V "grid_add_index"
            if (-not (Wait-Until { (Layout $V).overlay -eq "grid_add" } 5)) { return @{ pass = $false; detail = "Add a grid did not open" } }
            Click $V "grid_address"
            [void](Api $V "input" @{ kind = "text"; text = $typedAddress })
            $warned = [bool](Layout $V).add.warning
            Click $V "grid_lookup"
            $found = Wait-Until { $a = (Layout $V).add; if ($a -and $a.can_add) { $a } } 15
            if (-not $found) { return @{ pass = $false; detail = "Look up found nothing: $((Layout $V).add.message)" } }
            Click $V "grid_add_button"
            $l = Wait-Until { $x = Layout $V; if (-not $x.overlay -and $x.grid) { $x } } 5
            $plainHttp = $typedAddress.StartsWith("http://")
            $pass = $l -and @($l.grids).Count -eq 1 -and $l.grid -eq $found.found -and ($warned -eq $plainHttp)
            @{ pass = [bool]$pass; detail = "found '$($found.found)'; listed $(@($l.grids).Count), chosen '$($l.grid)'; http warning shown $warned (plain http $plainHttp)" }
        } }
        if ($ok) { $ok = Step "4" "login" {
            Click $V "username"
            [void](Api $V "input" @{ kind = "account"; accounts_file = $Credentials; account = $Account; part = "name" })
            if (-not (Layout $V).remember) { Click $V "remember" }
            Click $V "password"
            [void](Api $V "input" @{ kind = "account"; accounts_file = $Credentials; account = $Account; part = "password" })
            $l = Layout $V
            $typed = $l.username; $ticked = [bool]$l.remember; $pwTyped = [bool]$l.password_typed
            Key $V "ENTER"
            $world = Wait-Until { $s = Api $V "session" '{"action":"state"}'; if ($s -and $s.has_session -and -not $s.ended -and $s.stage -eq "InWorld") { $s } } 150
            $s = Api $V "session" '{"action":"state"}'
            @{ pass = [bool]$world; detail = "name typed '$typed', remember name $ticked, password typed $pwTyped, Enter pressed; stage $($s.stage) status '$($s.status)'"; name = $typed }
        } }
        if ($ok) { $ok = Step "5" "again" {
            $name = (Layout $V).username
            [void](Api $V "session" '{"action":"logout"}')
            $l = Wait-Until { $x = Layout $V; if ($x -and $x.controls -and -not $x.hidden) { $x } } 60
            if (-not $l) { return @{ pass = $false; detail = "the login screen did not come back" } }
            $saved = @($l.saved_names)
            $pass = $l.username.Length -gt 0 -and $saved.Count -ge 1 -and $saved[0] -eq $l.username -and $l.grid
            @{ pass = [bool]$pass; detail = "back on the login screen: grid '$($l.grid)', name field '$($l.username)', saved names $($saved.Count) (first '$($saved[0])')" }
        } }
        if ($ok) { $ok = Step "6" "remove" {
            Pick-GridItem $V "grid_manage_index"
            if (-not (Wait-Until { (Layout $V).overlay -eq "grid_manage" } 5)) { return @{ pass = $false; detail = "Manage grids did not open" } }
            $rows = @((Layout $V).manage.rows)
            if ($rows.Count -ne 1) { return @{ pass = $false; detail = "Manage grids lists $($rows.Count) grids" } }
            [void](Api $V "input" @{ kind = "mouse"; x = $rows[0].x; y = $rows[0].y })
            [void](Api $V "input" @{ kind = "mouse"; button = "left"; down = $true; x = $rows[0].x; y = $rows[0].y })
            [void](Api $V "input" @{ kind = "mouse"; button = "left"; down = $false; x = $rows[0].x; y = $rows[0].y })
            Start-Sleep -Milliseconds 250
            Click $V "grid_remove"
            $m = Layout $V
            $emptyShown = [bool]$m.manage.empty
            Click $V "grid_manage_close"
            $l = Wait-Until { $x = Layout $V; if (-not $x.overlay) { $x } } 5
            $pass = $emptyShown -and $l -and @($l.grids).Count -eq 0 -and -not $l.grid
            @{ pass = [bool]$pass; detail = "after Remove: the window says empty $emptyShown; grids listed $(@($l.grids).Count), chosen '$($l.grid)'" }
        } }
    }
}
finally {
    $quitOk = $true
    if ($V) {
        try { [void](Api $V "quit" '{}') } catch { }
        $quitOk = [bool](Wait-Until { $V.Proc.HasExited } 30)
    }
    foreach ($p in $startedViewers) { if (-not $p.HasExited) { Stop-Tree $p.Id } }
    $stopCode = if ($serverStarted) { Server "stop" } else { 0 }
    $null = Step "7" "server" { @{ pass = ($stopCode -eq 0 -and $quitOk); detail = "viewer quit $quitOk; server stop exit $stopCode" } }
    $report.finished_utc = (Get-Date).ToUniversalTime().ToString("o")
    $report.pass = $allPass
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "live.json") -Encoding utf8
    Say ("RESULT {0}" -f $(if ($allPass) { "PASS" } else { "FAIL" }))
}
exit $(if ($allPass) { 0 } else { 1 })
