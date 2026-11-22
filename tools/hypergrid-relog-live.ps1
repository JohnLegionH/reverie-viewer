# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# hypergrid-relog-live.ps1 - does a log out and a log in again inside one run of the viewer draw the world as a fresh start does?
#   Three modes, on two test grids of your own (a home grid and a visited grid), through the development export's automation:
#     Fresh     log in at -StartRegion; the world must be drawn (the reference every other mode is held to).
#     SameGrid  log in, wait until drawn, log out and log in again in the same run (as the login screen does); drawn again.
#     Visit     log in at -StartRegion; type a Hypergrid address (-VisitAddress) in the location field and arrive on the visited grid;
#               make a landmark with the star; ask for home; when the home grid refuses, answer the dialog's "Log out and back in at
#               home"; at home the world must be drawn; then go to -StartRegion and take the landmark back to the visited grid.
#   "Drawn" is read twice, two seconds apart, from the observe command: the terrain view holds chunks, the
#   presenter's frame count moves on (it stops when a frame throws before the world is drawn), the own avatar is placed (drawn soles)
#   and the camera follows it. The viewer log is searched for ObjectDisposedException (a frame calling into a node freed at the logout).
#   A picture is taken at each drawn check.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\hypergrid-relog-live.ps1 -Mode Fresh|SameGrid|Visit
#     -Sandbox <new folder> -OutDir <folder> -Credentials <accounts file> -Account <alias> -LoginUri <home login address>
#     -StartRegion <home region> [-VisitAddress <address>] [-VisitRegion <name>] [-VisitGrid <host:port>] [-Exe <development export>]
#
# Run it under a live-test claim naming the home region (tools\claim-guard.ps1). Output: relog-<mode>.json in -OutDir.
# Exit: 0 every check passed, 1 a check failed, 5 the viewer or the first login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("Fresh", "SameGrid", "Visit")][string]$Mode,
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Credentials,
    [Parameter(Mandatory)][string]$Account,
    [Parameter(Mandatory)][string]$LoginUri,
    [Parameter(Mandatory)][string]$StartRegion,
    [string]$VisitAddress = "",
    [string]$VisitRegion = "",
    [string]$VisitGrid = "",
    [string]$SelectAt = "126,124,23",   # an object of the visited region to select (x,y,z in region metres)
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "live-driver.ps1")
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if ($Mode -eq "Visit" -and (-not $VisitAddress -or -not $VisitRegion -or -not $VisitGrid)) { throw "Visit needs -VisitAddress, -VisitRegion and -VisitGrid" }
$Start = "uri:" + $StartRegion + "&128&128&25"

