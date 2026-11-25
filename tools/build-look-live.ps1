# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# build-look-live.ps1 - the live check of the Build window's buttons and guide with the operating system's own mouse and keyboard, then of
# the Mirror notice for a mesh part. Starts the development export in an ordinary window, logs a test avatar in, and:
#   1 minimize   real clicks: Minimize, then the Restore button standing in the same place, then Minimize again and one click on the bar
#   2 guide      Snap to objects ticked, a real drag of box A's Move X arrow towards box B; while the button is held the viewer's window is
#                copied off the screen and the guide's pink pixels counted; after the release (control) they are gone
#   3 Ctrl+W     a real Ctrl+W closes the Build window
#   4 mirror     a generated mesh is uploaded, rezzed and linked under a box; Mirror shows the notice, and a click on its OK closes it.
#                Control: Mirror on a linkset of box prims alone shows no notice
# The real pointer is used only while the viewer's window is in front and under the pointer: otherwise the run stops without pressing.
# Takes back everything it rezzed by id, proven by re-reading the region, and logs out.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\build-look-live.ps1 -Sandbox <new folder> -ShotsDir <existing folder>
#     -Start <start location> [-Exe <development export>]
#
# Run it under an avatar claim, a live-test claim naming its region, a live-login claim and the pointer claim.
# Output: build-look-report.json in the sandbox. Exit: 0 all checks passed, 1 a check failed, 3 blocked, 5 the viewer or login did not come up.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$ShotsDir,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = $(if ($env:CLAIM_AVATAR) { $env:CLAIM_AVATAR } elseif ($env:NV_LIVE_ACCOUNT) { $env:NV_LIVE_ACCOUNT } else { "viewer1" }),
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [Parameter(Mandatory)][string]$Start,
    [string]$Exe = "",
    [int]$StartSeconds = 60
)
# the accounts file, the grid address and the account come from the run's settings (live.env) and the avatar claim, never from an argument
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$script:port = $null; $script:token = $null; $script:proc = $null
trap {
    Write-Output ("ERROR " + $_.Exception.GetType().Name + ": " + $_.Exception.Message); Write-Output $_.InvocationInfo.PositionMessage
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
function Interact([hashtable]$b) { Api "interact" $b }
function Build([hashtable]$b) { Api "build" $b }
function Edit([hashtable]$b) { Api "edit" $b }
function Shot([string]$name) { [void](Invoke-Raw $script:port "screenshot" $script:token (@{ dir = $ShotsDir; name = $name } | ConvertTo-Json -Compress)) }
. (Join-Path $PSScriptRoot "live-takeback.ps1")
function Wait-Until([scriptblock]$cond, [int]$seconds) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; return $null }

New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$checks = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$pass, [string]$detail) { $checks.Add([ordered]@{ name = $name; pass = $pass; detail = $detail }); Note ("{0,-60} {1} {2}" -f $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail) }
$report = [ordered]@{ account = $Account; checks = $null; completed = $false }
function Finish([int]$code) {
    $tb = Invoke-TakeBack -Seconds 40
    $report.take_back = $tb
    [void](Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'); [void](Invoke-Raw $script:port "quit" $script:token)
    $script:proc.WaitForExit(60000) | Out-Null
    $report.checks = $checks
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "build-look-report.json") -Encoding UTF8
    exit $code
}

# ---- the viewer and the login ----
$appdata = Join-Path $Sandbox "appdata"; $local = Join-Path $Sandbox "localappdata"; $log = Join-Path $Sandbox "run.log"
$script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "600", "-FullSizeWindow", "-HangSnapshotSeconds", "0",
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

