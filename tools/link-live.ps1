# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# link-live.ps1 - location and app links through the automation API, on the development export.
#   Headless part (always): before any login the viewer opens fixtures\notecards\links.notecard (one line per link kind) and
#   the script checks the links the card shows and their labels; the same text rendered as every chat place shows it
#   (nearby, object chat, an object's message, the own line); and what clicks do without a session: a hop:// link to another
#   grid and a maps.secondlife.com place say so, the inventory link opens the inventory, a second throttled link inside the
#   throttle period is refused, a profile link shows the note that profiles are not built, a link that is not built is plain.
#   Live part (-Live): logs in as -Account; a notecard written into the sandbox holds a secondlife:// link to a place in the
#   start region, and clicking it opens the map there with Teleport ready; the same link said in local chat is clicked in
#   nearby chat, the map shows the place, the map's Teleport is pressed and the avatar arrives there; then it logs out.
#   Nothing is made or changed in-world (one line of local chat is said).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\link-live.ps1 -Sandbox <new folder> -OutDir <existing folder>
#     [-Live] [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <uri: start location>] [-Exe <development export>]
#
# Run it under a build claim (headless only), or a live-login claim and an avatar claim on the account (-Live). Output:
# link-check.json in -OutDir. Exit: 0 pass, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [switch]$Live,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = "viewer1",
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 90
)
if ($Live) {
    if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
    if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
    if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
}
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
$fixture = Join-Path $repo "fixtures\notecards\links.notecard"
$script:proc = $null

function Invoke-Raw([int]$port, [string]$command, [string]$token, [string]$body = "{}") {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $port)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($body)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: 127.0.0.1:$port`r`nAuthorization: Bearer $token`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $r = New-Object System.IO.StreamReader($s, [System.Text.Encoding]::UTF8)
        $all = $r.ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function Parse([string]$json) { try { return $json | ConvertFrom-Json } catch { return $null } }
function Call($c, [string]$command, $body = $null) {
    $text = if ($null -eq $body) { "{}" } else { $body | ConvertTo-Json -Compress }
    return Parse (Invoke-Raw $c.port $command $c.token $text).Body
}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
function Note([string]$what) { Write-Output ("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) {
    $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail })
    Write-Output ("  {0}  {1}  {2}" -f $(if ($pass) { "PASS" } else { "FAIL" }), $name, $detail)
}

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) { $problems.Add("-OutDir must be an existing folder") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
$loginUri = $null
$region = $null; $sx = 128.0; $sy = 128.0; $sz = 30.0
if ($Live) {
    if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
    if (-not $Start -or $Start -notmatch '^uri:([^&]+)&(\d+(?:\.\d+)?)&(\d+(?:\.\d+)?)&(\d+(?:\.\d+)?)') { $problems.Add("-Start (or NV_LIVE_START) must be a uri: start location with the region and x, y, z") }
    else { $region = $Matches[1]; $sx = [double]$Matches[2]; $sy = [double]$Matches[3]; $sz = [double]$Matches[4] }
    if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
    if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
    if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
    $viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
    if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null

function Start-Viewer([string]$dir) {
    $appdata = Join-Path $dir "appdata"; $local = Join-Path $dir "localappdata"; $log = Join-Path $dir "run.log"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $argLine = "--rendering-driver vulkan -- --automation"
    $script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$dir`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = $null
    for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
    if (-not $tf) { return $null }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    return @{ port = [int]$j.port; token = [string]$j.token }
}
function Until($c, [string]$command, $body, [scriptblock]$ok, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $r = $null
    while ((Get-Date) -lt $deadline) {
        $r = Call $c $command $body
        if ($r -and (& $ok $r)) { return $r }
        Start-Sleep -Milliseconds 500
    }
    return $r
}
# a notecard file in the official format holding one text
function Write-Card([string]$path, [string]$text) {
    $b = [System.Text.Encoding]::UTF8.GetBytes($text)
    $head = [System.Text.Encoding]::ASCII.GetBytes("Linden text version 2`n{`nLLEmbeddedItems version 1`n{`ncount 0`n}`nText length $($b.Length)`n")
    $tail = [System.Text.Encoding]::ASCII.GetBytes("}`n")
    [System.IO.File]::WriteAllBytes($path, [byte[]]($head + $b + $tail))
}

