# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# world-sound-check.ps1 - sounds in the world and the Sound Tuner through the automation interface, on the development export, offline
# (the login screen is enough). The world_sounds command's feed puts sound messages, as the protocol adapter reads them, into a sound
# service made for the check (its sounds all play the viewer's own click file), with an object and the listener placed by the check.
# Each step feeds one message and checks, through world_sounds state, which sources play, where, and at what gain (the official
# fall-off: gain x the Sounds level 0.5 x 1/distance), and that the engine was asked to play:
#   trigger       a one-shot 10 m north of the listener at gain 0.8        -> playing at that point, gain 0.04
#   loop          a loop on an object 10 m east, gain 1                   -> playing at the object, gain 0.05, looping
#   gain change   the object's sound to gain 0.2                          -> gain 0.01
#   stop          a null sound for the object                             -> the object's source is gone
#   tuner         the Sound Tuner: the typing sound's length moved, saved, reset -> tuned and played, then back to the committed recipe;
#                 the click, a recording, has no recipe to choose
# Output: the sandbox folder (world-sound-check.txt, world-sound-check.json, the viewer's log). Exit 0 all passed, 1 a step failed, 3 blocked.
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\world-sound-check.ps1 -Sandbox <new folder> [-Exe <viewer>]
# Needs the development export (tools\s1.ps1 makes it) and a granted lane claim.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Exe = "",
    [int]$StartSeconds = 120
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "BLOCKED: the development export is missing: $Exe"; exit 3 }
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$textLog = Join-Path $Sandbox "world-sound-check.txt"
$results = [System.Collections.Generic.List[object]]::new()
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
function Record([string]$name, [bool]$pass, [string]$detail) {
    $script:results.Add([ordered]@{ step = $name; pass = $pass; detail = $detail })
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
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function Api($v, [string]$command, $body) { $r = Invoke-Api $v $command $body; if ($r.Status -eq 200 -and $r.Body) { try { $r.Body | ConvertFrom-Json } catch { $null } } else { $null } }
function Wait-Until([scriptblock]$cond, [double]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 200 }
    return $null
}
function Near([double]$a, [double]$b, [double]$tol = 0.0005) { [math]::Abs($a - $b) -le $tol }