function OwnObj([uint32]$local) { @(@((Build @{ action = "state" }).own_objects) | Where-Object { [uint32]$_.local -eq $local }) | Select-Object -First 1 }
function Near($a, $b, [double]$tol) { [math]::Abs([double]$a - [double]$b) -le $tol }
function Press([string]$k, [bool]$ctrl = $false, [bool]$shift = $false) {
    [void](In @{ kind = "key"; key = $k; down = $true; shift = $shift; alt = $false; ctrl = $ctrl })
    [void](In @{ kind = "key"; key = $k; down = $false; shift = $shift; alt = $false; ctrl = $ctrl })
}
function EditUntil([scriptblock]$test) { $r = Wait-Until { $s = Edit @{ action = "state" }; if ($s -and (& $test $s)) { $s } } 5; if ($r) { $r } else { Edit @{ action = "state" } } }
function NoFocus() { [void](In @{ kind = "release_focus" }) }
# the pointer, always through the engine's input path
function Mouse([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$shift = $false) { In @{ kind = "mouse"; button = $button; down = $down; shift = $shift; ctrl = $false; alt = $false; x = $x; y = $y } }
function MoveTo([double]$x, [double]$y) { Mouse "none" $false $x $y }
function ClickAt([double]$x, [double]$y, [bool]$shift = $false) { [void](MoveTo $x $y); Start-Sleep -Milliseconds 120; [void](Mouse "left" $true $x $y $shift); Start-Sleep -Milliseconds 120; [void](Mouse "left" $false $x $y $shift); Start-Sleep -Milliseconds 300 }
function ScreenOf($id) { $h = Interact @{ action = "hover"; id = [string]$id }; if ($h -and $null -ne $h.screen_x) { @([double]$h.screen_x, [double]$h.screen_y) } else { $null } }
# a control of the Build window: the first call scrolls it into view, the second reads where it stands once the scroll is drawn
function CtlPoint([string]$name) { [void](Edit @{ action = "control"; text = $name }); Start-Sleep -Milliseconds 300; $d = (Edit @{ action = "control"; text = $name }).done; if ($d) { @([double]$d[0], [double]$d[1]) } else { $null } }
function ClickCtl([string]$name) { $p = CtlPoint $name; if ($p) { ClickAt $p[0] $p[1]; $true } else { Note "not shown: $name"; $false } }
function Mouse2([string]$button, [bool]$down, [double]$x, [double]$y, [bool]$alt) { In @{ kind = "mouse"; button = $button; down = $down; shift = $false; ctrl = $false; alt = $alt; x = $x; y = $y } }
function State() { Edit @{ action = "state" } }
function OffGrid([double]$v) { [math]::Abs($v - [math]::Round($v * 2) / 2) }


$models = Join-Path $Sandbox "models"
New-Item -ItemType Directory -Force -Path $models | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString("MMddHHmmss")
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

# ---- the operating system's mouse and keyboard ----
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class NvReal {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    // the pink of the Snap to objects guide, counted in a copy of the screen
    public static int Pink(System.Drawing.Bitmap b) {
        var d = b.LockBits(new System.Drawing.Rectangle(0, 0, b.Width, b.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var px = new byte[d.Stride * d.Height]; Marshal.Copy(d.Scan0, px, 0, px.Length); b.UnlockBits(d);
        int n = 0; for (int i = 0; i < px.Length; i += 4) if (px[i] > 230 && px[i + 2] > 230 && px[i + 1] > 40 && px[i + 1] < 175) n++;
        return n;
    }
}
"@ -ReferencedAssemblies System.Drawing
[void][NvReal]::SetProcessDPIAware()
$vp = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "Reverie.exe" -and $_.CommandLine -match "--automation" })
if ($vp.Count -ne 1) { Note "expected one viewer process, found $($vp.Count)"; Finish 1 }
$script:viewerPid = [uint32]$vp[0].ProcessId
$script:hwnd = (Get-Process -Id $script:viewerPid).MainWindowHandle
function PidOf([IntPtr]$h) { $p = [uint32]0; [void][NvReal]::GetWindowThreadProcessId($h, [ref]$p); $p }
function Front() {
    if ((PidOf ([NvReal]::GetForegroundWindow())) -ne $script:viewerPid) {
        [NvReal]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [NvReal]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)   # an Alt tap lets this process hand the foreground over
        [void][NvReal]::SetForegroundWindow($script:hwnd); Start-Sleep -Milliseconds 500
    }
    (PidOf ([NvReal]::GetForegroundWindow())) -eq $script:viewerPid
}
function ClientSize() { $r = New-Object NvReal+Rect; [void][NvReal]::GetClientRect($script:hwnd, [ref]$r); @($r.Right, $r.Bottom) }
function ClientOrigin() { $p = New-Object NvReal+Point; [void][NvReal]::ClientToScreen($script:hwnd, [ref]$p); @($p.X, $p.Y) }
# a point of the viewer's picture (what the automation gives) as a point of the screen
function ToScreen([double]$x, [double]$y) { $o = ClientOrigin; @([int][math]::Round($o[0] + $x * $script:scaleX), [int][math]::Round($o[1] + $y * $script:scaleY)) }
function RealMove([double]$x, [double]$y) { $s = ToScreen $x $y; [void][NvReal]::SetCursorPos($s[0], $s[1]); [NvReal]::mouse_event(1, 0, 0, 0, [UIntPtr]::Zero) }
function Assert-Ours([double]$x, [double]$y) {
    $s = ToScreen $x $y; $p = New-Object NvReal+Point; $p.X = $s[0]; $p.Y = $s[1]
    $under = PidOf ([NvReal]::GetAncestor([NvReal]::WindowFromPoint($p), 2))
    if (-not (Front) -or $under -ne $script:viewerPid) { throw "the viewer's window is not in front and under the pointer: nothing pressed" }
}
function RealDown([double]$x, [double]$y) { RealMove $x $y; Start-Sleep -Milliseconds 150; Assert-Ours $x $y; [NvReal]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero) }
function RealUp() { [NvReal]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
function RealClick([double]$x, [double]$y) { RealDown $x $y; Start-Sleep -Milliseconds 120; RealUp; Start-Sleep -Milliseconds 600 }
function Grab([string]$name) {
    $c = ClientSize; $o = ClientOrigin
    $bmp = New-Object System.Drawing.Bitmap ([int]$c[0]), ([int]$c[1]), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$o[0], [int]$o[1], 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save((Join-Path $ShotsDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $n = [NvReal]::Pink($bmp); $bmp.Dispose(); $n
}
function Find([string]$name) { Api "ui_sounds" @{ action = "find"; name = $name } }

if (-not (Front)) { Note "the viewer's window could not be brought to the front: stopping"; Finish 3 }
# the automation's points are in the interface's own units, which an interface size or a dense screen makes larger than a pixel
Press "B" $true; Start-Sleep -Milliseconds 1200
$view = Find "floater_build"
if (-not $view.view_w) { Note "the view's size is not reported: stopping"; Finish 1 }
$cs = ClientSize; $script:scaleX = $cs[0] / $view.view_w; $script:scaleY = $cs[1] / $view.view_h
Note ("window client {0}x{1}, view {2}x{3}, view to screen {4:N3} x {5:N3}" -f $cs[0], $cs[1], $view.view_w, $view.view_h, $script:scaleX, $script:scaleY)
Press "B" $true; Start-Sleep -Milliseconds 800

# ---- two 1 m boxes in front of the avatar, along x ----
$me = @((Obs).self.region_position)
$baseX = [math]::Floor([double]$me[0]) - 1.0; $y = [double]$me[1] - 7.0; $z = [double]$me[2] + 2.1
$ids = @(); $locals = @()
function New-Box([double]$bx, [double]$by) {
    [void](Interact @{ action = "aim"; x = $bx; y = $by; z = ([double]$me[2] - 0.9); distance = 7 }); Start-Sleep -Milliseconds 1200
    $before = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
    [void](Build @{ action = "create"; x = $bx; y = $by; z = ([double]$me[2] - 0.9) })
    $box = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $before -notcontains $_.id }) | Select-Object -First 1 } 20
    if (-not $box) { Note "a box was not made: stopping"; Finish 1 }
    Add-Rezzed ([uint32]$box.local)
    $box
}
foreach ($k in 0, 1) { $box = New-Box ($baseX + 3 * $k) $y; $locals += [uint32]$box.local; $ids += [string]$box.id }
$A = $locals[0]; $B = $locals[1]
NoFocus
if (-not (State).build_mode) { Press "B" $true }
[void](EditUntil { param($s) $s.build_mode })
Press "3" $true
[void](EditUntil { param($s) $s.tool -eq "edit" })
function Place([uint32]$local, [double]$px, [double]$py, [double]$pz) {
    [void](Edit @{ action = "select"; text = "$local" })
    [void](Edit @{ action = "size"; x = 1; y = 1; z = 1 })
    [void](Edit @{ action = "rotation"; x = 0; y = 0; z = 0 })
    [void](Edit @{ action = "position"; x = $px; y = $py; z = $pz })
}
$flush = $baseX + 1.635; $bx = $flush + 1.0
Place $A $baseX $y $z
Place $B $bx ($y + 0.3) $z
$ready = Wait-Until { $a = OwnObj $A; $b = OwnObj $B; if ($a -and $b -and (Near $a.sx 1 0.001) -and (Near $a.x $baseX 0.002) -and (Near $b.x $bx 0.002)) { $true } } 20
Check "0 two upright 1 m boxes stand in the region" ([bool]$ready) (@((OwnObj $A), (OwnObj $B)) | ConvertTo-Json -Compress)
if (-not $ready) { Finish 1 }

