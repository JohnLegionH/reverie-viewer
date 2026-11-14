# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# takeoff-live.ps1 - the live check of Take Off from the own avatar's menu. Logs a scratch test avatar in with the development export,
# reads its current outfit, right-clicks its own avatar and chooses Take Off, Clothes and the type of one clothing layer it wears
# once, waits for the layer's link to leave the current outfit, puts the same item back on (the inventory's Add), and compares the
# outfit's links (item and layer note) before and after. Needs NV_OUTFIT_SCRATCH_OK=1 (a scratch test account only).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\takeoff-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: takeoff-report.json in the sandbox, pictures in -ShotsDir.
# Exit: 0 the layer came off and went back on, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ShotsDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 60
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$script:port = $null; $script:token = $null; $script:proc = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($script:port -and $script:token) { try { [void](Invoke-Raw $script:port "session" $script:token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $script:port "quit" $script:token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $ShotsDir -PathType Container)) { $problems.Add("-ShotsDir must be an existing folder") }
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
function Note([string]$what) { [Console]::Out.WriteLine(("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what)) }   # not part of a function's result
function Obs() { Parse (Invoke-Raw $script:port "observe" $script:token).Body }
function In([hashtable]$b) { Parse (Invoke-Raw $script:port "input" $script:token ($b | ConvertTo-Json -Compress)).Body }
function Key([string]$k, [bool]$down) { In @{ kind = "key"; key = $k; down = $down; shift = $false; alt = $false; ctrl = $false } }
function Interact([hashtable]$b) { Parse (Invoke-Raw $script:port "interact" $script:token ($b | ConvertTo-Json -Compress)).Body }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
function Flat($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }
function SelfState() {
    $ob = Obs; $is = Interact @{ action = "state" }
    return [ordered]@{ position = @($ob.self.region_position); flying = $ob.self.flying; controls = $ob.self.controls; seat = $ob.self.seat
        playing = (@($ob.self.playing) -join ","); sitting = $is.sitting; stand_button = $is.stand_button; stop_flying_button = $is.stop_flying_button }
}

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-52} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

function Start-Viewer([string]$tag, [string]$where) {
    $appdata = Join-Path $Sandbox "appdata-$tag"; $local = Join-Path $Sandbox "localappdata-$tag"; $log = Join-Path $Sandbox "run-$tag.log"
    $argLine = "--rendering-driver vulkan -- --automation"
    $script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "400", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
    Note "viewer ($tag) started through godot.ps1, pid $($script:proc.Id)"
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = $null
    for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
    if (-not $tf) { Note "no automation token file: stopping"; return $false }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    $script:port = [int]$j.port; $script:token = [string]$j.token
    $login = Invoke-Raw $script:port "session" $script:token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $where } | ConvertTo-Json -Compress)
    Note "login ($tag, start $(if ($where -eq 'last') { 'last' } else { 'the usual start' })) requested: $($login.Status)"
    $stable = 0; $last = -1; $o = $null
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $o = Obs
        if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
            if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
            $last = $o.world.objects
            if ($stable -ge 3) { return $true }
        }
    }
    return [bool]($o -and $o.self)
}
function Stop-Viewer([string]$tag) {
    $logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
    $after = Parse $logout.Body
    Note "logout ($tag): $($logout.Status) ended=$($after.ended)"
    [void](Invoke-Raw $script:port "quit" $script:token)
    $script:proc.WaitForExit(60000) | Out-Null
    Note "viewer ($tag) exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
    $script:port = $null; $script:token = $null
    return [bool]$after.ended
}
function Walk([double]$seconds) {
    $from = @((Obs).self.region_position)
    [void](Key "UP" $true); Start-Sleep -Milliseconds ([int]($seconds * 1000)); $mid = SelfState; [void](Key "UP" $false); Start-Sleep -Seconds 2
    $to = @((Obs).self.region_position)
    return [ordered]@{ from = $from; to = $to; moved = (Flat $from $to); while_walking = $mid }
}