$checks = New-Object System.Collections.Generic.List[object]
$script:bodies = @{}
function Check([string]$name, [bool]$pass, [string]$detail) {
    $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail })
    Write-LiveNote ("{0}  {1}  {2}" -f $(if ($pass) { "PASS" } else { "FAIL" }), $name, $detail)
}
function State { Invoke-LiveApi "hypergrid" @{ action = "state" } }
function Shot([string]$name) { [void](Invoke-LiveApi "screenshot" @{ dir = $OutDir; name = $name }) }
function ViewerLog { Join-Path $Sandbox "localappdata\ReverieViewer\Logs\viewer.log" }
# The interaction layers of the session drawn now: an object of the visited region is right-clicked (picked and selected, its menu
# opened), and its selection outline must be drawn; then the menu is closed and the camera given back.
function LayersCheck([string]$label) {
    $p = $SelectAt.Split(",") | ForEach-Object { [double]$_ }
    [void](Invoke-LiveApi "interact" @{ action = "aim"; x = $p[0]; y = $p[1]; z = $p[2]; distance = 6 })
    Start-Sleep -Seconds 2
    $rc = Invoke-LiveApi "interact" @{ action = "rightclick"; x = $p[0]; y = $p[1]; z = $p[2] }
    $st = Wait-LiveUntil { $x = Invoke-LiveApi "interact" @{ action = "state" }; if ($x.selected -and [int]$x.outline_pieces -gt 0) { $x } } 10
    $last = if ($st) { $st } else { Invoke-LiveApi "interact" @{ action = "state" } }
    Shot "$label-selected"
    Check "layers_$label" ($null -ne $st) "right-clicked the object at ${SelectAt}: menu=$($rc.menu) selected=$($last.selected) outline_pieces=$($last.outline_pieces) disposed_errors_in_log=$(Disposed)"
    [void](Invoke-LiveKey "ESC"); Start-Sleep -Milliseconds 500
    [void](Invoke-LiveApi "interact" @{ action = "aim"; x = 0; y = 0; z = 0; distance = 0 })
    Start-Sleep -Seconds 1
}
# The modal alert up now (its body key), answered by a real click on one of its buttons (button_0 is its first: the confirmation's yes).
function ModalBody { (Invoke-LiveApi "dialog" @{ action = "find" }).modal_body }
function ClickModal([int]$button) {
    Start-Sleep -Milliseconds 1500
    $c = Invoke-LiveClick ("button_" + $button)
    Start-Sleep -Seconds 1
    return $c
}
function Disposed {
    $f = ViewerLog
    if (-not (Test-Path -LiteralPath $f)) { return -1 }
    return @(Select-String -LiteralPath $f -SimpleMatch "ObjectDisposedException" -ErrorAction SilentlyContinue).Count
}
# One reading of what is drawn.
function Reading {
    $o = Invoke-LiveApi "observe"
    if (-not $o) { return $null }
    $p = $o.presentation
    [ordered]@{
        stage = $o.stage; chunks = $(if ($p) { [int]$p.chunks } else { 0 }); patches = $(if ($p) { [int]$p.patches_presented } else { 0 })
        frames = $(if ($p) { [long]$p.frames } else { 0 }); soles = $(if ($o.self -and $o.self.drawn_soles) { ($o.self.drawn_soles | ForEach-Object { [math]::Round([double]$_, 1) }) -join "," } else { "" })
        camera = $(if ($o.camera) { $o.camera.mode } else { "" }); terrain_complete = $(if ($o.terrain) { [bool]$o.terrain.complete } else { $false })
        body = [bool]($o.self -and $o.self.drawn_limbs -and @($o.self.drawn_limbs.PSObject.Properties).Count -gt 0)   # a body of the own avatar is shown (its limbs are placed)
    }
}
# Drawn: two readings two seconds apart, the frame count moving on, within -Seconds of being asked.
function WaitDrawn([string]$label, [double]$seconds = 90) {
    $last = $null
    $drawn = Wait-LiveUntil {
        $a = Reading
        Start-Sleep -Seconds 2
        $b = Reading
        $script:lastReading = @{ a = $a; b = $b }
        if ($a -and $b -and $b.stage -eq "InWorld" -and $b.chunks -gt 0 -and $b.frames -gt $a.frames -and $b.soles -and $b.camera -eq "follow") { $b }
    } $seconds
    $r = if ($drawn) { $drawn } else { $script:lastReading.b }
    $first = if ($script:lastReading.a) { $script:lastReading.a.frames } else { "" }
    # the own body: given up to 30 s to be shown (its look arrives after the land)
    $body = Wait-LiveUntil { $x = Reading; if ($x -and $x.body) { $true } } 30
    $script:bodies[$label] = [bool]$body
    $d = Disposed
    Shot "$label"
    $detail = "stage=$($r.stage) chunks=$($r.chunks) patches=$($r.patches) presenter_frames=$first->$($r.frames) own_soles=[$($r.soles)] camera=$($r.camera) terrain_complete=$($r.terrain_complete) own_body_shown=$([bool]$body) disposed_errors_in_log=$d"
    Check "drawn_$label" ($null -ne $drawn -and $d -eq 0) $detail
    return ($null -ne $drawn)
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Start-LiveViewer -Sandbox $Sandbox -Exe $Exe -StartSeconds 120 -TimeoutSeconds 1800)) { Write-LiveNote "viewer did not start"; exit 5 }
$inWorld = Enter-LiveWorld -Credentials $Credentials -Account $Account -LoginUri $LoginUri -Start $Start -Seconds 180
if (-not $inWorld) { Write-LiveNote "login did not complete"; Stop-LiveViewer; exit 5 }
try {
    $s0 = Wait-LiveUntil { $s = State; if ($s.region -eq $StartRegion -and $s.current_grid) { $s } } 60
    $homeGrid = $s0.home_grid
    Check "login_at_start" ($null -ne $s0 -and $null -eq $s0.visited_grid) "region=$($s0.region) grid=$($s0.current_grid)"
    [void](WaitDrawn "01-first-login")
    if ($env:NV_CHARACTER_DIR) { Check "body_at_first_login" ($script:bodies["01-first-login"]) "the character folder is set, so the own body must be shown at the first login: $($script:bodies['01-first-login'])" }

    if ($Mode -eq "SameGrid") {
        $out = Invoke-LiveApi "session" @{ action = "logout" }
        $gone = Wait-LiveUntil { $x = Invoke-LiveApi "session" @{ action = "state" }; if ($x.ended -or -not $x.has_session) { $x } } 30
        Check "logged_out" ($null -ne $gone) "ended=$($gone.ended) reason=$($gone.end_reason)"
        Start-Sleep -Seconds 3
        $again = Enter-LiveWorld -Credentials $Credentials -Account $Account -LoginUri $LoginUri -Start $Start -Seconds 180
        Check "logged_in_again" ($null -ne $again) "in world again in the same run"
        [void](WaitDrawn "02-relogged-same-grid")
        Check "body_as_first_login" ($script:bodies["02-relogged-same-grid"] -eq $script:bodies["01-first-login"]) "own body shown: first login $($script:bodies['01-first-login']), after the relog $($script:bodies['02-relogged-same-grid'])"
    }

    if ($Mode -eq "Visit") {
        [void](Invoke-LiveApi "topbar" @{ action = "type"; text = $VisitAddress })
        $v = Wait-LiveUntil { $s = State; if ($s.region -eq $VisitRegion -and $s.visited_grid -eq $VisitGrid -and $s.teleport_stage -eq "Idle") { $s } } 120
        Check "arrived_visit" ($null -ne $v) "region=$($v.region) visited=$($v.visited_grid) label='$($v.grid_label)'"
        if (-not $v) { throw "no arrival on the visited grid" }
        [void](WaitDrawn "02-visiting")
        LayersCheck "02-visiting"
        $lm0 = Invoke-LiveApi "hypergrid" @{ action = "landmarks" }
        $click = Invoke-LiveClick "landmark"
        $lm1 = Wait-LiveUntil { $x = Invoke-LiveApi "hypergrid" @{ action = "landmarks" }; if ($x -and [int]$x.count -gt [int]$lm0.count) { $x } } 45
        $made = if ($lm1) { @($lm1.landmarks)[0] } else { $null }
        Check "landmark_made_on_visit" ($null -ne $made -and $made.grid -eq $VisitGrid) "star clicked: $($click.clicked); landmarks $($lm0.count) -> $($lm1.count); newest names grid: $($made.grid)"

        # home: the home grid refuses (the dialog), and its "Log out and back in at home" is answered by a click on it, as a person answers it
        [void](Invoke-LiveApi "teleport" @{ action = "home" })
        $offer = Wait-LiveUntil { $d = Invoke-LiveApi "dialog" @{ action = "find" }; if ($d.modal_body -eq "home.refused.body") { $d } } 120
        $sNow = State
        Check "home_refused_offer" ($null -ne $offer) "failure='$($sNow.teleport_failure_text)' visited=$($sNow.visited_grid)"
        if (-not $offer) { throw "no home-refused dialog" }
        Shot "03-home-refused"
        $disposedBefore = Disposed
        $press = ClickModal 0   # "Log out and back in at home", clicked as a person clicks it
        Check "relog_button_clicked" ([bool]$press.clicked -and (ModalBody) -ne "home.refused.body") "clicked=$($press.clicked) reach=$($press.reach) modal_after=$(ModalBody)"
        $back = Wait-LiveUntil { $x = State; if ($x.in_world -and $x.current_grid -eq $homeGrid -and $null -eq $x.visited_grid) { $x } } 180
        Check "relogged_at_home" ($null -ne $back) "region=$($back.region) grid=$($back.current_grid) label='$($back.grid_label)' disposed_errors_before_answer=$disposedBefore"
        [void](WaitDrawn "04-home-after-relog")
        Check "body_as_first_login" ($script:bodies["04-home-after-relog"] -eq $script:bodies["01-first-login"]) "own body shown: first login $($script:bodies['01-first-login']), after the relog $($script:bodies['04-home-after-relog'])"

        # the landmark made on the visited grid, from home (from -StartRegion: a home region on the visited region's map spot is refused by OpenSim)
        if ($back -and $back.region -ne $StartRegion) {
            [void](Invoke-LiveApi "topbar" @{ action = "type"; text = "$StartRegion/128/128/25" })
            [void](Wait-LiveUntil { $x = State; if ($x.region -eq $StartRegion -and $x.teleport_stage -eq "Idle") { $x } } 60)
        }
        # the landmark as the home session's inventory holds it (the inventory window opened, as a person opens it to double-click one)
        [void](Invoke-LiveApi "open_window" @{ name = "inventory" })
        $held = if ($made) { Wait-LiveUntil { $x = Invoke-LiveApi "hypergrid" @{ action = "landmarks" }; $m = @($x.landmarks) | Where-Object { [string]$_.id -eq [string]$made.id }; if ($m) { $m } } 60 } else { $null }
        Check "landmark_held_at_home" ($null -ne $held) "the landmark made on the visited grid is in the home session's inventory, naming $($held.grid)"
        if ($held) {
            [void](Invoke-LiveApi "hypergrid" @{ action = "open_landmark"; text = [string]$made.id })
            $dlg = Wait-LiveUntil { if ((ModalBody) -eq "landmark.info.body") { $true } } 30
            Check "landmark_window_shown" ($null -ne $dlg) "opened the landmark made on the visited grid (its window: name, place, grid, Teleport)"
            Shot "05-landmark-window"
            if ($dlg) {
                $tp = ClickModal 0   # Teleport
                Write-LiveNote "landmark Teleport clicked=$($tp.clicked)"
                $lv = Wait-LiveUntil { $s = State; if ($s.region -eq $VisitRegion -and $s.visited_grid -eq $VisitGrid -and $s.teleport_stage -eq "Idle") { $s } } 120
                Check "back_by_landmark" ($null -ne $lv) "region=$($lv.region) visited=$($lv.visited_grid) label='$($lv.grid_label)' failure='$((State).teleport_failure_text)'"
                if ($lv) { [void](WaitDrawn "06-back-by-landmark"); LayersCheck "06-back-by-landmark" }
            }
        }
    }
}
catch { Check "script" $false $_.Exception.Message }
finally {
    $result = [ordered]@{ mode = $Mode; exe = $Exe; checks = $checks; passed = @($checks | Where-Object { $_.pass }).Count; failed = @($checks | Where-Object { -not $_.pass }).Count; disposed_errors = (Disposed) }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir "relog-$($Mode.ToLower()).json") -Encoding utf8
    Stop-LiveViewer
}
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0) { exit 1 }
exit 0