# ---- 1. minimize and restore with the real mouse ----
$whole = Find "floater_build"
$min = Find "minimize"
RealClick $min.x $min.y
$bar = Find "floater_build"; $res = Find "minimize"
[void](Grab "minimized-restore-button")
Check "1a a real click on Minimize leaves the bar, with Restore where Minimize was" ($whole.h -gt 100 -and $bar.h -gt 0 -and $bar.h -lt 40 -and $res.tip -eq "Restore" -and $min.tip -eq "Minimize" -and (Near $res.x $min.x 1.5) -and (Near $res.y $min.y 1.5)) ("height {0} -> {1}; tip '{2}' -> '{3}'; button {4},{5} -> {6},{7}" -f $whole.h, $bar.h, $min.tip, $res.tip, $min.x, $min.y, $res.x, $res.y)
RealClick $res.x $res.y
$back = Find "floater_build"; $again = Find "minimize"
Check "1b a real click on Restore brings the window back at its size and place" ((Near $back.x $whole.x 1.5) -and (Near $back.y $whole.y 1.5) -and (Near $back.h $whole.h 1.5) -and $again.tip -eq "Minimize") ("{0},{1} h {2}; tip '{3}'" -f $back.x, $back.y, $back.h, $again.tip)
RealClick $again.x $again.y
$tb2 = Find "title_bar"
$barBefore = Find "floater_build"
RealClick ($tb2.x - 60) $tb2.y
$back2 = Find "floater_build"
Check "1c one real click on the minimized bar restores it" ($barBefore.h -gt 0 -and $barBefore.h -lt 40 -and (Near $back2.h $whole.h 1.5) -and (Near $back2.y $whole.y 1.5)) ("height {0} -> {1}" -f $barBefore.h, $back2.h)

