# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# trade-live.ps1 - the live test of opening an object, copying its contents, buying a free object, and (optionally) detaching a worn
# object and wearing it again. Logs a test avatar in with the development export and drives the viewer through its automation:
# scan for objects that have contents, aim at each, right-click it, read what the menu and the properties say, then open one, press
# Copy To Inventory, buy a free one, and detach and re-wear. Nothing is rezzed, edited, taken, returned or deleted in the world; the
# only changes are items arriving in the avatar's own inventory (and, with -Wear, one worn object taken off and put on again).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\trade-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>] [-Wear]
#
# Settings come from the parameters or the environment as in stopfly-live.ps1. Run it under a live-login claim and an avatar claim on the
# account. Output: trade-report.json in the sandbox, pictures in -ShotsDir.
# Exit: 0 every check that could run passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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
    [int]$StartSeconds = 60,
    [int]$ScanMetres = 120,
    [int]$MaxCandidates = 10,
    [switch]$Wear,
    [switch]$ScanOnly
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
Write-Output "LIVE TRADE RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
if ($Wear) { $env:NV_OUTFIT_SCRATCH_OK = "1" }   # the detach and re-wear step changes what the test avatar wears
$argLine = "--rendering-driver vulkan -- --automation"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
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
$deadline = (Get-Date).AddSeconds(180)
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
function Check([string]$name, [string]$result, [string]$detail) { $checks.Add([ordered]@{ name = $name; result = $result; detail = $detail }); Note ("{0,-58} {1} {2}" -f $name, $result, $detail) }
function Shot([string]$name) { [void](Invoke-Raw $port "screenshot" $token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
function Interact([hashtable]$b) { Parse (Invoke-Raw $port "interact" $token ($b | ConvertTo-Json -Compress)).Body }
function Inv([hashtable]$b) { Parse (Invoke-Raw $port "inventory" $token ($b | ConvertTo-Json -Compress)).Body }
function Obs() { Parse (Invoke-Raw $port "observe" $token).Body }
function ObjectFolderChildren() { $i = Inv @{ action = "status" }; $k = @($i.system | Where-Object { $_.kind -eq "Object" }) | Select-Object -First 1; if ($k) { [int]$k.children } else { -1 } }
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 700 }; return $false }
function Aim-At($obj) { [void](Interact @{ action = "aim"; x = [double]$obj.x; y = [double]$obj.y; z = [double]$obj.z; distance = 6 }); Start-Sleep -Milliseconds 1800 }
function Menu-On($obj) {
    Aim-At $obj
    $r = Interact @{ action = "rightclick"; id = $obj.id }
    Start-Sleep -Milliseconds 2500
    $s = Interact @{ action = "state" }
    return @{ click = $r; state = $s }
}
function Close-Menu() { [void](Interact @{ action = "choose"; entry = "" }) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false; candidates = @() }

