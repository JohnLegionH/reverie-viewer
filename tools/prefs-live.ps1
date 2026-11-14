# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# prefs-live.ps1 - the live check of the Preferences settings that change what a person sees. Two copies of the development export
# (automation on) log in to the same region, one account each, and the script changes settings on the first through the preferences
# command (a preview, as the window's controls do it) and checks what is in force and what the world does, step by step, each with its
# own pass condition and a PASS/FAIL line:
#   1 login           both accounts log in by region name and reach the world in the same region, each seeing the other
#   2 draw distance   the first account sets a low draw distance, then a high one: the objects it holds drop out, then return
#   3 clouds          clouds off, then on: the sky in the first account's view loses its clouds and gets them back
#   4 graphics        the Low preset, then Ultra: the values in force follow and the picture changes
#   5 chat            a new chat text size and a nearby-chat fade time: the second account's next line shows both
#   6 online status   "show my online status to friends" off: the second account lists the first as offline; on again: online
#   7 logout          both accounts log out and both viewers exit
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input or precondition is missing); 4 skipped (the person's own
# avatar is online or its state is unknown, or the lane claim was not granted): nothing was started.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\prefs-live.ps1 -Sandbox <new evidence folder>
#     [-Credentials <accounts file>] [-AccountA <alias>] [-AccountB <alias>] [-Region <name>] [-GridInfoUrl <url>]
#     [-OtherClientPattern <regex or none>] [-PresenceCheck <bash script or none>] [-Exe <development export>] [-DryRun]
#
# The inputs, the presence check and the claims are those of live-smoke.ps1 (NV_TEST_ACCOUNTS, NV_SMOKE_ACCOUNT_A and _B,
# NV_SMOKE_REGION or NV_LIVE_START, NV_GRID_INFO_URL, NV_OTHER_CLIENT_PATTERN, NV_PRESENCE_CHECK, NV_CLAIM_WRAPPER, NV_CLAIM_LANE,
# NV_CLAIM_ORDER, CLAIM_WAIT). Output: the sandbox folder (prefs.txt, prefs.json, the screenshots and each viewer's log). Messages are
# made of a random token, and only ids, counts and numbers are written, never message text or names.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$AccountA = $env:NV_SMOKE_ACCOUNT_A,
    [string]$AccountB = $env:NV_SMOKE_ACCOUNT_B,
    [string]$Region = $env:NV_SMOKE_REGION,
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$OtherClientPattern = $env:NV_OTHER_CLIENT_PATTERN,
    [string]$PresenceCheck = $env:NV_PRESENCE_CHECK,
    [string]$Exe = "",   # default: the development export, host\Viewer.Godot\build\dev\Reverie.console.exe
    [int]$StartSeconds = 120,
    [switch]$DryRun
)
# A live setting that is not set comes from the untracked local settings file (live-env.ps1).
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $OtherClientPattern) { $OtherClientPattern = & "$PSScriptRoot\live-env.ps1" NV_OTHER_CLIENT_PATTERN }
$ErrorActionPreference = "Stop"
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "host\Viewer.Godot\build\dev\Reverie.console.exe" }

