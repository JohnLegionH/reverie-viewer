# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
# =====================================================================================
# topbar-live.ps1: the top bar and the dock on a real grid with a test account, as the UI spec's checks describe them:
#   title       the window title reads the viewer's name with the avatar's and the grid's, no time of day
#   app_menu    the far left shows the accent mark and "Reverie"; a click opens About, Preferences, Add-ons, Check for updates, Log out, Quit
#   hover       moving across the titles with nothing open opens no menu
#   world_icons a click on World opens it, every entry with an icon
#   location    the region's name once, then the coordinates
#   typed       a place typed in the location field (this region, another spot) and Enter: the avatar moves there
#   status      the grid's name, the time and "Connected" at the right, taking no clicks and no focus
#   dock        every button with its name under it; the AO button toggles "AO · On" and "AO · Off"; its menu ends with "Edit sets..."
# Writes only into -Sandbox.
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\topbar-live.ps1 -Sandbox <new folder> -LoginUri <uri> -Account <alias>
#     [-Start <uri:region&x&y&z>] [-Accounts <accounts file>]
# Needs the development export (tools\s1.ps1 makes it), a granted lane claim (claim-guard.ps1) and the avatar claim for the account,
# taken by the caller. Exit: 0 passed; 1 a step failed; 3 blocked (an input is missing).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Exe = "",
    [string]$LoginUri = "",
    [string]$Account = "",
    [string]$Start = "",
    [string]$Accounts = $env:NV_TEST_ACCOUNTS,
    [int]$StartSeconds = 120
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "BLOCKED: the development export is missing: $Exe"; exit 3 }
if (-not $Accounts) { $Accounts = & (Join-Path $PSScriptRoot "live-env.ps1") NV_TEST_ACCOUNTS }
if (-not $LoginUri -or -not $Account -or -not $Accounts) { Write-Output "BLOCKED: needs -LoginUri, -Account and an accounts file (NV_TEST_ACCOUNTS)"; exit 3 }
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$textLog = Join-Path $Sandbox "topbar-live.txt"
$failed = 0
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
function Record([string]$name, [bool]$pass, [string]$detail) {
    if (-not $pass) { $script:failed++ }
    Say ("{0}: {1}  {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail)
}
function Invoke-Api($v, [string]$command, $body) {
    $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Compress -Depth 6 }
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
        return $all.Substring($i + 4)
    } finally { $tcp.Close() }
}
function Api($v, [string]$command, $body = '{}') { $t = Invoke-Api $v $command $body; if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null } }
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }
    return $null
}
$appdata = Join-Path $Sandbox "appdata"
$local = Join-Path $Sandbox "localappdata"
function Start-Viewer([string]$label) {
    New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
    $log = Join-Path $Sandbox "viewer-$label.log"
    Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue | ForEach-Object { Rename-Item -LiteralPath $_.FullName -NewName ($_.Name + ".old-$label") }
    $args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"", "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"",
        "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0", "-PlaceWindow", "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"-- --automation`"")
    $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $args2
    $v = @{ Label = $label; Proc = $p; Log = $log; Port = 0; Token = "" }
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
    if (-not $tf) { Stop-Viewer $v; return $null }
    Start-Sleep -Milliseconds 500
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $v.Port = [int]$j.port; $v.Token = [string]$j.token
    Start-Sleep -Seconds 2
    return $v
}
function Stop-Viewer($v) {
    try { if ($v.Port) { [void](Invoke-Api $v "quit" '{}') } } catch { }
    if (-not $v.Proc.WaitForExit(60000)) { Stop-Process -Id $v.Proc.Id -Force -ErrorAction SilentlyContinue }   # only the launcher this script started
    Start-Sleep -Milliseconds 800
}
function LogIn($v) {
    $body = @{ action = "submit"; accounts_file = $Accounts; account = $Account; login_uri = $LoginUri; remember = $false }
    if ($Start) { $body.start = $Start }
    [void](Api $v "login_screen" $body)
    $s = Wait-Until { $s = Api $v "session" '{"action":"state"}'; if ($s -and "$($s.stage)" -eq "InWorld") { $s } } 120
    if ($s) { [void](Api $v "windows" '{"action":"open","name":"inventory"}'); Start-Sleep -Seconds 2 }
    return $s
}
function Inv($v, [string]$action, [string]$item = "", [string]$folder = "") { Api $v "inventory_view" (@{ action = $action; item = $item; folder = $folder }) }


function Top($v, [string]$action = "state", [int]$index = 0, [string]$text = "") { Api $v "topbar" (@{ action = $action; index = $index; text = $text }) }

$v = Start-Viewer "topbar"
if (-not $v) { Record "start" $false "no automation token file"; exit 1 }
try {
    $before = Top $v
    Record "title_before_login" ("$($before.window_title)" -eq "Reverie Viewer") "the title before login: [$($before.window_title)]"
    if (-not (LogIn $v)) { Record "login" $false "not in world"; exit 1 }
    $s = Wait-Until { $t = Top $v; if ($t.location -and "$($t.status[1].text)" -eq "Connected") { $t } } 60
    if (-not $s) { $s = Top $v }
    $title = "$($s.window_title)"
    Record "title" ($title.StartsWith("Reverie Viewer - ") -and ($title -split ' - ').Count -ge 3 -and $title -notmatch "time of day") "title has the viewer's name, the avatar's and the grid's ($(($title -split ' - ').Count) parts), no time of day: $($title -notmatch 'time of day')"
    Record "app_menu_mark" ($s.titles[0] -eq "Reverie" -and $s.app_mark) "first title [$($s.titles[0])], accent mark $($s.app_mark)"
    $app = Top $v "click" 0
    $want = @("About Reverie Viewer", "Preferences...", "Add-ons...", "Check for updates", "Log out", "Quit")
    $got = @($app.items | ForEach-Object { $_.text })
    Record "app_menu" ((@($app.open).Count -eq 1) -and (($got -join "|") -eq ($want -join "|"))) "opened [$(@($app.open) -join ',')], entries [$($got -join ' | ')]"
    $hover = Top $v "hover_all"
    Record "hover_opens_nothing" (@($hover.opened).Count -eq 0 -and -not $hover.open) "menus open after moving across $(@($s.titles).Count) titles: [$(@($hover.opened) -join ',')]"
    $wi = [array]::IndexOf(@($s.titles), "World")
    $world = Top $v "click" $wi
    $noIcon = @($world.items | Where-Object { -not $_.icon })
    Record "world_icons" ($wi -ge 0 -and @($world.items).Count -gt 0 -and $noIcon.Count -eq 0) "World at title $wi opened [$(@($world.open) -join ',')], $(@($world.items).Count) entries, $($noIcon.Count) without an icon"
    $parts = "$($s.location)" -split ' \| '
    $region = if ($parts.Count -ge 2) { $parts[$parts.Count - 2] } else { "" }
    $coords = if ($parts.Count -ge 2) { $parts[$parts.Count - 1] } else { "" }
    $regionCount = @($parts | Where-Object { $_ -eq $region }).Count
    Record "location" ($region -and $regionCount -eq 1 -and $coords -match '^\d+, \d+, \d+$' -and $s.location_shown) "the region once ($regionCount), then coordinates [$coords]; shown $($s.location_shown)"
    $sx = [int](($coords -split ', ')[0]); $tx = if ($sx -lt 128) { 200 } else { 60 }
    [void](Top $v "type" 0 "$region/$tx/128/40")
    $moved = Wait-Until { $t = Top $v; $c = ("$($t.location)" -split ' \| ')[-1]; if ($c -match '^(\d+), ') { if ([Math]::Abs([int]$Matches[1] - $tx) -le 8) { $t } } } 60
    Record "typed_teleport" ($null -ne $moved) "typed [$region/$tx/128/40]: the location now reads [$((("$($moved.location)" -split ' \| ')[-1]))] (wanted x near $tx)"
    $st = Top $v
    $statusOk = "$($st.status[0].text)".Length -gt 0 -and "$($st.time)".Length -gt 0 -and "$($st.status[1].text)" -eq "Connected" -and -not $st.status_zone_takes_mouse -and -not (@($st.status) | Where-Object { $_.takes_mouse -or $_.focus })
    Record "status" $statusOk "grid name present $("$($st.status[0].text)".Length -gt 0), time [$($st.time)], connection [$($st.status[1].text)], the area takes clicks $($st.status_zone_takes_mouse)"
    $unnamed = @($st.dock | Where-Object { -not $_.name -or -not $_.icon })
    $ao = @($st.dock | Where-Object { $_.id -eq "ao" })
    Record "dock_names" (@($st.dock).Count -ge 11 -and $unnamed.Count -eq 0 -and $st.quick_controls -eq 0) "$(@($st.dock).Count) buttons, $($unnamed.Count) without a name or icon, in $($st.dock_rows) row(s); separate quick controls $($st.quick_controls)"
    if ($ao.Count -eq 1) {
        $first = "$($ao[0].name)"
        $c1 = Api $v "dock" (@{ id = "ao"; owner = $ao[0].owner })
        Start-Sleep -Milliseconds 800
        $after = @((Top $v).dock | Where-Object { $_.id -eq "ao" })[0].name
        $c2 = Api $v "dock" (@{ id = "ao"; owner = $ao[0].owner })
        Start-Sleep -Milliseconds 800
        $back = @((Top $v).dock | Where-Object { $_.id -eq "ao" })[0].name
        Record "ao_toggle" ($first -ne $after -and $back -eq $first -and ("$first $after" -match "AO · On") -and ("$first $after" -match "AO · Off")) "AO reads [$first], after a click [$after], after another [$back]"
        Record "ao_menu" (@($c2.menu)[-1] -eq "Edit sets...") "the AO menu: [$(@($c2.menu) -join ' | ')]"
    } else { Record "ao_toggle" $false "no AO button on the dock" }
    [void](Api $v "session" '{"action":"logout"}')
    Start-Sleep -Seconds 3
} finally { Stop-Viewer $v }

Say ("topbar-live: {0} failed" -f $failed)
if ($failed -gt 0) { exit 1 }
exit 0
