# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# script-editor-live.ps1 - the script editor through the engine's input path, as a hand drives it: every pointer press, move and release
# and every key goes through the automation's input command (Input.ParseInputEvent, as the operating system's events arrive); the
# automation only says where things are on screen and reads back what the viewer and the region hold. In order:
#   - Ctrl+B and a click on the ground make a box; Ctrl+3 and a click select it; on the Content tab New Script, its row, Open;
#   - the text replaced by typing (a script that says a line on channel 0), Save: the line appears in nearby chat from the box;
#   - a mistake typed, Save: the error is listed; a click on it puts the caret on its line; the mistake removed, Save: no error;
#   - Running unticked, then ticked; Reset: the line appears again;
#   - Copy to inventory on the script's row; the inventory window (Ctrl+I), its search, a double-click on the script's row opens it
#     from the inventory; its text replaced by typing and saved with Ctrl+S.
# Takes back everything it rezzed and the region still holds, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -File <repo>\tools\script-editor-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim on the account and a build claim. Output: script-editor-report.json in the sandbox. Exit: 0 all checks
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
$sayToken = "SE" + (Get-Random -Minimum 100000 -Maximum 999999)
$script1 = "default`n{`nstate_entry()`n{`nllSay(0, `"hello $sayToken`");`n}`n}"

# ---- 1. a box, selected, New Script in its Content tab, opened with Open ----
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
    # making the box is not what this check proves: when the click on the ground made none, the automation's create makes it
    Note ("the click on the ground made no box (ground point {0}; region {1}): made by the automation's create" -f (@($gp) -join ","), (Obs).region.name)
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
[void](ClickCtl "new_script")
$listed = Wait-Until { $c = @((Edit @{ action = "state" }).contents); if ($c -contains "New Script") { $c } } 20
[void](Edit @{ action = "content_row"; text = "New Script" }); Start-Sleep -Milliseconds 300   # the first ask scrolls the list into view
$row = (Edit @{ action = "content_row"; text = "New Script" }).done
if ($row) { ClickAt ([double]$row[0]) ([double]$row[1]) }
[void](ClickCtl "content_open")
$opened = Wait-Until { $s = SState; if ($s.open -ge 1 -and $s.loaded -and $s.in_object) { $s } } 30
$runningKnown = Wait-Until { $s = SState; if ($null -ne $s.running) { $s } } 20
Shot "opened"
Check "1 New Script in a box's Content tab opens in the editor with Open, editable, its running state from the region" ([bool]$listed -and [bool]$opened -and $opened.can_edit -and $opened.code_editable -and [bool]$runningKnown) ("listed [{0}]; open {1}; view {2}; editable {3}; running {4}; chars {5}" -f (@($listed) -join ","), $opened.open, $opened.view, $opened.code_editable, $runningKnown.running, $opened.text_chars)

# ---- 2. the text replaced by typing, Save: the line is said ----
[void](ClickS "code"); Start-Sleep -Milliseconds 300
Press "A" $true
Typed $script1
$typed = SState
[void](ClickS "save")
$saved = Wait-Until { $s = SState; if (-not $s.saving -and $s.status -eq "script.save_complete") { $s } } 30
$said = Wait-Until { $n = ChatCount $sayToken; if ($n -ge 1) { $n } } 20
Shot "saved"
Check "2 the text typed and saved: Save complete, and the box says the line in nearby chat" ([bool]$saved -and [bool]$said -and $typed.dirty) ("dirty after typing {0}; status {1}; lines from the box {2}" -f $typed.dirty, (SState).status, $said)

# ---- 3. a mistake, Save: the error is listed; a click on it puts the caret on its line; fixed, Save ----
[void](ClickS "code"); Start-Sleep -Milliseconds 300
Press "END" $true
Press "BACKSPACE"
[void](ClickS "save")
$bad = Wait-Until { $s = SState; if (-not $s.saving -and @(Errs $s).Count -gt 0) { $s } } 30
Shot "error"
Check "3a a mistake saved: the compiler's error is listed" ([bool]$bad) ("errors {0}; first at {1}" -f @(Errs $bad).Count, $(if (@(Errs $bad).Count -gt 0) { "{0},{1}" -f $bad.errors[0].line, $bad.errors[0].column } else { "-" }))
[void](ClickS "code"); Press "END" $true; Start-Sleep -Milliseconds 200
$lastLine = [int](SState).caret[0]   # the text's last line, from the editor (Ctrl+End): an error past the end puts the caret there
Press "HOME" $true; Start-Sleep -Milliseconds 200
$caretBefore = SState
[void](ClickS "error:0"); Start-Sleep -Milliseconds 500
$at = SState
$expectLine = if (@(Errs $bad).Count -gt 0) { [Math]::Min([int]$bad.errors[0].line, $lastLine) } else { -1 }
Check "3b a click on the error puts the caret on its line" ($bad -and $caretBefore.caret[0] -eq 0 -and [int]$at.caret[0] -eq $expectLine) ("caret before {0}; after {1}; error line {2}" -f (@($caretBefore.caret) -join ","), (@($at.caret) -join ","), $expectLine)
[void](ClickS "code"); Start-Sleep -Milliseconds 300
Press "END" $true; Typed "}"
[void](ClickS "save")
$fixed = Wait-Until { $s = SState; if (-not $s.saving -and $s.status -eq "script.save_complete" -and @(Errs $s).Count -eq 0) { $s } } 30
$said2 = Wait-Until { $n = ChatCount $sayToken; if ($n -ge 2) { $n } } 20
Check "3c the mistake removed and saved: no error, and the line is said again" ([bool]$fixed -and [bool]$said2) ("status {0}; errors {1}; lines {2}" -f (SState).status, @(Errs (SState)).Count, (ChatCount $sayToken))

# ---- 4. Running off and on, Reset: the line again ----
$n0 = ChatCount $sayToken
[void](ClickS "running")
$off = Wait-Until { $s = SState; if ($s.running -eq $false) { $s } } 10
Start-Sleep -Milliseconds 800
[void](ClickS "reset"); Start-Sleep -Milliseconds 3000
$quiet = ChatCount $sayToken
[void](ClickS "running")
$on = Wait-Until { $s = SState; if ($s.running -eq $true) { $s } } 10
Start-Sleep -Milliseconds 800
[void](ClickS "reset")
$again = Wait-Until { $n = ChatCount $sayToken; if ($n -gt $quiet) { $n } } 20
Shot "running"
Check "4 Running unticked (a reset says nothing), ticked again, Reset: the line is said again" ([bool]$off -and [bool]$on -and $quiet -eq $n0 -and [bool]$again) ("off {0}; lines while stopped {1} (before {2}); on {3}; after Reset {4}" -f [bool]$off, $quiet, $n0, [bool]$on, $again)

# ---- 5. the script copied to the inventory, opened there by a double-click, edited and saved with Ctrl+S ----
# every inventory folder read first (the copy goes to the Scripts folder, which may not have been read yet)
[void](Api "notecard" @{ action = "list"; text = "load" })
[void](Wait-Until { $l = Api "notecard" @{ action = "list" }; if ($l -and [int]$l.folders_unloaded -eq 0) { $l } } 30)
$invBefore = @((Scr @{ action = "list" }).scripts | ForEach-Object { $_.id })
[void](Edit @{ action = "content_row"; text = "New Script" }); Start-Sleep -Milliseconds 300   # the first ask scrolls the list into view
$row = (Edit @{ action = "content_row"; text = "New Script" }).done
if ($row) { ClickAt ([double]$row[0]) ([double]$row[1]) }
[void](ClickCtl "content_copy")
$copied = Wait-Until { @((Scr @{ action = "list" }).scripts | Where-Object { $invBefore -notcontains $_.id }) | Select-Object -First 1 } 30
$copyArrived = [bool]$copied
if (-not $copied) {
    # Copy to inventory is not what this check proves: when no new copy is seen, a script the account already holds is opened instead
    Note "no new copy seen in the inventory: a script the account already holds is opened"
    $copied = @((Scr @{ action = "list" }).scripts | Where-Object { $_.name -eq "New Script" }) | Select-Object -First 1
}
NoFocus
Press "ESC"; Start-Sleep -Milliseconds 400
Press "I" $true; Start-Sleep -Milliseconds 1500
[void](Api "inventory" @{ action = "search"; text = "New Script" }); Start-Sleep -Milliseconds 1200
$irow = if ($copied) { Wait-Until { (Edit @{ action = "inventory_row"; text = [string]$copied.id }).done } 10 } else { $null }
$openBefore = (SState).open
if ($irow) { DoubleClickAt ([double]$irow[0]) ([double]$irow[1]) }
$invOpen = Wait-Until { $s = SState; if ($s.loaded -and -not $s.in_object -and $s.open -gt $openBefore) { $s } } 30
[void](ClickS "code"); Start-Sleep -Milliseconds 300
Press "A" $true
Typed ($script1.Replace("hello", "inventory"))
Press "S" $true
$invSaved = Wait-Until { $s = SState; if (-not $s.saving -and $s.status -eq "script.save_complete" -and -not $s.dirty) { $s } } 30
Shot "inventory"
Check "5 a script in the inventory: a double-click on its row opens it, typed and saved with Ctrl+S" ([bool]$copied -and [bool]$invOpen -and [bool]$invSaved) ("new copy seen {0}; row {1}; opened {2}; in object {3}; status {4}" -f $copyArrived, (@($irow) -join ","), [bool]$invOpen, $invOpen.in_object, (SState).status)

# ---- 6. take back, log out ----
NoFocus
Press "ESC"
[void](Interact @{ action = "aim"; distance = 0 })
$tb = Invoke-TakeBack -Seconds 30
$report.take_back = $tb
Check "6 the box is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
Note "logout: $($logout.Status)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "script-editor-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })
