# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# neighbours-live.ps1 - the live check of neighbouring regions. One command logs a test avatar in near the border of a region with the
# development export and --neighbours=on, and records through the viewer's automation API:
#   1. that the neighbour's terrain and objects are drawn (the "neighbours" command: grid cells, offsets, chunks, objects), with a screenshot;
#   2. (when -ExpectOtherAvatar) that an avatar other than the agent's own is held by a neighbour region's world;
#   3. (when -Cross) a walk east across the border and back, -Crossings times in the one login: each time the viewer stays in world, the
#      region the viewer reports changes and changes back, and the regions' worlds are not emptied or reloaded by it. Every leg's UTC start and end
#      are written to the report so another client's observations can be matched to them.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\neighbours-live.ps1 -Sandbox <new folder> -Start <start location>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-OtherClientPattern <regex or none>]
#     [-ExpectOtherAvatar] [-Cross] [-HeadingEastDeg <degrees>] [-Exe <development export>] [-DryRun]
#     [-DefaultNeighbours] [-Trace] [-ExpectGrids "x,y;x,y"] [-HoldAddresses "ip:port;ip:port"] [-PretendEast "ip:port" -PretendHome "ip:port"]
#   -Trace switches the viewer's crossing record on (messages, promotions, the circuit and content of agent updates).
#   -ExpectGrids waits for each listed grid cell to be a neighbour with terrain and chunks drawn (north and corner neighbours included).
#   -HoldAddresses: the viewer is told, before the login, not to connect these region addresses in advance (the agent's own region at login
#   always connects). A crossing into one of them then has no circuit and the viewer makes it itself. The addresses are given by the caller
#   and never written to a report. (On a grid that needs a child circuit at the destination the server fails such a crossing: the run shows it.)
#   -PretendEast / -PretendHome: the first east leg (and the first west leg) is walked with the viewer told to treat the destination's circuit as
#   not held for that crossing, so its own connect (the runner, the seed capability, the promotion) runs against the real grid.
#
# Settings come from the parameters or, when not given, from NV_TEST_ACCOUNTS, NV_LIVE_ACCOUNT, NV_GRID_INFO_URL, NV_OTHER_CLIENT_PATTERN.
# Run it under a live-login claim and an avatar claim on the account. Output goes to the sandbox folder (neighbours-report.json, screenshots,
# run.log). Region names and positions are not written to the report: grid cells, offsets and counts only.
# Exit: 0 every requested check passed, 1 one failed, 3 blocked (preconditions), 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$Start,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$OtherClientPattern = $env:NV_OTHER_CLIENT_PATTERN,
    [string]$Exe = "",
    [int]$StartSeconds = 60,
    [switch]$ExpectOtherAvatar,
    [switch]$Cross,
    [switch]$DefaultNeighbours,
    [switch]$Trace,
    [string]$ExpectGrids = "",
    [string]$HoldAddresses = "",
    [string]$PretendEast = "",
    [string]$PretendHome = "",
    [int]$Crossings = 1,
    [switch]$WaterShots,   # midday, the follow camera low and level, a picture in each of four directions (the neighbour's water seen in daylight)
    [int]$Axis = 0,   # 0: the outward direction runs east (x); 1: it runs north (y), for a neighbour to the north
    [double]$HeadingEastDeg = 0,
    [switch]$DryRun
)
# A live setting that is not set comes from the untracked local settings file (live-env.ps1).
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $OtherClientPattern) { $OtherClientPattern = & "$PSScriptRoot\live-env.ps1" NV_OTHER_CLIENT_PATTERN }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token '{"action":"logout"}'); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