function Find-Bash {
    foreach ($candidate in @((Get-Command bash.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
                             (Join-Path $env:ProgramFiles "Git\bin\bash.exe"), (Join-Path $env:ProgramFiles "Git\usr\bin\bash.exe"))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}
function ToUnixPath([string]$p) { $u = $p -replace '\\', '/'; if ($u -match '^([A-Za-z]):(.*)$') { $u = "/" + $Matches[1].ToLower() + $Matches[2] }; return $u }

# ---------------- inputs and preconditions (also the whole of -DryRun) ----------------
$problems = New-Object System.Collections.Generic.List[string]
if (-not $Region -and $(& "$PSScriptRoot\live-env.ps1" NV_LIVE_START) -match '^uri:([^&]+)') { $Region = $Matches[1] }
if (-not [System.IO.Path]::IsPathRooted($Sandbox)) { $problems.Add("-Sandbox must be an absolute path") }
elseif (Test-Path -LiteralPath $Sandbox) { $problems.Add("-Sandbox exists (a fresh evidence folder is required): $Sandbox") }
if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
if (-not $AccountA) { $problems.Add("-AccountA (or NV_SMOKE_ACCOUNT_A) is required") }
if (-not $AccountB) { $problems.Add("-AccountB (or NV_SMOKE_ACCOUNT_B) is required") }
if ($AccountA -and $AccountB -and $AccountA -eq $AccountB) { $problems.Add("-AccountA and -AccountB must be two different accounts") }
if (-not $Region) { $problems.Add("-Region (or NV_SMOKE_REGION, or NV_LIVE_START) is required") }
elseif ($Region -notmatch '^[A-Za-z0-9 _-]{1,64}$') { $problems.Add("-Region must be a plain region name") }
if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { $problems.Add("-Exe not found (run tools\s1.ps1 for the development export): $Exe") }
if (-not (Test-Path -LiteralPath $godot -PathType Leaf)) { $problems.Add("tools\godot.ps1 not found") }
if (-not $OtherClientPattern) { $problems.Add("-OtherClientPattern (or NV_OTHER_CLIENT_PATTERN) is required: a regex of other clients that may use the accounts, or none") }
if (-not $PresenceCheck) { $problems.Add("-PresenceCheck (or NV_PRESENCE_CHECK) is required: a script that prints online, offline or unknown for your own avatar, or none") }
elseif ($PresenceCheck -ne "none" -and -not (Test-Path -LiteralPath $PresenceCheck -PathType Leaf)) { $problems.Add("-PresenceCheck is not a file (the presence check must exist, or be none)") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }

# the person's own avatar must be offline; online or unknown skips the live run (nothing is asked, nothing waits)
if ($PresenceCheck -ne "none") {
    $bash = Find-Bash
    if (-not $bash) { Write-Output "BLOCKED: bash not found, so -PresenceCheck cannot run"; exit 3 }
    $word = ""
    # the check prints its word on stdout and its reason on stderr; under 'Stop' Windows PowerShell turns any stderr line of a
    # native command into an error, so the call runs with errors continuing and the word is read from stdout alone
    $ErrorActionPreference = "Continue"
    try { $word = (@(& $bash (ToUnixPath $PresenceCheck) 2>$null) | Where-Object { $_ -and "$_".Trim() -and $_ -isnot [System.Management.Automation.ErrorRecord] } | Select-Object -Last 1) } catch { $word = "" }
    $ErrorActionPreference = "Stop"
    $word = "$word".Trim().ToLowerInvariant()
    if ($word -ne "offline") { Write-Output "SKIPPED: the presence check said '$word' (online or unknown): the live run is skipped, nothing started"; exit 4 }
    Write-Output "presence: offline"
}

if ($OtherClientPattern -ne "none") {
    . (Join-Path $PSScriptRoot "other-clients.ps1")
    $agent = @(Find-OtherClients -Pattern $OtherClientPattern)   # by executable, never by command-line text
    if ($agent.Count -gt 0) { $problems.Add("another test client is running ($($agent.Count) process(es)): a test account may be in use") }
}
$loginUri = $null
try {
    $req = [System.Net.WebRequest]::Create($GridInfoUrl); $req.Timeout = 10000
    $resp = $req.GetResponse()
    try { $loginUri = ([xml](New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd()).gridinfo.login } finally { $resp.Close() }
} catch { }
if (-not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login address") }
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
$start = "uri:" + $Region + "&128&128&25"
Write-Output ("preconditions ok: region {0}, accounts {1} and {2}" -f $Region, $AccountA, $AccountB)

# ---------------- the lane claims: run this script again under them ----------------
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_PREFS_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "prefs-live") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials,
        "-AccountA", $AccountA, "-AccountB", $AccountB, "-Region", $Region, "-GridInfoUrl", $GridInfoUrl, "-OtherClientPattern", $OtherClientPattern,
        "-PresenceCheck", $PresenceCheck, "-Exe", $Exe, "-StartSeconds", "$StartSeconds")
    $cmd = @("bash", $wrapper) + $order + @("--resource", $AccountA, $lane, "avatar", "prefs live", "--",
            "bash", $wrapper) + $order + @("--resource", $AccountB, $lane, "avatar", "prefs live", "--",
            "bash", $wrapper) + $order + @($lane, "live-login", "prefs live", "--") + $inner
    if ($DryRun) { Write-Output ("would run under claims: " + ($cmd -join " ")); Write-Output "dry run: nothing started, nothing logged in"; exit 0 }
    $env:NV_PREFS_CLAIMED = "1"
    if (-not $env:CLAIM_WAIT) { $env:CLAIM_WAIT = "120" }
    $bash = Find-Bash
    if (-not $bash) { Write-Output "BLOCKED: bash not found, so the claim wrapper cannot run"; exit 3 }
    & $bash @($cmd[1..($cmd.Count - 1)])
    $code = $LASTEXITCODE
    if ($code -eq 125) { Write-Output "SKIPPED: a claim was not granted within CLAIM_WAIT minutes; nothing was started"; exit 4 }
    exit $code
}
if ($DryRun) { Write-Output "dry run: nothing started, nothing logged in"; exit 0 }

. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
if ($viewers.Count -gt 0) { Write-Output "BLOCKED: a viewer that can log in is already running ($($viewers.Count)): one account in one session at a time"; exit 3 }

# ---------------- helpers ----------------
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$startedAt = (Get-Date).ToUniversalTime()
$report = [ordered]@{ started_utc = $startedAt.ToString("o"); steps = @() }
$textLog = Join-Path $Sandbox "prefs.txt"
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
function Record([int]$n, [string]$name, [bool]$pass, [string]$detail) {
    $script:report.steps += [ordered]@{ step = $n; name = $name; pass = $pass; detail = $detail }
    Say ("STEP {0} {1}: {2}  {3}" -f $n, $name, $(if ($pass) { "PASS" } else { "FAIL" }), $detail)
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
function Api($v, [string]$command, $body) { $r = Invoke-Api $v $command $body; if ($r.Body) { try { $r.Body | ConvertFrom-Json } catch { $null } } else { $null } }
function State($v) { Api $v "session" '{"action":"state"}' }
function Status($v) { Api $v "teleport" @{ action = "status" } }
# the avatar's own place in its region, as the region last stated it (the observe answer's self block), as x, y, z
function Where-Am($v) {
    $o = Api $v "observe" '{}'
    $p = $o.self.region_position
    if ($p -and @($p).Count -ge 3) { return [pscustomobject]@{ x = [double]@($p)[0]; y = [double]@($p)[1]; z = [double]@($p)[2] } }
    return $null
}
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }
    return $null
}
function Cell($s) { if ($s -and $s.region) { "$($s.region.grid_x),$($s.region.grid_y)" } else { "" } }
function Dist2D($p, $q) { [math]::Round([math]::Sqrt([math]::Pow($p.x - $q.x, 2) + [math]::Pow($p.y - $q.y, 2)), 2) }
function Start-Viewer([string]$label) {
    $local = Join-Path $Sandbox "localappdata-$label"
    $appdata = Join-Path $Sandbox "appdata-$label"
    New-Item -ItemType Directory -Force -Path $appdata | Out-Null
    $vlog = Join-Path $Sandbox "viewer-$label.log"
    $vp = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "1500", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$vlog`"", "-ArgLine", "`"--rendering-driver vulkan -- --automation`"")
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
    if (-not $tf) { throw "viewer $label has no token file" }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    return @{ Label = $label; Proc = $vp; Log = $vlog; Port = [int]$j.port; Token = [string]$j.token }
}
function Login($v, [string]$account) {
    $l = Invoke-Api $v "session" @{ action = "login"; accounts_file = $Credentials; account = $account; login_uri = $loginUri; start = $start }
    if ($l.Status -ne 200) { return $false }
    $st = Wait-Until { $s = State $v; if ($s -and $s.has_session -and -not $s.ended -and $s.stage -eq "InWorld") { $s } } 150
    return [bool]$st
}
function Find-Chat($v, [string]$token) { Api $v "chat" @{ action = "find"; token = $token } }

