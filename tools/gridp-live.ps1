# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# gridp-live.ps1 - the live check of a grid that personalises itself: a standalone OpenSim whose grid information sets gridname, currency,
# currencyname, help, about, viewerurl and welcome (or, in the second run, currency = none). It starts that server through a control
# script (tools\stock-server.ps1 does the starting and stopping, as the stock smoke does), runs one development export (automation on)
# with the grid given as --grid, and reads what a person meets:
#   1 server      the server starts and answers get_grid_info with the keys the grid set
#   2 login       the login screen: the grid list shows the host beside the name, the welcome link reads "Visit <grid>'s page"
#   3 world       the account logs in and arrives
#   4 top bar     the grid's name is in the top bar and the window title
#   5 help menu   the Help menu has the grid's help, about and viewer pages (and says which host before opening: the question is a
#                 notification, checked by the unit tests)
#   6 money       the Edit window's price field carries the grid's symbol (empty with currency = none), the balance is shown in the grid's
#                 symbol, and hidden when the grid has no money
#   7 logout      the account logs out and the viewer exits
#   8 server      the server is stopped
# "poser" is a grid that names itself "Second Life" (run with -GridName "Second Life"): its name must be shown under its host everywhere.
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input is missing); 4 skipped (a lane claim was not granted).
#
#   powershell -NoProfile -File <repo>\tools\gridp-live.ps1 -Sandbox <new evidence folder> -Variant full|none|poser -ServerControl <bash script>
#     -GridInfoUrl <url> [-Account <alias>] [-Credentials <accounts file>] [-Region <region>] [-GridName <name>] [-Exe <development export>]
#
# Claims as grids-live.ps1 takes them: an avatar claim for the account and a build claim on this checkout, through NV_CLAIM_WRAPPER.
# Only ids, counts and flags are written.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][ValidateSet("full", "none", "poser")][string]$Variant,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $env:NV_STOCK_ACCOUNT_A,
    [string]$Region = $env:NV_STOCK_REGION,
    [string]$GridInfoUrl = $env:NV_STOCK_GRID_INFO_URL,
    [string]$ServerControl = $env:NV_STOCK_SERVER_CONTROL,
    [string]$GridName = "Example Grid",
    [string]$Symbol = "Ez",
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
$ServerControl = Resolve-StockSetting $ServerControl "NV_STOCK_SERVER_CONTROL" $loaded
Set-StockOptionalEnvironment $loaded
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh evidence folder is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not $Account) { $problems.Add("-Account (or NV_STOCK_ACCOUNT_A) is required") }
if (-not $Region -or $Region -notmatch '^[A-Za-z0-9 _-]{1,64}$') { $problems.Add("the region must be a plain name") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_STOCK_GRID_INFO_URL) is required") }
if (-not $ServerControl -or -not (Test-Path -LiteralPath $ServerControl -PathType Leaf)) { $problems.Add("-ServerControl (or NV_STOCK_SERVER_CONTROL) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (build the development export): $Exe") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

# ---------------- the lane claims: run this script again under them ----------------
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_GRIDP_LIVE_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "gridp-live") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Variant", $Variant, "-Credentials", $Credentials, "-Account", $Account,
        "-Region", $Region, "-GridInfoUrl", $GridInfoUrl, "-ServerControl", $ServerControl, "-GridName", $GridName, "-Symbol", $Symbol, "-Exe", $Exe, "-StartSeconds", "$StartSeconds")
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $cmd = @($wrapper) + $order + @("--resource", $Account, $lane, "avatar", "gridp live", "--",
            "bash", $wrapper) + $order + @("--resource", (ToUnixPath $repoRoot), $lane, "build", "gridp live", "--") + $inner
    $env:NV_GRIDP_LIVE_CLAIMED = "1"
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
$report = [ordered]@{ started_utc = (Get-Date).ToUniversalTime().ToString("o"); variant = $Variant; steps = @() }
$textLog = Join-Path $Sandbox "gridp.txt"
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

