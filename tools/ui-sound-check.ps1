# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# ui-sound-check.ps1 - the interface sounds through the automation interface, on the development export, offline (the login screen
# is enough). Each step does one thing and checks that the viewer played exactly the slot it should, once, and nothing else:
#   window open    Preferences opens                                      -> WindowOpen
#   button         a category button of Preferences is pressed and let go
#                  through the engine's input path (a real mouse click)   -> ClickRelease
#   window close   Preferences closes                                     -> WindowClose
#   notification   a login-flow alert (the update notice) appears         -> Alert
#   play button    a sound is played as its Play button plays it          -> that slot, even while it is switched off
# A step waits for the sound to be decided (polling, with a cap), then keeps watching for a short window so that a second or a wrong
# sound would be seen. It also checks the engine was asked to play each sound (the player's count).
# Output: the sandbox folder (ui-sound-check.txt, ui-sound-check.json, the viewer's log). Exit 0 all passed, 1 a step failed, 3 blocked.
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\ui-sound-check.ps1 -Sandbox <new folder> [-Exe <viewer>]
# Needs the development export (tools\s1.ps1 makes it) and a granted lane claim.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Exe = "",
    [int]$StartSeconds = 120,
    [double]$QuietSeconds = 1.0
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "BLOCKED: the development export is missing: $Exe"; exit 3 }
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$textLog = Join-Path $Sandbox "ui-sound-check.txt"
$results = [System.Collections.Generic.List[object]]::new()
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
function Record([string]$name, [bool]$pass, [string]$detail) {
    $script:results.Add([ordered]@{ step = $name; pass = $pass; detail = $detail })
    Say ("{0}: {1}  {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail)
}

function Invoke-Api($v, [string]$command, $body) {
    $json = if ($body -is [string]) { $body } else { $body | ConvertTo-Json -Compress -Depth 5 }
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
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function Api($v, [string]$command, $body) { $r = Invoke-Api $v $command $body; if ($r.Status -eq 200 -and $r.Body) { try { $r.Body | ConvertFrom-Json } catch { $null } } else { $null } }
function Wait-Until([scriptblock]$cond, [double]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 200 }
    return $null
}

# one viewer: its own settings and local folders, its own log, started through godot.ps1 as every Godot run is
$local = Join-Path $Sandbox "localappdata"
$appdata = Join-Path $Sandbox "appdata"
New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
$log = Join-Path $Sandbox "viewer.log"
$args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"", "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"",
    "-TimeoutSeconds", "300", "-HangSnapshotSeconds", "0", "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"-- --automation`"")
$proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $args2
$v = @{ Port = 0; Token = "" }
try {
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
    if (-not $tf) { Record "start" $false "no automation token file within $StartSeconds s"; throw "no viewer" }
    Start-Sleep -Milliseconds 400
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $v.Port = [int]$j.port; $v.Token = [string]$j.token
    $ready = Wait-Until { $l = Api $v "ui_sounds" '{"action":"log"}'; if ($l -and $null -ne $l.events) { $l } } 60
    if (-not $ready) { Record "start" $false "the viewer has no ui_sounds command (or no sounds service)"; throw "no sounds" }
    Record "start" $true "automation and the sounds service answer; in_world=$($ready.in_world)"

    # one step: clear, act, wait for the first decision, keep watching for the quiet window, then compare what was played
    function Step([string]$name, [scriptblock]$act, [string[]]$want) {
        [void](Api $v "ui_sounds" '{"action":"clear"}')
        $before = (Api $v "ui_sounds" '{"action":"log"}').started
        & $act
        $first = Wait-Until { $l = Api $v "ui_sounds" '{"action":"log"}'; if ($l.events.Count -ge $want.Count -and $want.Count -gt 0) { $l } } 10
        Start-Sleep -Milliseconds ([int]($QuietSeconds * 1000))   # a wrong or second sound would arrive in this window
        $l = Api $v "ui_sounds" '{"action":"log"}'
        $got = @($l.events | ForEach-Object { "$($_.slot):$($_.outcome)" })
        $played = @($l.events | Where-Object { $_.outcome -like "Played*" } | ForEach-Object { $_.slot })
        $started = [int]$l.started - [int]$before
        $ok = ($played.Count -eq $want.Count) -and (@(Compare-Object $played $want -SyncWindow 0).Count -eq 0) -and ($got.Count -eq $want.Count) -and ($started -eq $want.Count)
        Record $name $ok ("wanted [{0}], decided [{1}], engine asked to play {2}" -f ($want -join ","), ($got -join ","), $started)
    }

    Step "window open" { [void](Api $v "open_window" '{"name":"preferences"}') } @("WindowOpen")

    $pos = Wait-Until { $p = Api $v "ui_sounds" '{"action":"find","name":"category_graphics"}'; if ($p.found) { $p } } 10
    if (-not $pos) { Record "button" $false "the Preferences category button was not found" }
    else {
        Step "button" {
            [void](Api $v "input" (@{ kind = "mouse"; x = $pos.x; y = $pos.y } | ConvertTo-Json -Compress))
            Start-Sleep -Milliseconds 100
            [void](Api $v "input" (@{ kind = "mouse"; button = "left"; down = $true; x = $pos.x; y = $pos.y } | ConvertTo-Json -Compress))
            Start-Sleep -Milliseconds 100
            [void](Api $v "input" (@{ kind = "mouse"; button = "left"; down = $false; x = $pos.x; y = $pos.y } | ConvertTo-Json -Compress))
        } @("ClickRelease")
    }

    Step "window close" { [void](Api $v "ui_sounds" '{"action":"close_window","name":"preferences"}') } @("WindowClose")

    Step "notification" { [void](Api $v "login_dialog" '{"action":"show","kind":"update"}') } @("Alert")
    [void](Api $v "login_dialog" '{"action":"close"}')

    Step "play button" { [void](Api $v "ui_sounds" '{"action":"play","slot":"ChatMention"}') } @("ChatMention")
}
catch { Say "stopped: $($_.Exception.Message)" }
finally {
    try { if ($v.Port) { [void](Invoke-Api $v "quit" '{}') } } catch { }
    if (-not $proc.WaitForExit(30000)) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }   # only the launcher this script started
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Sandbox "ui-sound-check.json") -Encoding utf8
$failed = @($results | Where-Object { -not $_.pass }).Count
Say ("ui-sound-check: {0} steps, {1} failed" -f $results.Count, $failed)
if ($failed -gt 0 -or $results.Count -lt 6) { exit 1 }
exit 0