$A = $null; $B = $null
$allPass = $true
$token = "PRF" + (Get-Random -Minimum 100000 -Maximum 999999)
$ids = @{}
$inWorld = $false

# one step: the body returns a hashtable with pass and detail; an error inside it is a failed step with the line it came from
function Step([int]$n, [string]$name, [scriptblock]$body) {
    $r = $null
    try { $r = & $body }
    catch { $r = @{ pass = $false; detail = ("error at line {0}: {1}" -f $_.InvocationInfo.ScriptLineNumber, $_.Exception.Message) } }
    Record $n $name ([bool]$r.pass) ([string]$r.detail)
    if (-not $r.pass) { $script:allPass = $false }
}

# the preferences command: action state|set|preset|save|cancel|reset, key, value, page
function Prefs($v, [string]$action, $more) {
    $body = @{ action = $action }
    if ($more) { foreach ($k in $more.Keys) { $body[$k] = $more[$k] } }
    Api $v "prefs" $body
}
function Objects($v) { [int](Api $v "observe" '{}').world.objects }
function Shot($v, [string]$name) { $s = Api $v "screenshot" @{ dir = $Sandbox; name = $name }; if ($s -and $s.file -and (Test-Path -LiteralPath $s.file)) { $s.file } else { $null } }
# the mean absolute difference, per colour channel (0 to 255), between two pictures over the rows from $from to $part of their height, sampling every third pixel
function Picture-Diff([string]$fileA, [string]$fileB, [double]$part, [double]$from = 0.0) {
    Add-Type -AssemblyName System.Drawing
    $x = New-Object System.Drawing.Bitmap $fileA
    $y = New-Object System.Drawing.Bitmap $fileB
    try {
        $w = [math]::Min($x.Width, $y.Width); $h = [int]([math]::Min($x.Height, $y.Height) * $part); $j0 = [int]([math]::Min($x.Height, $y.Height) * $from)
        $sum = 0.0; $n = 0
        for ($j = $j0; $j -lt $h; $j += 3) { for ($i = 0; $i -lt $w; $i += 3) {
            $p = $x.GetPixel($i, $j); $q = $y.GetPixel($i, $j)
            $sum += [math]::Abs($p.R - $q.R) + [math]::Abs($p.G - $q.G) + [math]::Abs($p.B - $q.B); $n += 3 } }
        return [math]::Round($sum / [math]::Max(1, $n), 3)
    } finally { $x.Dispose(); $y.Dispose() }
}

