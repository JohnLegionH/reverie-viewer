# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# live-smoke.ps1 - the reusable two-account smoke test. Two copies of the development export (automation on) log in to the
# same region, one account each, and the script checks what a person would check first, step by step, each with its own pass
# condition and a PASS/FAIL line:
#   1 login      both accounts log in by region name and reach the world in the same region, each seeing the other
#   2 chat       a nearby chat line each way
#   3 im         an instant message each way
#   4 friends    each sees the other online in the friends list
#   5 inventory  the first account opens Inventory: the tree shows and the Recent tab fills
#   6 movement   the first account walks and then runs a few metres; the second account's view of it moves
#   7 name tag   a screenshot from the first account with the second account's name tag in view, kept as evidence
#   8 logout     both accounts log out and both viewers exit
# Exit: 0 every step passed; 1 a step failed; 3 blocked (an input or precondition is missing); 4 skipped (the person's own
# avatar is online or its state is unknown, or the lane claim was not granted): nothing was started.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\live-smoke.ps1 -Sandbox <new evidence folder>
#     [-Credentials <accounts file>] [-AccountA <alias>] [-AccountB <alias>] [-Region <name>] [-GridInfoUrl <url>]
#     [-OtherClientPattern <regex or none>] [-PresenceCheck <bash script or none>] [-Exe <development export>] [-DryRun]
#
# Settings come from the parameters or, when a parameter is not given, from the environment, never from a committed file:
#   NV_TEST_ACCOUNTS (the accounts file the viewer reads at login; this script never opens it), NV_SMOKE_ACCOUNT_A and
#   NV_SMOKE_ACCOUNT_B (the two account aliases), NV_SMOKE_REGION (the region name; else the region of NV_LIVE_START, a start
#   location written uri:<region>&x&y&z), NV_GRID_INFO_URL (the grid's get_grid_info address; the login address is read from it
#   at run time and never printed), NV_OTHER_CLIENT_PATTERN (a regex over process executables matching any other client that may
#   use the accounts; none when there is none), NV_PRESENCE_CHECK (a bash script that prints online, offline or unknown for the
#   avatar of the person running the test, or none): the run goes ahead only on offline, and online or unknown ends it with
#   exit 4 before anything starts.
# Claims: when NV_CLAIM_WRAPPER names a bash script that runs a command under a lane claim (claim.sh's form: [--order O]
# [--label L] [--resource R] <lane> <kind> <note> -- <command>), the script runs itself under an avatar claim for each account and
# a live-login claim for the lane NV_CLAIM_LANE (default viewer), waiting up to CLAIM_WAIT minutes (default 120) for each; a claim
# that is not granted ends the run with exit 4. NV_CLAIM_ORDER names the order in the claim log. Every Godot process goes through
# tools\godot.ps1, which refuses to start without a granted claim when the machine's guard is set (tools\claim-guard.ps1).
# Output: the sandbox folder (smoke.txt, smoke.json, the screenshot and each viewer's log). Messages are made of a random
# token, and only ids, counts and numbers are written, never message text or names.
# -DryRun checks every input and precondition, runs the presence check, prints the claim command it would run, and starts nothing.
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
if ($env:NV_CLAIM_WRAPPER -and -not $env:NV_SMOKE_CLAIMED) {
    $lane = if ($env:NV_CLAIM_LANE) { $env:NV_CLAIM_LANE } else { "viewer" }
    $order = if ($env:NV_CLAIM_ORDER) { @("--order", $env:NV_CLAIM_ORDER, "--label", "live-smoke") } else { @() }
    $wrapper = ToUnixPath $env:NV_CLAIM_WRAPPER
    $inner = @("powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Sandbox", $Sandbox, "-Credentials", $Credentials,
        "-AccountA", $AccountA, "-AccountB", $AccountB, "-Region", $Region, "-GridInfoUrl", $GridInfoUrl, "-OtherClientPattern", $OtherClientPattern,
        "-PresenceCheck", $PresenceCheck, "-Exe", $Exe, "-StartSeconds", "$StartSeconds")
    $cmd = @("bash", $wrapper) + $order + @("--resource", $AccountA, $lane, "avatar", "live smoke", "--",
            "bash", $wrapper) + $order + @("--resource", $AccountB, $lane, "avatar", "live smoke", "--",
            "bash", $wrapper) + $order + @($lane, "live-login", "live smoke", "--") + $inner
    if ($DryRun) { Write-Output ("would run under claims: " + ($cmd -join " ")); Write-Output "dry run: nothing started, nothing logged in"; exit 0 }
    $env:NV_SMOKE_CLAIMED = "1"
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
$textLog = Join-Path $Sandbox "smoke.txt"
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

# the point on screen test of step 7: the follow camera looks at the own avatar from the orbit it is given; the other avatar's
# head is in view when the angle between the view direction and the direction to the head is within the vertical field of view
# (the engine's axes: x east, y up, z south = minus the region's y)
function In-View($obs, $other) {
    $c = $obs.camera.position; $fov = [double]$obs.camera.fov
    $me = @($obs.presentation.avatars.each | Where-Object { $_.self }) | Select-Object -First 1
    if (-not $me -or -not $other -or -not $other.placed) { return $false }
    $cam = @([double]$c[0], [double]$c[1], [double]$c[2])
    $target = @([double]$me.soles[0], ([double]$me.soles[2] + 1.0), (-[double]$me.soles[1]))
    $head = @([double]$other.soles[0], ([double]$other.soles[2] + 1.7), (-[double]$other.soles[1]))
    $f = @(($target[0] - $cam[0]), ($target[1] - $cam[1]), ($target[2] - $cam[2]))
    $d = @(($head[0] - $cam[0]), ($head[1] - $cam[1]), ($head[2] - $cam[2]))
    $lf = [math]::Sqrt($f[0] * $f[0] + $f[1] * $f[1] + $f[2] * $f[2]); $ld = [math]::Sqrt($d[0] * $d[0] + $d[1] * $d[1] + $d[2] * $d[2])
    if ($lf -lt 0.01 -or $ld -lt 0.5) { return $false }
    $cos = ($f[0] * $d[0] + $f[1] * $d[1] + $f[2] * $d[2]) / ($lf * $ld)
    $angle = [math]::Acos([math]::Max(-1, [math]::Min(1, $cos))) * 180 / [math]::PI
    return ($angle -le ($fov / 2) * 0.9)
}

$A = $null; $B = $null
$allPass = $true
$token = "SMK" + (Get-Random -Minimum 100000 -Maximum 999999)
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
        # 2 chat, each way
        Step 2 "chat" {
            [void](Api $A "chat" @{ action = "say"; text = "$token say a"; kind = "say" })
            $gotB = Wait-Until { $c = Find-Chat $B "$token say a"; if (@($c.nearby).Count -gt 0) { $c } } 30
            [void](Api $B "chat" @{ action = "say"; text = "$token say b"; kind = "say" })
            $gotA = Wait-Until { $c = Find-Chat $A "$token say b"; if (@($c.nearby).Count -gt 0) { $c } } 30
            @{ pass = ([bool]$gotB -and [bool]$gotA); detail = "A to B heard $([bool]$gotB), B to A heard $([bool]$gotA)" }
        }

        # 3 instant messages, each way
        Step 3 "im" {
            [void](Api $A "chat" @{ action = "im"; agent = $ids.B; text = "$token im a" })
            $imB = Wait-Until { $c = Find-Chat $B "$token im a"; if (@($c.conversations | Where-Object { $_.matching -gt 0 }).Count -gt 0) { $c } } 30
            [void](Api $B "chat" @{ action = "im"; agent = $ids.A; text = "$token im b" })
            $imA = Wait-Until { $c = Find-Chat $A "$token im b"; if (@($c.conversations | Where-Object { $_.matching -gt 0 }).Count -gt 0) { $c } } 30
            @{ pass = ([bool]$imB -and [bool]$imA); detail = "A to B arrived $([bool]$imB), B to A arrived $([bool]$imA)" }
        }

        # 4 each sees the other online in the friends list. Seeing a friend online is a right the friend gives, so each account
        # first gives the other the right to see it online (the call keeps whatever was already given), then the lists are read.
        Step 4 "friends" {
            $missing = 0
            foreach ($pair in @(@($A, $B, $ids.B), @($B, $A, $ids.A))) {   # (the account that gives, the other one, the other one's id)
                $row = @((Api $pair[0] "friends" @{ action = "list" }).friends | Where-Object { $_.id -eq $pair[2] }) | Select-Object -First 1
                if (-not $row -or -not ([int]$row.granted -band 1)) { $missing++; [void](Api $pair[0] "friends" @{ action = "grant_map"; agent = $pair[2] }) }
            }
            $fa = Wait-Until { $l = Api $A "friends" @{ action = "list" }; if (@($l.friends | Where-Object { $_.id -eq $ids.B -and $_.online }).Count -gt 0) { $l } } $(if ($missing) { 20 } else { 60 })
            $fb = Wait-Until { $l = Api $B "friends" @{ action = "list" }; if (@($l.friends | Where-Object { $_.id -eq $ids.A -and $_.online }).Count -gt 0) { $l } } $(if ($missing) { 20 } else { 60 })
            $note = if ($missing) { "; the right to see the other online was missing for $missing of the two and was given now (the grid shows it from the next login)" } else { "" }
            $ra = @((Api $A "friends" @{ action = "list" }).friends | Where-Object { $_.id -eq $ids.B }) | Select-Object -First 1
            $rb = @((Api $B "friends" @{ action = "list" }).friends | Where-Object { $_.id -eq $ids.A }) | Select-Object -First 1
            $rights = "; rights (1 = see online): A gave $($ra.granted) and got $($ra.received), B gave $($rb.granted) and got $($rb.received)"
            @{ pass = ([bool]$fa -and [bool]$fb); detail = "A lists B online $([bool]$fa), B lists A online $([bool]$fb)$note$rights" }
        }

        # 5 inventory: the tree shows, then the Recent tab fills (a fresh sandbox has no stored logout time, so Recent lists the newest items)
        Step 5 "inventory" {
            [void](Api $A "inventory" @{ action = "open"; kind = "root" })
            $tree = Wait-Until { $r = Api $A "inventory" @{ action = "search"; text = ""; tab = "all" }; if ($r -and $r.tab -eq "All" -and $r.rows -gt 0) { $r } } 90
            $recent = Wait-Until { $r = Api $A "inventory" @{ action = "search"; text = ""; tab = "recent" }; if ($r -and $r.tab -eq "Recent" -and $r.items -gt 0) { $r } } 90
            @{ pass = ([bool]$tree -and [bool]$recent); detail = ("tree rows {0}, Recent items {1}" -f $(if ($tree) { $tree.rows } else { 0 }), $(if ($recent) { $recent.items } else { 0 })) }
        }

        # 6 A walks, then runs; B's view of A moves
        Step 6 "movement" {
            [void](Api $B "motion" @{ action = "start" })
            $p0 = Where-Am $A
            [void](Api $A "keys" @{ key = "W"; down = $true }); Start-Sleep -Seconds 4; [void](Api $A "keys" @{ key = "W"; down = $false }); Start-Sleep -Seconds 1
            $p1 = Where-Am $A
            [void](Api $A "keys" @{ key = "R"; down = $true; control = $true }); [void](Api $A "keys" @{ key = "R"; down = $false; control = $true }); Start-Sleep -Milliseconds 500
            [void](Api $A "keys" @{ key = "W"; down = $true }); Start-Sleep -Seconds 3; [void](Api $A "keys" @{ key = "W"; down = $false }); Start-Sleep -Seconds 1
            $p2 = Where-Am $A
            [void](Api $A "keys" @{ key = "R"; down = $true; control = $true }); [void](Api $A "keys" @{ key = "R"; down = $false; control = $true })   # Always Run off again
            $trace = Api $B "motion" @{ action = "stop" }
            $walked = if ($p0 -and $p1) { Dist2D $p0 $p1 } else { 0 }
            $ran = if ($p1 -and $p2) { Dist2D $p1 $p2 } else { 0 }
            $rows = @($trace.samples | Where-Object { -not $_[2] })   # the other avatar's rows: not the own avatar
            $moved = 0
            if ($rows.Count -gt 1) { $x0 = [double]$rows[0][3]; $y0 = [double]$rows[0][4]; foreach ($r in $rows) { $moved = [math]::Max($moved, [math]::Sqrt([math]::Pow([double]$r[3] - $x0, 2) + [math]::Pow([double]$r[4] - $y0, 2))) } }
            $moved = [math]::Round($moved, 2)
            @{ pass = (($walked -ge 2) -and ($ran -ge 3) -and ($moved -ge 3)); detail = "walked $walked m, ran $ran m, B saw A move $moved m over $($rows.Count) samples" }
        }

        # 7 a screenshot from A with B's name tag in view: look round until B is in view (and, where the viewer has the name tag
        # command, until it says B's tag is drawn)
        Step 7 "name tag" {
            $shotFile = $null; $yawUsed = $null; $tagCmd = $false; $tagDrawn = $null; $tried = 0
            foreach ($yaw in @(0, 45, 90, 135, 180, 225, 270, 315)) {
                [void](Api $A "orbit" @{ yaw_deg = $yaw; pitch_deg = 5; zoom = 8.0 })
                Start-Sleep -Milliseconds 1500
                $tried++
                $raw = (Invoke-Api $A "observe" '{}').Body
                if ($tried -le 2) { Set-Content -LiteralPath (Join-Path $Sandbox "observe-yaw$yaw.json") -Value $raw -Encoding utf8 }   # kept for diagnosis
                $obs = $raw | ConvertFrom-Json
                $pb = Where-Am $B
                $others = @($obs.presentation.avatars.each | Where-Object { -not $_.self })
                $other = $null
                foreach ($o in $others) { if ($pb -and $o.soles -and [math]::Abs([double]$o.soles[0] - [double]$pb.x) -lt 2 -and [math]::Abs([double]$o.soles[1] - [double]$pb.y) -lt 2) { $other = $o; break } }
                if (-not (In-View $obs $other)) { continue }
                $nt = Invoke-Api $A "nametags" '{}'
                if ($nt.Status -eq 200) {
                    $tagCmd = $true
                    $tags = $nt.Body | ConvertFrom-Json
                    $tagDrawn = @($tags.drawn | Where-Object { $_ -eq $ids.B }).Count -gt 0
                    if (-not $tagDrawn) { continue }
                }
                $shot = Api $A "screenshot" @{ dir = $Sandbox; name = "tag-yaw$yaw" }
                $shotFile = $shot.file; $yawUsed = $yaw
                break
            }
            $shotOk = $shotFile -and (Test-Path -LiteralPath $shotFile) -and ((Get-Item -LiteralPath $shotFile).Length -gt 0)
            @{ pass = [bool]$shotOk; detail = ("B in view at yaw {0} (looked from {1} yaws); name tag command {2}; tag drawn {3}; screenshot saved {4}" -f $yawUsed, $tried, $tagCmd, $tagDrawn, [bool]$shotOk) }
        }
    }
}
catch { Say ("ERROR: " + $_.Exception.Message); $allPass = $false }
finally {
    # 8 logout: both accounts, then both viewers
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
    $pass8 = ($null -ne $A) -and ($null -ne $B) -and ($loA -and $loA.ended) -and ($loB -and $loB.ended) -and ($exA -eq 0) -and ($exB -eq 0)
    Record 8 "logout" ([bool]$pass8) ("A ended {0} (viewer exit {1}), B ended {2} (viewer exit {3})" -f $(if ($loA) { $loA.ended } else { "no session" }), $exA, $(if ($loB) { $loB.ended } else { "no session" }), $exB)
    $allPass = $allPass -and [bool]$pass8
    # a step that did not run is a failure, not a pass
    $ranSteps = @($report.steps | ForEach-Object { $_.step })
    foreach ($n in 1..8) { if ($ranSteps -notcontains $n) { $report.steps += [ordered]@{ step = $n; name = "not run"; pass = $false; detail = "the run ended before this step" }; $allPass = $false } }
    $report.finished_utc = (Get-Date).ToUniversalTime().ToString("o")
    $report.pass = [bool]$allPass
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Sandbox "smoke.json") -Encoding utf8
    Say ("SMOKE " + $(if ($allPass) { "PASS" } else { "FAIL" }) + ": " + (@($report.steps | Where-Object { $_.pass }).Count) + " of 8 steps passed")
}
if ($allPass) { exit 0 } else { exit 1 }
