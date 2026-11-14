# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# model-live.ps1 - the live check of model uploads. Starts the development export, logs a test avatar in, and through the automation
# interface:
#   1 model     uploads one generated .glb (a 1 m box: four sides with a red texture, top and bottom with a blue one) with Include textures
#               (Upload > Model...): the fee and land impact are shown before anything is sent, the upload completes and the object item is
#               in the Objects folder without a relog
#   2 rez       (with -Rez) that item dropped on the world: a new object of the avatar's own appears; each face's texture is read: the sides
#               share one texture, top and bottom another, and none is blank or the default
#   3 take      (with -Rez) the object taken back: it leaves the world and an object item arrives in the Objects folder
#   4 bulk      (unless -SingleOnly) three generated models at once (Upload > Bulk models...: a .glb, a .gltf with its buffer and images
#               beside it, a .dae with its image beside it) and a broken .glb that is marked as refused: each model completes and is in Objects
# The models and their textures are plain shapes and colours made here; no other content is used.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\model-live.ps1 -Sandbox <new folder> -GridInfoUrl <url> -Start <start>
#     [-Credentials <accounts file>] [-Account <alias>] [-Exe <development export>] [-Rez] [-SingleOnly] [-TakeIds <id,...>]
#
# Run it under the claims the target needs (a live-login or build claim, and an avatar claim on the account). Output: model-report.json in
# the sandbox (ids, counts and states only). Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
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
    [switch]$Rez,
    [switch]$SingleOnly,
    [string[]]$TakeIds = @()   # only take these objects of the avatar's own back (a run that left them), nothing uploaded
)
$TakeIds = @($TakeIds | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # -File passes a comma list as one string
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
function Wait-Until([scriptblock]$test, [int]$seconds) {
    $until = (Get-Date).AddSeconds($seconds)
    do { $r = & $test; if ($r) { return $r }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $until)
    return $null
}
# takes one object of the avatar's own back: the camera is aimed at it, then its menu is opened until Take is enabled, at most 20 s
function Take-Back([string]$id) {
    $itemsBefore = @((Build @{ action = "state" }).object_items)
    # the camera looks at the object from between it and the avatar (the aim comes from the avatar's side), so the avatar is not in the way
    $at = @((Build @{ action = "state" }).own_objects | Where-Object { $_.id -eq $id }) | Select-Object -First 1
    if ($at) { [void](Api "interact" @{ action = "aim"; x = [double]$at.x; y = [double]$at.y; z = [double]$at.z; distance = 1.8 }); Start-Sleep -Seconds 2 }
    $ready = Wait-Until { $m = Api "interact" @{ action = "rightclick"; id = $id }; if (@($m.entries | Where-Object { $_.Id -eq "take" -and $_.Enabled }).Count -gt 0) { $m } } 20
    $chosen = if ($ready) { Api "interact" @{ action = "choose"; entry = "take" } } else { $null }
    $gone = Wait-Until { $b = Build @{ action = "state" }; if (@($b.own_objects | Where-Object { $_.id -eq $id }).Count -eq 0) { $b } } 20
    $back = Wait-Until { $b = Build @{ action = "state" }; @($b.object_items | Where-Object { $itemsBefore -notcontains $_ }) | Select-Object -First 1 } 30
    $last = Api "interact" @{ action = "rightclick"; id = $id }
    [void](Api "interact" @{ action = "aim"; distance = 0 })
    return @{ menu = @($last.entries | ForEach-Object { "$($_.Id):$($_.Enabled)" }) -join " "; pick = "$($last.pick.kind) $($last.pick.local) $($last.pick.distance)"; error = $last.error; take_enabled = [bool]$ready; chosen = [bool]$chosen.chosen; gone = [bool]$gone; item = $back; taken = [bool]$chosen.chosen -and [bool]$gone -and [bool]$back }
}

# ---------------- the generated models ----------------
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$models = Join-Path $Sandbox "models"
New-Item -ItemType Directory -Force -Path $models | Out-Null
Add-Type -AssemblyName System.Drawing
$stamp = (Get-Date).ToUniversalTime().ToString("MMddHHmmss")
function New-Png([int]$r, [int]$g, [int]$b) {
    $bmp = New-Object System.Drawing.Bitmap 8, 8
    for ($y = 0; $y -lt 8; $y++) { for ($x = 0; $x -lt 8; $x++) { $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $r, $g, $b)) } }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}
