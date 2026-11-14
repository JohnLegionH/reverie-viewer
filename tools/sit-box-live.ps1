# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# sit-box-live.ps1 - the live check of sitting on a plain box through the right-click menu. Starts the development export, logs a
# test avatar in, makes a 0.5 m box on the ground in front of it (Build, Create), points the camera at the box from behind and above
# the avatar, right-clicks the centre of the box's top face and chooses Sit Here, waits for the avatar.sit line and reads it, takes a
# picture from the side, stands up (the Stand button), and then, without pressing Escape, walks with the Up arrow and flies with
# Page Up through the engine's own key path: both must reach the avatar. Takes the box back into the inventory by its id (no pick),
# re-reads the region to prove it is gone, and logs out. -TakeBackLeftoverBoxes also takes back every plain 0.5 m box of the
# avatar's own that an earlier run left in the region.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\sit-box-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#     [-Label <name for the pictures>]
#
# Run it under an avatar claim on the account and a build claim (the kind the viewer guard accepts). Output: sit-box-report.json in
# the sandbox. Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ShotsDir,
    [Parameter(Mandatory)][string]$Credentials,
    [Parameter(Mandatory)][string]$Account,
    [Parameter(Mandatory)][string]$GridInfoUrl,
    [Parameter(Mandatory)][string]$Start,
    [string]$Exe = "",
    [string]$Label = "sit-box",
    [int]$StartSeconds = 60,
    [switch]$TakeBackLeftoverBoxes
)
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
if (-not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { }
if (-not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
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
function Note([string]$what) { [Console]::Out.WriteLine(("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what)) }
function Api([string]$command, [hashtable]$b) { Parse (Invoke-Raw $script:port $command $script:token ($b | ConvertTo-Json -Compress)).Body }
function Obs() { Parse (Invoke-Raw $script:port "observe" $script:token).Body }
function In([hashtable]$b) { Api "input" $b }
function Key([string]$k, [bool]$down) { In @{ kind = "key"; key = $k; down = $down; shift = $false; alt = $false; ctrl = $false } }
function Interact([hashtable]$b) { Api "interact" $b }
function Build([hashtable]$b) { Api "build" $b }
function World([hashtable]$b) { Api "world_sounds" $b }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
function Flat($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }
. (Join-Path $PSScriptRoot "live-takeback.ps1")
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; return $null }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-56} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }

# ---- the viewer and the login ----
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
$script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "300", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
Note "viewer started through godot.ps1, pid $($script:proc.Id)"
$tokDir = Join-Path $local "ReverieViewer\Automation"
$tf = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$script:port = [int]$j.port; $script:token = [string]$j.token
$login = Invoke-Raw $script:port "session" $script:token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
Note "login requested: $($login.Status)"
$inWorld = Wait-Until { $o = Obs; if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) { $o } } 150
if (-not $inWorld) { Note "not in the world: stopping"; [void](Invoke-Raw $script:port "quit" $script:token); exit 5 }
Start-Sleep -Seconds 4

# ---- 1. a 0.5 m box on the ground in front of the avatar ----
$me = @((Obs).self.region_position)
$spot = @{ x = [double]$me[0] + 3.0; y = [double]$me[1]; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 6 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
$made = Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z }
$box = Wait-Until { $b = Build @{ action = "state" }; @($b.own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
$report.box = $box
if ($box) { Add-Rezzed ([uint32]$box.local) }
Check "1 a 0.5 m box is made in front of the avatar" ([bool]$made.placed -and [bool]$box -and [math]::Abs([double]$box.sz - 0.5) -lt 0.01) ("placed {0}, box {1}" -f $made.placed, ($box | ConvertTo-Json -Compress))
if (-not $box) { [void](Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'); [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
$bx = [double]$box.x; $by = [double]$box.y; $bz = [double]$box.z
$top = $bz + [double]$box.sz / 2

# ---- 2. right-click the centre of its top face, Sit Here ----
# the camera between the avatar and the box and above it, looking down at the box (the aim comes from the avatar's side, 0.35 of the distance up)
$report.avatar_at_create = $me
$report.avatar_before_click = @((Obs).self.region_position)
$report.aim = Interact @{ action = "aim"; x = $bx; y = $by; z = $bz; distance = 2.2 }; Start-Sleep -Milliseconds 1500
$rc = Interact @{ action = "rightclick"; x = $bx; y = $by; z = $top }
$report.rightclick = $rc
$offered = @($rc.entries | Where-Object { $_.Id -eq "sit" -and $_.Enabled }).Count -gt 0
$chosen = $offered -and (Interact @{ action = "choose"; entry = "sit" }).chosen
[void](Interact @{ action = "aim"; distance = 0 })
$sat = [bool](Wait-Until { (Interact @{ action = "state" }).sitting -eq $true } 10)
Check "2 right-click on the top's centre, Sit Here: the avatar sits" ($offered -and $chosen -and $sat) ("pick {0} local {1}, offered {2}, chosen {3}, sitting {4}" -f $rc.pick.kind, $rc.pick.local, $offered, $chosen, $sat)

# ---- 3. the avatar.sit line, measured from the posed skeleton ----
$line = $null
if ($sat) {
    $logFile = $null
    $lineHit = Wait-Until {
        $f = @(Get-ChildItem -LiteralPath $Sandbox -Recurse -Filter "viewer.log" -ErrorAction SilentlyContinue) | Select-Object -First 1
        if ($f) { @(Select-String -LiteralPath $f.FullName -Pattern "avatar.sit " -SimpleMatch) | Select-Object -Last 1 }
    } 15
    if ($lineHit) { $line = $lineHit.Line }
    $report.sit_line = $line
    $report.seat = World @{ action = "seat" }
    Note ("avatar.sit: " + $line)
    Check "3 one avatar.sit line once seated" ([bool]$line) ("{0}" -f $line)
    # ---- 4. a picture from the side ----
    $seat = $report.seat
    [void](Interact @{ action = "aim"; x = $bx; y = $by; z = $top + 0.5; distance = 3 }); Start-Sleep -Seconds 2
    Shot "$Label-side"
    [void](Interact @{ action = "aim"; distance = 0 }); Start-Sleep -Milliseconds 500
}

# ---- 5. stand, then the arrow keys and Page Up reach the avatar without Escape ----
$standVisible = (Interact @{ action = "state" }).stand_button
$stood = $false
if ($sat) {
    [void](Interact @{ action = "stand" })
    $stood = [bool](Wait-Until { (Interact @{ action = "state" }).sitting -eq $false } 10)
    Start-Sleep -Seconds 2
}
Check "4 Stand: the avatar stands" ($sat -and $stood) ("stand button shown while seated {0}, standing {1}" -f $standVisible, $stood)
$from = @((Obs).self.region_position)
$down = Key "UP" $true
$report.up_key_state = $down
Start-Sleep -Milliseconds 1800
[void](Key "UP" $false); Start-Sleep -Seconds 2
$to = @((Obs).self.region_position)
$walked = Flat $from $to
Check "5 the Up arrow walks the avatar, no Escape pressed" ($walked -gt 0.8) ("moved {0:N2} m; text entry {1}, movement allowed {2}, window focused {3}" -f $walked, $down.text_entry, $down.movement_allowed, $down.window_focused)
[void](Key "PGUP" $true); Start-Sleep -Milliseconds 1500; [void](Key "PGUP" $false); Start-Sleep -Seconds 2
$o = Obs
Check "6 Page Up flies, no Escape pressed" ([bool]$o.self.flying) ("flying {0}" -f $o.self.flying)
[void](Key "HOME" $true); Start-Sleep -Milliseconds 200; [void](Key "HOME" $false); Start-Sleep -Seconds 3
$f = @(Get-ChildItem -LiteralPath $Sandbox -Recurse -Filter "viewer.log" -ErrorAction SilentlyContinue) | Select-Object -First 1
if ($f) { $report.arrow_lines = @(Select-String -LiteralPath $f.FullName -Pattern "input.arrow" -SimpleMatch | ForEach-Object { $_.Line }) }

# ---- 6. everything the run rezzed back to the inventory, by id, proven by re-reading the region; log out ----
if ($TakeBackLeftoverBoxes) {
    @((Build @{ action = "state" }).own_objects) | Where-Object { [math]::Abs([double]$_.sz - 0.5) -lt 0.01 -and [math]::Abs([double]$_.sx - 0.5) -lt 0.01 -and [math]::Abs([double]$_.sy - 0.5) -lt 0.01 } |
        ForEach-Object { Add-Rezzed ([uint32]$_.local) }
}
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "7 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "sit-box-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })
