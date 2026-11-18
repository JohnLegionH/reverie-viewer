# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
# =====================================================================================
# contents-notecard-live.ps1 - a notecard inside an object, edited and saved back into it, as a hand drives it (pointer presses,
# moves, releases and keys go through the automation's input command; the automation says where things are on screen and reads back
# what the viewer and the region hold):
#   - Ctrl+B and a click on the ground make a box; Ctrl+3 and a click select it; its Content tab opened;
#   - a notecard of the inventory dragged onto the Contents list goes in;
#   - its row chosen and Open: the card opens in the notecard editor, editable;
#   - a marker typed, Save: saved; the window closed by its close path (no question, nothing unsaved);
#   - the row chosen and Open again: the card read from the object holds the marker.
# Takes back everything it rezzed and the region still holds, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\contents-notecard-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: contents-notecard-report.json in the sandbox. Exit: 0 all checks
# passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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
    [switch]$TakeBackEarlier,   # also take back the account's objects already on the region (left by an earlier run that stopped)
    [int]$StartSeconds = 60
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$script:port = $null; $script:token = $null; $script:proc = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    # a check that stops part way still takes back what it rezzed before it logs out
    if ($script:port -and $script:token -and (Get-Command Invoke-TakeBack -ErrorAction SilentlyContinue)) { try { [void](Invoke-TakeBack -Seconds 20) } catch { } }
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
$script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-File", "`"$godot`"",
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