$red = New-Png 220 30 30; $blue = New-Png 30 60 220
# a 1 m box in glTF's axes: four sides (material 0) and top and bottom (material 1), each side its own four corners
$sides = @(
    @(@(1, 0, 0), @(@(0.5, -0.5, 0.5), @(0.5, -0.5, -0.5), @(0.5, 0.5, -0.5), @(0.5, 0.5, 0.5))),
    @(@(-1, 0, 0), @(@(-0.5, -0.5, -0.5), @(-0.5, -0.5, 0.5), @(-0.5, 0.5, 0.5), @(-0.5, 0.5, -0.5))),
    @(@(0, 0, 1), @(@(-0.5, -0.5, 0.5), @(0.5, -0.5, 0.5), @(0.5, 0.5, 0.5), @(-0.5, 0.5, 0.5))),
    @(@(0, 0, -1), @(@(0.5, -0.5, -0.5), @(-0.5, -0.5, -0.5), @(-0.5, 0.5, -0.5), @(0.5, 0.5, -0.5))),
    @(@(0, 1, 0), @(@(-0.5, 0.5, 0.5), @(0.5, 0.5, 0.5), @(0.5, 0.5, -0.5), @(-0.5, 0.5, -0.5))),
    @(@(0, -1, 0), @(@(-0.5, -0.5, -0.5), @(0.5, -0.5, -0.5), @(0.5, -0.5, 0.5), @(-0.5, -0.5, 0.5))))
