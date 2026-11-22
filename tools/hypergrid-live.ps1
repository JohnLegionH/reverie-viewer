# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# hypergrid-live.ps1 - a Hypergrid trip on two test grids of your own, through the development export's automation.
#   The home grid has a region (-HomeRegion) on the same map spot as the visited grid's region (-VisitRegion), and a neighbour beside it
#   (-HomeNeighbour). OpenSim refuses a teleport between two regions on the same map spot (EntityTransferModule: "Can't teleport to a
#   region on same map position"), so the trip checks that refusal is shown in the grid's own words, and leaves from the neighbour.
#   Steps: log in at the home region; teleport inside the home grid by a secondlife:// address and a plain region name; try the visited
#   region from the home region (refused, the grid's text shown); set home at the neighbour; for each Hypergrid address form given,
#   type it in the location field (the "Go to" box), arrive, check the visiting label, the location's host, the region's size, that only
#   the visited region is held and the map is the visited grid's, then go home (taken); on the visited grid say a line of chat, count the
#   objects drawn, open the inventory, make a landmark with the star; go home; come back by the landmark (opened from the inventory,
#   Teleport in its window); go home; set home at the home region; from the neighbour click a hop:// link said in chat, check nothing
#   moved before the question was answered, answer Teleport, arrive; ask for home (refused by the visited grid: same map spot), check the
#   log-out-and-back-in offer, answer it and check the session is back at the home region. Pictures of each stage go to -OutDir.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\hypergrid-live.ps1 -Sandbox <new folder> -OutDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -LoginUri <home login address> -Start <uri: start location>
#     -HomeRegion <name> -HomeNeighbour <name> -VisitRegion <name> -VisitGrid <host:port> -Forms "<address>|<address>|..."
#     [-Exe <development export>]
#
# Run it under a live-test claim naming the home region (tools\claim-guard.ps1). Output: hypergrid-trip.json in -OutDir.
# Exit: 0 every check passed, 1 a check failed, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Credentials,
    [Parameter(Mandatory)][string]$Account,
    [Parameter(Mandatory)][string]$LoginUri,
    [Parameter(Mandatory)][string]$Start,
    [Parameter(Mandatory)][string]$HomeRegion,
    [Parameter(Mandatory)][string]$HomeNeighbour,
    [Parameter(Mandatory)][string]$VisitRegion,
    [Parameter(Mandatory)][string]$VisitGrid,
    [Parameter(Mandatory)][string]$Forms,   # the address forms, separated by |
    [string]$Exe = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "live-driver.ps1")