# ---- 2. the guide during a real drag ----
$clicked = ClickCtl "snap_objects"
$s1 = EditUntil { param($s) $s.snap_objects -eq $true }
[void](Edit @{ action = "select"; text = "" })
[void](Interact @{ action = "aim"; x = ($baseX + 1.3); y = $y; z = $z; distance = 6 }); Start-Sleep -Milliseconds 1500
$p = ScreenOf $ids[0]
if (-not $p) { Check "2 box A is on screen" $false "not shown"; Finish 1 }
RealClick $p[0] $p[1]
$sel = EditUntil { param($s) @($s.selection).Count -eq 1 -and [uint32]@($s.selection)[0] -eq $A }
Check "2a the real pointer picks box A" ($s1.snap_objects -eq $true -and @($sel.selection).Count -eq 1 -and [uint32]@($sel.selection)[0] -eq $A) ("selection {0}" -f (@($sel.selection) -join ","))
Start-Sleep -Milliseconds 800
$atRest = Grab "drag-0-before"
$a0 = OwnObj $A; $b0 = OwnObj $B
$dx = ([double]$b0.x - 1.0) - 0.02 - [double]$a0.x
$pts = (Edit @{ action = "handle_points"; text = "axis:x+"; x = $dx; y = 0; z = 0 }).done
if (-not $pts -or $pts.hit -ne "axis") { Check "2 the Move X arrow is on screen" $false ($pts | ConvertTo-Json -Compress); Finish 1 }
$pa = $pts.from; $pb = $pts.to
RealDown $pa[0] $pa[1]
try {
    for ($i = 1; $i -le 12; $i++) { $t = $i / 12.0; RealMove ($pa[0] + ($pb[0] - $pa[0]) * $t) ($pa[1] + ($pb[1] - $pa[1]) * $t); Start-Sleep -Milliseconds 120 }
    Start-Sleep -Milliseconds 900
    $held = State
    $pinkHeld = Grab "drag-1-held-guide"
} finally { RealUp }
$moved = Wait-Until { $a = OwnObj $A; if ($a -and [math]::Abs([double]$a.x - [double]$a0.x) -gt 0.5) { $true } } 10
Start-Sleep -Milliseconds 1500
$a1 = OwnObj $A; $after = State
$pinkAfter = Grab "drag-2-released"
$off = [math]::Abs([double]$a1.x - ([double]$b0.x - 1.0))
Check "2b a real drag catches flush and the viewer holds a guide" ([bool]$held.dragging -and [int]$held.snap_lines -eq 4 -and [bool]$moved -and $off -le 0.001) ("guide lines {0}; off flush {1:N5} m" -f $held.snap_lines, $off)
Check "2c the guide's pink is in the window while held, and gone after release" ($pinkHeld -ge 400 -and $pinkHeld -gt 10 * ($pinkAfter + 1) -and [int]$after.snap_lines -eq 0) ("pink pixels: before {0}, held {1}, after {2}" -f $atRest, $pinkHeld, $pinkAfter)