$corner = @(@(0, 1), @(1, 1), @(1, 0), @(0, 0))
function New-Gltf([bool]$imagesInBuffer, [string]$bufferUri) {
    $bin = New-Object System.IO.MemoryStream
    $views = New-Object System.Collections.Generic.List[string]; $accessors = New-Object System.Collections.Generic.List[string]
    function View([byte[]]$data) { while ($bin.Length % 4 -ne 0) { $bin.WriteByte(0) }; $views.Add("{""buffer"":0,""byteOffset"":$($bin.Length),""byteLength"":$($data.Length)}"); $bin.Write($data, 0, $data.Length); return $views.Count - 1 }
    function Floats($list) { $o = New-Object System.Collections.Generic.List[byte]; foreach ($f in $list) { $o.AddRange([BitConverter]::GetBytes([single]$f)) }; return ,$o.ToArray() }
    $prims = @()
    foreach ($m in 0, 1) {
        $set = if ($m -eq 0) { $sides[0..3] } else { $sides[4..5] }
        $pos = @(); $nrm = @(); $uv = @(); $idx = New-Object System.Collections.Generic.List[byte]; $nv = 0
        foreach ($side in $set) {
            $b0 = $nv
            for ($k = 0; $k -lt 4; $k++) { $pos += $side[1][$k]; $nrm += $side[0]; $uv += $corner[$k]; $nv++ }
            foreach ($i in @($b0, ($b0 + 1), ($b0 + 2), $b0, ($b0 + 2), ($b0 + 3))) { $idx.AddRange([BitConverter]::GetBytes([uint16]$i)) }
        }
        $vp = View (Floats $pos); $vn = View (Floats $nrm); $vu = View (Floats $uv); $vi = View $idx.ToArray()
        $accessors.Add("{""bufferView"":$vp,""componentType"":5126,""count"":$nv,""type"":""VEC3"",""min"":[-0.5,-0.5,-0.5],""max"":[0.5,0.5,0.5]}")
        $accessors.Add("{""bufferView"":$vn,""componentType"":5126,""count"":$nv,""type"":""VEC3""}")
        $accessors.Add("{""bufferView"":$vu,""componentType"":5126,""count"":$nv,""type"":""VEC2""}")
        $accessors.Add("{""bufferView"":$vi,""componentType"":5123,""count"":$($idx.Count / 2),""type"":""SCALAR""}")
        $a = $m * 4
        $prims += "{""attributes"":{""POSITION"":$a,""NORMAL"":$($a + 1),""TEXCOORD_0"":$($a + 2)},""indices"":$($a + 3),""material"":$m}"
    }
    if ($imagesInBuffer) { $i0 = View $red; $i1 = View $blue; $images = "[{""bufferView"":$i0,""mimeType"":""image/png""},{""bufferView"":$i1,""mimeType"":""image/png""}]" }
    else { $images = "[{""uri"":""red.png""},{""uri"":""blue.png""}]" }
    $buffer = if ($bufferUri) { "{""byteLength"":$($bin.Length),""uri"":""$bufferUri""}" } else { "{""byteLength"":$($bin.Length)}" }
    $json = "{""asset"":{""version"":""2.0""},""scene"":0,""scenes"":[{""nodes"":[0]}],""nodes"":[{""mesh"":0,""name"":""box""}],""meshes"":[{""primitives"":[$($prims -join ',')]}]," +
        """materials"":[{""name"":""sides"",""pbrMetallicRoughness"":{""baseColorTexture"":{""index"":0}}},{""name"":""ends"",""pbrMetallicRoughness"":{""baseColorTexture"":{""index"":1}}}]," +
        """textures"":[{""source"":0},{""source"":1}],""images"":$images,""accessors"":[$($accessors -join ',')],""bufferViews"":[$($views -join ',')],""buffers"":[$buffer]}"
    return @{ Bin = $bin.ToArray(); Json = $json }
}
function Save-Glb([string]$path) {
    $g = New-Gltf $true $null
    $j = [System.Text.Encoding]::UTF8.GetBytes($g.Json); $jl = ($j.Length + 3) -band -4; $bl = ($g.Bin.Length + 3) -band -4
    $o = New-Object System.IO.MemoryStream
    function U32([uint32]$v) { $b = [BitConverter]::GetBytes($v); $o.Write($b, 0, 4) }
    U32 0x46546C67; U32 2; U32 (12 + 8 + $jl + 8 + $bl)
    U32 $jl; U32 0x4E4F534A; $o.Write($j, 0, $j.Length); for ($i = $j.Length; $i -lt $jl; $i++) { $o.WriteByte(0x20) }
    U32 $bl; U32 0x004E4942; $o.Write($g.Bin, 0, $g.Bin.Length); for ($i = $g.Bin.Length; $i -lt $bl; $i++) { $o.WriteByte(0) }
    [System.IO.File]::WriteAllBytes($path, $o.ToArray())
}
$single = Join-Path $models "nv-model-$stamp-box.glb"; Save-Glb $single
$bulkGlb = Join-Path $models "nv-model-$stamp-b1.glb"; Save-Glb $bulkGlb
$g = New-Gltf $false "nv-model-$stamp-b2.bin"
[System.IO.File]::WriteAllBytes((Join-Path $models "nv-model-$stamp-b2.bin"), $g.Bin)
[System.IO.File]::WriteAllText((Join-Path $models "nv-model-$stamp-b2.gltf"), $g.Json)
[System.IO.File]::WriteAllBytes((Join-Path $models "red.png"), $red); [System.IO.File]::WriteAllBytes((Join-Path $models "blue.png"), $blue)
$bulkGltf = Join-Path $models "nv-model-$stamp-b2.gltf"
$bulkDae = Join-Path $models "nv-model-$stamp-b3.dae"
[System.IO.File]::WriteAllText($bulkDae, @"
<?xml version="1.0" encoding="utf-8"?>
<COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
  <asset><unit name="meter" meter="1"/><up_axis>Z_UP</up_axis></asset>
  <library_images><image id="img"><init_from>red.png</init_from></image></library_images>
  <library_effects><effect id="fx"><profile_COMMON>
    <newparam sid="surf"><surface type="2D"><init_from>img</init_from></surface></newparam>
    <newparam sid="samp"><sampler2D><source>surf</source></sampler2D></newparam>
    <technique sid="common"><lambert><diffuse><texture texture="samp" texcoord="UV"/></diffuse></lambert></technique>
  </profile_COMMON></effect></library_effects>
  <library_materials><material id="mat"><instance_effect url="#fx"/></material></library_materials>
  <library_geometries><geometry id="geo"><mesh>
    <source id="pos"><float_array id="pa" count="12">0 0 0 1 0 0 1 1 0 0 1 0</float_array><technique_common><accessor source="#pa" count="4" stride="3"/></technique_common></source>
    <source id="uv"><float_array id="ua" count="8">0 0 1 0 1 1 0 1</float_array><technique_common><accessor source="#ua" count="4" stride="2"/></technique_common></source>
    <vertices id="v"><input semantic="POSITION" source="#pos"/></vertices>
    <polylist material="M" count="1"><input semantic="VERTEX" source="#v" offset="0"/><input semantic="TEXCOORD" source="#uv" offset="1"/><vcount>4</vcount><p>0 0 1 1 2 2 3 3</p></polylist>
  </mesh></geometry></library_geometries>
  <library_visual_scenes><visual_scene id="s"><node name="square"><instance_geometry url="#geo"><bind_material><technique_common><instance_material symbol="M" target="#mat"/></technique_common></bind_material></instance_geometry></node></visual_scene></library_visual_scenes>
  <scene><instance_visual_scene url="#s"/></scene>
</COLLADA>
"@)
$broken = Join-Path $models "nv-model-$stamp-broken.glb"
[System.IO.File]::WriteAllBytes($broken, [byte[]](0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0, 99, 0, 0, 0, 1, 2, 3))

# ---------------- the run ----------------
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
Write-Output "LIVE MODEL RUN STARTING - no grid restarts, measuring, or other clients on account $Account until it is DONE"
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
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-52} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; rez = [bool]$Rez; checks = $null; completed = $false; steps = [ordered]@{} }
# the library's blank and default textures: a face showing one of these did not get the model's texture
$blank = @("00000000-0000-0000-0000-000000000000", "5748decc-f629-461c-9a36-a35a221fe21f", "89556747-24cb-43ed-920b-47caed15465f")

