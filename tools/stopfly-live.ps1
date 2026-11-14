# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# stopfly-live.ps1 - the live Stop Flying button test. Logs a test avatar in with the development export, flies and presses the button through the
# engine's own input path (the automation input and interact commands), and
# reads back what the avatar and the button did.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\stopfly-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Settings come from the parameters or the environment as in movement-live.ps1. Run it under a live-login claim and an avatar
# claim on the account. Output: stopfly-report.json and stopfly-report.txt in the sandbox, pictures in -ShotsDir.
# Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
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
function Note([string]$what) { Write-Output ("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output "LIVE STOPFLY RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
$stable = 0; $last = -1; $o = $null
$deadline = (Get-Date).AddSeconds(150)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Parse (Invoke-Raw $port "observe" $token).Body
    if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
        if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
        $last = $o.world.objects
        if ($stable -ge 3) { break }
    }
}
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-48} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
function Shot([string]$name) { [void](Invoke-Raw $port "screenshot" $token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
function Cam([string]$op, [double]$value = 0) { Parse (Invoke-Raw $port "orbit" $token (@{ op = $op; value = $value } | ConvertTo-Json -Compress)).Body }
function Dist($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2) + [math]::Pow($a[2] - $b[2], 2)) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

function In([hashtable]$b) { Parse (Invoke-Raw $port "input" $token ($b | ConvertTo-Json -Compress)).Body }
function Key([string]$k, [bool]$down, [bool]$shift = $false, [bool]$alt = $false, [bool]$ctrl = $false) { In @{ kind = "key"; key = $k; down = $down; shift = $shift; alt = $alt; ctrl = $ctrl } }
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$alt = $false, [bool]$ctrl = $false, [bool]$shift = $false) {
    In @{ kind = "mouse"; button = $button; down = $down; x = $x; y = $y; alt = $alt; ctrl = $ctrl; shift = $shift }
}
function Pos() { $ob = Parse (Invoke-Raw $port "observe" $token).Body; return @($ob.self.region_position) }
function Flat($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }
function Turn([double]$from, [double]$to) { $d = $to - $from; while ($d -gt 180) { $d -= 360 }; while ($d -lt -180) { $d += 360 }; return $d }
function Drag([double]$x0, [double]$y0, [double]$dx, [double]$dy, [bool]$alt = $false, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](Mouse "none" $false $x0 $y0 $alt $ctrl $shift)
    [void](Mouse "left" $true $x0 $y0 $alt $ctrl $shift)
    $steps = 10
    for ($i = 1; $i -le $steps; $i++) { [void](Mouse "none" $false ($x0 + $dx * $i / $steps) ($y0 + $dy * $i / $steps) $alt $ctrl $shift); Start-Sleep -Milliseconds 60 }
    $s = In @{ kind = "state" }
    [void](Mouse "left" $false ($x0 + $dx) ($y0 + $dy) $alt $ctrl $shift)
    Start-Sleep -Milliseconds 300
    return $s
}


function Obs() { Parse (Invoke-Raw $port "observe" $token).Body }
function IState() { In @{ kind = "state" } }
function Interact([hashtable]$b) { Parse (Invoke-Raw $port "interact" $token ($b | ConvertTo-Json -Compress)).Body }
function Playing($ob) { @($ob.self.playing) -join "," }

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 5
    # start on the ground
    for ($k = 0; $k -lt 30; $k++) { $ob = Obs; if (-not $ob.self.flying) { break }; [void](Key "C" $true); Start-Sleep -Milliseconds 700 }
    [void](Key "C" $false); Start-Sleep -Seconds 2
    $ob = Obs; $is = Interact @{ action = "state" }
    $pz = [double]$ob.self.region_position[2]
    Check "1 on the ground: no Stop Flying button" ($ob.self.flying -eq $false -and $is.stop_flying_button -eq $false) ("flying {0}, button {1}" -f $ob.self.flying, $is.stop_flying_button)
    # fly up (Home toggles flying, Page Up climbs)
    [void](Key "HOME" $true); Start-Sleep -Milliseconds 200; [void](Key "HOME" $false); Start-Sleep -Milliseconds 400
    [void](Key "PGUP" $true); Start-Sleep -Milliseconds 1800; [void](Key "PGUP" $false); Start-Sleep -Seconds 2
    $ob = Obs; $is = Interact @{ action = "state" }
    Shot "stopfly_1_flying"
    Check "2 flying: she is in the air and the Stop Flying button shows" ($ob.self.flying -eq $true -and [double]$ob.self.region_position[2] -gt ($pz + 2) -and $is.stop_flying_button -eq $true -and $is.stand_button -eq $false) ("flying {0}, z {1:N1} (ground {2:N1}), button {3}" -f $ob.self.flying, $ob.self.region_position[2], $pz, $is.stop_flying_button)
    # press the button itself
    $pressed = Interact @{ action = "press_stop_flying" }
    $landed = $false
    for ($k = 0; $k -lt 25; $k++) { Start-Sleep -Seconds 1; $ob = Obs; if (-not $ob.self.flying -and [double]$ob.self.region_position[2] -lt ($pz + 1.2)) { $landed = $true; break } }
    Start-Sleep -Seconds 1
    $is = Interact @{ action = "state" }
    Shot "stopfly_2_landed"
    Check "3 Stop Flying pressed: she lands and the button goes away" ($pressed.sent -eq $true -and $landed -and $is.stop_flying_button -eq $false) ("pressed {0}, flying {1}, z {2:N1} (ground {3:N1}), button {4}" -f $pressed.sent, $ob.self.flying, $ob.self.region_position[2], $pz, $is.stop_flying_button)
    $report.completed = $true
}
$report.checks = $checks
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "stopfly-report.json") -Encoding utf8
Write-Output "LIVE STOPFLY RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0
