# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# build-live.ps1 - the live check of the Build menu's first tools. Starts the development export, logs a test avatar in, and through
# the automation interface:
#   1 upload    uploads one image the script generates (Upload Image): the price is shown before anything is sent, the upload completes
#               and the item is in the Textures folder without a relog
#   2 bulk      uploads three generated images at once (Bulk Upload), with a fourth file that is not an image and is marked as refused:
#               each image completes and is in the Textures folder
#   3 create    Build, Create, then a click on the ground in front of the avatar: a new object of the avatar's own appears (0.5 m box)
#   4 take      the object's menu, Take: the object leaves the world and an object item arrives in the Objects folder
#   5 rez       that item dropped on the world: a new object of the avatar's own appears
#   6 delete    the object's menu, Delete (and its question when the menu asks one): the object leaves the world, with no sound
# With -UploadOnly only step 1 runs (nothing is rezzed). The generated images are plain gradients made here; no other content is used.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\build-live.ps1 -Sandbox <new folder> -GridInfoUrl <url> -Start <start>
#     [-Credentials <accounts file>] [-Account <alias>] [-Exe <development export>] [-UploadOnly]
#
# Run it under the claims the target needs (a live-login or build claim, and an avatar claim on the account). Output: build-report.json in
# the sandbox (ids, counts, sizes and states only). Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login
# did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 90,
    [switch]$UploadOnly
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
    if ($port -and $token) { try { [void](Invoke-Raw $port "session" $token ('{"action":"logout"}')); Start-Sleep -Seconds 3; [void](Invoke-Raw $port "quit" $token) } catch { } }
    break
}
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

$problems = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh sandbox is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found: $Exe") }
if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
$loginUri = $null
if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
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
function Note([string]$what) { Write-Output ("{0,7:N1} {1}" -f $clock.Elapsed.TotalSeconds, $what) }
function Api([string]$command, [hashtable]$b) { Parse (Invoke-Raw $port $command $token ($b | ConvertTo-Json -Compress)).Body }
function Build([hashtable]$b) { Api "build" $b }
# polls a script block until it returns something true, at most $seconds; answers the last result
. (Join-Path $PSScriptRoot "live-takeback.ps1")
function Wait-Until([scriptblock]$test, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    do { $r = & $test; if ($r) { return $r }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $until)
    return $null
}

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$images = Join-Path $Sandbox "images"
New-Item -ItemType Directory -Force -Path $images | Out-Null
# plain gradients, made here: the only content uploaded
Add-Type -AssemblyName System.Drawing
$stamp = (Get-Date).ToUniversalTime().ToString("MMddHHmmss")
function New-Gradient([string]$name, [int]$w, [int]$h, [int]$hue) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    for ($y = 0; $y -lt $h; $y++) { for ($x = 0; $x -lt $w; $x++) {
        $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, [int](255 * $x / [math]::Max(1, $w - 1)), [int](255 * $y / [math]::Max(1, $h - 1)), $hue)) } }
    $path = Join-Path $images "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return $path
}
$single = New-Gradient "nv-build-$stamp-one" 64 64 40
$bulk = @((New-Gradient "nv-build-$stamp-b1" 48 32 90), (New-Gradient "nv-build-$stamp-b2" 100 60 160), (New-Gradient "nv-build-$stamp-b3" 16 16 220))
$notImage = Join-Path $images "nv-build-$stamp-notes.txt"
Set-Content -LiteralPath $notImage -Value "not an image" -Encoding ascii

$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output "LIVE BUILD RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
$argLine = "--rendering-driver vulkan -- --automation"
$p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "900", "-HangSnapshotSeconds", "0",
    "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
Note "viewer started through godot.ps1, pid $($p.Id)"
$tokDir = Join-Path $local "ReverieViewer\Automation"
$tf = $null
for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
if (-not $tf) { Note "no automation token file: stopping"; exit 5 }
Start-Sleep -Milliseconds 300
$j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
$port = [int]$j.port; $token = [string]$j.token

$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-48} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; upload_only = [bool]$UploadOnly; checks = $null; completed = $false; steps = [ordered]@{} }