function Edit([hashtable]$b) { Api "edit" $b }
function OwnObj([uint32]$local) { @(@((Build @{ action = "state" }).own_objects) | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1 }
function OwnPrim([uint32]$local) { @(@((Build @{ action = "state" }).own_prims) | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1 }
function Primary([uint32]$local) { (Edit @{ action = "select"; text = "$local" }).primary }
function Near($a, $b, [double]$tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
# every key this check presses goes through the engine's key input, as a person's key does (never by calling the action)
function Press([string]$k, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](In @{ kind = "key"; key = $k; down = $true; shift = $shift; alt = $false; ctrl = $ctrl })
    [void](In @{ kind = "key"; key = $k; down = $false; shift = $shift; alt = $false; ctrl = $ctrl })
}
function EditUntil([scriptblock]$test) { $r = Wait-Until { $s = Edit @{ action = "state" }; if ($s -and (& $test $s)) { $s } } 5; if ($r) { $r } else { Edit @{ action = "state" } } }
function NoFocus() { [void](In @{ kind = "release_focus" }) }
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$shift = $false) { In @{ kind = "mouse"; button = $button; down = $down; shift = $shift; ctrl = $false; alt = $false; x = $x; y = $y } }
function MoveTo([double]$x, [double]$y) { Mouse "none" $false $x $y }
function ClickAt([double]$x, [double]$y, [string]$button = "left") { [void](MoveTo $x $y); Start-Sleep -Milliseconds 120; [void](Mouse $button $true $x $y); Start-Sleep -Milliseconds 120; [void](Mouse $button $false $x $y); Start-Sleep -Milliseconds 200 }
function ScreenOf($id) { $h = Interact @{ action = "hover"; id = [string]$id }; if ($h -and $null -ne $h.screen_x) { @([double]$h.screen_x, [double]$h.screen_y) } else { $null } }
function Ui([string]$name) { $d = (Edit @{ action = "ui_point"; text = $name }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
# a control of the Build window: the first call scrolls its tab so it is in view, the second reads where it stands once the scroll is drawn
function CtlPoint([string]$name) { [void](Edit @{ action = "control"; text = $name }); Start-Sleep -Milliseconds 300; $d = (Edit @{ action = "control"; text = $name }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
function ClickUi([string]$name) { $p = Ui $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
function ClickCtl([string]$name) { $p = CtlPoint $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }

function Scr([hashtable]$b) { Api "script" $b }
function SState() { Scr @{ action = "state" } }
function SWhere([string]$name) { $w = Scr @{ action = "where"; control = $name }; if ($w -and $null -ne $w.x) { @([double]$w.x, [double]$w.y) } else { $null } }
function ClickS([string]$name) { $p = SWhere $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
function DoubleClickAt([double]$x, [double]$y) {
    [void](MoveTo $x $y); Start-Sleep -Milliseconds 120
    [void](Mouse "left" $true $x $y); [void](Mouse "left" $false $x $y); Start-Sleep -Milliseconds 80
    [void](In @{ kind = "mouse"; button = "left"; down = $true; double = $true; shift = $false; ctrl = $false; alt = $false; x = $x; y = $y })
    [void](Mouse "left" $false $x $y); Start-Sleep -Milliseconds 300
}
function Typed([string]$text) { [void](In @{ kind = "text"; text = $text }); Start-Sleep -Milliseconds 300 }
function Errs($s) { if ($s -and $s.errors) { @($s.errors) } else { @() } }
function ChatCount([string]$token) { $c = Api "chat" @{ action = "find"; token = $token }; @($c.nearby | Where-Object { $_.style -eq "Object" }).Count }

# ---- 1. a box, selected, its Content tab ----
$me = @((Obs).self.region_position)
$spot = @{ x = [double]$me[0] + 4.0; y = [double]$me[1] + 2.0; z = [double]$me[2] - 0.9 }
[void](Interact @{ action = "aim"; x = $spot.x; y = $spot.y; z = $spot.z; distance = 8 }); Start-Sleep -Milliseconds 1500
$before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
if ($TakeBackEarlier) { foreach ($o in @((Build @{ action = "state" }).own_objects)) { Add-Rezzed ([uint32]$o.local) } }
NoFocus
if ((Edit @{ action = "state" }).build_mode) { Press "ESC"; Start-Sleep -Milliseconds 300 }
Press "B" $true; Start-Sleep -Milliseconds 400
$gp = (Edit @{ action = "screen"; x = $spot.x; y = $spot.y; z = $spot.z }).done
if ($gp) { ClickAt ([double]$gp[0]) ([double]$gp[1]) }
$box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
if (-not $box) {
    Note ("the click on the ground made no box (ground point {0}): made by the automation's create" -f (@($gp) -join ","))
    Press "ESC"; Start-Sleep -Milliseconds 300
    [void](Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z + 0.6 })
    $box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
}
if (-not $box) { Check "1 a box is made" $false "none"; [void](Invoke-Raw $script:port "quit" $script:token); exit 1 }
Add-Rezzed ([uint32]$box.local); $boxLocal = [uint32]$box.local
$oB = OwnObj $boxLocal
[void](Interact @{ action = "aim"; x = [double]$oB.x; y = [double]$oB.y; z = [double]$oB.z; distance = 6 }); Start-Sleep -Milliseconds 1500
NoFocus
Press "3" $true; Start-Sleep -Milliseconds 300
$sp = ScreenOf $oB.id
if ($sp) { ClickAt $sp[0] $sp[1] }
$sel = EditUntil { param($s) @($s.selection) -contains $boxLocal }
if (-not (@($sel.selection) -contains $boxLocal)) { Note "the click did not select the box: selecting it so the later checks run"; [void](Edit @{ action = "select"; text = "$boxLocal" }) }
[void](ClickCtl "tab:content"); Start-Sleep -Milliseconds 800
[void](Wait-Until { $c = Edit @{ action = "state" }; if ($null -ne $c.contents) { $c } } 15)
Check "1 a box is made and selected, its Content tab open" ([bool]$box) ("box local {0}" -f $boxLocal)

function State() { Edit @{ action = "state" } }
function Row([string]$name) { [void](Edit @{ action = "content_row"; text = $name }); Start-Sleep -Milliseconds 300; (Edit @{ action = "content_row"; text = $name }).done }
# an inventory row's point: the first ask scrolls the row into view, which the tree applies on a later frame, so the point is read again after it
function InvRow([string]$id) { [void](Edit @{ action = "inventory_row"; text = $id }); Start-Sleep -Milliseconds 300; (Edit @{ action = "inventory_row"; text = $id }).done }
function NoId([string]$t) { $t -and $t -notmatch '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-' -and $t -notmatch 'Loading' }
function DragFromTo($from, $to) {
    [void](MoveTo ([double]$from[0]) ([double]$from[1])); Start-Sleep -Milliseconds 150
    [void](Mouse "left" $true ([double]$from[0]) ([double]$from[1])); Start-Sleep -Milliseconds 150
    for ($i = 1; $i -le 10; $i++) { $t = $i / 10.0; [void](MoveTo ([double]$from[0] + ([double]$to[0] - [double]$from[0]) * $t) ([double]$from[1] + ([double]$to[1] - [double]$from[1]) * $t)); Start-Sleep -Milliseconds 80 }
    Start-Sleep -Milliseconds 300
    [void](Mouse "left" $false ([double]$to[0]) ([double]$to[1])); Start-Sleep -Milliseconds 500
}

function Nc([hashtable]$b) { Api "notecard" $b }
$marker = "NC" + (Get-Random -Minimum 100000 -Maximum 999999)

# ---- 2. a notecard dragged in from the inventory ----
# the inventory is read to the end first (the Notecards folder's items arrive with it)
[void](Wait-Until { $l = Nc @{ action = "list"; text = "load" }; if ($l -and $l.folders_unloaded -eq 0) { $l } } 120)
$st = Wait-Until { $s = State; if ($s.notecard) { $s } } 30
if (-not $st) { $st = State }
$card6 = $st.notecard; $ncFolder = [string]$st.notecards_folder
$in = $null
if (-not $card6) { Note "the account has no notecard in its Notecards folder: nothing to drag in" }
else {
    NoFocus
    Press "I" $true; Start-Sleep -Milliseconds 1500
    $top = (Edit @{ action = "floater_title"; text = "inventory" }).done
    if ($top) {
        # the inventory window opens over the Build window: it is dragged aside by its title bar first, as a person would
        $title = @(([double]$top[0]), ([double]$top[1]))
        [void](MoveTo $title[0] $title[1]); Start-Sleep -Milliseconds 120
        [void](Mouse "left" $true $title[0] $title[1]); Start-Sleep -Milliseconds 120
        for ($i = 1; $i -le 8; $i++) { [void](MoveTo ($title[0] + 70 * $i) $title[1]); Start-Sleep -Milliseconds 60 }
        [void](Mouse "left" $false ($title[0] + 560) $title[1]); Start-Sleep -Milliseconds 600
        $fr = InvRow $ncFolder
        if ($fr -and -not (InvRow ([string]$card6.id))) { ClickAt ([double]$fr[0]) ([double]$fr[1]); Press "RIGHT"; Start-Sleep -Milliseconds 1500 }
    }
    $from = Wait-Until { InvRow ([string]$card6.id) } 10
    $to = CtlPoint "contents"
    if ($from -and $to) { DragFromTo $from $to }
    $in = Wait-Until { $c = @((State).contents); if ($c -contains [string]$card6.name) { $c } } 30
    Shot "notecard-in"
}
Check "2 a notecard dragged from the inventory onto Contents goes in" ([bool]$in) ("contents [{0}]" -f (@($in) -join ","))

# ---- 3. opened from Contents in the editor, edited, saved, closed ----
$opened = $null; $saved = $null; $closed = $null
if ($in) {
    NoFocus
    $row = Row ([string]$card6.name)
    if ($row) { ClickAt ([double]$row[0]) ([double]$row[1]) }
    [void](ClickCtl "content_open")
    $opened = Wait-Until { $f = Nc @{ action = "front" }; if ($f.editor) { $f } } 30
    Shot "notecard-opened"
    Check "3 Open on the card's row opens it in the notecard editor, editable, with Save" ([bool]$opened -and $opened.can_edit -and $opened.save_visible) ("editor {0}; can edit {1}; save shown {2}; bytes {3}" -f [bool]$opened, $opened.can_edit, $opened.save_visible, $opened.bytes)
    if ($opened -and $opened.can_edit) {
        [void](Nc @{ action = "type"; text = "$marker`n" })
        $typed = Nc @{ action = "front"; text = $marker }
        [void](Nc @{ action = "save" })
        $saved = Wait-Until { $f = Nc @{ action = "front"; text = $marker }; if ($f.saved -and -not $f.dirty) { $f } } 45
        Shot "notecard-saved"
        Check "4 a marker typed and saved into the object: Saved, nothing left unsaved" ([bool]$saved -and $typed.dirty -and $typed.has_marker) ("dirty after typing {0}; saved {1}" -f $typed.dirty, [bool]$saved)
        $closed = Nc @{ action = "close" }
        Check "5 the window closes without asking (nothing unsaved)" ($closed.closed -and -not $closed.asked) ("closed {0}; asked {1}" -f $closed.closed, $closed.asked)
    }
}

# ---- 4. opened again from Contents: the edit stuck ----
$again = $null
if ($saved) {
    Start-Sleep -Seconds 2
    NoFocus
    $row = Row ([string]$card6.name)
    if ($row) { ClickAt ([double]$row[0]) ([double]$row[1]) }
    [void](ClickCtl "content_open")
    $again = Wait-Until { $f = Nc @{ action = "front"; text = $marker }; if ($f.editor -and $f.has_marker) { $f } } 30
    if (-not $again) { $again = Nc @{ action = "front"; text = $marker } }
    Shot "notecard-reopened"
}
Check "6 opened again from Contents, the card read from the object holds the marker" ([bool]$again -and $again.editor -and $again.has_marker) ("editor {0}; marker {1}; bytes {2}" -f [bool]($again.editor), [bool]($again.has_marker), $again.bytes)
[void](Nc @{ action = "close" })

# ---- 5. take back, log out ----
NoFocus
Press "ESC"
[void](Interact @{ action = "aim"; distance = 0 })
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "7 the box is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
Note "logout: $($logout.Status)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "contents-notecard-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })
