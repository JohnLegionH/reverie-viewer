# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# stock-smoke.ps1 - the live smoke test against a stock upstream OpenSim standalone. The viewer must work on an unmodified OpenSim,
# not only on the grid it is developed against. The script starts the stock server (tools\stock-server.ps1), runs two copies of the
# development export (automation on) against it, one account each, checks what a person checks first, and stops the server on every
# way out, including a failure. Each step reports PASS or FAIL with figures.
#   1 server    the server starts and answers get_grid_info with a login address
#   2 arrival   account A logs in; within 5 s of the world the avatar is in the scene and the camera is behind it; a walk key moves it 5 m
#   3 world     the land (terrain complete, drawn), the water and the sky from the region's own environment are drawn
#   4a others   account B logs in 5 m from A; each sees the other drawn
#   4b looks    each other avatar carries its outfit and bakes (a look version, bakes held and drawn)
#   5 teleport  A teleports inside the region: the progress view closes, A arrives where it was sent
#   6 logout    both accounts log out and both viewers exit
#   7 server    the server is stopped and reported down
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input is missing); 4 skipped (a lane claim was not granted).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\stock-smoke.ps1 -Sandbox <new evidence folder>
#     [-Credentials <accounts file>] [-AccountA <alias>] [-AccountB <alias>] [-Region <name>] [-GridInfoUrl <url>]
#     [-ServerControl <bash script>] [-Views <folder for the 3 m pictures>] [-Exe <development export>] [-DryRun] [-CheckClaimsOnly]
#     [-ViewerProcessName <wildcard>]   (default Reverie*.exe: the check that no viewer that can log in is running; tests name a program of their own)
#
# Settings come from the parameters or the environment: NV_TEST_ACCOUNTS (the accounts file the viewer reads at login; this script
# never opens it), NV_STOCK_ACCOUNT_A and NV_STOCK_ACCOUNT_B (the two aliases), NV_STOCK_REGION (the region name),
# NV_STOCK_GRID_INFO_URL (the stock server's get_grid_info address), NV_STOCK_SERVER_CONTROL (the control script stock-server.ps1
# uses). Any of them can instead be a NAME=VALUE line of one settings file kept outside the repository, named by -Settings or
# NV_STOCK_SETTINGS (tools\stock-settings.ps1); a parameter wins over the environment, which wins over the file. With a setting
# missing the script exits 3 and names it and where it looked. The accounts must exist on the stock server and be in the viewer's test-account pool file (NV_AVATAR_POOL). Nothing here touches any other grid.
# Claims: when NV_CLAIM_WRAPPER names a bash script that runs a command under a lane claim (claim.sh's form), the script runs itself
# under an avatar claim for each account, for the lane NV_CLAIM_LANE (default viewer), and inside them a build claim on this checkout:
# the avatar claims keep two sessions off an account, the build claim is the kind the machine's guard accepts for starting a viewer.
# Every Godot process goes through tools\godot.ps1, which refuses to start without a granted claim when the machine's guard is set
# (tools\claim-guard.ps1).
# Output: the sandbox folder (smoke.txt, smoke.json, screenshots, observe answers, each viewer's log). Only ids, counts and numbers
# are written, never message text.
# -DryRun checks every input and prints the claim command it would run; it starts nothing.
# -CheckClaimsOnly runs under the claims as usual, then stops right after the guard has accepted them (prints "claims accepted"):
# it starts no server and no viewer and writes nothing.
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
    [string]$Views = $env:NV_STOCK_VIEWS,   # a folder (made when needed): after step 4b each viewer's picture of the other avatar from 3 m, seen-by-A.png and seen-by-B.png
    [string]$Exe = "",   # default: the development export, host\Viewer.Godot\build\dev\Reverie.console.exe
    [string]$Settings = "",   # a settings file (NAME=VALUE lines) for every setting above that is not given; default NV_STOCK_SETTINGS (tools\stock-settings.ps1)
    [string]$ViewerProcessName = "Reverie*.exe",   # the program name (a wildcard) of a viewer that can log in, for the check that none is already running
    [int]$StartSeconds = 150,   # the wait for each viewer's automation token; the same as the wait for the world after a login
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
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_STOCK_SMOKE_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "stock-smoke") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials,
        "-AccountA", $AccountA, "-AccountB", $AccountB, "-Region", $Region, "-GridInfoUrl", $GridInfoUrl, "-ServerControl", $ServerControl,
        "-Exe", $Exe, "-StartSeconds", "$StartSeconds", "-ViewerProcessName", $ViewerProcessName)
    if ($loaded.File) { $inner += @("-Settings", $loaded.File) }
    if ($CheckClaimsOnly) { $inner += "-CheckClaimsOnly" }
    # the innermost claim is the one the viewer guard looks at, and the guard accepts build, measure, live-test and live-login, not avatar
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $cmd = @("bash", $wrapper) + $order + @("--resource", $AccountA, $lane, "avatar", "stock smoke", "--",
            "bash", $wrapper) + $order + @("--resource", $AccountB, $lane, "avatar", "stock smoke", "--",
            "bash", $wrapper) + $order + @("--resource", (ToUnixPath $repoRoot), $lane, "build", "stock smoke", "--") + $inner
    if ($DryRun) { Write-Output ("would run under claims: " + ($cmd -join " ")); Write-Output "dry run: nothing started, nothing logged in"; exit 0 }
    $env:NV_STOCK_SMOKE_CLAIMED = "1"
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