# ---------------- preconditions (also the whole of -DryRun) ----------------
$problems = New-Object System.Collections.Generic.List[string]
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (run tools\s1.ps1 for the development export): $Exe") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
if (-not $OtherClientPattern) { $problems.Add("-OtherClientPattern (or NV_OTHER_CLIENT_PATTERN) is required: a regex of other clients that may use the account, or none") }
elseif ($OtherClientPattern -ne "none") {
    . (Join-Path $PSScriptRoot "other-clients.ps1")
    $agent = @(Find-OtherClients -Pattern $OtherClientPattern)
    if ($agent.Count -gt 0) { $problems.Add("another test client is running ($($agent.Count) process(es)): the test account may be in use") }
}
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count)): one account in one session at a time") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
Write-Output "preconditions ok: account $Account"
if ($DryRun) { Write-Output "dry run: nothing started, nothing logged in"; exit 0 }

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
function Observe { Parse (Invoke-Raw $port "observe" $token).Body }
function Neighbours { $r = Parse (Invoke-Raw $port "neighbours" $token).Body; if ($r -and -not $r.regions) { $r | Add-Member -NotePropertyName regions -NotePropertyValue @() -Force }; return $r }
function Current($n) { @($n.regions | Where-Object { $_.current }) | Select-Object -First 1 }
function SameGrid($a, $b) { $a -and $b -and $a[0] -eq $b[0] -and $a[1] -eq $b[1] }
function StateName { (Parse (Invoke-Raw $port "session" $token '{"action":"state"}').Body).region.name }   # the location bar's region (kept in memory only)
function Key([string]$key, [bool]$down) { [void](Invoke-Raw $port "keys" $token (@{ key = $key; down = $down } | ConvertTo-Json -Compress)) }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output ""
Write-Output "LIVE NEIGHBOURS RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan -- --automation" + $(if ($DefaultNeighbours) { "" } else { " --neighbours=on" })   # -DefaultNeighbours leaves the setting to its default
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