$exit = 0
$V = $null
$result = [ordered]@{}
try {
    $V = Start-Viewer (Join-Path $Sandbox "viewer")
    if (-not $V) { Note "no automation token file"; $exit = 5; throw "the viewer did not start" }
    Note "viewer up"

    # ---------------- headless: the card's links and labels ----------------
    [void](Until $V "notecard" @{ action = "fixture"; text = $fixture } { param($r) $r.open -ge 1 -and (@($r.windows) | Select-Object -Last 1).loaded } 30)
    $l = Call $V "notecard" @{ action = "links" }
    $labels = @($l.links | ForEach-Object { [string]$_.label })
    $result.card_links = $l
    $expectLabels = @("Sandbox One (10, 20, 30)", "Example (1, 2, 3)", "Example (7, 8, 9)", "Far Place (1, 2, 3)", "Example (5, 6, 7)", "Example (5, 6, 7)")
    Check "card.link_count" ([int]$l.count -eq 10) "links $($l.count) (9 location and app links and 1 web address; the link that is not built is plain text)"
    Check "card.location_labels" ((($labels | Select-Object -First 6) -join "|") -eq ($expectLabels -join "|")) (($labels | Select-Object -First 6) -join " | ")
    Check "card.app_links_as_written_before_names_are_known" ($labels[6] -like "secondlife:///app/agent/*/about" -and $labels[7] -like "secondlife:///app/group/*/about" -and $labels[8] -eq "secondlife:///app/inventory/show") ($labels[6..8] -join " | ")
    Check "card.web_link" ($labels[9] -eq "https://example.org/help") $labels[9]
    Check "card.not_built_is_plain" (-not ($labels -match "sharewithavatar")) "not among the links"

    # ---------------- headless: the same text in every chat place ----------------
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($fixture))
    $text = $text.Substring($text.IndexOf("Places and people."))
    $text = $text.Substring(0, $text.LastIndexOf("}"))
    $rend = Call $V "notecard" @{ action = "render"; text = $text }
    $result.render = $rend
    foreach ($place in @($rend.chat)) {
        $pl = @($place.links.links | ForEach-Object { [string]$_.label })
        Check ("chat." + $place.place) ([int]$place.links.count -eq 10 -and (($pl | Select-Object -First 6) -join "|") -eq ($expectLabels -join "|")) "links $($place.links.count)"
    }
    Check "render.notecard_same_as_chat" ([int]$rend.notecard.count -eq 10) "links $($rend.notecard.count)"

    # ---------------- headless: clicks ----------------
    $c3 = Call $V "notecard" @{ action = "link"; index = 3 }
    Check "click.hop_other_grid_note" ([string]$c3.note_key -eq "link.other_grid" -and $c3.note_shown -and -not $c3.map_open) "note $($c3.note_key), map open $($c3.map_open)"
    $c2 = Call $V "notecard" @{ action = "link"; index = 2 }
    Check "click.maps_place_off_second_life_note" ([string]$c2.note_key -eq "link.sl_place" -and $c2.note_shown) "note $($c2.note_key)"
    $c8 = Call $V "notecard" @{ action = "link"; index = 8 }
    Check "click.inventory_opens" ([string]$c8.kind -eq "OpenInventory") "kind $($c8.kind)"
    $c7 = Call $V "notecard" @{ action = "link"; index = 7 }
    Check "click.throttled_inside_the_period" ([string]$c7.note_key -eq "link.throttled") "note $($c7.note_key)"
    Start-Sleep -Milliseconds 5200
    $c6 = Call $V "notecard" @{ action = "link"; index = 6 }
    Check "click.profile_link_note" ([string]$c6.note_key -eq "link.profile_not_built" -and $c6.note_shown) "note $($c6.note_key)"
    $c0 = Call $V "notecard" @{ action = "link"; index = 0 }
    Check "click.location_before_login_asks_for_login" ([string]$c0.kind -eq "ShowOnMap" -and $c0.note_shown -and -not $c0.map_open) "kind $($c0.kind), map open $($c0.map_open)"

    # ---------------- live ----------------
    if ($Live) {
        $r = Invoke-Raw $V.port "session" $V.token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
        Note "login requested, $($r.Status)"
        $o = Until $V "observe" $null { param($x) $x.stage -eq "InWorld" -and $x.self } 180
        if (-not $o -or $o.stage -ne "InWorld") { $exit = 5; throw "the login did not reach the world" }
        Note "in world"
        $tx = [math]::Round([math]::Min([math]::Max($sx + 8, 4), 250)); $ty = [math]::Round([math]::Min([math]::Max($sy + 8, 4), 250)); $tz = [math]::Round($sz)
        $link = "secondlife://" + [uri]::EscapeDataString($region) + "/$tx/$ty/$tz"
        # a card in the sandbox with the link: clicking it opens the map at the place, with Teleport ready once the region is known
        # the card names a place 4 m from the chat line's, so each check sees the map move to its own link's place
        $cx = $tx - 4; $cy = $ty - 4
        $cardLink = "secondlife://" + [uri]::EscapeDataString($region) + "/$cx/$cy/$tz"
        $card = Join-Path $Sandbox "place.notecard"
        Write-Card $card ("The place: " + $cardLink + "`n")
        [void](Until $V "notecard" @{ action = "fixture"; text = $card } { param($x) (@($x.windows) | Select-Object -Last 1).loaded } 30)
        $k = Call $V "notecard" @{ action = "link"; index = 0; text = $region }
        Check "live.card_click_opens_map_there" ($k.map_open -and $k.map_region_matches -and [string]$k.kind -eq "ShowOnMap") "map open $($k.map_open), region matches $($k.map_region_matches)"
        $m = Until $V "notecard" @{ action = "link"; index = 0; text = $region } { param($x) $x.teleport_ready } 30
        Check "live.card_teleport_ready" ($m.teleport_ready -and $m.map_region_matches -and [math]::Abs([double]$m.map_local[0] - $cx) -lt 0.5 -and [math]::Abs([double]$m.map_local[1] - $cy) -lt 0.5) ("ready {0}, place {1}" -f $m.teleport_ready, (@($m.map_local) -join ","))
        # the same link said in local chat, clicked in nearby chat
        [void](Call $V "chat" @{ action = "say"; text = ("Link check " + $link) })
        $n = Until $V "notecard" @{ action = "chat_link"; index = 0; text = $region } { param($x) -not $x.error -and $x.teleport_ready -and [math]::Abs([double]$x.map_local[0] - $tx) -lt 0.5 } 30
        Check "live.chat_click_opens_map_there" ($n -and -not $n.error -and $n.map_open -and $n.map_region_matches -and $n.teleport_ready -and [math]::Abs([double]$n.map_local[0] - $tx) -lt 0.5 -and [math]::Abs([double]$n.map_local[1] - $ty) -lt 0.5) ("map open {0}, region matches {1}, ready {2}{3}" -f $n.map_open, $n.map_region_matches, $n.teleport_ready, $(if ($n.error) { ", " + $n.error } else { "" }))
        $before = [int](Call $V "teleport" @{ action = "status" }).arrivals
        $p0 = (Call $V "observe").self.region_position
        $tp = Call $V "map" @{ action = "teleport" }
        $arr = Until $V "teleport" @{ action = "status" } { param($x) [int]$x.arrivals -gt $before } 60
        Start-Sleep -Seconds 2
        $p1 = (Call $V "observe").self.region_position
        $dx = [double]$p1[0] - $tx; $dy = [double]$p1[1] - $ty
        $result.live = [ordered]@{ target = @($tx, $ty, $tz); before = $p0; after = $p1; arrivals_before = $before; arrivals_after = $arr.arrivals }
        Check "live.teleport_arrives" ([int]$arr.arrivals -gt $before -and [math]::Sqrt($dx * $dx + $dy * $dy) -lt 3.0) ("arrivals {0} -> {1}, distance to the place {2:N2} m" -f $before, $arr.arrivals, [math]::Sqrt($dx * $dx + $dy * $dy))
        [void](Invoke-Raw $V.port "session" $V.token '{"action":"logout"}')
        Start-Sleep -Seconds 3
        Note "logged out"
    }
} catch {
    Note "stopped: $($_.Exception.Message)"
    if ($exit -eq 0) { $exit = 1 }
} finally {
    if ($V) { try { [void](Invoke-Raw $V.port "quit" $V.token) } catch { } }
    if ($script:proc) { try { $script:proc.WaitForExit(60000) | Out-Null } catch { } }
}
$result.checks = $checks
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir "link-check.json") -Encoding utf8
if ($exit -eq 0 -and @($checks | Where-Object { -not $_.pass }).Count -gt 0) { $exit = 1 }
Write-Output ("link check: {0} checks, {1} failed, exit {2}" -f $checks.Count, @($checks | Where-Object { -not $_.pass }).Count, $exit)
exit $exit