try {
    $A = Start-Viewer "A"
    $B = Start-Viewer "B"

    # 1 login
    Step 1 "login" {
        $okA = Login $A $AccountA
        $okB = Login $B $AccountB
        $script:inWorld = $okA -and $okB
        $detail = "A in world $okA, B in world $okB"
        $pass = $script:inWorld
        if ($script:inWorld) {
            $script:ids.A = [string](Api $A "friends" @{ action = "list" }).self
            $script:ids.B = [string](Api $B "friends" @{ action = "list" }).self
            $same = (Cell (Status $A)) -ne "" -and (Cell (Status $A)) -eq (Cell (Status $B))
            $seen = Wait-Until { $oa = Api $A "observe" '{}'; $ob = Api $B "observe" '{}'; if ($oa.world.other_avatars -ge 1 -and $ob.world.other_avatars -ge 1) { $true } } 60
            $detail += "; same region cell $same; each sees the other $([bool]$seen)"
            $pass = $same -and [bool]$seen
        }
        @{ pass = $pass; detail = $detail }
    }

    if ($inWorld) {
        # 2 draw distance: the first account's view loses what lies beyond a low distance and gets it back; the objects it holds are counted too
        Step 2 "draw distance" {
            [void](Api $A "orbit" @{ yaw_deg = 0; pitch_deg = -5; zoom = 2.0 })
            Start-Sleep -Seconds 3
            $before = Objects $A
            $baseline = Prefs $A "state" $null
            $shotHigh = Shot $A "draw-1024-before"
            $low = Prefs $A "set" @{ key = "draw_distance"; value = 64 }; Start-Sleep -Seconds 4
            $shotLow = Shot $A "draw-64"
            $atLow = Objects $A
            $high = Prefs $A "set" @{ key = "draw_distance"; value = 1024 }; Start-Sleep -Seconds 4
            $shotBack = Shot $A "draw-1024-after"
            $after = Objects $A
            if (-not ($shotHigh -and $shotLow -and $shotBack)) { return @{ pass = $false; detail = "a screenshot was not made" } }
            # the band of the view where the horizon and the far land are (the sky above it and the people below it move with the clouds and the animations)
            $dropped = Picture-Diff $shotHigh $shotLow 0.58 0.42
            $returned = Picture-Diff $shotHigh $shotBack 0.58 0.42
            $inForce = "in force at 64 m: distance $($low.in_force.draw_distance), camera far $($low.in_force.camera_far); at 1024 m: distance $($high.in_force.draw_distance), camera far $($high.in_force.camera_far); before: camera far $($baseline.in_force.camera_far)"
            @{ pass = ([int]$low.in_force.draw_distance -eq 64 -and [int]$high.in_force.draw_distance -eq 1024 -and [double]$low.in_force.camera_far -eq 64 -and [double]$high.in_force.camera_far -eq [double]$baseline.in_force.camera_far -and $dropped -gt 0.5 -and $returned -lt ($dropped / 2)); detail = "$inForce; horizon band difference at 64 m $dropped, back at 1024 m $returned (mean level per channel); objects held: $before, $atLow at 64 m, $after back" }
        }

        # 3 clouds: look up at the sky, clouds off and on and off; the sky must change with the switch and not with the time between two off pictures
        Step 3 "clouds" {
            [void](Api $A "orbit" @{ yaw_deg = 0; pitch_deg = -60; zoom = 1.0 })
            Start-Sleep -Seconds 2
            $r1 = Prefs $A "set" @{ key = "clouds_enabled"; value = $false }; Start-Sleep -Seconds 2
            $off1 = Shot $A "clouds-off-1"
            $r2 = Prefs $A "set" @{ key = "clouds_enabled"; value = $true }; Start-Sleep -Seconds 2
            $on = Shot $A "clouds-on"
            $r3 = Prefs $A "set" @{ key = "clouds_enabled"; value = $false }; Start-Sleep -Seconds 2
            $off2 = Shot $A "clouds-off-2"
            [void](Prefs $A "set" @{ key = "clouds_enabled"; value = $true })   # back on, as it was
            if (-not ($off1 -and $on -and $off2)) { return @{ pass = $false; detail = "a screenshot was not made" } }
            $change = Picture-Diff $off1 $on 0.9 0.1
            $same = Picture-Diff $off1 $off2 0.9 0.1
            $flags = "in force off/on/off: $($r1.in_force.clouds)/$($r2.in_force.clouds)/$($r3.in_force.clouds)"
            @{ pass = ($r1.in_force.clouds -eq $false -and $r2.in_force.clouds -eq $true -and $r3.in_force.clouds -eq $false -and $change -gt 0.3 -and $change -gt ($same * 3)); detail = "$flags; picture difference off to on $change, off to off $same (mean level per channel)" }
        }

        # 4 graphics: the Low preset, then Ultra
        Step 4 "graphics" {
            [void](Api $A "orbit" @{ yaw_deg = 0; pitch_deg = 15; zoom = 1.0 })
            Start-Sleep -Seconds 1
            $lowR = Prefs $A "preset" @{ key = "low" }; Start-Sleep -Seconds 4
            $shotLow = Shot $A "graphics-low"
            $objLow = Objects $A
            $ultraR = Prefs $A "preset" @{ key = "ultra" }; Start-Sleep -Seconds 6
            $shotUltra = Shot $A "graphics-ultra"
            $objUltra = Objects $A
            $d = if ($shotLow -and $shotUltra) { Picture-Diff $shotLow $shotUltra 1.0 } else { -1 }
            $lowOk = $lowR.preset -eq "low" -and [int]$lowR.in_force.draw_distance -eq 128 -and [int]$lowR.in_force.max_texture_resolution -eq 512 -and $lowR.in_force.clouds -eq $false
            $ultraOk = $ultraR.preset -eq "ultra" -and [int]$ultraR.in_force.draw_distance -eq 1024 -and [int]$ultraR.in_force.max_texture_resolution -eq 2048 -and $ultraR.in_force.clouds -eq $true
            @{ pass = ($lowOk -and $ultraOk -and $d -gt 0.3); detail = "Low in force: draw $($lowR.in_force.draw_distance), texture $($lowR.in_force.max_texture_resolution), clouds $($lowR.in_force.clouds), $objLow objects; Ultra in force: draw $($ultraR.in_force.draw_distance), texture $($ultraR.in_force.max_texture_resolution), clouds $($ultraR.in_force.clouds), $objUltra objects; picture difference $d" }
        }

        # 5 chat: a new text size and a fade time on the first account; the second account's next line shows both
        Step 5 "chat" {
            $sizeR = Prefs $A "set" @{ key = "chat_font_size"; value = 26 }
            $fadeR = Prefs $A "set" @{ key = "chat_overlay_seconds"; value = 6 }
            $saved = Prefs $A "save" @{ page = "chat" }
            [void](Api $B "chat" @{ action = "say"; text = "$token font"; kind = "say" })
            $heard = Wait-Until { $c = Find-Chat $A "$token font"; if (@($c.nearby).Count -gt 0) { $c } } 30
            Start-Sleep -Milliseconds 800
            $now = Prefs $A "state" $null
            $shot = Shot $A "chat-big-text"
            $shownNow = [int]$now.in_force.overlay_lines
            $fontPx = [int]$now.in_force.overlay_font_px
            Start-Sleep -Seconds 8
            $later = Prefs $A "state" $null
            $shownLater = [int]$later.in_force.overlay_lines
            [void](Prefs $A "set" @{ key = "chat_font_size"; value = 15 }); [void](Prefs $A "set" @{ key = "chat_overlay_seconds"; value = 0 }); [void](Prefs $A "save" @{ page = "chat" })   # back as it was
            @{ pass = ([bool]$heard -and [bool]$saved.ok -and $fontPx -eq 26 -and $shownNow -ge 1 -and $shownLater -eq 0); detail = "line heard $([bool]$heard); text size in force $fontPx px (held $($now.held.chat_font_size)); lines over the view at once $shownNow, 8 s later $shownLater (fade time $($now.held.chat_overlay_seconds) s); saved $($saved.ok); screenshot $([bool]$shot)" }
        }

        # 6 online status: off, B sees A offline; on, B sees A online. The friends need the right to see each other online first.
        Step 6 "online status" {
            foreach ($pair in @(@($A, $ids.B), @($B, $ids.A))) {
                $row = @((Api $pair[0] "friends" @{ action = "list" }).friends | Where-Object { $_.id -eq $pair[1] }) | Select-Object -First 1
                if (-not $row -or -not ([int]$row.granted -band 1)) { [void](Api $pair[0] "friends" @{ action = "grant_map"; agent = $pair[1] }) }
            }
            # the account that logs in second is not told the first is online until a right changes, so the right is cycled once first
            [void](Prefs $A "set" @{ key = "share_online_status"; value = $false }); Start-Sleep -Seconds 3
            [void](Prefs $A "set" @{ key = "share_online_status"; value = $true })
            $seenOnline = Wait-Until { $l = Api $B "friends" @{ action = "list" }; if (@($l.friends | Where-Object { $_.id -eq $ids.A -and $_.online }).Count -gt 0) { $l } } 60
            $offR = Prefs $A "set" @{ key = "share_online_status"; value = $false }
            $seenOffline = Wait-Until { $l = Api $B "friends" @{ action = "list" }; if (@($l.friends | Where-Object { $_.id -eq $ids.A -and -not $_.online }).Count -gt 0) { $l } } 60
            $rowOff = @((Api $B "friends" @{ action = "list" }).friends | Where-Object { $_.id -eq $ids.A }) | Select-Object -First 1
            $onR = Prefs $A "set" @{ key = "share_online_status"; value = $true }
            $backOnline = Wait-Until { $l = Api $B "friends" @{ action = "list" }; if (@($l.friends | Where-Object { $_.id -eq $ids.A -and $_.online }).Count -gt 0) { $l } } 60
            $rowOn = @((Api $B "friends" @{ action = "list" }).friends | Where-Object { $_.id -eq $ids.A }) | Select-Object -First 1
            @{ pass = ([bool]$seenOnline -and [bool]$seenOffline -and [bool]$backOnline -and $offR.ok -and $onR.ok); detail = "B listed A online before $([bool]$seenOnline); with the switch off, B listed A offline $([bool]$seenOffline) (B's view of what A gives: received $($rowOff.received)); switched on again, online $([bool]$backOnline) (received $($rowOn.received))" }
        }
    }
}
catch { Say ("ERROR: " + $_.Exception.Message); $allPass = $false }
finally {
    # 7 logout: both accounts, then both viewers
    $loA = $null; $loB = $null; $exA = "not started"; $exB = "not started"
    foreach ($v in @($B, $A)) {
        if ($null -eq $v) { continue }
        $lo = $null
        try { $lo = (Invoke-Api $v "session" '{"action":"logout"}').Body | ConvertFrom-Json } catch { }
        try { Invoke-Api $v "quit" "{}" | Out-Null } catch { }
        $exited = $v.Proc.WaitForExit(60000)
        $code = if ($exited) { $v.Proc.ExitCode } else { "still running" }
        if ($v.Label -eq "A") { $loA = $lo; $exA = $code } else { $loB = $lo; $exB = $code }
    }
    $pass7 = ($null -ne $A) -and ($null -ne $B) -and ($loA -and $loA.ended) -and ($loB -and $loB.ended) -and ($exA -eq 0) -and ($exB -eq 0)
    Record 7 "logout" ([bool]$pass7) ("A ended {0} (viewer exit {1}), B ended {2} (viewer exit {3})" -f $(if ($loA) { $loA.ended } else { "no session" }), $exA, $(if ($loB) { $loB.ended } else { "no session" }), $exB)
    $allPass = $allPass -and [bool]$pass7
    # a step that did not run is a failure, not a pass
    $ranSteps = @($report.steps | ForEach-Object { $_.step })
    foreach ($n in 1..7) { if ($ranSteps -notcontains $n) { $report.steps += [ordered]@{ step = $n; name = "not run"; pass = $false; detail = "the run ended before this step" }; $allPass = $false } }
    $report.finished_utc = (Get-Date).ToUniversalTime().ToString("o")
    $report.pass = [bool]$allPass
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "prefs.json") -Encoding utf8
    Say ("PREFS LIVE " + $(if ($allPass) { "PASS" } else { "FAIL" }) + ": " + (@($report.steps | Where-Object { $_.pass }).Count) + " of 7 steps passed")
}
if ($allPass) { exit 0 } else { exit 1 }
