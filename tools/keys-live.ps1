# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# keys-live.ps1 - the live check of the keyboard settings that only had unit tests. Two copies of the development export (automation on)
# log in to the same region, one account each; the first is driven through the automation interface (the preferences command for the
# settings, the keys command, and a development-only typing command that sends an arrow key through the engine's own input path to the
# chat field), and what happens is measured from outside the viewer: where the region says the avatar is, how fast it goes, whether it
# flies. The second account is in the same region and sees the first. Steps, each with its own pass condition and a PASS/FAIL line:
#   1 login            both accounts reach the world in the same region, each seeing the other
#   2 arrow keys       with the chat field in use and "arrow keys always move" off, Left moves the text cursor and Up does not move the avatar;
#                      with it on, Up held moves the avatar and the text cursor stays
#   3 double tap run   with the option on a double tap and hold runs (the run animation, a top speed over 4.2 m/s); off, it walks
#   4 fly on hold      with the option off, holding jump does not fly; on, it flies and rises
#   5 logout           both accounts log out and both viewers exit
# Exit: 0 every step passed; 1 a step failed; 3 blocked; 4 skipped (the person's own avatar is online or unknown, or a claim was not granted).
# The inputs, the presence check and the claims are those of keys-live.ps1. Output: the sandbox folder (keys.txt, keys.json, each viewer's log).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\keys-live.ps1 -Sandbox <new evidence folder> [the options of keys-live.ps1]
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
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "keys-live") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials,
        "-AccountA", $AccountA, "-AccountB", $AccountB, "-Region", $Region, "-GridInfoUrl", $GridInfoUrl, "-OtherClientPattern", $OtherClientPattern,
        "-PresenceCheck", $PresenceCheck, "-Exe", $Exe, "-StartSeconds", "$StartSeconds")
    $cmd = @("bash", $wrapper) + $order + @("--resource", $AccountA, $lane, "avatar", "keys live", "--",
            "bash", $wrapper) + $order + @("--resource", $AccountB, $lane, "avatar", "keys live", "--",
            "bash", $wrapper) + $order + @($lane, "live-login", "keys live", "--") + $inner
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
$textLog = Join-Path $Sandbox "keys.txt"
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

function Typing($v, [string]$action, $more) {
    $body = @{ action = $action }
    if ($more) { foreach ($k in $more.Keys) { $body[$k] = $more[$k] } }
    Api $v "typing" $body
}
function Pos($v) {
    # the region's own statement of where the avatar is; it can be missing for a moment, so wait for it
    $p = Wait-Until { $q = (Api $v "observe" '{}').self.region_position; if ($q -and @($q).Count -ge 3) { $q } } 30
    if (-not $p) { throw "the region has not stated the avatar's position" }
    [pscustomobject]@{ x = [double]$p[0]; y = [double]$p[1]; z = [double]$p[2] }
}
function Walked($p, $q) { [math]::Round([math]::Sqrt([math]::Pow($p.x - $q.x, 2) + [math]::Pow($p.y - $q.y, 2)), 2) }
# what the second account sees of the first: that it is there (the region sends the first's position to every observer, and the steps read it as the region states it)
function Seen-By-B([string]$what) {
    $ob = Api $B "observe" '{}'
    "second account sees $($ob.world.other_avatars) other avatar(s) ($what)"
}
# stand still and let the avatar settle: no text field in use
function Settle($v) { [void](Typing $v "release" $null); Start-Sleep -Seconds 2 }