$expectSymbol = if ($Variant -eq "none") { "" } else { $Symbol }
$shown = $GridName
$V = $null; $vp = $null; $loginUri = $null; $serverStarted = $false; $info = $null
try {
    Step "1" "server" {
        $code = Server "start"
        $script:serverStarted = ($code -ne 1)
        $keys = ""
        if ($code -eq 0) {
            try { $req = [System.Net.WebRequest]::Create($GridInfoUrl); $req.Timeout = 10000; $resp = $req.GetResponse()
                  try { $script:info = ([xml](New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd()).gridinfo; $script:loginUri = $script:info.login; if ($Variant -eq "poser") { $script:shown = ([uri]$script:loginUri).Authority } } finally { $resp.Close() } } catch { }
        }
        $i = $script:info
        $wantCurrency = if ($Variant -eq "none") { "none" } else { $Symbol }
        $ok = ($code -eq 0 -and [bool]$script:loginUri -and $i.gridname -eq $GridName -and $i.currency -eq $wantCurrency -and [bool]$i.help -and [bool]$i.about -and [bool]$i.viewerurl -and [bool]$i.welcome)
        @{ pass = $ok; detail = "start exit $code; gridname '$($i.gridname)', currency '$($i.currency)', currencyname '$($i.currencyname)', help $([bool]$i.help), about $([bool]$i.about), viewerurl $([bool]$i.viewerurl), welcome $([bool]$i.welcome)" }
    }
    if ($loginUri) {
        $local = Join-Path $Sandbox "localappdata"; $appdata = Join-Path $Sandbox "appdata"
        New-Item -ItemType Directory -Force -Path $appdata | Out-Null
        $vlog = Join-Path $Sandbox "viewer.log"
        $vp = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
            "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "1800", "-HangSnapshotSeconds", "0",
            "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$vlog`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation --grid=$loginUri`"")
        $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
        if (-not $tf) { throw "the viewer has no token file" }
        Start-Sleep -Milliseconds 300
        $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
        $V = @{ Port = [int]$j.port; Token = [string]$j.token }

        Step "2" "login screen" {
            # the screen asks the grid for its grid-info document when it comes up and keeps what it says; the link shows when it has answered
            $s = Wait-Until { $x = Api $V "login_screen" '{"action":"layout"}'; if ($x -and $x.links -and $x.links.visit) { $x } } 60
            if (-not $s) { $s = Api $V "login_screen" '{"action":"layout"}' }
            Save-Json "login-screen.json" $s
            Shot $V "login-screen"
            $label = @($s.grid_labels) | Where-Object { $_ -like "*127.0.0.1:*" } | Select-Object -First 1
            $wantVisit = "Visit $shown's page"
            $wantLabel = if ($Variant -eq "poser") { $shown } else { "$GridName ($(([uri]$loginUri).Authority))" }
            @{ pass = ($s.links.visit -eq $true -and $s.links.visit_text -eq $wantVisit -and $label -eq $wantLabel)
               detail = "grid list shows '$label'; welcome link shown $($s.links.visit) reading '$($s.links.visit_text)' (wanted '$wantVisit')" }
        }

        Step "3" "world" {
            $l = Invoke-Api $V "session" @{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = ("uri:" + $Region + "&128&128&25") }
            $st = Wait-Until { $s = State $V; if ($s -and $s.has_session -and $s.stage -eq "InWorld") { $s } } 150
            Start-Sleep -Seconds 6
            @{ pass = ($l.Status -eq 200 -and [bool]$st); detail = "login status $($l.Status); in world $([bool]$st); region '$($st.region.name)'" }
        }

        Step "4" "top bar" {
            $tb = Api $V "topbar" '{"action":"state"}'
            Save-Json "topbar.json" $tb
            Shot $V "world"
            $chip = [string]$tb.status[0].text
            @{ pass = ($chip -eq $shown -and ([string]$tb.window_title) -like "*$shown*"); detail = "grid chip '$chip'; window title '$($tb.window_title)'" }
        }

        Step "5" "help menu" {
            $tb = Api $V "topbar" '{"action":"state"}'
            $at = [array]::IndexOf(@($tb.titles), "Help")
            if ($at -lt 0) { return @{ pass = $false; detail = "no Help menu in $(@($tb.titles) -join ', ')" } }
            $m = Api $V "topbar" @{ action = "click"; index = $at }
            Save-Json "help-menu.json" $m
            $items = @($m.items | ForEach-Object { [string]$_.text })
            $need = @("$shown help", "About $shown", "Get Reverie from $shown")
            $missing = @($need | Where-Object { $items -notcontains $_ })
            @{ pass = ($missing.Count -eq 0); detail = "Help menu: $($items -join ' | '); missing: $($missing -join ', ')" }
        }

        Step "6" "money" {
            [void](Api $V "edit" @{ action = "toggle" })
            Start-Sleep -Seconds 2
            $f = Wait-Until { $x = Api $V "ui" @{ action = "find"; name = "sale_price_unit" }; if ($x -and $x.found) { $x } } 20
            $tb = Api $V "topbar" '{"action":"state"}'
            Shot $V "edit-window"
            $unit = if ($f) { [string]$f.text } else { "<not found>" }
            $balanceShown = [bool]$tb.balance.shown
            # the stock server answers a balance of 0: with a currency the top bar shows it in the grid's symbol, with currency = none it shows none
            $balanceText = [string]$tb.balance.text
            $balanceOk = if ($Variant -eq "none") { -not $balanceShown } else { $balanceShown -and $balanceText -eq ($Symbol + "0") }
            @{ pass = ($f -and $unit -eq $expectSymbol -and $balanceOk); detail = "price unit reads '$unit' (wanted '$expectSymbol'); balance shown $balanceShown reading '$balanceText'" }
        }

        Step "7" "logout" {
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
        Say ("STEP 8 server stopped: {0}  stop exit {1}" -f $(if ($code -eq 0) { "PASS" } else { "FAIL" }), $code)
        if ($code -ne 0) { $allPass = $false }
    }
    Save-Json "gridp.json" $report
}
if ($allPass) { exit 0 } else { exit 1 }
