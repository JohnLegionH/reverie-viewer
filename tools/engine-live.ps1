# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# engine-live.ps1 - the script engine and compile target choice on a live region, in the script editor and the Contents list.
#   -Mode A (a region with the Lua engine): a Lua script in an object, SLua (Lua) chosen and saved, runs as Lua; opened again and saved
#     without touching the engine row it stays Lua. An LSL script with SLua (LSL) saved as lsl-luau; copied to the inventory, opened and
#     saved there; dropped into another object the region still runs it as LSL on Lua.
#   -Mode B (a region that keeps engine choices): the running reply names the engine (sent only to a viewer whose seed request lists
#     SetScriptEngine); "Choose engine myself", YEngine, saved and read back; unticked, the choice is cleared and read back. The box stays.
#   -Mode Other -BoxLocal <local id>: another account opens that box's Contents; the script's right-click Engine entry is greyed.
# Each mode also answers the account's friends list (ids only). The engine row and the drop-down are read through the automation's script
# command; the tick and the editor's buttons and keys go through the engine's input path, the drop-down's entry is chosen as the open list
# chooses it (its entries have no place on screen until it opens).
#
#   powershell -NoProfile -File <repo>\tools\engine-live.ps1 -Mode A|B|Other [-BoxLocal <id>] -Sandbox <new folder> -ShotsDir <existing folder>
#     -Credentials <accounts file> -Account <alias> -GridInfoUrl <get_grid_info URL> -Start <start location> [-Exe <development export>]
#     [-Token <text>]  the chat search token to use instead of a random one, so a second account can count what the first one's scripts say.
#       The automation's own token is kept in $script:apiToken: PowerShell variable names ignore case, so a variable named $script:token
#       would silently replace this parameter.
#
# Run it under an avatar claim on the account and a live-test claim on the region. Output: engine-live-report.json in the sandbox.
# Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("A", "B", "Other")][string]$Mode,
    [uint32]$BoxLocal = 0,
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ShotsDir,
    [Parameter(Mandatory)][string]$Credentials,
    [Parameter(Mandatory)][string]$Account,
    [Parameter(Mandatory)][string]$GridInfoUrl,
    [Parameter(Mandatory)][string]$Start,
    [string]$Exe = "",
    [int]$StartSeconds = 60,
    [string]$Token = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$script:port = $null; $script:apiToken = $null; $script:proc = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    # a check that stops part way still takes back what it rezzed before it logs out
    if ($script:port -and $script:apiToken -and (Get-Command Invoke-TakeBack -ErrorAction SilentlyContinue)) { try { [void](Invoke-TakeBack -Seconds 20) } catch { } }
    if ($script:port -and $script:apiToken) { try { [void](Invoke-Raw $script:port "session" $script:apiToken ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $script:port "quit" $script:apiToken) } catch { } }
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
function Api([string]$command, [hashtable]$b) { Parse (Invoke-Raw $script:port $command $script:apiToken ($b | ConvertTo-Json -Compress)).Body }
function Obs() { Parse (Invoke-Raw $script:port "observe" $script:apiToken).Body }
function In([hashtable]$b) { Api "input" $b }
function Key([string]$k, [bool]$down) { In @{ kind = "key"; key = $k; down = $down; shift = $false; alt = $false; ctrl = $false } }
function Interact([hashtable]$b) { Api "interact" $b }
function Build([hashtable]$b) { Api "build" $b }
function World([hashtable]$b) { Api "world_sounds" $b }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:apiToken (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
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
$script:port = [int]$j.port; $script:apiToken = [string]$j.token
$login = Invoke-Raw $script:port "session" $script:apiToken (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
Note "login requested: $($login.Status) $($login.Body)"
$inWorld = Wait-Until { $o = Obs; if ($o -and $o.stage -eq "InWorld" -and $o.terrain.complete -and $o.self) { $o } } 150
if (-not $inWorld) { Note "not in the world: stopping"; [void](Invoke-Raw $script:port "quit" $script:apiToken); exit 5 }
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


function Row([string]$name) { [void](Edit @{ action = "content_row"; text = $name }); Start-Sleep -Milliseconds 300; (Edit @{ action = "content_row"; text = $name }).done }
function InvRow([string]$id) { [void](Edit @{ action = "inventory_row"; text = $id }); Start-Sleep -Milliseconds 300; (Edit @{ action = "inventory_row"; text = $id }).done }
function DragFromTo($from, $to) {
    [void](MoveTo ([double]$from[0]) ([double]$from[1])); Start-Sleep -Milliseconds 150
    [void](Mouse "left" $true ([double]$from[0]) ([double]$from[1])); Start-Sleep -Milliseconds 150
    for ($i = 1; $i -le 10; $i++) { $t = $i / 10.0; [void](MoveTo ([double]$from[0] + ([double]$to[0] - [double]$from[0]) * $t) ([double]$from[1] + ([double]$to[1] - [double]$from[1]) * $t)); Start-Sleep -Milliseconds 80 }
    Start-Sleep -Milliseconds 300
    [void](Mouse "left" $false ([double]$to[0]) ([double]$to[1])); Start-Sleep -Milliseconds 500
}
# the index of the first drop-down entry, not greyed, whose text matches
function ComboIndex($s, [string]$pattern) { $items = @($s.combo_items); for ($i = 0; $i -lt $items.Count; $i++) { if ([string]$items[$i].text -match $pattern -and -not $items[$i].disabled) { return $i } }; return -1 }
function Items($s) { (@($s.combo_items) | ForEach-Object { "{0}{1}" -f $_.text, $(if ($_.disabled) { " (greyed)" } else { "" }) }) -join " | " }
function Rt($s) { if ($s.runtime) { "mono {0} luau {1} lua-language {2} engine '{3}' choice '{4}'" -f $s.runtime.Mono, $s.runtime.Luau, $s.runtime.LuauLanguage, $s.runtime.Engine, $s.runtime.EngineChoice } else { "no reply" } }
# every script editor closed through the window list, so the next Open asks the region again for the running state
function CloseEditors() {
    foreach ($w in @((Api "windows" @{ action = "state" }).open)) { if ([string]$w.id -match '^script\.\d+$') { [void](Api "windows" @{ action = "close"; name = [string]$w.id }) } }
    [void](Wait-Until { if ((SState).open -eq 0) { $true } } 10)
}
# a new box near the avatar, selected, its Content tab shown (the automation's create: making boxes is not what this check proves)
function NewBox([double]$dx) {
    $me = @((Obs).self.region_position)
    $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
    NoFocus
    if (-not (Edit @{ action = "state" }).build_mode) { Press "B" $true; Start-Sleep -Milliseconds 400 }
    [void](Build @{ action = "create"; x = [double]$me[0] + $dx; y = [double]$me[1] + 2.0; z = [double]$me[2] - 0.3 })
    $b = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
    if (-not $b) { return $null }
    Add-Rezzed ([uint32]$b.local)
    [void](Edit @{ action = "select"; text = "$([uint32]$b.local)" }); Start-Sleep -Milliseconds 500
    # Create lands where the click's ray meets something, which can be the water 20+ m away (past llSay's 20 m): put the box where it was meant to go
    [void](Edit @{ action = "position"; x = [double]$me[0] + $dx; y = [double]$me[1] + 2.0; z = [double]$me[2] }); Start-Sleep -Milliseconds 1200
    [void](ClickCtl "tab:content"); Start-Sleep -Milliseconds 800
    [void](Wait-Until { $c = Edit @{ action = "state" }; if ($null -ne $c.contents) { $c } } 15)
    return $b
}
function SelectBox($b) { [void](Edit @{ action = "select"; text = "$([uint32]$b.local)" }); Start-Sleep -Milliseconds 500; [void](ClickCtl "tab:content"); Start-Sleep -Milliseconds 800 }
# New Script in the selected box, opened with Open; answers the editor's state once the region has said how it runs
function NewScriptOpened() {
    [void](ClickCtl "new_script")
    [void](Wait-Until { $c = @((Edit @{ action = "state" }).contents); if ($c -contains "New Script") { $c } } 20)
    return (OpenRow "New Script")
}
function OpenRow([string]$name) {
    CloseEditors
    $row = Row $name
    if ($row) { ClickAt ([double]$row[0]) ([double]$row[1]) }
    [void](ClickCtl "content_open")
    [void](Wait-Until { $s = SState; if ($s.open -ge 1 -and $s.loaded -and $s.in_object) { $s } } 30)
    return (Wait-Until { $s = SState; if ($s.runtime) { $s } } 45)
}
function TypeAll([string]$text) { [void](ClickS "code"); Start-Sleep -Milliseconds 300; Press "A" $true; Typed $text }
function SaveNow() {
    Press "S" $true
    return (Wait-Until { $s = SState; if (-not $s.saving -and $s.status -eq "script.save_complete" -and -not $s.dirty) { $s } } 40)
}
function Said([string]$token, [int]$atLeast) { Wait-Until { if ((ChatCount $token) -ge $atLeast) { $true } } 30 }
$tok = if ($Token) { $Token } else { "EN" + (Get-Random -Minimum 100000 -Maximum 999999) }
$report.token = $tok

if ($Mode -eq "A") {
    # ---- A1. a Lua script in an object: the engine row, SLua (Lua) chosen, saved; it runs as Lua ----
    $box1 = NewBox 3.0
    $s = NewScriptOpened
    Note ("A1 opened: row {0}; entries [{1}]; {2}" -f $s.engine_row, (Items $s), (Rt $s))
    TypeAll ("ll.Say(0, `"lua $tok`")")
    [void](ClickS "engine_choose"); Start-Sleep -Milliseconds 400
    $i = ComboIndex (SState) '\(Lua'
    $p = if ($i -ge 0) { Scr @{ action = "pick"; text = "$i" } } else { $null }
    $before = SState
    $saved = SaveNow
    $ran = Said "lua $tok" 1
    Shot "a1-lua-saved"
    Check "A1 a Lua script in an object, SLua (Lua) chosen, is saved with target luau and runs" ([bool]$saved -and $before.target -eq "luau" -and [bool]$ran) ("ticked {0}; entry {1}; target sent {2}; saved {3}; said {4}" -f $before.choose_engine, $i, $before.target, [bool]$saved, [bool]$ran)
    # ---- A2. opened again: the region says Lua; edited and saved without touching the row: still luau, still Lua ----
    $s2 = OpenRow "New Script"
    TypeAll ("ll.Say(0, `"lua again $tok`")")
    $before2 = SState
    $saved2 = SaveNow
    $ran2 = Said "lua again $tok" 1
    $s3 = OpenRow "New Script"
    Shot "a2-lua-again"
    Check "A2 opened again and saved without touching the engine row, the Lua script stays Lua" ($s2.runtime.Luau -and $s2.runtime.LuauLanguage -and $before2.target -eq "luau" -and [bool]$saved2 -and [bool]$ran2 -and $s3.runtime.Luau -and $s3.runtime.LuauLanguage) ("after first save: {0}; target sent {1}; said {2}; after second save: {3}" -f (Rt $s2), $before2.target, [bool]$ran2, (Rt $s3))
    CloseEditors

    # ---- A3. an LSL-on-Lua script: SLua (LSL) chosen in a second box, saved ----
    $box2 = NewBox 5.0
    $s = NewScriptOpened
    TypeAll ("default`n{`nstate_entry()`n{`nllSay(0, `"lsl-luau $tok`");`n}`n}")
    [void](ClickS "engine_choose"); Start-Sleep -Milliseconds 400
    $i = ComboIndex (SState) '\(LSL'
    if ($i -ge 0) { [void](Scr @{ action = "pick"; text = "$i" }) }
    $before3 = SState
    $saved3 = SaveNow
    $ran3 = Said "lsl-luau $tok" 1
    $s4 = OpenRow "New Script"
    Check "A3 an LSL script, SLua (LSL) chosen, is saved with target lsl-luau and the region says LSL on Lua" ([bool]$saved3 -and $before3.target -eq "lsl-luau" -and $s4.runtime.Luau -and -not $s4.runtime.LuauLanguage) (("entry {0}; target sent {1}; said (shown, not judged) {2}; region: {3}") -f $i, $before3.target, [bool]$ran3, (Rt $s4))
    CloseEditors

    # ---- A4. copied to the inventory, opened there, edited and saved (the engine row untouched) ----
    [void](Api "notecard" @{ action = "list"; text = "load" })
    [void](Wait-Until { $l = Api "notecard" @{ action = "list" }; if ($l -and [int]$l.folders_unloaded -eq 0) { $l } } 30)
    $invBefore = @((Scr @{ action = "list" }).scripts | ForEach-Object { $_.id })
    $row = Row "New Script"
    if ($row) { ClickAt ([double]$row[0]) ([double]$row[1]) }
    [void](ClickCtl "content_copy")
    $copied = Wait-Until { @((Scr @{ action = "list" }).scripts | Where-Object { $invBefore -notcontains $_.id }) | Select-Object -First 1 } 30
    NoFocus
    Press "I" $true; Start-Sleep -Milliseconds 1500
    [void](Api "inventory" @{ action = "search"; text = "New Script" }); Start-Sleep -Milliseconds 1200
    $irow = if ($copied) { Wait-Until { (Edit @{ action = "inventory_row"; text = [string]$copied.id }).done } 10 } else { $null }
    $openBefore = (SState).open
    if ($irow) { DoubleClickAt ([double]$irow[0]) ([double]$irow[1]) }
    $invOpen = Wait-Until { $s = SState; if ($s.loaded -and -not $s.in_object -and $s.open -gt $openBefore) { $s } } 30
    TypeAll ("default`n{`nstate_entry()`n{`nllSay(0, `"inventory $tok`");`n}`n}")
    $before4 = SState
    $saved4 = SaveNow
    Shot "a4-inventory-saved"
    Check "A4 the LSL-on-Lua script copied to the inventory is opened there and saved with the engine row untouched" ([bool]$copied -and [bool]$invOpen -and [bool]$saved4) ("copy seen {0}; opened {1}; target sent '{2}'; row {3}; saved {4}" -f [bool]$copied, [bool]$invOpen, $before4.target, $before4.engine_row, [bool]$saved4)
    $report.inventory_target_sent = $before4.target
    # ---- A5. dragged back into a third box: the region still says LSL on Lua, and runs the inventory's new text ----
    CloseEditors
    $box3 = NewBox 7.0
    $from = if ($copied) { Wait-Until { InvRow ([string]$copied.id) } 10 } else { $null }
    $to = CtlPoint "contents"
    if ($from -and $to) { DragFromTo $from $to }
    $in = Wait-Until { $c = @((Edit @{ action = "state" }).contents); if ($c -contains "New Script") { $c } } 30
    $ran5 = Said "inventory $tok" 1
    $s5 = if ($in) { OpenRow "New Script" } else { $null }
    Shot "a5-dropped-back"
    Check "A5 saved from the inventory, the script keeps lsl-luau: dropped into a box the region runs it as LSL on Lua" ([bool]$in -and $s5.runtime.Luau -and -not $s5.runtime.LuauLanguage) (("in contents {0}; said (shown, not judged) {1}; region: {2}") -f [bool]$in, [bool]$ran5, (Rt $s5))
    CloseEditors
}

if ($Mode -eq "B") {
    # ---- B1. the region tells this viewer which engine runs a script (sent only to a viewer whose seed request lists SetScriptEngine) ----
    $box1 = NewBox 3.0
    $s = NewScriptOpened
    Note ("B1 opened: row {0}; entries [{1}]; {2}; why {3}/{4}" -f $s.engine_row, (Items $s), (Rt $s), $s.why.engine, $s.why.reason)
    Check "B1 the running reply carries Engine (the seed opt-in reached the region) and the engine row lists the region's engines" ([bool]$s.runtime -and [string]$s.runtime.Engine -ne "" -and $s.engine_row) ("{0}; entries [{1}]" -f (Rt $s), (Items $s))
    $report.b1_engine = $s.runtime.Engine
    # ---- B2. Choose engine myself, YEngine, saved; read back ----
    TypeAll ("default`n{`nstate_entry()`n{`nllSay(0, `"yengine $tok`");`n}`n}")
    [void](ClickS "engine_choose"); Start-Sleep -Milliseconds 400
    $i = ComboIndex (SState) '^YEngine'
    if ($i -ge 0) { [void](Scr @{ action = "pick"; text = "$i" }) }
    $before = SState
    $saved = SaveNow
    $ran = Said "yengine $tok" 1
    $s2 = OpenRow "New Script"
    Shot "b2-yengine"
    Check "B2 ticked, YEngine picked and saved: the save sends engine:YEngine and the region reads back Engine and EngineChoice YEngine" ([bool]$saved -and $before.target -eq "engine:YEngine" -and [bool]$ran -and $s2.runtime.Engine -eq "YEngine" -and $s2.runtime.EngineChoice -eq "YEngine") ("entry {0}; target sent {1}; said {2}; read back: {3}; why {4}/{5}" -f $i, $before.target, [bool]$ran, (Rt $s2), $s2.why.engine, $s2.why.reason)
    # ---- B3. unticked: the choice is cleared (SetScriptEngine with an empty target); read back ----
    $tick = SState
    if (-not $tick.choose_engine) { [void](ClickS "engine_choose"); Start-Sleep -Milliseconds 400 }   # the tick starts unticked even with a saved choice
    [void](ClickS "engine_choose"); Start-Sleep -Milliseconds 2500
    $afterUntick = SState
    $s3 = OpenRow "New Script"
    Shot "b3-cleared"
    Check "B3 unticking clears the saved choice: the region reads back no EngineChoice" ([bool]$s3.runtime -and [string]$s3.runtime.EngineChoice -eq "" -and -not $afterUntick.choose_engine) ("tick shown at reopen {0}; status after untick '{1}'; read back: {2}" -f $tick.choose_engine, $afterUntick.status, (Rt $s3))
    $report.box_local = [uint32]$box1.local
    CloseEditors
    $report.keep_objects = $true
}

if ($Mode -eq "Other") {
    # ---- B4. another avatar's object: its script's Contents > Engine entry is greyed (the viewer may not modify it) ----
    NoFocus
    if (-not (Edit @{ action = "state" }).build_mode) { Press "B" $true; Start-Sleep -Milliseconds 400 }
    [void](Edit @{ action = "select"; text = "$BoxLocal" }); Start-Sleep -Milliseconds 800
    [void](ClickCtl "tab:content"); Start-Sleep -Milliseconds 800
    $listed = Wait-Until { $c = @((Edit @{ action = "state" }).contents); if ($c -contains "New Script") { $c } } 30
    # the row's text carries what the viewer may not do with the item (" (no copy) (no modify) (no transfer)" for someone else's script);
    # the Build window is scrolled to the Contents list first, so the row is not under the selection card
    [void](CtlPoint "contents")
    $row = Row "New Script"
    if (-not $row) { $row = Row "New Script (no copy) (no modify) (no transfer)" }
    if (-not $row) { $row = Row "New Script (no modify)" }
    Note ("B4 the script's row at {0}" -f (@($row) -join ","))
    # the list's first row can sit at the clipped top edge of the Build window's scroll area: the press goes to its lower part
    if ($row) { ClickAt ([double]$row[0]) ([double]$row[1] + 6) "right" }
    $menu = Wait-Until { $m = (Api "menus" @{}).content_menu; if ($m -and $m.visible) { $m } } 10
    if (-not $menu) { Note ("B4 menus answer: " + ((Api "menus" @{}).content_menu | ConvertTo-Json -Compress -Depth 4)) }
    $engine = @($menu.items) | Where-Object { [string]$_.text -match "Engine" } | Select-Object -First 1
    Shot "b4-nomod-menu"
    Check "B4 a script the viewer may not modify: its Contents > Engine entry is greyed with a reason" ([bool]$listed -and [bool]$engine -and $engine.disabled -and [string]$engine.tooltip -ne "") ("listed {0}; menu [{1}]" -f [bool]$listed, ((@($menu.items) | ForEach-Object { "{0}{1}" -f $_.text, $(if ($_.disabled) { " (greyed: " + $_.tooltip + ")" } else { "" }) }) -join " | "))
    Press "ESC"
    $report.keep_objects = $true
}

# ---- friends: whether the other test account is a friend (answers ids only) ----
$fr = Api "friends" @{ action = "list" }
$report.friends = $fr

# ---- log out (a throwaway region: what was rezzed goes with it; Mode A takes back as usual) ----
NoFocus
Press "ESC"
if ($Mode -eq "A") { [void](Interact @{ action = "aim"; distance = 0 }); $report.take_back = Invoke-TakeBack -Seconds 30 }
$logout = Invoke-Raw $script:port "session" $script:apiToken '{"action":"logout"}'
Note "logout: $($logout.Status)"
[void](Invoke-Raw $script:port "quit" $script:apiToken)
$script:proc.WaitForExit(60000) | Out-Null
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "engine-live-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} checks, {1} failed" -f $checks.Count, $failed)
exit $(if ($failed -eq 0) { 0 } else { 1 })