try {
    $A = Start-Viewer "A"
    $B = Start-Viewer "B"

    Step 1 "login" {
        $okA = Login $A $AccountA
        $okB = Login $B $AccountB
        $script:inWorld = $okA -and $okB
        $detail = "A in world $okA, B in world $okB"
        $pass = $script:inWorld
        if ($script:inWorld) {
            $seen = Wait-Until { $oa = Api $A "observe" '{}'; $ob = Api $B "observe" '{}'; if ($oa.world.other_avatars -ge 1 -and $ob.world.other_avatars -ge 1) { $true } } 60
            $detail += "; each sees the other $([bool]$seen)"
            $pass = [bool]$seen
        }
        @{ pass = $pass; detail = $detail }
    }

    if ($inWorld) {
        Start-Sleep -Seconds 6   # the world settles
        # the avatar arrives in the air and falls to the ground: no key moves it sideways before it has landed
        [void](Wait-Until { $v = (Api $A "observe" '{}').self.region_velocity; if ($v -and [math]::Abs([double]$v[2]) -lt 0.3) { $true } } 90)
        Start-Sleep -Seconds 2
        Set-Content -LiteralPath (Join-Path $Sandbox "self-sample.json") -Value ((Invoke-Api $A "observe" '{}').Body) -Encoding utf8
        # 2 arrow keys while typing: with the chat field in use and "arrow keys always move" off, an arrow key moves the text cursor and not the avatar;
        # with it on, the avatar moves and the text cursor stays
        Step 2 "arrow keys while typing" {
            # the arrival point is a small platform: walk off it onto open ground first, then stand still
            [void](Api $A "keys" @{ key = "W"; down = $true }); Start-Sleep -Seconds 4; [void](Api $A "keys" @{ key = "W"; down = $false })
            Settle $A
            [void](Wait-Until { $v = (Api $A "observe" '{}').self.region_velocity; if ($v -and [math]::Abs([double]$v[2]) -lt 0.3 -and [math]::Abs([double]$v[0]) -lt 0.3 -and [math]::Abs([double]$v[1]) -lt 0.3) { $true } } 60)
            [void](Typing $A "focus" $null)
            [void](Prefs $A "set" @{ key = "arrow_keys_always_move"; value = $false })
            [void](Typing $A "text" @{ text = "abcdef"; caret = 3 })
            $s0 = Typing $A "state" $null
            $p0 = Pos $A
            [void](Typing $A "key" @{ key = "LEFT"; down = $true }); Start-Sleep -Milliseconds 1500; [void](Typing $A "key" @{ key = "LEFT"; down = $false })
            $s1 = Typing $A "state" $null
            [void](Typing $A "key" @{ key = "UP"; down = $true }); Start-Sleep -Milliseconds 2500; [void](Typing $A "key" @{ key = "UP"; down = $false })
            $s2 = Typing $A "state" $null
            $p1 = Pos $A
            $offMoved = Walked $p0 $p1
            $offOk = $s0.text_entry -and $s1.caret -eq 2 -and $s2.caret -le 2 -and $offMoved -lt 0.5 -and @($s2.keys_held).Count -eq 0
            $detail = "off: text field in use $($s0.text_entry), window focused $($s0.window_focused); caret 3 -> $($s1.caret) after Left, $($s2.caret) after Up; avatar moved $offMoved m; keys held $(@($s2.keys_held).Count)"
            [void](Prefs $A "set" @{ key = "arrow_keys_always_move"; value = $true })
            [void](Typing $A "focus" $null)
            [void](Typing $A "text" @{ text = "abcdef"; caret = 3 })
            $p2 = Pos $A
            [void](Typing $A "key" @{ key = "UP"; down = $true }); Start-Sleep -Milliseconds 2500
            $mid = Typing $A "state" $null
            $midSelf = (Api $A "observe" '{}').self
            [void](Typing $A "key" @{ key = "UP"; down = $false }); Start-Sleep -Milliseconds 800
            $s3 = Typing $A "state" $null
            $p3 = Pos $A
            $onMoved = Walked $p2 $p3
            $onOk = $mid.text_entry -and $onMoved -gt 2.0 -and $s3.caret -eq 3 -and @($s3.keys_held).Count -eq 0
            $detail += "; on: Up held 2.5 s moved the avatar $onMoved m (keys held mid-press $(@($mid.keys_held) -join ','), movement allowed $($mid.movement_allowed), window focused $($mid.window_focused), controls sent $($midSelf.controls), driver focused $($midSelf.focused), velocity $((@($midSelf.region_velocity) | % { [math]::Round([double]$_,2) }) -join ',')), caret stayed $($s3.caret), keys held after $(@($s3.keys_held).Count); " + (Seen-By-B "A at $([math]::Round($p3.x,1)),$([math]::Round($p3.y,1)) by the region's report")
            [void](Prefs $A "set" @{ key = "arrow_keys_always_move"; value = $false })   # put back (a preview; nothing was saved)
            Settle $A
            @{ pass = ($offOk -and $onOk); detail = $detail }
        }

        # 3 run: a double tap and hold runs when the option is on and walks when it is off
        Step 3 "double tap to run" {
            $out = @()
            $pass = $true
            foreach ($on in @($true, $false)) {
                [void](Prefs $A "set" @{ key = "double_tap_run"; value = $on })
                Settle $A
                [void](Api $A "keys" @{ key = "W"; down = $true }); Start-Sleep -Milliseconds 80; [void](Api $A "keys" @{ key = "W"; down = $false }); Start-Sleep -Milliseconds 80
                [void](Api $A "keys" @{ key = "W"; down = $true })
                $maxSpeed = 0.0; $ranSeen = $false
                $sw = [Diagnostics.Stopwatch]::StartNew()
                while ($sw.Elapsed.TotalSeconds -lt 4.0) {
                    $o = Api $A "observe" '{}'
                    $v = $o.self.region_velocity
                    if ($v) { $sp = [math]::Sqrt([double]$v[0] * [double]$v[0] + [double]$v[1] * [double]$v[1]); if ($sw.Elapsed.TotalSeconds -gt 1.5 -and $sp -gt $maxSpeed) { $maxSpeed = $sp } }
                    if (@($o.self.playing) -contains "run") { $ranSeen = $true }
                    Start-Sleep -Milliseconds 250
                }
                [void](Api $A "keys" @{ key = "W"; down = $false })
                $out += "option $(if ($on) { 'on' } else { 'off' }): top speed $([math]::Round($maxSpeed,2)) m/s, run animation seen $ranSeen"
                $good = if ($on) { $ranSeen -and $maxSpeed -gt 4.2 } else { (-not $ranSeen) -and $maxSpeed -gt 1.0 -and $maxSpeed -lt 4.2 }
                if (-not $good) { $pass = $false }
                Start-Sleep -Seconds 2
            }
            [void](Prefs $A "set" @{ key = "double_tap_run"; value = $true })
            $out += (Seen-By-B "A walked and ran")
            @{ pass = $pass; detail = ($out -join "; ") }
        }

        # 4 fly: holding the jump key flies when the option is on and only jumps when it is off
        Step 4 "fly on holding jump" {
            $out = @()
            $pass = $true
            foreach ($on in @($false, $true)) {
                [void](Prefs $A "set" @{ key = "automatic_fly"; value = $on })
                Settle $A
                $z0 = (Pos $A).z
                [void](Api $A "keys" @{ key = "E"; down = $true })
                $flew = $false; $zMax = $z0
                $sw = [Diagnostics.Stopwatch]::StartNew()
                while ($sw.Elapsed.TotalSeconds -lt 3.5) {
                    $o = Api $A "observe" '{}'
                    if ($o.self.flying) { $flew = $true }
                    $z = [double]$o.self.region_position[2]; if ($z -gt $zMax) { $zMax = $z }
                    Start-Sleep -Milliseconds 250
                }
                [void](Api $A "keys" @{ key = "E"; down = $false })
                $out += "option $(if ($on) { 'on' } else { 'off' }): flying seen $flew, highest rise $([math]::Round($zMax - $z0, 2)) m"
                $good = if ($on) { $flew -and ($zMax - $z0) -gt 1.5 } else { -not $flew }
                if (-not $good) { $pass = $false }
                if ($flew) {
                    # come down: hold crouch until the avatar is no longer flying
                    [void](Api $A "keys" @{ key = "C"; down = $true })
                    [void](Wait-Until { $o = Api $A "observe" '{}'; if (-not $o.self.flying) { $true } } 20)
                    [void](Api $A "keys" @{ key = "C"; down = $false })
                }
                Start-Sleep -Seconds 3
            }
            [void](Prefs $A "set" @{ key = "automatic_fly"; value = $true })
            $out += (Seen-By-B "A rose")
            @{ pass = $pass; detail = ($out -join "; ") }
        }
    }
}
catch { Say ("ERROR: " + $_.Exception.Message); $allPass = $false }
finally {
    $loA = $null; $loB = $null; $exA = "not started"; $exB = "not started"
    foreach ($v in @($B, $A)) {
        if ($null -eq $v) { continue }
        $lo = $null
        try { [void](Prefs $v "cancel" @{ page = "moveview" }) } catch { }
        try { $lo = (Invoke-Api $v "session" '{"action":"logout"}').Body | ConvertFrom-Json } catch { }
        try { Invoke-Api $v "quit" "{}" | Out-Null } catch { }
        $exited = $v.Proc.WaitForExit(60000)
        $code = if ($exited) { $v.Proc.ExitCode } else { "still running" }
        if ($v.Label -eq "A") { $loA = $lo; $exA = $code } else { $loB = $lo; $exB = $code }
    }
    $pass5 = ($null -ne $A) -and ($null -ne $B) -and ($loA -and $loA.ended) -and ($loB -and $loB.ended) -and ($exA -eq 0) -and ($exB -eq 0)
    Record 5 "logout" ([bool]$pass5) ("A ended {0} (viewer exit {1}), B ended {2} (viewer exit {3})" -f $(if ($loA) { $loA.ended } else { "no session" }), $exA, $(if ($loB) { $loB.ended } else { "no session" }), $exB)
    $allPass = $allPass -and [bool]$pass5
    $ranSteps = @($report.steps | ForEach-Object { $_.step })
    foreach ($n in 1..5) { if ($ranSteps -notcontains $n) { $report.steps += [ordered]@{ step = $n; name = "not run"; pass = $false; detail = "the run ended before this step" }; $allPass = $false } }
    $report.finished_utc = (Get-Date).ToUniversalTime().ToString("o")
    $report.pass = [bool]$allPass
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "keys.json") -Encoding utf8
    Say ("KEYS LIVE " + $(if ($allPass) { "PASS" } else { "FAIL" }) + ": " + (@($report.steps | Where-Object { $_.pass }).Count) + " of 5 steps passed")
}
if ($allPass) { exit 0 } else { exit 1 }