if ($HoldAddresses) {
    $hold = Invoke-Raw $port "neighbour_debug" $token (@{ action = "hold_next_login"; addresses = @($HoldAddresses -split ';' | Where-Object { $_ }) } | ConvertTo-Json -Compress)
    Note "neighbours held back from the advance connect: $($hold.Status)"
}
$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
$report = [ordered]@{ account = $Account; checks = @(); completed = $false }
function Check([string]$name, [bool]$pass, $detail) { $script:report.checks += [ordered]@{ check = $name; pass = $pass; detail = $detail }; Note ("CHECK {0}: {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), ($detail | ConvertTo-Json -Compress -Depth 4)) }

# settle: in world, terrain complete, the object count stable
$stable = 0; $last = -1; $o = $null; $ended = $false
$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $o = Observe
    if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) {
        if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
        $last = $o.world.objects
        if ($stable -ge 3) { break }
    }
    $stRaw = Invoke-Raw $port "session" $token '{"action":"state"}'
    $st = Parse $stRaw.Body
    if (($stRaw.Status -ne 200 -and $clock.Elapsed.TotalSeconds -gt 25) -or ($st -and $st.ended)) { Note "session ended or gone during settle (status $($stRaw.Status), $($st.end_reason))"; $ended = $true; break }
}
if (-not $ended -and $o -and $o.self) {
    Note "in world, settled (objects $last)"
    if ($Trace) { $tr = Invoke-Raw $port "neighbour_debug" $token '{"action":"trace_on"}'; Note "crossing record on: $($tr.Status)" }
    $startName = $o.region.name   # kept in memory only, to see the region change; never written
    # 1. a neighbour whose terrain and objects are drawn
    $n = $null; $good = $false
    $until = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $until -and -not $good) {
        Start-Sleep -Seconds 3
        $n = Neighbours
        $good = $n -and $n.enabled -and @($n.regions | Where-Object { -not $_.current -and $_.chunks -gt 0 }).Count -gt 0   # drawn: terrain chunks (a quiet region may hold no objects)
    }
    Check "neighbour_drawn" $good ([ordered]@{ regions = @($n.regions | ForEach-Object { [ordered]@{ grid = $_.grid; size = $_.size; current = $_.current; origin_offset = $_.origin_offset; patches = $_.terrain_patches; chunks = $_.chunks; objects = $_.objects; instances = $_.instances; drawn = $_.drawn; avatars = @($_.avatars).Count } }) })
    if ($ExpectGrids) {
        $want = @($ExpectGrids -split ';' | Where-Object { $_ } | ForEach-Object { , @($_ -split ',' | ForEach-Object { [int]$_ }) })
        $first = @{}; $until = (Get-Date).AddSeconds(150); $allDrawn = $false; $t0e = $clock.Elapsed.TotalSeconds
        while ((Get-Date) -lt $until -and -not $allDrawn) {
            $n = Neighbours; $allDrawn = $true
            foreach ($g in $want) {
                $r = @($n.regions | Where-Object { $_.grid[0] -eq $g[0] -and $_.grid[1] -eq $g[1] })[0]
                $ok = $r -and $r.terrain_patches -gt 0 -and $r.chunks -gt 0
                if ($ok -and -not $first.ContainsKey("$($g[0]),$($g[1])")) { $first["$($g[0]),$($g[1])"] = [math]::Round($clock.Elapsed.TotalSeconds - $t0e, 1) }
                if (-not $ok) { $allDrawn = $false }
            }
            if (-not $allDrawn) { Start-Sleep -Seconds 3 }
        }
        Check "expected_neighbours_drawn" $allDrawn ([ordered]@{ expected = $ExpectGrids; first_data_seconds = $first; regions = @($n.regions | ForEach-Object { [ordered]@{ grid = $_.grid; patches = $_.terrain_patches; chunks = $_.chunks; current = $_.current } }) })
    }
    # the neighbour's water: the anchor draws water and the water plan holds a region of its own (each neighbour with a water height)
    $waterNeighbours = @($n.regions | Where-Object { -not $_.current -and $null -ne $_.water_height })
    Check "neighbour_water" ($n.anchor_has_water -and $waterNeighbours.Count -gt 0 -and $n.water_plan_regions -ge $waterNeighbours.Count) ([ordered]@{ anchor_has_water = $n.anchor_has_water; neighbours_with_water_height = $waterNeighbours.Count; water_plan_regions = $n.water_plan_regions })
    if ($WaterShots) {
        [void](Invoke-Raw $port "sky" $token '{"choice":"midday"}')
        Start-Sleep -Seconds 2
        foreach ($yaw in 0, 90, 180, 270) {
            [void](Invoke-Raw $port "orbit" $token (@{ yaw_deg = $yaw; pitch_deg = 8 } | ConvertTo-Json -Compress))
            Start-Sleep -Seconds 2
            [void](Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = ("neighbours-water-yaw{0}" -f $yaw) } | ConvertTo-Json -Compress))
        }
        [void](Invoke-Raw $port "sky" $token '{"choice":"region"}')
    }
    $shot = Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = "neighbours-border" } | ConvertTo-Json -Compress)
    Note "screenshot: $($shot.Status)"
    # 2. an avatar other than the agent's own in a neighbour's world
    if ($ExpectOtherAvatar) {
        $seen = $false; $until = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $until -and -not $seen) {
            Start-Sleep -Seconds 3
            $n = Neighbours
            $seen = @($n.regions | Where-Object { -not $_.current -and @($_.avatars).Count -gt 0 }).Count -gt 0
        }
        [void](Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = "neighbours-other-avatar" } | ConvertTo-Json -Compress))
        Check "other_avatar_in_neighbour" $seen ([ordered]@{ avatars_in_neighbours = @($n.regions | Where-Object { -not $_.current } | ForEach-Object { @($_.avatars).Count }) })
    }
    # 3. walks across the border and back
    if ($Cross) {
        $report.legs = @()
        function Face([double]$deg) {
            for ($k = 0; $k -lt 40; $k++) {
                $h = [double](Observe).self.region_heading_deg
                $d = (($deg - $h + 540) % 360) - 180
                if ([math]::Abs($d) -lt 12) { break }
                $turn = if ($d -gt 0) { "A" } else { "D" }
                Key $turn $true; Start-Sleep -Milliseconds 250; Key $turn $false; Start-Sleep -Milliseconds 150
            }
        }
        function NoReload($before, $mid) {
            $ok = $true
            for ($r = 0; $r -lt $mid.regions.Count; $r++) {
                $m = $mid.regions[$r]; $b = @($before.regions | Where-Object { $_.grid[0] -eq $m.grid[0] -and $_.grid[1] -eq $m.grid[1] })[0]
                if ($b -and ($m.objects -lt ($b.objects * 0.9) -or $m.chunks -lt $b.chunks -or $m.terrain_patches -lt $b.terrain_patches)) { $ok = $false }
            }
            return $ok
        }
        # one leg: face the heading, walk until the region the viewer reports is not the one it started in (or the wanted one), then a few metres on
        function Leg([string]$name, [double]$heading, $fromGrid, $wantGrid, [double]$dirX = 1, [string]$pretend = "") {
            $before = Neighbours
            $fromName = StateName
            if ($pretend) { $pr = Invoke-Raw $port "neighbour_debug" $token (@{ action = "pretend_not_held"; addresses = @($pretend) } | ConvertTo-Json -Compress); Note "$name : destination treated as not held: $($pr.Status)" }
            Face $heading
            $t0 = [DateTime]::UtcNow
            $arrivedName = $null; $disc = $false; $samples = 0
            Key "W" $true
            $until = (Get-Date).AddSeconds(75)
            while ((Get-Date) -lt $until) {
                Start-Sleep -Milliseconds 400
                $ob = Observe
                if ($ob.stage -ne "InWorld") { $disc = $true; break }
                $cn = Current (Neighbours)
                $samples++
                if ($cn -and -not (SameGrid $cn.grid $fromGrid) -and ($null -eq $wantGrid -or (SameGrid $cn.grid $wantGrid))) { $arrivedName = StateName; if (-not $arrivedName) { $arrivedName = "(unnamed)" }; break }
            }
            $arrPos = $null; $arrGrid = $null; if ($arrivedName) { $arrPos = (Observe).self.region_position; $arrGrid = (Current (Neighbours)).grid }
            # three seconds more walking, the region the viewer reports sampled every 0.3 s: a bounce back over the border shows as a change
            $bounces = 0
            for ($k = 0; $k -lt 10; $k++) { Start-Sleep -Milliseconds 300; $cn2 = Current (Neighbours); if ($arrGrid -and $cn2 -and -not (SameGrid $cn2.grid $arrGrid)) { $bounces++ } }
            $endPos = (Observe).self.region_position
            $gridEnd = Current (Neighbours)
            Key "W" $false
            $t1 = [DateTime]::UtcNow
            $mid = Neighbours; $curMid = Current $mid; $obMid = Observe
            $inWorld = $obMid.stage -eq "InWorld"
            $reload = NoReload $before $mid
            $changed = $null -ne $arrivedName -and $arrivedName -ne $fromName
            $moved = if ($arrPos -and $endPos) { [math]::Round(($endPos[$Axis] - $arrPos[$Axis]) * $dirX, 1) } else { $null }
            $walkedOn = $null -ne $moved -and $moved -ge 6
            $noBounce = $bounces -eq 0 -and $gridEnd -and (SameGrid $gridEnd.grid $curMid.grid)
            $pass = ($null -ne $arrivedName) -and -not $disc -and $inWorld -and $reload -and $changed -and $walkedOn -and $noBounce
            $script:report.legs += [ordered]@{ leg = $name; start_utc = $t0.ToString("o"); end_utc = $t1.ToString("o"); region_changed = ($null -ne $arrivedName); location_bar_changed = $changed; disconnected = $disc; in_world = $inWorld; no_reload = $reload; walked_on_metres = $moved; bounces = $bounces; pass = $pass }
            Check $name $pass ([ordered]@{ region_changed = ($null -ne $arrivedName); location_bar_name_changed = $changed; disconnected = $disc; in_world = $inWorld; no_reload = $reload; walked_on_metres = $moved; bounces = $bounces; samples = $samples; current_grid = $curMid.grid; origin_offset = $curMid.origin_offset; before = @($before.regions | ForEach-Object { [ordered]@{ grid = $_.grid; objects = $_.objects; chunks = $_.chunks; patches = $_.terrain_patches } }); after = @($mid.regions | ForEach-Object { [ordered]@{ grid = $_.grid; current = $_.current; objects = $_.objects; chunks = $_.chunks; patches = $_.terrain_patches } }) }) | Out-Host
            return @{ Pass = $pass; Grid = @($curMid.grid[0], $curMid.grid[1]); Name = $arrivedName; Disconnected = $disc }
        }
        $cur0 = Current (Neighbours)
        $homeGrid = @($cur0.grid[0], $cur0.grid[1])
        $west = $HeadingEastDeg + 180
        for ($c = 1; $c -le $Crossings; $c++) {
            $pe = if ($c -eq 1) { $PretendEast } else { "" }
            $e = Leg ("cross_east_{0}" -f $c) $HeadingEastDeg $homeGrid $null 1 $pe
            [void](Invoke-Raw $port "screenshot" $token (@{ dir = $Sandbox; name = ("neighbours-crossed-{0}" -f $c) } | ConvertTo-Json -Compress))
            if ($e.Disconnected -or -not $e.Name) { Note "stopping the walks: the east leg $c did not arrive"; break }
            $pw = if ($c -eq 1) { $PretendHome } else { "" }
            $w = Leg ("cross_west_{0}" -f $c) $west $e.Grid $homeGrid -1 $pw
            if ($w.Disconnected -or -not $w.Name) { Note "stopping the walks: the west leg $c did not arrive"; break }
        }
    }
    $report.completed = $true
}
if ($HoldAddresses -or $PretendEast -or $PretendHome) { [void](Invoke-Raw $port "neighbour_debug" $token '{"action":"release"}') }
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended) reason=$($after.end_reason)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$after.ended
# what the viewer's own log says about the crossings (read after it has exited, when the file is complete)
if ($Cross -and (Test-Path -LiteralPath $log)) {
    $legsDone = @($report.legs).Count
    $promotes = @(Select-String -LiteralPath $log -Pattern 'crossing\.promote').Count
    $connects = @(Select-String -LiteralPath $log -Pattern 'crossing\.connect').Count
    $library = @(Select-String -LiteralPath $log -Pattern 'crossing\.library').Count
    $crashes = @(Select-String -LiteralPath $log -Pattern 'Stack overflow|Unhandled exception').Count
    Check "one_promote_per_leg" ($promotes -eq $legsDone) ([ordered]@{ promotes = $promotes; legs = $legsDone })
    $wantConnects = @($PretendEast, $PretendHome | Where-Object { $_ }).Count
    if ($wantConnects -gt 0) { Check "viewer_own_connect_used" ($connects -ge $wantConnects -and $library -eq 0) ([ordered]@{ own_connects = $connects; wanted = $wantConnects; library_fallbacks = $library }) }
    Check "no_crash_in_log" ($crashes -eq 0) ([ordered]@{ crash_lines = $crashes })
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "neighbours-report.json") -Encoding utf8
Write-Output ""
Write-Output "LIVE NEIGHBOURS RUN DONE - other work may run again"
if (-not $report.completed) { exit 5 }
if (@($report.checks | Where-Object { -not $_.pass }).Count -gt 0) { exit 1 }
exit 0