$repo = Split-Path -Parent $PSScriptRoot
$FormList = @($Forms.Split("|") | Where-Object { $_.Trim() })
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) {
    $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail })
    Write-LiveNote ("{0}  {1}  {2}" -f $(if ($pass) { "PASS" } else { "FAIL" }), $name, $detail)
}
function State { Invoke-LiveApi "hypergrid" @{ action = "state" } }
function Shot([string]$name) { [void](Invoke-LiveApi "screenshot" @{ dir = $OutDir; name = $name }) }
function Short($s) { "region=$($s.region) size=$($s.size_x) grid=$($s.current_grid) visited=$($s.visited_grid) label='$($s.grid_label)' location='$($s.location)' worlds=$($s.worlds) objects=$($s.objects) map_grid=$($s.map_grid) map_regions=$($s.map_regions) stage=$($s.teleport_stage) failure=$($s.teleport_failure)" }
function At([string]$region, [bool]$visiting, [double]$seconds = 90) {
    Wait-LiveUntil { $s = State; if ($s -and $s.region -eq $region -and (($null -ne $s.visited_grid) -eq $visiting) -and $s.teleport_stage -eq "Idle") { $s } } $seconds
}
function Go([string]$text, [string]$region) {
    [void](Invoke-LiveApi "topbar" @{ action = "type"; text = $text })
    return At $region $false 60
}
function WaitingWith([string]$token) {
    $d = Invoke-LiveApi "dialog" @{ action = "find"; token = $token }
    $i = 0
    foreach ($w in @($d.waiting)) { if ($w.contains) { return @{ nth = $i; report = $d } }; $i++ }
    if ($d.modal_contains) { return @{ nth = -1; report = $d } }   # an alert is not in the list: -1 names the alert on screen
    return $null
}
$homeOutcomes = New-Object System.Collections.Generic.List[string]
# Home while visiting: the teleport home is tried; when the home grid refuses it, the dialog must offer to log out and back in at home,
# and its OK must bring the session back at home. Either way ends at home; the outcome is recorded.
function GoHome([string]$label, [string]$homeAt) {
    [void](Invoke-LiveApi "teleport" @{ action = "home" })
    $first = Wait-LiveUntil {
        $x = State
        if ($x.region -eq $homeAt -and $null -eq $x.visited_grid -and $x.teleport_stage -eq "Idle") { return @{ taken = $x } }
        $d = Invoke-LiveApi "dialog" @{ action = "find" }
        if ($d.modal_body -eq "home.refused.body") { return @{ offer = $d; state = $x } }
    } 120
    if ($first -and $first.taken) {
        $homeOutcomes.Add("taken")
        Check "home_$label" $true ("teleport home taken: " + (Short $first.taken))
        return $true
    }
    if (-not $first) {
        $s = State
        $homeOutcomes.Add("neither")
        Check "home_$label" $false ("no arrival and no offer: '$($s.teleport_failure_text)'; " + (Short $s))
        return $false
    }
    $s = $first.state
    Shot "home-refused-$label"
    # the offer is an alert, which the list leaves out: nth -1 answers the alert on screen
    [void](Invoke-LiveApi "dialog" @{ action = "answer"; nth = -1; button = 0 })
    $relog = Wait-LiveUntil { $x = State; if ($x.in_world -and $x.region -eq $homeAt -and $null -eq $x.visited_grid) { $x } } 180
    $homeOutcomes.Add($(if ($relog) { "refused-relogged" } else { "refused-relog-failed" }))
    Check "home_$label" ($null -ne $relog) ("teleport home refused '$($s.teleport_failure_text)' while visiting " + $s.visited_grid + "; offer answered OK; back at home by a fresh login: " + (Short $(if ($relog) { $relog } else { State })))
    return ($null -ne $relog)
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Start-LiveViewer -Sandbox $Sandbox -Exe $Exe -StartSeconds 120 -TimeoutSeconds 2400)) { Write-LiveNote "viewer did not start"; exit 5 }
$inWorld = Enter-LiveWorld -Credentials $Credentials -Account $Account -LoginUri $LoginUri -Start $Start -Seconds 180
if (-not $inWorld) { Write-LiveNote "login did not complete"; Stop-LiveViewer; exit 5 }
try {
    $s0 = Wait-LiveUntil { $s = State; if ($s.region -eq $HomeRegion -and $s.current_grid) { $s } } 60
    Check "home_login" ($null -ne $s0 -and $null -eq $s0.visited_grid -and $s0.grid_label -notmatch "Visiting") (Short $s0)
    Shot "01-home"

    # ---- addresses on the home grid ----
    $n = Go "secondlife://$([uri]::EscapeDataString($HomeNeighbour))/128/128/25" $HomeNeighbour
    Check "form_secondlife_on_home_grid" ($null -ne $n) (Short $(if ($n) { $n } else { State }))
    $h = Go "$HomeRegion/128/128/25" $HomeRegion
    Check "form_plain_name_on_home_grid" ($null -ne $h) (Short $(if ($h) { $h } else { State }))

    # ---- the overlap: the home region is on the visited region's map spot; the grid refuses, and its own words are shown ----
    [void](Invoke-LiveApi "topbar" @{ action = "type"; text = $FormList[0] })
    $refused = Wait-LiveUntil { $x = State; if ($x.teleport_stage -eq "Failed") { $x } } 90
    $refusalOk = $null -ne $refused -and $refused.teleport_failure -eq "teleport.error.raw" -and [string]$refused.teleport_failure_text -and $refused.region -eq $HomeRegion -and $null -eq $refused.visited_grid
    Check "same_spot_refusal_shows_grid_text" $refusalOk ("failure '$($refused.teleport_failure_text)'; " + (Short $refused))
    Shot "02-same-spot-refused"
    Start-Sleep -Seconds 2

    # ---- home set at the neighbour: the trips leave from there ----
    [void](Go "$HomeNeighbour/128/128/25" $HomeNeighbour)
    $sh = Invoke-LiveApi "teleport" @{ action = "set_home" }
    Start-Sleep -Seconds 2
    Check "set_home_beside" ([bool]$sh.sent) "set home at the neighbour"

    # ---- every Hypergrid address form, there and home ----
    $i = 0
    foreach ($form in $FormList) {
        $i++
        $before = State
        [void](Invoke-LiveApi "topbar" @{ action = "type"; text = $form })
        $v = At $VisitRegion $true 120
        $s = if ($v) { $v } else { State }
        $ok = $null -ne $v -and $s.visited_grid -eq $VisitGrid -and $s.grid_label -match "Visiting" -and $s.grid_label -match [regex]::Escape($VisitGrid.Split(':')[0]) `
            -and $s.location -match [regex]::Escape("($VisitGrid)") -and [int]$s.size_x -eq 256 -and [int]$s.worlds -eq 1 -and $s.map_grid -eq $VisitGrid -and $s.back_grid -eq $before.current_grid
        Check "form_$i" $ok ("'$form' -> " + (Short $s) + " back_grid=$($s.back_grid) failure_text='$($s.teleport_failure_text)'")
        if ($i -eq 1) { Shot "03-arrived-visit" }
        if (-not $v) { continue }
        if ($i -lt $FormList.Count) { [void](GoHome "after_form_$i" $HomeNeighbour) }
    }

    # ---- on the visited grid ----
    $token = "hg-check-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
    [void](Invoke-LiveApi "chat" @{ action = "say"; text = "Hypergrid chat check $token" })
    $heard = Wait-LiveUntil { $c = Invoke-LiveApi "chat" @{ action = "find"; token = $token }; if ($c -and @($c.nearby).Count -gt 0) { $c } } 20
    Check "visit_chat" ($null -ne $heard -and (State).visited_grid -eq $VisitGrid) "said a line on the visited grid; seen in nearby chat"
    $objs = Wait-LiveUntil { $s = State; if ([int]$s.objects -ge 3) { $s } } 45
    Check "visit_objects" ($null -ne $objs) (Short $(if ($objs) { $objs } else { State }))
    Start-Sleep -Seconds 8   # textures settle for the picture
    Shot "04-visit-objects"
    [void](Invoke-LiveApi "open_window" @{ name = "inventory" })
    $inv = Wait-LiveUntil { $x = Invoke-LiveApi "inventory" @{ action = "status" }; if ($x -and $x.items -gt 0 -and $x.root_load -eq "Loaded") { $x } } 60
    Check "visit_inventory" ($null -ne $inv) ("inventory while visiting: items $($inv.items), folders $($inv.folders), root $($inv.root_load)")
    Shot "05-visit-inventory"
    $lm0 = Invoke-LiveApi "hypergrid" @{ action = "landmarks" }
    $click = Invoke-LiveClick "landmark"
    $lm1 = Wait-LiveUntil { $x = Invoke-LiveApi "hypergrid" @{ action = "landmarks" }; if ($x -and [int]$x.count -gt [int]$lm0.count) { $x } } 45
    $made = if ($lm1) { @($lm1.landmarks)[0] } else { $null }
    Check "visit_landmark_made" ($null -ne $made -and $made.grid -eq $VisitGrid) ("star clicked: $($click.clicked); landmarks $($lm0.count) -> $($lm1.count); newest names grid: $($made.grid)")

    # ---- home (taken), then back by the landmark ----
    [void](GoHome "after_landmark" $HomeNeighbour)
    if ($made) {
        [void](Invoke-LiveApi "hypergrid" @{ action = "open_landmark"; text = [string]$made.id })
        $dlg = Wait-LiveUntil { WaitingWith $VisitGrid } 30
        Check "landmark_properties_show_grid" ($null -ne $dlg) "the landmark's window names the visited grid"
        Shot "06-landmark-properties"
        if ($dlg) {
            [void](Invoke-LiveApi "dialog" @{ action = "answer"; nth = $dlg.nth; button = 0 })
            $lv = At $VisitRegion $true 120
            Check "back_by_landmark" ($null -ne $lv) (Short $(if ($lv) { $lv } else { State }))
        }
        [void](GoHome "after_landmark_trip" $HomeNeighbour)
    }

    # ---- a hop link in chat, from the neighbour at home ----
    $link = "hop://$VisitGrid/$([uri]::EscapeDataString($VisitRegion))/130/130/23"
    [void](Invoke-LiveApi "chat" @{ action = "say"; text = "Hypergrid link check $link" })
    [void](Wait-LiveUntil { $r = Invoke-LiveApi "notecard" @{ action = "chat_link"; index = 0 }; if ($r -and -not $r.error) { $r } } 20)
    $ask = Wait-LiveUntil { WaitingWith $VisitGrid } 15
    Start-Sleep -Seconds 3
    $still = State
    Check "hop_link_asks_first" ($null -ne $ask -and $still.region -eq $HomeNeighbour -and $still.teleport_stage -eq "Idle") ("clicked; question shown: $($null -ne $ask); still at home: " + (Short $still))
    Shot "07-hop-link-question"
    if ($ask) {
        [void](Invoke-LiveApi "dialog" @{ action = "answer"; nth = $ask.nth; button = 0 })
        $hv = At $VisitRegion $true 120
        Check "hop_link_teleports_after_yes" ($null -ne $hv) (Short $(if ($hv) { $hv } else { State }))
        if ($hv) { [void](GoHome "final" $HomeNeighbour) }
    }
}
finally {
    $result = [ordered]@{ account = $Account; checks = $checks; home_outcomes = $homeOutcomes; passed = @($checks | Where-Object { $_.pass }).Count; failed = @($checks | Where-Object { -not $_.pass }).Count }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir "hypergrid-trip.json") -Encoding utf8
    Stop-LiveViewer
}
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0) { exit 1 }
exit 0
