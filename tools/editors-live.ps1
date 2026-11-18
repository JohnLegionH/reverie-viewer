# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
# =====================================================================================
# editors-live.ps1 - the notecard editor, gestures and Inventory > New, live, through the automation API on the development export.
#   Logs in as -Account and, in its own inventory: makes a notecard (Inventory > New), opens it, types a marker, drops one of its
#   landmarks into the text, saves, closes and opens it again, and checks the marker and the embedded landmark came back; makes a
#   script and checks the item arrived; makes a gesture (it opens in the gesture editor and is activated), gives it a trigger and a chat
#   step, saves, says the trigger in nearby chat and checks the chat step was heard; makes a skirt and checks the item arrived. Logs out.
#   Every item it makes is new and named by the grid's defaults; nothing else in the inventory is changed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\editors-live.ps1 -Sandbox <new folder> -OutDir <existing folder>
#     [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Run it under a live-login or scratch claim and an avatar claim on the account. Output: editors-check.json in -OutDir.
# Exit: 0 pass, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = "viewer1",
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 90
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
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
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if (-not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null

function Start-Viewer([string]$dir) {
    $appdata = Join-Path $dir "appdata"; $local = Join-Path $dir "localappdata"; $log = Join-Path $dir "run.log"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $argLine = "--rendering-driver vulkan -- --automation"
    $script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
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
function Nc($c, [string]$action, $extra = @{}) { $b = @{ action = $action }; foreach ($k in $extra.Keys) { $b[$k] = $extra[$k] }; return Call $c "notecard" $b }
# makes an item through Inventory > New and waits for the newest item of that name and kind to change
function Make($c, [string]$entry, [string]$name, [string]$kind) {
    $before = [string](Nc $c "find_item" @{ text = "$name|$kind" }).id
    [void](Nc $c "new" @{ text = $entry })
    $r = Until $c "notecard" @{ action = "find_item"; text = "$name|$kind" } { param($x) [string]$x.id -ne $before -and [string]$x.id -ne "00000000-0000-0000-0000-000000000000" } 45
    $id = [string]$r.id
    return $(if ($id -ne $before -and $id -ne "00000000-0000-0000-0000-000000000000") { $id } else { $null })
}

$exit = 0
$V = $null
$result = [ordered]@{}
$marker = "edtest" + (Get-Random -Minimum 100000 -Maximum 999999)
try {
    $V = Start-Viewer (Join-Path $Sandbox "viewer")
    if (-not $V) { Note "no automation token file"; $exit = 5; throw "the viewer did not start" }
    Note "viewer up"
    $r = Invoke-Raw $V.port "session" $V.token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
    Note "login requested, $($r.Status)"
    $o = Until $V "observe" $null { param($x) $x.stage -eq "InWorld" -and $x.self } 180
    if (-not $o -or $o.stage -ne "InWorld") { $exit = 5; throw "the login did not reach the world" }
    Note "in world"
    $list = Until $V "notecard" @{ action = "list"; text = "load" } { param($x) $x.folders_unloaded -eq 0 } 120
    Note "inventory loaded (folders unloaded $($list.folders_unloaded))"

    # ---- notecard: make, type, embed a landmark, save, close, open again ----
    $card = Make $V "notecard" "New Note" "Notecard"
    Check "notecard.made" ([bool]$card) "item $card"
    $landmark = [string](Nc $V "find_item" @{ text = "*|Landmark" }).id
    if ($card) {
        [void](Nc $V "open" @{ id = $card })
        $f = Until $V "notecard" @{ action = "front" } { param($x) $x.editor -and [string]$x.item -eq $card } 45
        Check "notecard.opens_editable" ($f -and $f.editor -and $f.can_edit -and $f.save_visible) "editor $($f.editor), can edit $($f.can_edit), save shown $($f.save_visible)"
        [void](Nc $V "type" @{ text = "$marker`n" })
        if ($landmark -and $landmark -ne "00000000-0000-0000-0000-000000000000") { [void](Nc $V "embed" @{ id = $landmark }) } else { Note "the account has no landmark" }
        $f = Nc $V "front" @{ text = $marker }
        Check "notecard.edited" ($f.dirty -and $f.has_marker -and [int]$f.items -eq 1 -and @($f.kinds)[0] -eq "Landmark") "dirty $($f.dirty), marker $($f.has_marker), items $($f.items) $(@($f.kinds) -join ',')"
        [void](Nc $V "save")
        $f = Until $V "notecard" @{ action = "front"; text = $marker } { param($x) $x.saved -and -not $x.dirty } 45
        Check "notecard.saved" ($f -and $f.saved -and -not $f.dirty) "saved $($f.saved), dirty $($f.dirty)"
        $cl = Nc $V "close"
        Check "notecard.closes_without_asking" ($cl.closed -and -not $cl.asked) "closed $($cl.closed), asked $($cl.asked)"
        Start-Sleep -Seconds 1
        [void](Nc $V "open" @{ id = $card })
        $f = Until $V "notecard" @{ action = "front"; text = $marker } { param($x) $x.editor -and [string]$x.item -eq $card -and $x.has_marker } 45
        Check "notecard.reopened_with_text_and_landmark" ($f -and $f.has_marker -and [int]$f.items -eq 1 -and @($f.kinds)[0] -eq "Landmark") "marker $($f.has_marker), items $($f.items) $(@($f.kinds) -join ','), bytes $($f.bytes)"
        [void](Nc $V "close")
    }

    # ---- script ----
    $script = Make $V "script" "New Script" "Script"
    Check "script.made" ([bool]$script) "item $script"

    # ---- gesture: made, opens, trigger and a chat step, saved, played by its trigger ----
    $gesture = Make $V "gesture" "New Gesture" "Gesture"
    Check "gesture.made" ([bool]$gesture) "item $gesture"
    if ($gesture) {
        $g = Until $V "notecard" @{ action = "gesture_state" } { param($x) $x.open -and [string]$x.item -eq $gesture } 45
        Check "gesture.opens_in_editor" ($g -and $g.open -and [string]$g.item -eq $gesture) "open $($g.open)"
        $trigger = "/" + $marker
        [void](Nc $V "gesture_setup" @{ text = $trigger })
        $g = Until $V "notecard" @{ action = "gesture_state" } { param($x) $x.saved -and -not $x.dirty } 45
        Check "gesture.saved_and_active" ($g -and $g.saved -and $g.active -and [int]$g.steps -eq 1) "saved $($g.saved), active $($g.active), steps $($g.steps)"
        $s = Nc $V "say" @{ text = $trigger }
        $h = Until $V "notecard" @{ action = "heard"; text = "$trigger works" } { param($x) $x.heard } 30
        Check "gesture.plays_from_its_trigger" ($h -and $h.heard) "line sent $($s.cleared), chat step heard $($h.heard)"
    }

    # ---- skirt ----
    $skirt = Make $V "skirt" "New Skirt" "Clothing"
    Check "skirt.made" ([bool]$skirt) "item $skirt"

    [void](Invoke-Raw $V.port "session" $V.token '{"action":"logout"}')
    Start-Sleep -Seconds 3
} catch {
    Note "stopped: $($_.Exception.Message)"
    if ($exit -eq 0) { $exit = 1 }
} finally {
    if ($V) { try { [void](Invoke-Raw $V.port "quit" $V.token) } catch { } }
    if ($script:proc) { try { $script:proc.WaitForExit(60000) | Out-Null } catch { } }
}
$result.checks = $checks
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir "editors-check.json") -Encoding utf8
if ($exit -eq 0 -and @($checks | Where-Object { -not $_.pass }).Count -gt 0) { $exit = 1 }
Write-Output ("editors check: {0} checks, {1} failed, exit {2}" -f $checks.Count, @($checks | Where-Object { -not $_.pass }).Count, $exit)
exit $exit