$local = Join-Path $Sandbox "localappdata"
$appdata = Join-Path $Sandbox "appdata"
New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
$log = Join-Path $Sandbox "viewer.log"
$args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"", "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"",
    "-TimeoutSeconds", "300", "-HangSnapshotSeconds", "0", "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"-- --automation`"")
$proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $args2
$v = @{ Port = 0; Token = "" }
$box = "c0000000-0000-4000-8000-0000000000b1"
$bell = "b0000000-0000-4000-8000-000000000001"
$hum = "b0000000-0000-4000-8000-000000000002"
try {
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
    if (-not $tf) { Record "start" $false "no automation token file within $StartSeconds s"; throw "no viewer" }
    Start-Sleep -Milliseconds 400
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $v.Port = [int]$j.port; $v.Token = [string]$j.token
    $ready = Wait-Until { $s = Api $v "world_sounds" '{"action":"state"}'; if ($s -and $null -ne $s.in_world) { $s } } 60
    if (-not $ready) { Record "start" $false "the viewer has no world_sounds command"; throw "no sounds" }
    $fed = Api $v "world_sounds" (@{ action = "feed"; listener = @(128, 128, 25); objects = @(@{ id = $box; x = 138; y = 128; z = 25 }) })
    Record "start" ([bool]$fed.fed) "automation answers; in_world=$($ready.in_world); feed=$($fed.fed)"

    function Source($origin, $obj) {
        $s = Api $v "world_sounds" '{"action":"state"}'
        @($s.sources | Where-Object { $_.origin -eq $origin -and ($null -eq $obj -or $_.obj -eq $obj) -and $_.state -eq "Playing" }) | Select-Object -First 1
    }

    $before = (Api $v "world_sounds" '{"action":"state"}').started
    [void](Api $v "world_sounds" (@{ action = "feed"; events = @(@{ kind = "trigger"; sound = $bell; object = "c0000000-0000-4000-8000-0000000000b2"; owner = "a0000000-0000-4000-8000-000000000001"; gain = 0.8; x = 128; y = 138; z = 25 }) }))
    $t = Wait-Until { Source "Trigger" $null } 10
    $st = Api $v "world_sounds" '{"action":"state"}'
    Record "trigger" ($t -and (Near $t.x 128) -and (Near $t.y 138) -and (Near $t.gain 0.04) -and ([int]$st.started -eq [int]$before + 1)) `
        ("source at ({0}, {1}, {2}) gain {3}; voices started {4} -> {5}; engine playing {6}" -f $t.x, $t.y, $t.z, $t.gain, $before, $st.started, $st.playing)

    [void](Api $v "world_sounds" (@{ action = "feed"; events = @(@{ kind = "attached"; object = $box; sound = $hum; owner = "a0000000-0000-4000-8000-000000000001"; gain = 1; flags = 1 }) }))
    $a = Wait-Until { Source "Attached" $box } 10
    $st = Api $v "world_sounds" '{"action":"state"}'
    Record "loop" ($a -and (Near $a.x 138) -and (Near $a.y 128) -and (Near $a.gain 0.05) -and $a.loop -and [int]$st.playing -ge 1) `
        ("source at ({0}, {1}) gain {2} loop {3}; engine playing {4}" -f $a.x, $a.y, $a.gain, $a.loop, $st.playing)

    [void](Api $v "world_sounds" (@{ action = "feed"; events = @(@{ kind = "gain"; object = $box; gain = 0.2 }) }))
    $g = Wait-Until { $x = Source "Attached" $box; if ($x -and (Near $x.gain 0.01)) { $x } } 10
    Record "gain change" ([bool]$g) ("gain now {0}" -f $(if ($g) { $g.gain } else { (Source "Attached" $box).gain }))

    [void](Api $v "world_sounds" (@{ action = "feed"; events = @(@{ kind = "attached"; object = $box; sound = "00000000-0000-0000-0000-000000000000"; owner = "a0000000-0000-4000-8000-000000000001"; gain = 0; flags = 0 }) }))
    $gone = Wait-Until { $s = Api $v "world_sounds" '{"action":"state"}'; if (@($s.sources | Where-Object { $_.obj -eq $box }).Count -eq 0) { $s } } 10
    $outcomes = @((Api $v "world_sounds" '{"action":"state"}').recent | ForEach-Object { $_.outcome }) -join ","
    Record "stop" ([bool]$gone) ("decisions: " + $outcomes)

    # the Sound Tuner: the typing sound's length to 900 ms (the sound is made again and played), saved to the user folder, then reset;
    # the click is a recording, so the tuner has nothing to show for it
    $tc = Api $v "sound_tuner" '{"action":"choose","slot":"Click"}'
    $t0 = Api $v "sound_tuner" '{"action":"choose","slot":"Typing"}'
    $t1 = Api $v "sound_tuner" '{"action":"set","key":"Ms","value":900}'
    $ms = @($t1.numbers | Where-Object { $_.key -eq "Ms" })[0].value
    $saved = Api $v "sound_tuner" '{"action":"save"}'
    $file = Get-ChildItem -LiteralPath $appdata -Recurse -Filter "ui-sounds-tuned.json" -ErrorAction SilentlyContinue | Select-Object -First 1
    $t2 = Api $v "sound_tuner" '{"action":"reset"}'
    $msAfter = @($t2.numbers | Where-Object { $_.key -eq "Ms" })[0].value
    Record "tuner" ($tc.error -and $t1 -and (Near $ms 900) -and ($t1.tuned -contains "ui-typing.wav") -and ([int]$t1.started -gt [int]$t0.started) -and $saved.saved -and $file -and (Near $msAfter 1050) -and (@($t2.tuned).Count -eq 0)) `
        ("click: {8}; length {0} ms, tuned [{1}], played {2} -> {3}, saved {4} ({5}), after reset {6} ms, tuned [{7}]" -f $ms, ($t1.tuned -join ","), $t0.started, $t1.started, $saved.saved, $(if ($file) { "file written" } else { "no file" }), $msAfter, (@($t2.tuned) -join ","), $tc.error)
}
catch { Say "stopped: $($_.Exception.Message)" }
finally {
    try { if ($v.Port) { [void](Invoke-Api $v "quit" '{}') } } catch { }
    if (-not $proc.WaitForExit(30000)) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }   # only the launcher this script started
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Sandbox "world-sound-check.json") -Encoding utf8
$failed = @($results | Where-Object { -not $_.pass }).Count
Say ("world-sound-check: {0} steps, {1} failed" -f $results.Count, $failed)
if ($failed -gt 0 -or $results.Count -lt 6) { exit 1 }
exit 0