if ($o -and $o.self) {
    Note "in world, settled (objects $last)"
    Start-Sleep -Seconds 4
    # The region's own name comes from the start location (uri:<region>&x&y&z); the avatar hops about the region to find objects that have contents.
    $regionName = (($Start -replace '^uri:', '') -split '&')[0]
    $seen = @{}
    function Scan-Here() { $s = Interact @{ action = "scan"; distance = $ScanMetres }; foreach ($f in @($s.objects)) { if (-not $seen.ContainsKey([string]$f.id)) { $seen[[string]$f.id] = $f } } }
    function Flat2($a, $b) { [math]::Sqrt([math]::Pow($a[0] - $b[0], 2) + [math]::Pow($a[1] - $b[1], 2)) }
    function Hop([double]$x, [double]$y, [double]$z) {
        $t = Parse (Invoke-Raw $port "teleport" $token (@{ action = "region"; region = $regionName; x = $x; y = $y; z = $z } | ConvertTo-Json -Compress)).Body
        $arrived = Wait-Until { $ob = Obs; $ob.self -and (Flat2 @($ob.self.region_position) @($x, $y)) -lt 20 -and $ob.stage -eq "InWorld" } 45
        Start-Sleep -Seconds 7   # the objects around the new spot arrive
        return $arrived
    }
    Scan-Here
    foreach ($pt in @(@(60, 60), @(60, 196), @(196, 60), @(196, 196), @(128, 128))) {
        $ok = Hop $pt[0] $pt[1] 45
        Scan-Here
        Note ("hop to ({0}, {1}): arrived {2}, {3} objects with contents known" -f $pt[0], $pt[1], $ok, $seen.Count)
    }
    # objects the avatar may open (it owns them or may modify them) come first
    $found = @($seen.Values | Sort-Object -Property @{ Expression = { if ((([uint32]$_.flags -band 36) -ne 0) -or ([int]$_.click_action -eq 2)) { 0 } else { 1 } } }, @{ Expression = { $_.distance } })
    foreach ($f in $found) { Note ("  object at ({0:N0}, {1:N0}, {2:N0}), flags {3}, click action {4}" -f $f.x, $f.y, $f.z, $f.flags, $f.click_action) }
    if ($ScanOnly) { $found = @() }
    $openable = $null; $buyable = $null; $n = 0
    foreach ($obj in $found) {
        if ($n -ge $MaxCandidates -or ($openable -and $buyable)) { break }
        $n++
        [void](Hop ([double]$obj.x - 6) ([double]$obj.y) ([double]$obj.z + 1))
        $m = Menu-On $obj
        if ($m.click.error) { Note ("candidate {0}: {1}" -f $n, $m.click.error); continue }
        $entries = @($m.state.entries)
        $openOn = $entries -contains "open"
        $pr = $m.state.properties
        $e = @($m.click.entries | Where-Object { $_.Id -eq "open" }) | Select-Object -First 1
        $openEnabled = [bool]($e -and $e.Enabled)
        $report.candidates += [ordered]@{ n = $n; distance = [math]::Round($obj.distance, 1); open_enabled = $openEnabled; sale_type = $pr.sale_type; price = $pr.price; can_buy = $pr.can_buy; has_buy_entry = ($entries -contains "buy") }
        Note ("candidate {0}: {1:N1} m, open enabled {2}, sale type {3}, price {4}, can buy {5}" -f $n, $obj.distance, $openEnabled, $pr.sale_type, $pr.price, $pr.can_buy)
        if (-not $openable -and $openEnabled) { $openable = $obj }
        if (-not $buyable -and $pr.sale_type -gt 0 -and $pr.can_buy -and [int]$pr.price -eq 0) { $buyable = $obj }
        Close-Menu
    }
    $ownedItems = 0
    # ---- 1 open
    if ($openable) {
        [void](Hop ([double]$openable.x - 6) ([double]$openable.y) ([double]$openable.z + 1))
        $m = Menu-On $openable
        $c = Interact @{ action = "choose"; entry = "open" }
        $ok = Wait-Until { $s = Interact @{ action = "state" }; $s.contents.loaded -or $s.contents.failed } 40
        Start-Sleep -Milliseconds 800
        $s = Interact @{ action = "state" }
        Shot "trade_1_contents"
        $pass = $c.chosen -and $s.contents.loaded -and -not $s.contents.failed -and $s.contents.items -gt 0 -and $s.contents.window_rows -eq $s.contents.items
        Check "1 Open: the Contents window lists the items" $(if ($pass) { "PASS" } else { "FAIL" }) ("loaded {0}, failed {1}, items {2}, window rows {3}, status '{4}'" -f $s.contents.loaded, $s.contents.failed, $s.contents.items, $s.contents.window_rows, $s.contents.status)
        # ---- 2 copy
        if ($pass) {
            $s = Interact @{ action = "state" }
            $press = Interact @{ action = "press_copy" }
            if (-not $press.sent) {
                Check "2 Copy To Inventory: a new folder named after the object" "FAIL" ("the copy button was not usable (copy enabled {0}, can copy {1}, status '{2}')" -f $s.contents.copy_enabled, $s.contents.can_copy, $s.contents.status)
            } else {
                $arrived = Wait-Until { $t = Interact @{ action = "state" }; $t.copy -and $t.copy.name_matches -and $t.copy.items_in_folder -ge $t.copy.items_sent } 60
                $t = Interact @{ action = "state" }
                $ownedItems = $t.copy.items_in_folder
                Check "2 Copy To Inventory: a new folder named after the object" $(if ($arrived) { "PASS" } else { "FAIL" }) ("folder known {0}, name matches the object {1}, items in it {2} of {3}" -f $t.copy.folder_known, $t.copy.name_matches, $t.copy.items_in_folder, $t.copy.items_sent)
            }
        } else { Check "2 Copy To Inventory: a new folder named after the object" "SKIPPED" "the contents were not listed" }
    } else {
        Check "1 Open: the Contents window lists the items" "SKIPPED" ("no object with contents in reach had Open enabled ({0} tried)" -f $n)
        Check "2 Copy To Inventory: a new folder named after the object" "SKIPPED" "step 1 did not run"
    }
    # ---- 3 buy
    if ($buyable) {
        $before = ObjectFolderChildren
        [void](Hop ([double]$buyable.x - 6) ([double]$buyable.y) ([double]$buyable.z + 1))
        $m = Menu-On $buyable
        $c = Interact @{ action = "choose"; entry = "buy" }
        $off = Wait-Until { $s = Interact @{ action = "state" }; $s.buy.offered } 20
        Start-Sleep -Milliseconds 1500
        $s = Interact @{ action = "state" }
        Shot "trade_3_buy"
        if ($off -and $s.buy.buy_enabled) {
            $press = Interact @{ action = "press_buy" }
            $got = Wait-Until { (ObjectFolderChildren) -gt $before } 45
            Check "3 Buy a free object: the item arrives in inventory" $(if ($got) { "PASS" } else { "FAIL" }) ("sale type {0}, price {1}, Objects folder children {2} -> {3}" -f $s.buy.sale_type, $s.buy.price, $before, (ObjectFolderChildren))
        } else {
            Check "3 Buy a free object: the item arrives in inventory" "FAIL" ("offered {0}, buy enabled {1}, status '{2}'" -f $s.buy.offered, $s.buy.buy_enabled, $s.buy.status)
        }
    } else {
        Check "3 Buy a free object: the item arrives in inventory" "SKIPPED" ("no object for sale at 0 among the {0} tried" -f $n)
    }
    # ---- 4 detach and wear again
    if ($Wear) {
        $out = Inv @{ action = "outfit" }
        $objs = @($out.links | Where-Object { $_.kind -eq "Object" })
        if ($objs.Count -eq 0) {
            Check "4 Detach one worn object and wear it again" "SKIPPED" "the avatar wears no attachment"
        } else {
            $target = $objs[0].target
            $before = [int]$out.attachments
            $d = Inv @{ action = "detach"; id = $target }
            $off = Wait-Until { (Inv @{ action = "outfit" }).attachments -lt $before } 30
            $w = Inv @{ action = "attach"; id = $target; point = 0 }
            $on = Wait-Until { (Inv @{ action = "outfit" }).attachments -ge $before } 40
            Check "4 Detach one worn object and wear it again" $(if ($d.submitted -and $off -and $w.submitted -and $on) { "PASS" } else { "FAIL" }) ("worn {0}; detach sent {1}, came off {2}; attach sent {3} ({4}), back on {5}" -f $before, $d.submitted, $off, $w.submitted, $w.code, $on)
        }
    }
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
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "trade-report.json") -Encoding utf8
Write-Output "LIVE TRADE RUN DONE - other work may run again"
if (@($checks | Where-Object { $_.result -eq "FAIL" }).Count -gt 0 -or -not $report.completed) { exit 1 }
exit 0