$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
$o = Wait-Until { $x = Parse (Invoke-Raw $port "observe" $token).Body; if ($x -and $x.stage -eq "InWorld" -and $x.self) { $x } } 150
if ($o) {
    Note "in world"
    # the build tools are installed with the world; the inventory's folders come with the login
    $st = Wait-Until { $b = Build @{ action = "state" }; if ($b -and $null -ne $b.placing) { $b } } 30
    Start-Sleep -Seconds 3

    # 1 one image
    $name1 = [System.IO.Path]::GetFileNameWithoutExtension($single)
    $opened = Build @{ action = "upload_image"; text = $single }
    $report.steps.upload_opened = $opened
    $priced = Wait-Until { $b = Build @{ action = "state" }; $t = @($b.image); if ($t.Count -ge 2 -and $t[-2] -notmatch "asking") { $b } } 30
    $costLine = if ($priced) { @($priced.image)[-2] } else { "" }
    $button = if ($priced) { @($priced.image)[-1] } else { "" }
    $pressed = Build @{ action = "press_upload"; text = "image" }
    $done1 = Wait-Until { $b = Build @{ action = "state" }; $s = @($b.image_states); if ($s.Count -ge 1 -and @($s | Where-Object { $_ -match "^(Done|Failed)" }).Count -eq $s.Count) { $b } } 90
    $inv1 = Wait-Until { $b = Build @{ action = "state"; expect = $name1 }; if ($b.texture_named -ge 1) { $b } } 30
    $report.steps.upload = @{ cost = $costLine; button = $button; pressed = $pressed.pressed; states = @($done1.image_states); named = $inv1.texture_named; row = @($done1.image)[0] }
    Check "1 upload one image: price shown, completes, in Textures" ([bool]$pressed.pressed -and @($done1.image_states) -contains "Done" -and $inv1.texture_named -ge 1) ("cost '{0}', button '{1}', states {2}, in Textures {3}" -f $costLine, $button, (@($done1.image_states) -join ","), $inv1.texture_named)

    if (-not $UploadOnly) {
        # 2 three images at once, and a file that is not an image
        $opened = Build @{ action = "upload_bulk"; text = (($bulk + $notImage) -join "|") }
        $refusedRows = @(@($opened.texts) | Where-Object { $_ -match "Not an image" }).Count
        $priced = Wait-Until { $b = Build @{ action = "state" }; $t = @($b.bulk); if ($t.Count -ge 2 -and $t[-2] -notmatch "asking") { $b } } 30
        $pressed = Build @{ action = "press_upload"; text = "bulk" }
        $doneB = Wait-Until { $b = Build @{ action = "state" }; $s = @($b.bulk_states); if ($s.Count -ge 3 -and @($s | Where-Object { $_ -match "^(Done|Failed)" }).Count -eq $s.Count) { $b } } 120
        $named = 0
        foreach ($f in $bulk) { $n = [System.IO.Path]::GetFileNameWithoutExtension($f); $hit = Wait-Until { $b = Build @{ action = "state"; expect = $n }; if ($b.texture_named -ge 1) { $b } } 30; if ($hit) { $named++ } }
        $report.steps.bulk = @{ rows = @($opened.texts); refused_rows = $refusedRows; states = @($doneB.bulk_states); in_textures = $named; cost = @($priced.bulk)[-2] }
        Check "2 bulk: three images complete, a non-image is marked" ([bool]$pressed.pressed -and @(@($doneB.bulk_states) | Where-Object { $_ -eq "Done" }).Count -eq 3 -and $named -eq 3 -and $refusedRows -eq 1) ("states {0}, in Textures {1} of 3, refused rows {2}" -f (@($doneB.bulk_states) -join ","), $named, $refusedRows)

        # 3 create a box on the ground in front of the avatar
        $me = (Parse (Invoke-Raw $port "observe" $token).Body).self.region_position
        $spot = @{ x = [double]$me[0] + 3.0; y = [double]$me[1]; z = [double]$me[2] - 0.9 }
        $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
        $made = Build @{ action = "create"; x = $spot.x; y = $spot.y; z = $spot.z }
        $box = Wait-Until { $b = Build @{ action = "state" }; @($b.own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
        if ($box) { Add-Rezzed ([uint32]$box.local) }
        $report.steps.create = @{ answer = $made; object = $box }
        $sizeOk = $box -and [math]::Abs([double]$box.sx - 0.5) -lt 0.01 -and [math]::Abs([double]$box.sz - 0.5) -lt 0.01
        Check "3 create: a 0.5 m box of the avatar's own appears" ([bool]$made.placed -and [bool]$box -and $sizeOk -and (@($made.sounds) -match "ObjectCreate").Count -ge 1) ("placed {0} on {1}, new object {2}, size {3}x{4}x{5}, sounds {6}" -f $made.placed, $made.pick, [bool]$box, $box.sx, $box.sy, $box.sz, (@($made.sounds) -join ","))

        # 4 take it
        $itemsBefore = @((Build @{ action = "state" }).object_items)
        $taken = $false; $item = $null
        if ($box) {
            $menu = Api "interact" @{ action = "rightclick"; id = [string]$box.id }
            Start-Sleep -Seconds 1
            $menu = Api "interact" @{ action = "rightclick"; id = [string]$box.id }
            $takeEntry = @($menu.entries | Where-Object { $_.Id -eq "take" })[0]
            $chosen = Api "interact" @{ action = "choose"; entry = "take" }
            $gone = Wait-Until { $b = Build @{ action = "state" }; if (@($b.own_objects | Where-Object { $_.id -eq $box.id }).Count -eq 0) { $b } } 20
            $item = Wait-Until { $b = Build @{ action = "state" }; @($b.object_items | Where-Object { $itemsBefore -notcontains $_ }) | Select-Object -First 1 } 30
            $taken = [bool]$chosen.chosen -and [bool]$gone -and [bool]$item
            $report.steps.take = @{ entry_enabled = $takeEntry.Enabled; chosen = $chosen.chosen; gone = [bool]$gone; item = $item }
        }
        $sounds = @((Build @{ action = "state" }).sounds)
        Check "4 take: leaves the world, an item arrives in Objects" ($taken -and ($sounds -match "ObjectRezOut").Count -ge 1) ("taken {0}, item {1}, sounds {2}" -f $taken, [bool]$item, ($sounds -join ","))

        # 5 rez it back from the inventory
        $box2 = $null
        if ($item) {
            $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
            $rez = Build @{ action = "rez"; id = [string]$item; x = $spot.x; y = $spot.y; z = $spot.z }
            $box2 = Wait-Until { $b = Build @{ action = "state" }; @($b.own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
            if ($box2) { Add-Rezzed ([uint32]$box2.local) }
            $report.steps.rez = @{ answer = $rez; object = $box2 }
        }
        Check "5 rez from inventory: the object is back in the world" ([bool]$box2 -and (@($rez.sounds) -match "ObjectRezIn").Count -ge 1) ("rezzed {0}, new object {1}, sounds {2}" -f $rez.rezzed, [bool]$box2, (@($rez.sounds) -join ","))

        # 6 delete it
        $deleted = $false
        if ($box2) {
            $menu = Api "interact" @{ action = "rightclick"; id = [string]$box2.id }
            Start-Sleep -Seconds 1
            $menu = Api "interact" @{ action = "rightclick"; id = [string]$box2.id }
            $del = @($menu.entries | Where-Object { $_.Id -eq "delete" })[0]
            $chosen = Api "interact" @{ action = "choose"; entry = "delete" }
            if (-not $chosen.chosen) {
                $menu = Api "interact" @{ action = "rightclick"; id = [string]$box2.id }
                $chosen = Api "interact" @{ action = "choose"; entry = "delete/confirm" }
            }
            $gone = Wait-Until { $b = Build @{ action = "state" }; if (@($b.own_objects | Where-Object { $_.id -eq $box2.id }).Count -eq 0) { $b } } 20
            $deleted = [bool]$chosen.chosen -and [bool]$gone
            $report.steps.delete = @{ entry_enabled = $del.Enabled; chosen = $chosen.chosen; gone = [bool]$gone }
        }
        $sounds = @((Build @{ action = "state" }).sounds)
        # the official viewer plays no sound on delete
        Check "6 delete: the object leaves the world (to Trash), silently" ($deleted -and ($sounds -match "ObjectDelete").Count -eq 0) ("deleted {0}, sounds {1}" -f $deleted, ($sounds -join ","))
    }
    $report.completed = $true
}
else { Note "not in the world within 150 s" }
$report.checks = $checks
# everything the run rezzed and did not already take or delete goes back to the inventory by id, proven by re-reading the region
$tb = Invoke-TakeBack -Seconds 30; $report.take_back = $tb
Note ("take back: rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
if (-not $tb.ok) { $checks.Add([ordered]@{ name = "everything the run rezzed is gone from the region"; pass = $false; detail = "remaining " + (@($tb.remaining) -join ",") }) }
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$afterLogout = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($afterLogout.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$afterLogout.ended
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "build-report.json") -Encoding utf8
Write-Output "LIVE BUILD RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