$login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
$loginUri = $null
Note "login requested: $($login.Status)"
$o = Wait-Until { $x = Parse (Invoke-Raw $port "observe" $token).Body; if ($x -and $x.stage -eq "InWorld" -and $x.self) { $x } } 150
if ($o) {
    Note "in world"
    $st = Wait-Until { $b = Build @{ action = "state" }; if ($b -and $null -ne $b.placing) { $b } } 30
    Start-Sleep -Seconds 3

    if ($TakeIds.Count -gt 0) {
        # only the objects a run left: each taken back
        foreach ($id in $TakeIds) {
            $present = Wait-Until { $b = Build @{ action = "state" }; @($b.own_objects | Where-Object { $_.id -eq $id }) | Select-Object -First 1 } 20
            $t = if ($present) { Take-Back $id } else { @{ taken = $false; present = $false } }
            $report.steps["take_" + $id] = $t
            Check "take back $id" ([bool]$t.taken) ("present {0}, enabled {1}, chosen {2}, gone {3}" -f [bool]$present, $t.take_enabled, $t.chosen, $t.gone)
        }
        $SingleOnly = $true; $skipModel = $true
    }
    if (-not $skipModel) {
    # 1 one model with its textures
    $name1 = [System.IO.Path]::GetFileNameWithoutExtension($single)
    $opened = Build @{ action = "upload_model"; text = $single; expect = "textures" }
    $priced = Wait-Until { $b = Build @{ action = "state" }; $t = @($b.model); if ($t.Count -ge 2 -and $t[-2] -notmatch "asking" -and $t[-2] -ne "") { $b } } 60
    $row = if ($priced) { @($priced.model)[0] } else { "" }
    $feeLine = if ($priced) { @($priced.model)[-2] } else { "" }
    $button = if ($priced) { @($priced.model)[-1] } else { "" }
    $pressed = Build @{ action = "press_model_upload"; text = "model" }
    $done1 = Wait-Until { $b = Build @{ action = "state" }; $s = @($b.model_states); if ($s.Count -ge 1 -and @($s | Where-Object { $_ -match "^(Done|Failed)" }).Count -eq $s.Count) { $b } } 120
    $inv1 = Wait-Until { $b = Build @{ action = "state"; expect = $name1 }; if ($b.object_named -ge 1) { $b } } 30
    $item = if ($inv1) { $before = @($st.object_items); @($inv1.object_items | Where-Object { $before -notcontains $_ }) | Select-Object -First 1 } else { $null }
    $report.steps.model = @{ row = $row; fee = $feeLine; button = $button; pressed = $pressed.pressed; states = @($done1.model_states); named = $inv1.object_named }
    Check "1 model: fee and land impact shown, completes, in Objects" ([bool]$pressed.pressed -and $row -match "Land impact" -and @($done1.model_states) -contains "Done" -and $inv1.object_named -ge 1) ("row '{0}', fee '{1}', states {2}, in Objects {3}" -f $row, $feeLine, (@($done1.model_states) -join ","), $inv1.object_named)

    if ($Rez) {
        # 2 rez it in front of the avatar and read its faces
        $me = (Parse (Invoke-Raw $port "observe" $token).Body).self.region_position
        $spot = @{ x = [double]$me[0] + 3.0; y = [double]$me[1]; z = [double]$me[2] - 0.9 }
        $beforeObj = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
        $rezA = $null; $obj = $null; $faces = @()
        if ($item) {
            $rezA = Build @{ action = "rez"; id = [string]$item; x = $spot.x; y = $spot.y; z = $spot.z }
            $obj = Wait-Until { $b = Build @{ action = "state" }; @($b.own_objects | Where-Object { $beforeObj -notcontains $_.id }) | Select-Object -First 1 } 30
        }
        if ($obj) {
            $prim = Wait-Until { $b = Build @{ action = "state" }; $pp = @($b.own_prims | Where-Object { $_.id -eq $obj.id }) | Select-Object -First 1; if ($pp -and @($pp.faces)[0] -ne $blank[0]) { $pp } } 15
            if (-not $prim) { $prim = @((Build @{ action = "state" }).own_prims | Where-Object { $_.id -eq $obj.id }) | Select-Object -First 1 }
            $faces = @($prim.faces | ForEach-Object { [string]$_ })
        }
        $sidesTex = if ($faces.Count -ge 2) { $faces[0] } else { "" }; $endsTex = if ($faces.Count -ge 2) { $faces[1] } else { "" }
        $textured = $faces.Count -ge 2 -and $blank -notcontains $sidesTex -and $blank -notcontains $endsTex -and $sidesTex -ne $endsTex
        $report.steps.rez = @{ answer = $rezA; object = $obj; size = if ($obj) { @($obj.sx, $obj.sy, $obj.sz) } else { $null }; faces = $faces; mesh = $prim.mesh }
        Check "2 rez: the model is in the world, sides and ends textured" ([bool]$obj -and $textured) ("object {0}, size {1}x{2}x{3}, face 0 {4}, face 1 {5}, faces 2-7 {6}" -f [bool]$obj, $obj.sx, $obj.sy, $obj.sz, $sidesTex, $endsTex, (($faces | Select-Object -Skip 2) -join ","))

        # 3 take it back
        $taken = $false
        if ($obj) {
            $t = Take-Back ([string]$obj.id)
            $taken = $t.taken
            $report.steps.take = $t
        }
        Check "3 take: the model leaves the world, back in Objects" $taken ("taken {0}" -f $taken)
    }

    }
    if (-not $SingleOnly) {
        # 4 three models at once, and a broken file
        $files = @($bulkGlb, $bulkGltf, $bulkDae, $broken)
        $opened = Build @{ action = "upload_bulk_models"; text = ($files -join "|"); expect = "textures" }
        $refusedRows = @(@($opened.texts) | Where-Object { $_ -match "could not be read" }).Count
        $priced = Wait-Until { $b = Build @{ action = "state" }; $t = @($b.bulk_models); if ($t.Count -ge 2 -and $t[-2] -notmatch "asking" -and $t[-2] -ne "") { $b } } 90
        $pressed = Build @{ action = "press_model_upload"; text = "bulk" }
        $doneB = Wait-Until { $b = Build @{ action = "state" }; $s = @($b.bulk_model_states); if ($s.Count -ge 4 -and @($s | Where-Object { $_ -match "^(Done|Failed)" }).Count -eq $s.Count) { $b } } 180
        $named = 0
        foreach ($f in $files[0..2]) { $n = [System.IO.Path]::GetFileNameWithoutExtension($f); $hit = Wait-Until { $b = Build @{ action = "state"; expect = $n }; if ($b.object_named -ge 1) { $b } } 30; if ($hit) { $named++ } }
        $report.steps.bulk = @{ rows = @($priced.bulk_models); refused_rows = $refusedRows; states = @($doneB.bulk_model_states); in_objects = $named; total = @($priced.bulk_models)[-2] }
        Check "4 bulk: three models complete, a broken file is refused" ([bool]$pressed.pressed -and @(@($doneB.bulk_model_states) | Where-Object { $_ -eq "Done" }).Count -eq 3 -and $named -eq 3 -and $refusedRows -eq 1) ("states {0}, in Objects {1} of 3, refused rows {2}, total '{3}'" -f (@($doneB.bulk_model_states) -join ","), $named, $refusedRows, @($priced.bulk_models)[-2])
    }
    $report.completed = $true
}
else { Note "not in the world within 150 s" }
$report.checks = $checks
$logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
$afterLogout = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($afterLogout.ended)"
[void](Invoke-Raw $port "quit" $token)
$p.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($p.HasExited) { $p.ExitCode } else { 'still running' })"
$report.logged_out = [bool]$afterLogout.ended
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "model-report.json") -Encoding utf8
Write-Output "LIVE MODEL RUN DONE - other work may run again"
if (@($checks | Where-Object { -not $_.pass }).Count -gt 0 -or -not $report.completed) { exit 1 }