function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 700 }; return $false }
function Outfit() { Parse (Invoke-Raw $script:port "inventory" $script:token '{"action":"outfit"}').Body }
function Links($of) { @(@($of.links) | Where-Object { $_.target } | ForEach-Object { "{0} {1}" -f $_.target, $_.desc } | Sort-Object) }
$TypeNames = @{ 4 = "shirt"; 5 = "pants"; 6 = "shoes"; 7 = "socks"; 8 = "jacket"; 9 = "gloves"; 10 = "undershirt"; 11 = "underpants"; 12 = "skirt"; 13 = "alpha"; 14 = "tattoo"; 15 = "physics" }
if ($env:NV_OUTFIT_SCRATCH_OK -ne "1") { Write-Output "BLOCKED: NV_OUTFIT_SCRATCH_OK=1 is required (a scratch test account only)"; exit 3 }

if (-not (Start-Viewer "takeoff" $Start)) { exit 5 }
[void](Invoke-Raw $script:port "inventory" $script:token '{"action":"open"}'); Start-Sleep -Seconds 6
$of = Outfit
for ($i = 0; $i -lt 20 -and -not $of.cof_loaded; $i++) { Start-Sleep -Seconds 1; $of = Outfit }
$before = Links $of
$report.before = $before
Note ("current outfit: {0} links, loaded {1}" -f $before.Count, $of.cof_loaded)
# a clothing layer worn once: its type, from the layer note (@ type*100 + layer)
$typed = @(@($of.links) | Where-Object { $_.target -and $_.desc -match '^@(\d+)$' } | ForEach-Object { [ordered]@{ target = $_.target; type = [int]([int]$Matches[1] / 100) } } | Where-Object { $TypeNames.ContainsKey($_.type) })
$once = @($typed | Group-Object { $_.type } | Where-Object { $_.Count -eq 1 } | ForEach-Object { $_.Group[0] })
$pref = @($once | Sort-Object { @(6, 11, 7, 4, 5).IndexOf([int]$_.type) -band 0x7fffffff })
$pick = $pref | Select-Object -First 1
if (-not $pick) {
    Check "1 a clothing layer worn once is found" $false ("{0} typed clothing links" -f $typed.Count)
} else {
    $name = $TypeNames[[int]$pick.type]
    Note "layer chosen: type $name"
    $ob = Obs
    $p = @($ob.self.region_position)
    $menu = Interact @{ action = "rightclick"; x = [double]$p[0]; y = [double]$p[1]; z = ([double]$p[2] + 0.3) }
    Start-Sleep -Milliseconds 800
    $offered = @($menu.entries | ForEach-Object { $_.Id })
    $report.menu = [ordered]@{ kind = $menu.menu; entries = $offered }
    Shot "takeoff_1_menu"
    Check "1 right-clicking her own avatar (or a worn object on it) offers Take Off" ($menu.menu -in @("Self", "Object") -and $offered -contains "take_off") ("menu {0}, entries {1}" -f $menu.menu, ($offered -join ","))
    $chosen = Interact @{ action = "choose"; entry = "take_off_type/$name" }
    $gone = Wait-Until { $o2 = Outfit; -not (@($o2.links) | Where-Object { $_.target -eq $pick.target }) } 40
    $mid = Links (Outfit)
    $report.after_take_off = $mid
    Shot "takeoff_2_off"
    Check "2 Take Off > Clothes > $name takes the layer off" ($chosen.chosen -eq $true -and $gone) ("chosen {0}, link gone {1}, {2} links now" -f $chosen.chosen, $gone, $mid.Count)
    $add = Parse (Invoke-Raw $script:port "inventory" $script:token (@{ action = "add"; id = $pick.target } | ConvertTo-Json -Compress)).Body
    $back = Wait-Until { $o3 = Outfit; [bool](@($o3.links) | Where-Object { $_.target -eq $pick.target }) } 40
    Start-Sleep -Seconds 3
    $after = Links (Outfit)
    $report.after = $after
    Shot "takeoff_3_back"
    Check "3 the same layer goes back on" ($add.submitted -eq $true -and $back) ("submitted {0}, code {1}, link back {2}" -f $add.submitted, $add.code, $back)
    $same = (@(Compare-Object $before $after).Count -eq 0)
    Check "4 her outfit ends exactly as it started (items and layer notes)" $same ("before {0} links, after {1}" -f $before.Count, $after.Count)
    $report.completed = $true
}
$report.logged_out = Stop-Viewer "takeoff"
$report.checks = $checks
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "takeoff-report.json") -Encoding utf8
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0
