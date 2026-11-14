# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# ui-checks.ps1 - checks of the login screen, the shortcuts, the menus, Preferences > Sound, the upload notice, the texture preview,
# the animation overrider's set editor and remember password, driven through the automation interface the way a person uses them,
# on the development export. Each check prints PASS or FAIL with its figures.
# Offline (the login screen is enough):
#   enter_submits        Enter in the password field starts a login (a name with no password stops at the missing password)
#   remember_offered     the remember password tick is offered (Windows: DPAPI), off by default
#   ctrl_p               control+P opens Preferences
#   menu_click_close     a top menu opens on a click; a click on its title closes it and it stays closed while the pointer moves
#                        over the bar; it opens again on a new click; Esc closes it and moving opens nothing
#   sound_page_fits      every setting of Preferences > Sound, Mono included, is visible without scrolling at the default window size
#   suitcase_refresh     a made-up suitcase folder opened and closed with its arrow while its contents arrive again and again: it stays
#                        open, then closed, and the tree is not drawn again for the repeated answers
#   window_reopens       a window moved and resized, closed and opened again, comes back where it was, at its size
#   window_after_restart that window, left open at quit, opens where it was after the viewer is started again
#   sort_after_restart   the inventory sorted by date stays sorted by date after the viewer is started again
#   ao_editor_fits       the overrider's set editor at its default size: no control cut short, past the window's edge, or overlapping
#   size_cleanup_once    a remembered place that still has the old default size gives way to the new first-open size; a place the
#                        person changed stays (the run's settings are seeded with one of each before the first start)
#   first_open_sizes     every window opened for the first time is at least its first-open size (ours or the official, the larger)
#                        and opens without needing to scroll; the table of old, new and official sizes is printed
#   folders_click_only   a made-up inventory driven with real mouse and key events: the pointer over five folders opens none; a
#                        click on a name selects only; a click on the arrow opens and closes; a double-click toggles; a right-click
#                        on My Suitcase shows its menu and toggles nothing; Right and Left; a dragged item held over a closed folder
#                        opens it after the official delay, not before, and the drop still moves it; Esc closes the window
# -Live (a test account; -LoginUri the grid's login address):
#   remember_login       the login screen, password typed and remember ticked, logs in; the saved file holds no readable hash
#   ctrl_i               control+I opens the inventory
#   ao_new_set           New set asks for a name with the keyboard in the field; Esc closes the prompt; the name typed and Enter makes
#                        the set, which is then listed and picked in the editor (-InventoryWrites only)
#   ao_prompt_keys       the real Enter key in New set's empty name field asks for a name; the real Esc key closes the prompt (-InventoryWrites only)
#   folder_toggle_live   My Suitcase (or the Trash when the account has none) opened and closed with its arrow while it is read again
#                        from the grid: it stays open, then closed; the own Current Outfit folder is the one under My Inventory
#   sort_after_relog     the inventory sorted by date in the first start is sorted by date after the second start's login
#   upload_notice        one generated image uploaded: the notice names it, Show in inventory opens the inventory on it (-InventoryWrites only)
#   texture_preview      that texture opened as Enter does: the preview draws it with its size (-InventoryWrites only)
#   folders_click_only_live  the same pointer and key steps on the real inventory (reading only: the drag is cancelled, nothing moved)
#   main_windows_live    Inventory, Preferences, the overrider's editor, Nearby Chat and the other main windows opened once each and
#                        captured (screenshots in the sandbox); each opens without needing to scroll
#   saved_login          the viewer started again: the login screen with an empty password field logs in with the saved hash
#   saved_entry_aside    the run's saved-login file is moved aside (move-aside), so no test login stays remembered
# Exit: 0 all passed; 1 a check failed; 3 blocked (an input is missing).
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\ui-checks.ps1 -Sandbox <new folder> [-Live -LoginUri <uri>
#     -Account <alias> [-Start <uri:region&x&y&z>] [-InventoryWrites] [-Accounts <accounts file>]]
# Needs the development export (tools\s1.ps1 makes it) and a granted lane claim (claim-guard.ps1); a live run also needs the
# avatar claim for its account, taken by the caller.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Exe = "",
    [switch]$Live,
    [string]$LoginUri = "",
    [string]$Account = "",
    [string]$Start = "",
    [switch]$InventoryWrites,
    [string]$Accounts = $env:NV_TEST_ACCOUNTS,
    [string]$MoveAside = $env:NV_MOVE_ASIDE,   # the machine's move-aside script: the run's saved-login file is moved with it, never deleted
    [int]$StartSeconds = 120
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "BLOCKED: the development export is missing: $Exe"; exit 3 }
if ($Live) {
    if (-not $Accounts) { $Accounts = & (Join-Path $PSScriptRoot "live-env.ps1") NV_TEST_ACCOUNTS }
    if (-not $LoginUri -or -not $Account -or -not $Accounts) { Write-Output "BLOCKED: -Live needs -LoginUri, -Account and an accounts file (NV_TEST_ACCOUNTS)"; exit 3 }
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$textLog = Join-Path $Sandbox "ui-checks.txt"
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

# one viewer, its settings and data in this run's own folders (kept between the two live starts, so the saved login carries over)
$appdata = Join-Path $Sandbox "appdata"
$local = Join-Path $Sandbox "localappdata"
function Start-Viewer([string]$label) {
    New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
    $log = Join-Path $Sandbox "viewer-$label.log"
    Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue | ForEach-Object { Rename-Item -LiteralPath $_.FullName -NewName ($_.Name + ".old-$label") }
    $args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"", "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"",
        "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0", "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"-- --automation`"")
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
function Key($v, [string]$key, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](Invoke-Api $v "input" @{ kind = "key"; key = $key; down = $true; ctrl = $ctrl; shift = $shift })
    Start-Sleep -Milliseconds 120
    [void](Invoke-Api $v "input" @{ kind = "key"; key = $key; down = $false; ctrl = $ctrl; shift = $shift })
    Start-Sleep -Milliseconds 400
}
function MouseTo($v, [double]$x, [double]$y) { [void](Invoke-Api $v "input" @{ kind = "mouse"; x = $x; y = $y }); Start-Sleep -Milliseconds 250 }
function ClickAt($v, [double]$x, [double]$y) {
    MouseTo $v $x $y
    [void](Invoke-Api $v "input" @{ kind = "mouse"; button = "left"; down = $true; x = $x; y = $y }); Start-Sleep -Milliseconds 90
    [void](Invoke-Api $v "input" @{ kind = "mouse"; button = "left"; down = $false; x = $x; y = $y }); Start-Sleep -Milliseconds 450
}
function Menus($v) { Api $v "menus" }
function InWorld($v, [int]$seconds) { Wait-Until { $s = Api $v "session" '{"action":"state"}'; if ($s -and "$($s.stage)" -eq "InWorld") { $s } } $seconds }
function SavedFileHasNoHash {
    $f = Join-Path $appdata "ReverieViewer\remembered-logins.dat"
    if (-not (Test-Path -LiteralPath $f)) { return @{ Exists = $false; Clean = $true } }
    $text = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($f))
    return @{ Exists = $true; Clean = -not ([regex]'\$1\$[0-9a-fA-F]{32}').IsMatch($text) }
}

# ---------------- offline ----------------
# one remembered place at the old default size (never resized) and one the person changed, as an earlier version would have saved them
$seed = Join-Path $appdata "NativeViewer\settings.json"
$seeded = $false
if (-not (Test-Path -LiteralPath $seed)) {
    New-Item -ItemType Directory -Force -Path (Join-Path $appdata "NativeViewer") | Out-Null
    Set-Content -LiteralPath $seed -Value '{"floater_inventory_rect":"40,40,420,520","floater_about_rect":"100,100,520,470"}' -Encoding utf8
    $seeded = $true
}
$v = Start-Viewer "offline"
if (-not $v) { Record "start" $false "no automation token file within $StartSeconds s"; exit 1 }
try {
    # the one-time clean-up of remembered places at the old default size (once the shell is up and lists its windows)
    $list = Wait-Until { $w = Api $v "open_window" '{}'; if (@($w.windows).Count -gt 1) { $w } } 60
    [void](Api $v "inventory_view" '{"action":"state"}')   # the inventory window exists before a login once asked for
    $inv = @((Api $v "windows" '{"action":"open","name":"inventory"}').open | Where-Object { $_.id -eq "inventory" })
    $ppl = @((Api $v "windows" '{"action":"open","name":"about"}').open | Where-Object { $_.id -eq "about" })
    Start-Sleep -Milliseconds 800
    $inv = @((Api $v "windows" '{"action":"state"}').open | Where-Object { $_.id -eq "inventory" })
    $ppl = @((Api $v "windows" '{"action":"state"}').open | Where-Object { $_.id -eq "about" })
    $invOk = $inv.Count -eq 1 -and $inv[0].rect[2] -ge 420 -and $inv[0].rect[3] -ge 570
    $pplOk = $ppl.Count -eq 1 -and $ppl[0].rect[0] -eq 100 -and $ppl[0].rect[1] -eq 100 -and $ppl[0].rect[2] -ge 520 -and $ppl[0].rect[3] -ge 470
    Record "size_cleanup_once" ($seeded -and $invOk -and $pplOk) "seeded $seeded; inventory remembered at the old default 420x520 opened at [$(@($inv | ForEach-Object { $_.rect -join ',' }))]; about remembered at 100,100,520,470 (changed by the person) opened at [$(@($ppl | ForEach-Object { $_.rect -join ',' }))]"
    [void](Api $v "windows" '{"action":"close","name":"inventory"}')
    [void](Api $v "windows" '{"action":"close","name":"about"}')

    # every window's first opening: at least its first-open size, nothing needing to scroll
    $ids = @((Api $v "open_window" '{}').windows | Where-Object { $_ })
    $bounds = (Api $v "windows" '{"action":"state"}').bounds
    $capW = [Math]::Round($bounds[2] * 0.85); $capH = [Math]::Round($bounds[3] * 0.85)   # a window takes at most 85 % of the view; past that its content scrolls
    $bad = @(); $rows = @()
    foreach ($id in $ids) {
        if ($id -in @("inventory", "about")) { continue }
        $o = Api $v "windows" (@{ action = "open"; name = $id })
        if (-not $o.done) { $rows += "  $id`: did not open offline"; continue }
        Start-Sleep -Milliseconds 700
        $f = Api $v "windows" (@{ action = "fit"; name = $id })
        $small = $f.first_open -and ($f.rect[2] + 0.5 -lt [Math]::Min($f.first_open[0], $f.needed[0]) -or $f.rect[3] + 0.5 -lt [Math]::Min($f.first_open[1], $f.needed[1]))
        $row = "  $id`: old [$($f.spec_default -join 'x')] new [$($f.rect[2])x$($f.rect[3])] official [$(if ($f.official) { $f.official -join 'x' } else { 'none' })] first-open [$($f.first_open -join 'x')] content needs [$($f.needed -join 'x')] scrolls $($f.scrolls) problems $(@($f.problems).Count)"
        $rows += $row
        $capped = ($f.rect[2] -ge $capW - 1) -or ($f.rect[3] -ge $capH - 1)
        if (($f.scrolls -and -not $capped) -or $small) { $bad += $id }
        [void](Api $v "windows" (@{ action = "close"; name = $id }))
    }
    Record "first_open_sizes" ($ids.Count -gt 0 -and $bad.Count -eq 0) "$($ids.Count) windows, view [$($bounds -join ',')], 85 % cap ${capW}x${capH}; too small, or needing to scroll below the cap: [$($bad -join ', ')]"
    foreach ($r in $rows) { Say $r }

    # inventory folders open on a click only (a made-up inventory; real mouse and key events)
    $pc = Api $v "inventory_view" '{"action":"pointer"}'
    Record "folders_click_only" ($pc.opened_by_hover -eq 0 -and $pc.folders_hovered -ge 5 -and $pc.name_click_selects_without_toggle -and $pc.arrow_click_opens -and $pc.arrow_click_closes -and $pc.double_click_toggles -and
        $pc.right_click_menu -and $pc.right_click_selects -and $pc.right_click_toggled -eq $false -and $pc.key_right_opens -and $pc.key_left_closes -and $pc.key_left_to_parent -and
        $pc.drag_open_before_delay -eq $false -and $pc.drag_open_after_delay -and $pc.drops_moved -eq 1 -and -not $pc.drag_still_running -and $pc.esc_closed_window) `
        ("hovered $($pc.folders_hovered) folders, opened by hover $($pc.opened_by_hover); name click selects only $($pc.name_click_selects_without_toggle); arrow opens $($pc.arrow_click_opens) closes $($pc.arrow_click_closes) (x tried [$(@($pc.arrow_x_tried) -join ',')]); " +
         "double-click toggles $($pc.double_click_toggles); right-click on My Suitcase: menu $($pc.right_click_menu), selected $($pc.right_click_selects), toggled $($pc.right_click_toggled); keys Right opens $($pc.key_right_opens), Left closes $($pc.key_left_closes), Left to parent $($pc.key_left_to_parent); " +
         "drag started $($pc.drag_started) (button held $($pc.held_after_press); seen $($pc.drag_seen); target $($pc.target_row) tree $($pc.tree_rect)), held: open at 0.45 s $($pc.drag_open_before_delay), at 1 s $($pc.drag_open_after_delay), drops moved $($pc.drops_moved), drag left running $($pc.drag_still_running); Esc closed the window $($pc.esc_closed_window) $($pc.refused)")
    # Enter in the password field starts a login: with a name and no password it stops at the missing password
    $before = Api $v "login_screen" '{"action":"state"}'
    [void](Api $v "login_screen" @{ action = "submit"; name = "Test User"; login_uri = "https://grid.example.org:8002/" })
    $after = Wait-Until { $s = Api $v "login_screen" '{"action":"state"}'; if ($s.failure) { $s } } 10
    Record "enter_submits" ($null -eq $before.failure -and $after.failure -eq "invalid") "failure before [$($before.failure)], after Enter [$($after.failure)]"
    Record "remember_offered" ($after.remember.offered -and -not $after.remember.ticked -and -not $after.remember.note) "offered=$($after.remember.offered) ticked=$($after.remember.ticked) note=$($after.remember.note)"

    # control+P opens Preferences (the menu entry's official shortcut)
    $m0 = Menus $v
    Key $v "P" $true
    $m1 = Wait-Until { $m = Menus $v; if (@($m.open_windows) -contains "preferences") { $m } } 5
    Record "ctrl_p" ((@($m0.open_windows) -notcontains "preferences") -and $null -ne $m1) "preferences open before: $(@($m0.open_windows) -contains 'preferences'), after control+P: $($null -ne $m1)"

    # Preferences > Sound at the default size: every setting visible without scrolling
    [void](Api $v "prefs_layout" '{"page":"sound"}'); Start-Sleep -Milliseconds 1200
    $layout = Api $v "prefs_layout" '{"page":"sound"}'
    $hidden = @($layout.controls | Where-Object { -not $_.visible } | ForEach-Object { $_.key })
    $mono = @($layout.controls | Where-Object { $_.key -eq "audio_mono" })
    Record "sound_page_fits" ($layout -and $hidden.Count -eq 0 -and $mono.Count -eq 1 -and $mono[0].visible) ("window $($layout.window.w)x$($layout.window.h), page visible height $($layout.visible_height), " +
        "$(@($layout.controls).Count) settings, last bottom $(@($layout.controls)[-1].bottom), mono top $($mono[0].top); not visible: [$($hidden -join ', ')]")
    Key $v "ESC"
    [void](Api $v "open_window" '{"name":"preferences"}')
    ClickAt $v 900 900   # back to the login screen

    # the top menu bar: find a title by clicking along the bar
    $at = $null; $opened = $null
    foreach ($x in 1..60 | ForEach-Object { $_ * 6 }) {
        ClickAt $v $x 18
        $m = Menus $v
        if ($m.top) { $at = $x; $opened = $m.top; break }
    }
    if (-not $at) { Record "menu_click_close" $false "no top menu opened along the bar" } else {
        ClickAt $v $at 18
        $closed = Menus $v
        foreach ($x in 1..60 | ForEach-Object { $_ * 6 }) { MouseTo $v $x 18 }
        MouseTo $v $at 18; Start-Sleep -Milliseconds 600
        $hovered = Menus $v
        ClickAt $v $at 18
        $again = Menus $v
        Key $v "ESC"
        $esc = Menus $v
        MouseTo $v ($at + 40) 18; MouseTo $v $at 18; Start-Sleep -Milliseconds 600
        $afterEsc = Menus $v
        $ok = $closed.top -eq $null -and @($closed.top_popups_visible).Count -eq 0 -and $hovered.top -eq $null -and @($hovered.top_popups_visible).Count -eq 0 -and
            $again.top -eq $opened -and $esc.top -eq $null -and @($afterEsc.top_popups_visible).Count -eq 0
        Record "menu_click_close" $ok ("'$opened' opened by a click at x=$at; a click on its title: open [$($closed.top)] on screen [$(@($closed.top_popups_visible) -join ',')]; " +
            "hovering the bar after: open [$($hovered.top)] on screen [$(@($hovered.top_popups_visible) -join ',')]; a new click: [$($again.top)]; Esc: [$($esc.top)]; hovering after Esc: on screen [$(@($afterEsc.top_popups_visible) -join ',')]")
    }

    # a suitcase folder opened and closed while it is read again and again (a made-up inventory, before any login)
    $sc = Api $v "inventory_view" '{"action":"suitcase"}'
    Record "suitcase_refresh" ($sc.opened -and $sc.closed -and $sc.stayed_open -and $sc.stayed_closed -and $sc.draws_while_open -eq 0 -and $sc.draws_while_closed -eq 0 -and $sc.close_draws -ge 1) `
        "opened $($sc.opened), closed $($sc.closed); $($sc.refresh_rounds) answers while open: stayed open $($sc.stayed_open), tree drawn $($sc.draws_while_open) times; closing drew $($sc.close_draws); $($sc.refresh_rounds) answers while closed: stayed closed $($sc.stayed_closed), drawn $($sc.draws_while_closed) times"

    # a window's place and size: closed and opened again, then left open for the restart below
    [void](Api $v "windows" '{"action":"open","name":"preferences"}')
    [void](Api $v "windows" '{"action":"move","name":"preferences","x":300,"y":140}')
    [void](Api $v "windows" '{"action":"resize","name":"preferences","w":760,"h":580}')
    $placed = @((Api $v "windows" '{"action":"state"}').open | Where-Object { $_.id -eq "preferences" })
    [void](Api $v "windows" '{"action":"close","name":"preferences"}')
    $reopened = @((Api $v "windows" '{"action":"open","name":"preferences"}').open | Where-Object { $_.id -eq "preferences" })
    $wantRect = "300,140,760,580"
    Record "window_reopens" ($placed.Count -eq 1 -and ($placed[0].rect -join ",") -eq $wantRect -and $reopened.Count -eq 1 -and ($reopened[0].rect -join ",") -eq $wantRect) `
        "placed [$(@($placed | ForEach-Object { $_.rect -join ',' }))], opened again at [$(@($reopened | ForEach-Object { $_.rect -join ',' }))]"
    [void](Api $v "windows" '{"action":"move","name":"preferences","x":320,"y":160}')

    # the overrider's set editor at its default size (a new run's settings: no remembered place)
    $ed = Api $v "windows" '{"action":"open","name":"addon.ao.editor"}'
    Start-Sleep -Milliseconds 1200
    $fit = Api $v "windows" '{"action":"fit","name":"addon.ao.editor"}'
    Record "ao_editor_fits" ($ed.done -and $fit -and @($fit.problems).Count -eq 0) "editor open $($ed.done) at [$($fit.rect -join ',')]; problems: [$(@($fit.problems) -join '; ')]"
    [void](Api $v "windows" '{"action":"close","name":"addon.ao.editor"}')

    # the inventory sorted by date, kept for the restart below
    $sorted = Api $v "inventory_view" '{"action":"sort","sort":"date"}'
} finally { Stop-Viewer $v }

$v = Start-Viewer "offline-2"
if (-not $v) { Record "start again" $false "no automation token file within $StartSeconds s"; exit 1 }
try {
    $again = @((Api $v "windows" '{"action":"open","name":"preferences"}').open | Where-Object { $_.id -eq "preferences" })
    Record "window_after_restart" ($again.Count -eq 1 -and ($again[0].rect -join ",") -eq "320,160,760,580") "left open at quit at [320,160,760,580]; after the restart it opened at [$(@($again | ForEach-Object { $_.rect -join ',' }))]"
    $view = Api $v "inventory_view" '{"action":"state"}'
    Record "sort_after_restart" ($sorted.sort -eq "ByDate" -and $view.sort -eq "ByDate" -and ($view.stored -band 1) -eq 1) "sorted before quit: $($sorted.sort); after the restart the window shows $($view.sort) (stored InventorySortOrder $($view.stored))"
    [void](Api $v "inventory_view" '{"action":"sort","sort":"name"}')   # the run's own settings: back to the default
} finally { Stop-Viewer $v }

# ---------------- live ----------------
if ($Live) {
    $v = Start-Viewer "live-1"
    if (-not $v) { Record "start live" $false "no automation token file"; exit 1 }
    try {
        $body = @{ action = "submit"; accounts_file = $Accounts; account = $Account; login_uri = $LoginUri; remember = $true }
        if ($Start) { $body.start = $Start }
        $sub = Api $v "login_screen" $body
        $s = InWorld $v 120
        $saved = SavedFileHasNoHash
        Record "remember_login" ($null -ne $s -and $saved.Exists -and $saved.Clean) "in world: $($null -ne $s); saved-login file in the settings folder: $($saved.Exists); readable hash in it: $(-not $saved.Clean); submit answered busy=$($sub.busy)"
        if ($s) {
            Start-Sleep -Seconds 4
            $m0 = Menus $v
            Key $v "I" $true
            $m1 = Wait-Until { $m = Menus $v; if (@($m.open_windows) -contains "inventory") { $m } } 6
            Record "ctrl_i" ((@($m0.open_windows) -notcontains "inventory") -and $null -ne $m1) "inventory open before: $(@($m0.open_windows) -contains 'inventory'), after control+I: $($null -ne $m1)"

            # My Suitcase (or the Trash) opened and closed with its arrow while the grid's answer for it comes again (reading only)
            $tg = Api $v "inventory_view" '{"action":"toggle_live"}'
            Record "folder_toggle_live" ($tg.opened -and $tg.closed -and $tg.stayed_open -and $tg.stayed_closed -and $tg.own_outfit_folder_top_level) `
                "folder: $($tg.folder); opened $($tg.opened), stayed open over 3 reads $($tg.stayed_open) (tree drawn $($tg.draws_while_open) times); closed $($tg.closed), stayed closed over 3 reads $($tg.stayed_closed) (drawn $($tg.draws_while_closed)); own Current Outfit is the top-level one: $($tg.own_outfit_folder_top_level) $($tg.refused)"
            $liveSorted = Api $v "inventory_view" '{"action":"sort","sort":"date"}'

            # the same pointer and key steps on the real inventory (reading only)
            $pl = Api $v "inventory_view" '{"action":"pointer_live"}'
            Record "folders_click_only_live" ($pl.opened_by_hover -eq 0 -and $pl.folders_hovered -ge 1 -and $pl.name_click_selects_without_toggle -and $pl.arrow_click_opens -and $pl.arrow_click_closes -and
                ($pl.right_click_menu -or -not $pl.suitcase) -and $pl.right_click_toggled -ne $true -and $pl.key_right_opens -and $pl.key_left_closes -and
                ($null -eq $pl.drag_open_after_delay -or ($pl.drag_open_before_delay -eq $false -and $pl.drag_open_after_delay)) -and -not $pl.drag_still_running) `
                ("hovered $($pl.folders_hovered) folders, opened by hover $($pl.opened_by_hover); name click selects only $($pl.name_click_selects_without_toggle); arrow opens $($pl.arrow_click_opens) closes $($pl.arrow_click_closes) (a folder holding $($pl.arrow_folder_contents)); double-click toggles $($pl.double_click_toggles); " +
                 "My Suitcase present $($pl.suitcase): right-click menu $($pl.right_click_menu), selected $($pl.right_click_selects), toggled $($pl.right_click_toggled); keys Right $($pl.key_right_opens) Left $($pl.key_left_closes) parent $($pl.key_left_to_parent); " +
                 "a folder dragged (started $($pl.drag_started)) and held over a closed one: open at 0.45 s $($pl.drag_open_before_delay), at 1 s $($pl.drag_open_after_delay) (cancelled, nothing moved: moves $($pl.drops_moved)); Esc closed $($pl.esc_closed_window) $($pl.refused)")

            # the main windows opened once each and captured
            $shots = Join-Path $Sandbox "windows"
            New-Item -ItemType Directory -Force -Path $shots | Out-Null
            $mainBad = @(); $mainRows = @()
            foreach ($id in @("inventory", "preferences", "addon.ao.editor", "nearby_chat", "conversations", "people", "world_map", "appearance", "outfits", "about_land", "personal_lighting")) {
                $o = Api $v "windows" (@{ action = "open"; name = $id })
                if (-not $o.done) { $mainRows += "  $id`: did not open"; continue }
                Start-Sleep -Milliseconds 1500
                $f = Api $v "windows" (@{ action = "fit"; name = $id })
                [void](Api $v "screenshot" (@{ dir = $shots; name = ("window-" + ($id -replace '[^A-Za-z0-9_-]', '-')) }))
                $mainRows += "  $id`: at [$($f.rect -join ',')] content needs [$($f.needed -join 'x')] scrolls $($f.scrolls) first opening $($f.first_opening) problems $(@($f.problems).Count)"
                $bl = (Api $v "windows" '{"action":"state"}').bounds
                if ($f.first_opening -and $f.scrolls -and $f.rect[3] -lt [Math]::Round($bl[3] * 0.85) - 1 -and $f.rect[2] -lt [Math]::Round($bl[2] * 0.85) - 1) { $mainBad += $id }
                [void](Api $v "windows" (@{ action = "close"; name = $id }))
            }
            Record "main_windows_live" ($mainBad.Count -eq 0 -and $mainRows.Count -gt 0) "opened for the first time and needing to scroll below the 85 % cap: [$($mainBad -join ', ')] (a window this run sized itself keeps that size); screenshots in $shots"
            foreach ($r in $mainRows) { Say $r }

            if ($InventoryWrites) {
                # the AO set editor: New set asks for a name with the keyboard in the field; Esc closes it; Enter makes the set, which is then listed and picked
                $name = "Check set " + (Get-Date -Format "HHmmss")
                [void](Api $v "windows" '{"action":"open","name":"addon.ao.editor"}')   # a person presses New set in the open editor
                Start-Sleep -Milliseconds 800
                $ask = Api $v "overrider" '{"action":"newset_ask"}'
                # the real keys through the engine's input path: the keyboard must be in the prompt's field, Enter must reach it
                # (with the field empty it asks for a name), and Esc must close it
                Start-Sleep -Milliseconds 600
                Key $v "ENTER"
                $enterKey = Api $v "overrider" '{"action":"state"}'
                Key $v "ESC"
                $escKey = Api $v "overrider" '{"action":"state"}'
                Record "ao_prompt_keys" ($enterKey.asking_name -and "$($enterKey.editor_status)" -match "Type a name first" -and -not $escKey.asking_name) `
                    "after the Enter key: prompt open $($enterKey.asking_name), answered for an empty name: $("$($enterKey.editor_status)" -match 'Type a name first'); after the Esc key: prompt open $($escKey.asking_name)"
                $ask = Api $v "overrider" '{"action":"newset_ask"}'
                $esc = Api $v "overrider" '{"action":"prompt_cancel"}'
                $before = Api $v "overrider" '{"action":"state"}'
                [void](Api $v "overrider" (@{ action = "newset"; text = $name }))
                $made = Wait-Until { $o = Api $v "overrider" '{"action":"state"}'; $new = @(@($o.set_ids) | Where-Object { @($before.set_ids) -notcontains $_ }); if ($new.Count -eq 1 -and $o.edited_id -eq $new[0] -and $o.edited_set -ge 0) { $o } } 30
                Record "ao_new_set" ($ask.asking_name -and $ask.name_focus_requests -ge 1 -and -not $esc.asking_name -and $null -ne $made -and -not $made.asking_name) `
                    "asked for a name: $($ask.asking_name) (keyboard asked for the field: $($ask.name_focus_requests)); Esc closed it: $(-not $esc.asking_name); Enter made a set listed and picked in the editor: $($null -ne $made) (sets listed $($made.edit_sets))"

                # one generated image uploaded; the notice; Show in inventory
                Add-Type -AssemblyName System.Drawing
                $png = Join-Path $Sandbox "check-image.png"
                $bmp = New-Object System.Drawing.Bitmap 64, 64
                for ($y = 0; $y -lt 64; $y++) { for ($x = 0; $x -lt 64; $x++) { $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $x * 4, $y * 4, 128)) } }
                $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
                $b0 = Api $v "build" '{"action":"state"}'
                [void](Api $v "build" (@{ action = "upload_image"; text = $png }))
                [void](Wait-Until { $b = Api $v "build" '{"action":"state"}'; $t = @($b.image); if ($t.Count -ge 2 -and $t[-2] -notmatch "asking") { $b } } 30)   # the price is shown
                [void](Api $v "build" (@{ action = "press_upload"; text = "image" }))
                $notice = Wait-Until { $n = Api $v "upload_notice" '{"action":"state"}'; if ($n.posted) { $n } } 60
                $shown = $null; $newItems = @()
                if ($notice) {
                    $b1 = Wait-Until { $b = Api $v "build" '{"action":"state"}'; if (@(@($b.texture_items) | Where-Object { @($b0.texture_items) -notcontains $_ }).Count -ge 1) { $b } } 30
                    $newItems = @(@($b1.texture_items) | Where-Object { @($b0.texture_items) -notcontains $_ })
                    [void](Api $v "upload_notice" '{"action":"show"}')
                    # the row is selected once its folder is drawn: wait for that item, not for any selection
                    $shown = Wait-Until { $n = Api $v "upload_notice" '{"action":"state"}'; if ($newItems.Count -eq 1 -and $n.selected -eq $newItems[0]) { $n } } 20
                    if (-not $shown) { $shown = Api $v "upload_notice" '{"action":"state"}' }
                }
                Record "upload_notice" ($notice -and $notice.body -eq "upload.notice.one.textures" -and $notice.buttons -eq 1 -and $shown.inventory_open -and $newItems.Count -eq 1 -and $shown.selected -eq $newItems[0]) `
                    "notice: $($notice.body), buttons $($notice.buttons); after Show: inventory open $($shown.inventory_open), selected is the new texture: $($newItems.Count -eq 1 -and $shown.selected -eq $newItems[0])"

                # that texture opened as Enter does: the preview draws it with its size
                [void](Api $v "texture_preview" '{"action":"open_selected"}')
                $tp = Wait-Until { $t = Api $v "texture_preview" '{"action":"state"}'; if ($t.drawn -or $t.failed) { $t } } 30
                Record "texture_preview" ($tp.open -and $tp.drawn -and $tp.width -eq 64 -and $tp.height -eq 64 -and $tp.item -eq $newItems[0]) "open $($tp.open), drawn $($tp.drawn), size $($tp.width)x$($tp.height), the uploaded item: $($tp.item -eq $newItems[0])"
            }
            [void](Api $v "session" '{"action":"logout"}')
        }
    } finally { Stop-Viewer $v }

    # the viewer started again: an empty password field logs in with the saved hash
    $v = Start-Viewer "live-2"
    if (-not $v) { Record "start live again" $false "no automation token file"; exit 1 }
    try {
        $state = Api $v "login_screen" '{"action":"state"}'
        $body = @{ action = "submit"; accounts_file = $Accounts; account = $Account; login_uri = $LoginUri; remember = $true; use_saved = $true }
        if ($Start) { $body.start = $Start }
        [void](Api $v "login_screen" $body)
        $s = InWorld $v 120
        $after = Api $v "login_screen" '{"action":"state"}'
        Record "saved_login" ($null -ne $s) "in world with the password field empty: $($null -ne $s); remember ticked at start: $($state.remember.ticked); failure [$($after.failure)]"
        if ($s) {
            $kept = Api $v "inventory_view" '{"action":"state"}'
            Record "sort_after_relog" ($liveSorted.sort -eq "ByDate" -and $kept.sort -eq "ByDate") "sorted in the first start: $($liveSorted.sort); after the second start's login: $($kept.sort)"
            [void](Api $v "inventory_view" '{"action":"sort","sort":"name"}')
        }
        if ($s) { [void](Api $v "session" '{"action":"logout"}') }
    } finally { Stop-Viewer $v }

    # no test login stays remembered: the saved-login file is moved aside
    $f = Join-Path $appdata "ReverieViewer\remembered-logins.dat"
    if (Test-Path -LiteralPath $f) {
        if ($MoveAside -and (Test-Path -LiteralPath $MoveAside)) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File ($MoveAside -replace '\\', '/') -Label "saved test login of a ui-checks run, viewer" ($f -replace '\\', '/') | Out-Null
        } else { Say "saved_entry_aside: no move-aside script (-MoveAside or NV_MOVE_ASIDE): the file stays in the run's own settings folder" }
    }
    Record "saved_entry_aside" (-not (Test-Path -LiteralPath $f)) "saved-login file still in the run's settings folder: $(Test-Path -LiteralPath $f)"
}

Say ("ui-checks summary: {0} failed" -f $failed)
exit $(if ($failed -gt 0) { 1 } else { 0 })