try {
    # 1 the server
    Step "1" "server" {
        $code = Server "start"
        # exit 1 is a refusal: nothing was started and the server belongs to someone else, so the stop at the end must not touch it
        # (any other result, a failed start included, is stopped again by stock-server.ps1 itself, and once more at the end)
        $script:serverStarted = ($code -ne 1)
        $uri = $null
        if ($code -eq 0) {
            try { $req = [System.Net.WebRequest]::Create($GridInfoUrl); $req.Timeout = 10000; $resp = $req.GetResponse()
                  try { $script:loginUri = ([xml](New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd()).gridinfo.login } finally { $resp.Close() } } catch { }
        }
        @{ pass = ($code -eq 0 -and [bool]$script:loginUri); detail = "start exit $code; login address answered $([bool]$script:loginUri)" }
    }

    if ($script:loginUri) {
        $A = Start-Viewer "A"
        $B = Start-Viewer "B"
        $posA = $null

        # 2 account A arrives: the avatar is in the scene and the camera behind it within 5 s of the world, and a walk key moves it 5 m
        Step "2" "arrival" {
            $secs = Login $A $AccountA ("uri:" + $Region + "&128&128&25")
            if ($null -eq $secs) { return @{ pass = $false; detail = "A did not reach the world" } }
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            $obs = $null; $cam = $null
            $ready = Wait-Until { $o = Api $A "observe" '{}'; if ($o.self -and $o.self.drawn_soles) { $c = Api $A "orbit" '{"op":"state"}'; if ($c -and $c.eye) { $script:obsA = $o; $script:camA = $c; $true } } } 5
            $after = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            $behind = $false; $dist = $null
            if ($ready) {
                $o = $script:obsA; $c = $script:camA
                $dx = $c.eye[0] - $o.self.drawn_soles[0]; $dy = $c.eye[1] - $o.self.drawn_soles[1]; $dz = $c.eye[2] - $o.self.drawn_soles[2]
                $dist = [math]::Round([math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz), 1)
                $yaw = [double]$o.self.drawn_heading_deg * [math]::PI / 180
                $behind = (($dx * [math]::Cos($yaw) + $dy * [math]::Sin($yaw)) -lt 0) -and ($dist -lt 20)
            }
            $p0 = @((Api $A "observe" '{}').self.region_position)
            [void](Api $A "keys" @{ key = "W"; down = $true })
            $moved = Wait-Until { $p = @((Api $A "observe" '{}').self.region_position); if ($p.Count -ge 3 -and (Dist2D $p0 $p) -ge 5) { Dist2D $p0 $p } } 10
            [void](Api $A "keys" @{ key = "W"; down = $false })
            Start-Sleep -Milliseconds 800
            $script:posA = @((Api $A "observe" '{}').self.region_position)
            Shot $A "arrival"
            @{ pass = ([bool]$ready -and $behind -and [bool]$moved); detail = "world up after $([math]::Round($secs,1)) s; avatar and camera ready within 5 s of the world: $([bool]$ready) ($after s); camera behind and close: $behind ($dist m); walk key moved it 5 m: $([bool]$moved)" }
        }

        # 3 the land, the water and the sky are drawn
        Step "3" "world" {
            $o = Wait-Until { $x = Api $A "observe" '{}'; if ($x.terrain.complete) { $x } } 30
            $n = Api $A "neighbours" '{}'
            $o = Api $A "observe" '{}'
            Save-Json "observe-A-world.json" $o; Save-Json "neighbours-A.json" $n
            $cur = @($n.regions | Where-Object { $_.current }) | Select-Object -First 1
            $land = $o.terrain.complete -and $cur -and $cur.chunks -gt 0 -and $cur.drawn
            $water = [bool]$n.anchor_has_water
            $sky = [string]$o.presentation.environment
            Shot $A "world"
            @{ pass = ([bool]$land -and $water -and $sky -eq "region")
               detail = "terrain $($o.terrain.patches) of $($o.terrain.expected) patches, complete $($o.terrain.complete), chunks drawn $($cur.chunks) (land drawn $([bool]$land)); water drawn $water (height $($cur.water_height)); sky source '$sky' (pass needs 'region': the region's own environment, not the viewer's fallback)" }
        }

        # 4a account B logs in 5 m from A; each sees the other drawn
        Step "4a" "others drawn" {
            $bx = [math]::Min(250.0, [double]$script:posA[0] + 5.0)
            # whole numbers, as the official viewer writes a start location (a stock server reads them as integers and falls back to its default point on a decimal)
            $secs = Login $B $AccountB ("uri:" + $Region + "&" + [int][math]::Round($bx) + "&" + [int][math]::Round([double]$script:posA[1]) + "&" + [int][math]::Round([double]$script:posA[2]))
            if ($null -eq $secs) { return @{ pass = $false; detail = "B did not reach the world" } }
            $seen = Wait-Until { $oa = Api $A "observe" '{}'; $ob = Api $B "observe" '{}'
                if (@(Others $oa | Where-Object { $_.placed }).Count -ge 1 -and @(Others $ob | Where-Object { $_.placed }).Count -ge 1) { $true } } 60
            # Listing the other avatar comes before drawing it: the second body can be a moment behind on a busy machine.
            # Wait (up to 90 s) until each viewer draws both bodies, then judge exactly as before.
            if ($seen) { [void](Wait-Until { $xa = Api $A "observe" '{}'; $xb = Api $B "observe" '{}'
                if (($xa.presentation.avatars.body -eq "ready") -and ($xa.presentation.avatars.drawn -ge 2) -and ($xb.presentation.avatars.body -eq "ready") -and ($xb.presentation.avatars.drawn -ge 2)) { $true } } 90) }
            $oa = Api $A "observe" '{}'; $ob = Api $B "observe" '{}'
            Save-Json "observe-A-pair.json" $oa; Save-Json "observe-B-pair.json" $ob
            $gap = $null
            if ($seen) { $gap = [math]::Round((Dist2D @((Api $A "observe" '{}').self.region_position) @((Api $B "observe" '{}').self.region_position)), 1) }
            $drawnA = ($oa.presentation.avatars.body -eq "ready") -and ($oa.presentation.avatars.drawn -ge 2)
            $drawnB = ($ob.presentation.avatars.body -eq "ready") -and ($ob.presentation.avatars.drawn -ge 2)
            $near = $null -ne $gap -and $gap -le 10   # the server may place the second avatar at its landing point, not at the spot asked for
            Shot $A "pair-A"; Shot $B "pair-B"
            @{ pass = ([bool]$seen -and $drawnA -and $drawnB -and $near); detail = "each lists the other $([bool]$seen) (gap $gap m); A draws $($oa.presentation.avatars.drawn) avatars (body $($oa.presentation.avatars.body)), B draws $($ob.presentation.avatars.drawn)" }
        }

        # 4b each other avatar carries its outfit and bakes: a look version, bakes held and bakes drawn
        Step "4b" "outfit and bakes" {
            # a viewer bakes its own look after it arrives (a few seconds of compositing and uploading on a region that does not bake), so wait
            # for the result: each side holding and drawing the other's bakes, up to 90 s. The figures below are read after that wait.
            [void](Wait-Until {
                $xa = Others (Api $A "observe" '{}') | Select-Object -First 1; $xb = Others (Api $B "observe" '{}') | Select-Object -First 1
                ($xa -and $xa.bakes -gt 0 -and $xa.bakes_drawn -gt 0 -and $xb -and $xb.bakes -gt 0 -and $xb.bakes_drawn -gt 0)
            } 90)
            $oa = Api $A "observe" '{}'; $ob = Api $B "observe" '{}'
            $na = Api $A "neighbours" '{}'; $nb = Api $B "neighbours" '{}'
            $ea = Others $oa | Select-Object -First 1; $eb = Others $ob | Select-Object -First 1
            $la = @(@($na.regions | Where-Object { $_.current }).looks | Where-Object { $_.agent -ne $null }); $lb = @(@($nb.regions | Where-Object { $_.current }).looks | Where-Object { $_.agent -ne $null })
            $okA = $ea -and $ea.bakes -gt 0 -and $ea.bakes_drawn -gt 0
            $okB = $eb -and $eb.bakes -gt 0 -and $eb.bakes_drawn -gt 0
            if ($Views -and $okA -and $okB) {
                # each viewer's picture of the other avatar from 3 m: the camera is put 3 m from the other avatar's chest, then the view is saved
                New-Item -ItemType Directory -Force -Path $Views | Out-Null
                foreach ($side in @(@($A, $ea, "seen-by-A"), @($B, $eb, "seen-by-B"))) {
                    $viewer = $side[0]; $other = $side[1]
                    if ($other.soles) {
                        [void](Api $viewer "interact" @{ action = "aim"; x = [double]$other.soles[0]; y = [double]$other.soles[1]; z = ([double]$other.soles[2] + 1.0); distance = 3.0 })
                        Start-Sleep -Seconds 2
                        [void](Api $viewer "screenshot" @{ dir = $Views; name = $side[2] })
                        [void](Api $viewer "interact" @{ action = "aim"; x = 0.0; y = 0.0; z = 0.0; distance = 0.0 })
                    }
                }
            }
            $verA = ($la | ForEach-Object { "v$($_.look.version) cof$($_.look.cof_version) bakes$($_.look.bakes)" }) -join ","
            $verB = ($lb | ForEach-Object { "v$($_.look.version) cof$($_.look.cof_version) bakes$($_.look.bakes)" }) -join ","
            @{ pass = [bool]($okA -and $okB); detail = "A sees B with $($ea.bakes) bakes held and $($ea.bakes_drawn) drawn; B sees A with $($eb.bakes) held and $($eb.bakes_drawn) drawn; looks held by A: [$verA], by B: [$verB]" }
        }

        # 5 a teleport inside the region: the progress view closes and A arrives where it was sent
        Step "5" "teleport" {
            $p = @((Api $A "observe" '{}').self.region_position)
            $tx = [math]::Min(230.0, [math]::Max(25.0, [double]$p[0] + 25.0)); $ty = [math]::Min(230.0, [math]::Max(25.0, [double]$p[1]))
            $st0 = Api $A "teleport" @{ action = "status" }
            $sent = Api $A "teleport" @{ action = "region"; region = $Region; x = $tx; y = $ty; z = ([double]$p[2] + 2.0) }
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            $everShown = $false; $s = $null
            $end = Wait-Until { $s = Api $A "teleport" @{ action = "status" }
                if ($s.stage -in @("Requested", "Active", "Arriving")) { $script:shown = $true }
                if ($s.stage -eq "Failed") { $s } elseif ($s.stage -eq "Idle" -and $s.arrivals -gt $st0.arrivals) { $s } } 45
            $took = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            Start-Sleep -Seconds 2
            $final = Api $A "teleport" @{ action = "status" }
            $q = @((Api $A "observe" '{}').self.region_position)
            $off = if ($q.Count -ge 3) { Dist2D @($tx, $ty) $q } else { $null }
            Shot $A "after-teleport"
            @{ pass = ($end -and $end.stage -eq "Idle" -and $final.stage -eq "Idle" -and $null -ne $off -and $off -lt 5)
               detail = "teleport reply sent=$($sent.sent); over after $took s; final stage $($final.stage), failure '$($final.failure)'; arrived $off m from the target" }
        }
    }
}
catch { Say ("ERROR: " + $_.Exception.Message); $allPass = $false }
finally {
    # 6 logout: both accounts, then both viewers
    $loA = $null; $loB = $null; $exA = "not started"; $exB = "not started"
    foreach ($v in @($B, $A)) {
        if ($null -eq $v) { continue }
        $lo = $null
        try { $lo = (Invoke-Api $v "session" '{"action":"logout"}').Body | ConvertFrom-Json } catch { }
        try { Invoke-Api $v "quit" "{}" | Out-Null } catch { }
        $exited = $v.Proc.WaitForExit(60000)
        $code = if ($exited) { $v.Proc.ExitCode } else { "still running" }
        if ($v.Label -eq "A") { $loA = $lo; $exA = $code } else { $loB = $lo; $exB = $code }
    }
    $pass6 = ($null -ne $A) -and ($null -ne $B) -and ($loA -and $loA.ended) -and ($loB -and $loB.ended) -and ($exA -eq 0) -and ($exB -eq 0)
    Record "6" "logout" ([bool]$pass6) ("A ended {0} (viewer exit {1}), B ended {2} (viewer exit {3})" -f $(if ($loA) { $loA.ended } else { "no session" }), $exA, $(if ($loB) { $loB.ended } else { "no session" }), $exB)
    $allPass = $allPass -and [bool]$pass6
    # every viewer this run started is gone before it ends, whichever way it ended: one that is still running is stopped here
    foreach ($vp in $startedViewers) {
        if ($vp.HasExited) { continue }
        $stopped = Stop-Tree $vp.Id
        Say ("stopped a viewer this run started that was still running: PIDs {0}" -f ($stopped -join ", "))
        $allPass = $false
    }
    # 7 the server: stopped on every way out, whatever happened above
    if ($serverStarted) {
        $stopCode = Server "stop"
        $statusCode = Server "status"
        $pass7 = ($stopCode -eq 0) -and ($statusCode -ne 0)
        Record "7" "server stopped" $pass7 "stop exit $stopCode, status exit $statusCode (1 means down)"
    } else { $pass7 = $false; Record "7" "server stopped" $false "the server was not started by this run (refused), so it was left alone" }
    $allPass = $allPass -and $pass7
    $ranSteps = @($report.steps | ForEach-Object { $_.step })
    foreach ($n in @("1", "2", "3", "4a", "4b", "5", "6", "7")) { if ($ranSteps -notcontains $n) { $report.steps += [ordered]@{ step = $n; name = "not run"; pass = $false; detail = "the run ended before this step" }; $allPass = $false } }
    $report.finished_utc = (Get-Date).ToUniversalTime().ToString("o")
    $report.pass = [bool]$allPass
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "smoke.json") -Encoding utf8
    Say ("STOCK SMOKE " + $(if ($allPass) { "PASS" } else { "FAIL" }) + ": " + (@($report.steps | Where-Object { $_.pass }).Count) + " of 8 steps passed")
}
if ($allPass) { exit 0 } else { exit 1 }