# ---- 3. Ctrl+W with the real keyboard ----
$openBefore = Find "floater_build"
$title = Find "title_bar"
RealClick ($title.x - 60) $title.y   # the click puts the keyboard in the viewer with Build in front
if (-not (Front)) { throw "the viewer's window is not in front: no key sent" }
[NvReal]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
[NvReal]::keybd_event(0x57, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
[NvReal]::keybd_event(0x57, 0, 2, [UIntPtr]::Zero); [NvReal]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 800
$openAfter = Find "floater_build"
Check "3 a real Ctrl+W closes the Build window" ([bool]$openBefore.found -and -not [bool]$openAfter.found) ("shown before {0}, after {1}" -f $openBefore.found, $openAfter.found)

# ---- 4. the Mirror notice with a mesh part (the engine's input path) ----
function Open-Build() {
    if (-not (Find "floater_build").found) { NoFocus; Press "B" $true; Start-Sleep -Milliseconds 800 }
    if (-not (Find "floater_build").found) { NoFocus; Press "B" $true; Start-Sleep -Milliseconds 800 }
    [void](EditUntil { param($s) $s.build_mode })
    Press "3" $true
    [void](EditUntil { param($s) $s.tool -eq "edit" })
}
Open-Build
[void](Edit @{ action = "snap"; text = "off" })
function Link-Pair([uint32]$rootLocal, [uint32]$childLocal) {
    [void](Edit @{ action = "select"; text = "$childLocal,$rootLocal" })
    [void](Edit @{ action = "link" })
    [bool](Wait-Until { $o = Build @{ action = "state" }; $c = @($o.own_prims | Where-Object { [uint32]$_.local -eq $childLocal -and [uint32]$_.parent -eq $rootLocal }); if ($c.Count -eq 1) { $true } } 20)
}
function Mirror-ByPointer() {
    if (-not (CtlPoint "mirror")) { [void](ClickCtl "section_arrange"); Start-Sleep -Milliseconds 500 }
    ClickCtl "mirror"
}
# control: a linkset of box prims alone
$linked = Link-Pair $A $B
$posBefore = (OwnObj $A)
$pressed = Mirror-ByPointer
Start-Sleep -Seconds 3
$ctl = State
Check "4a control: Mirror on two box prims shows no notice" ($linked -and $pressed -and @($ctl.arrange.mirror_notice).Count -eq 0 -and -not (Find "mirror_notice_ok").found) ("linked {0}, pressed {1}, notice '{2}'" -f $linked, $pressed, (@($ctl.arrange.mirror_notice) -join ","))

$modelName = "nv-model-$stamp-notice"
$glb = Join-Path $models "$modelName.glb"; Save-Glb $glb
$itemsBefore = @((Build @{ action = "state" }).object_items)
[void](Build @{ action = "upload_model"; text = $glb; expect = "textures" })
$priced = Wait-Until { $b = Build @{ action = "state" }; $t = @($b.model); if ($t.Count -ge 2 -and $t[-2] -notmatch "asking" -and $t[-2] -ne "") { $b } } 60
$press = Build @{ action = "press_model_upload"; text = "model" }
$done = Wait-Until { $b = Build @{ action = "state" }; $s = @($b.model_states); if ($s.Count -ge 1 -and @($s | Where-Object { $_ -match "^(Done|Failed)" }).Count -eq $s.Count) { $b } } 120
$item = Wait-Until { $b = Build @{ action = "state"; expect = $modelName }; @($b.object_items | Where-Object { $itemsBefore -notcontains $_ }) | Select-Object -First 1 } 30
Check "4b the generated mesh is uploaded and in Objects" ([bool]$press.pressed -and @($done.model_states) -contains "Done" -and [bool]$item) ("states {0}" -f (@($done.model_states) -join ","))
if (-not $item) { Finish 1 }
[void](Api "ui_sounds" @{ action = "close_window"; name = "upload_model" })
$rootBox = New-Box ($baseX) ($y + 3)
$beforeObj = @((Build @{ action = "state" }).own_objects | ForEach-Object { $_.id })
[void](Build @{ action = "rez"; id = [string]$item; x = ($baseX + 2.0); y = ($y + 3); z = ([double]$me[2] - 0.9) })
$mesh = Wait-Until { @((Build @{ action = "state" }).own_objects | Where-Object { $beforeObj -notcontains $_.id }) | Select-Object -First 1 } 30
if (-not $mesh) { Check "4c the mesh is rezzed" $false "no new object"; Finish 1 }
Add-Rezzed ([uint32]$mesh.local)
Start-Sleep -Seconds 3
$meshPrim = @((Build @{ action = "state" }).own_prims | Where-Object { $_.id -eq $mesh.id }) | Select-Object -First 1
Open-Build
$linked = Link-Pair ([uint32]$rootBox.local) ([uint32]$mesh.local)
Check "4c the rezzed object is a mesh, linked under a box" ([bool]$meshPrim.mesh -and $linked) ("mesh {0}, linked {1}" -f $meshPrim.mesh, $linked)
[void](Edit @{ action = "select"; text = "$([uint32]$rootBox.local)" })
[void](Interact @{ action = "aim"; x = ($baseX + 1.0); y = ($y + 3); z = $z; distance = 8 }); Start-Sleep -Milliseconds 1200
$pressed = Mirror-ByPointer
$shown = Wait-Until { $s = State; if (@($s.arrange.mirror_notice).Count -ge 1) { $s } } 15
Start-Sleep -Milliseconds 600
$okAt = CtlPoint "mirror_notice_ok"
$okNode = Find "mirror_notice_ok"
Shot "mirror-notice-shown"; Start-Sleep -Milliseconds 800
Check "4d Mirror with a mesh part shows the notice and its OK" ($pressed -and [bool]$shown -and [bool]$okNode.found -and [bool]$okAt) ("notice '{0}', OK shown {1}" -f (@($shown.arrange.mirror_notice) -join ","), $okNode.found)
if ($okAt) { ClickAt $okAt[0] $okAt[1] }
Start-Sleep -Milliseconds 800
$closed = State; $okGone = Find "mirror_notice_ok"
Shot "mirror-notice-closed"; Start-Sleep -Milliseconds 800
Check "4e a click on OK closes the notice" ([bool]$okAt -and @($closed.arrange.mirror_notice).Count -eq 0 -and -not [bool]$okGone.found) ("notice lines {0}, OK shown {1}" -f @($closed.arrange.mirror_notice).Count, $okGone.found)
# the linksets are unlinked so each object is taken back by its own id
foreach ($r in @($A, [uint32]$rootBox.local)) { [void](Edit @{ action = "select"; text = "$r" }); [void](Edit @{ action = "unlink" }); Start-Sleep -Seconds 2 }
# ---- 5. close build mode, take everything back, log out ----
NoFocus; Press "B" $true
$tb = Invoke-TakeBack -Seconds 40
$report.take_back = $tb
Check "5 everything the run rezzed is taken back (re-read)" ([bool]$tb.ok) ("rezzed {0}, sent {1}, remaining {2}" -f @($tb.rezzed).Count, @($tb.sent).Count, @($tb.remaining).Count)
$logout = Invoke-Raw $script:port "session" $script:token '{"action":"logout"}'
$after = Parse $logout.Body
Note "logout: $($logout.Status) ended=$($after.ended)"
[void](Invoke-Raw $script:port "quit" $script:token)
$script:proc.WaitForExit(60000) | Out-Null
Note "viewer exit: $(if ($script:proc.HasExited) { $script:proc.ExitCode } else { 'still running' })"
$report.checks = $checks
$report.completed = $true
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Sandbox "build-look-report.json") -Encoding UTF8
$failed = @($checks | Where-Object { -not $_.pass }).Count
Note ("{0} of {1} checks passed" -f ($checks.Count - $failed), $checks.Count)
exit $(if ($failed -eq 0) { 0 } else { 1 })
