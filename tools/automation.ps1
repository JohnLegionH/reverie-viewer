# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# automation.ps1 - the automation API against the EXPORTED DEVELOPMENT build (the test strategy 2.1), after
# tools\s1.ps1 has exported build\dev\. Every run goes through tools\godot.ps1 with APPDATA and LOCALAPPDATA sandboxed
# under tools\test-results\automation\ (the token file lands there, never in the real per-user directory).
#   off_by_default     started WITHOUT --automation: no listening socket, no token file (then the run is timed out)
#   token_file_owner_only / loopback_only / refusals (missing token, wrong token, foreign Origin, Sec-Fetch, foreign Host)
#   commands           session state, observe, screenshot (into test-results\automation\), quit -> exit 0, token file gone
#   token_not_in_output  the whole console output of the run never contains the token
# -Live (L6, read-only): additionally logs in with the test account (alias tester1) through the API, observes until terrain is
#   complete and the object count is stable, screenshots, logs out and confirms it. Needs NV_GRID_INFO_URL (the grid's
#   get_grid_info URL; the login URI is read from it into memory, never printed or written). BLOCKED (not failed) when the
#   grid is down or another test client is running (an account is never used by two tools). Also needs NV_TEST_ACCOUNTS (the
#   test client's accounts file), NV_LIVE_START (the start location, uri:<region>&x&y&z) and NV_OTHER_CLIENT_PATTERN (a regex
#   over process executables (tools\other-clients.ps1) matching any other client that may use the test account; "none" when there is none).
#     powershell -NoProfile -File <repo>\tools\automation.ps1 [-Live]
# =====================================================================================
[CmdletBinding()]
param([switch]$Live, [string]$Accounts = $env:NV_TEST_ACCOUNTS, [string]$Start = $env:NV_LIVE_START, [string]$OtherClientPattern = $env:NV_OTHER_CLIENT_PATTERN)
# A live setting that is not set comes from the untracked local settings file (live-env.ps1).
if (-not $Accounts) { $Accounts = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
if (-not $OtherClientPattern) { $OtherClientPattern = & "$PSScriptRoot\live-env.ps1" NV_OTHER_CLIENT_PATTERN }

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo "host\Viewer.Godot\build"
$exe = Join-Path $build "dev\Reverie.console.exe"
$godot = Join-Path $PSScriptRoot "godot.ps1"
$results = Join-Path $PSScriptRoot "test-results"
$out = Join-Path $results "automation"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$cases = New-Object System.Collections.Generic.List[object]
function Add-Case([string]$name, [string]$status, [string]$detail) {
    $cases.Add([pscustomobject]@{ Name = $name; Status = $status; Detail = $detail })
    Write-Output ("  {0,-10} Automation.{1}  {2}" -f $status, $name, $detail)
}
function Esc([string]$s) { [System.Security.SecurityElement]::Escape($s) }
function Write-JUnit {
    $fail = @($cases | Where-Object Status -eq "FAIL").Count
    $skip = @($cases | Where-Object { $_.Status -in "SKIP", "BLOCKED", "GRID-KNOWN" }).Count
    $xml = New-Object System.Text.StringBuilder
    [void]$xml.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$xml.AppendLine("<testsuites><testsuite name=""Automation"" tests=""$($cases.Count)"" failures=""$fail"" skipped=""$skip"" errors=""0"">")
    foreach ($c in $cases) {
        [void]$xml.Append("<testcase classname=""Automation"" name=""$(Esc $c.Name)"">")
        if ($c.Status -eq "FAIL") { [void]$xml.Append("<failure message=""$(Esc $c.Detail)"" />") }
        elseif ($c.Status -ne "PASS") { [void]$xml.Append("<skipped message=""$(Esc ($c.Status + ': ' + $c.Detail))"" />") }
        else { [void]$xml.Append("<system-out>$(Esc $c.Detail)</system-out>") }
        [void]$xml.AppendLine("</testcase>")
    }
    [void]$xml.AppendLine("</testsuite></testsuites>")
    Set-Content -LiteralPath (Join-Path $results "Automation.junit.xml") -Value $xml.ToString() -Encoding utf8
    Write-Output ("Automation summary: total {0}, passed {1}, failed {2}, skipped/blocked {3}" -f $cases.Count, @($cases | Where-Object Status -eq "PASS").Count, $fail, $skip)
}

# Raw HTTP/1.1 over a socket, so Host and Origin are exactly what the case says. Returns @{ Status; Body }.
function Invoke-Raw([int]$port, [string]$command, [string]$token, [string]$body = "{}", [string]$hostHeader = "", [string]$extra = "") {
    if (-not $hostHeader) { $hostHeader = "127.0.0.1:$port" }
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $port)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($body)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: $hostHeader`r`n"
        if ($token) { $h += "Authorization: Bearer $token`r`n" }
        $h += "Content-Type: application/json`r`n$extra" + "Content-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $r = New-Object System.IO.StreamReader($s)
        $all = $r.ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}

function Get-ViewerPids([datetime]$since) {
    @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CreationDate -ge $since.AddSeconds(-2) -and $_.ExecutablePath -like "$build\dev\*" } | ForEach-Object { [int]$_.ProcessId })
}

# Starts the development build through godot.ps1 in its own PowerShell (so this script can drive it). Returns the job.
$run = 0
function Start-Viewer([string]$userArgs, [int]$timeout, [switch]$ExpectTimeout) {
    $script:run++
    $local = Join-Path $out "localappdata-$($script:run)-$([DateTime]::UtcNow.ToString('HHmmss'))"
    $log = Join-Path $out "run-$($script:run).log"
    $argLine = "--rendering-driver vulkan -- $userArgs"
    $godotArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$exe`"", "-AppDataDir", "`"$(Join-Path $build 'appdata')`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "$timeout", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
    if ($ExpectTimeout) { $godotArgs += "-ExpectTimeout" }
    $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $godotArgs
    return @{ Proc = $p; Local = $local; Log = $log; Started = (Get-Date) }
}

function Wait-TokenFile($v, [int]$seconds) {
    $dir = Join-Path $v.Local "ReverieViewer\Automation"
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $f = @(Get-ChildItem -LiteralPath $dir -Filter "automation-*.json" -ErrorAction SilentlyContinue)
        if ($f.Count -gt 0) { Start-Sleep -Milliseconds 300; return $f[0].FullName }
        if ($v.Proc.HasExited) { return $null }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

Write-Output "Automation stage (exported development build)"
if (-not (Test-Path -LiteralPath $exe)) {
    Add-Case "all" "SKIP" "no development export at $exe (run tools\s1.ps1 first)"
    Write-JUnit
    exit 0
}

# ---- 1. off by default ----
$v = Start-Viewer "" 14 -ExpectTimeout
Start-Sleep -Seconds 9
$pids = Get-ViewerPids $v.Started
$listen = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $pids -contains $_.OwningProcess })
$tokenDir = Join-Path $v.Local "ReverieViewer\Automation"
$files = @(Get-ChildItem -LiteralPath $tokenDir -ErrorAction SilentlyContinue)
$v.Proc.WaitForExit(30000) | Out-Null
if ($pids.Count -gt 0 -and $listen.Count -eq 0 -and $files.Count -eq 0) { Add-Case "off_by_default" "PASS" "viewer ran ($($pids.Count) process(es)) without --automation: 0 listening sockets, no token file" }
else { Add-Case "off_by_default" "FAIL" "processes=$($pids.Count) listening=$($listen.Count) token files=$($files.Count)" }

# ---- 2. enabled: safety and commands ----
function Test-Enabled([switch]$LiveLogin) {
    $v = Start-Viewer "--automation" $(if ($LiveLogin) { 300 } else { 90 })
    $tf = Wait-TokenFile $v 40
    if (-not $tf) { Add-Case "token_file" "FAIL" "no token file within 40 s (see $($v.Log))"; return $null }
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $port = [int]$j.port; $token = [string]$j.token
    $pids = Get-ViewerPids $v.Started
    return @{ V = $v; File = $tf; Port = $port; Token = $token; Pids = $pids }
}

$e = Test-Enabled
if ($e) {
    $port = $e.Port; $token = $e.Token
    $acl = Get-Acl -LiteralPath $e.File
    $sids = @($acl.Access | Where-Object AccessControlType -eq "Allow" | ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } | Select-Object -Unique)
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($acl.AreAccessRulesProtected -and $sids.Count -eq 1 -and $sids[0] -eq $me) { Add-Case "token_file_owner_only" "PASS" "protected DACL, one allow entry: the current user; file in the per-user local data directory (sandboxed)" }
    else { Add-Case "token_file_owner_only" "FAIL" "protected=$($acl.AreAccessRulesProtected) allowed=$($sids -join ',')" }

    $listen = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $e.Pids -contains $_.OwningProcess })
    if ($listen.Count -ge 1 -and @($listen | Where-Object LocalAddress -ne "127.0.0.1").Count -eq 0 -and @($listen | Where-Object LocalPort -eq $port).Count -eq 1) {
        Add-Case "loopback_only" "PASS" "$($listen.Count) listening socket(s), all 127.0.0.1 (random port)"
    } else { Add-Case "loopback_only" "FAIL" "listening: $(($listen | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', ')" }

    $checks = @(
        @("missing_token", 401, (Invoke-Raw $port "session" "")),
        @("wrong_token", 401, (Invoke-Raw $port "session" ("0" * 64))),
        @("foreign_origin", 403, (Invoke-Raw $port "session" $token -extra "Origin: http://evil.example`r`n")),
        @("sec_fetch", 403, (Invoke-Raw $port "session" $token -extra "Sec-Fetch-Site: cross-site`r`n")),
        @("foreign_host", 403, (Invoke-Raw $port "session" $token -hostHeader "evil.example:$port")))
    foreach ($c in $checks) {
        $ok = $c[2].Status -eq $c[1] -and -not $c[2].Body.Contains($token)
        Add-Case "refuses_$($c[0])" ($(if ($ok) { "PASS" } else { "FAIL" })) "status $($c[2].Status) (expected $($c[1])) $($c[2].Body)"
    }

    $st = Invoke-Raw $port "session" $token '{"action":"state"}'
    $ob = Invoke-Raw $port "observe" $token
    $shotDir = $out
    $sh = Invoke-Raw $port "screenshot" $token (@{ dir = $shotDir; name = "offline-login-screen" } | ConvertTo-Json -Compress)
    $shot = Join-Path $shotDir "offline-login-screen.png"
    $bad = Invoke-Raw $port "screenshot" $token '{"dir":"relative\\dir"}'
    $ok = $st.Status -eq 200 -and ($st.Body | ConvertFrom-Json).stage -eq "Idle" -and $ob.Status -eq 200 -and $sh.Status -eq 200 -and (Test-Path -LiteralPath $shot) -and $bad.Status -eq 400
    Add-Case "commands" ($(if ($ok) { "PASS" } else { "FAIL" })) "session=$($st.Status) $($st.Body); observe=$($ob.Status); screenshot=$($sh.Status) -> $(Split-Path -Leaf $shot); relative dir refused=$($bad.Status)"

    $q = Invoke-Raw $port "quit" $token
    $e.V.Proc.WaitForExit(40000) | Out-Null
    $viewerGone = @(Get-ViewerPids $e.V.Started).Count -eq 0
    $godotExit = if ($e.V.Proc.HasExited) { $e.V.Proc.ExitCode } else { "still running" }   # godot.ps1's exit code = the viewer's
    $ok = $q.Status -eq 200 -and "$godotExit" -eq "0" -and $viewerGone -and -not (Test-Path -LiteralPath $e.File)
    Add-Case "quit" ($(if ($ok) { "PASS" } else { "FAIL" })) "quit=$($q.Status); viewer exit=$godotExit; viewer processes left: $(-not $viewerGone); token file removed: $(-not (Test-Path -LiteralPath $e.File))"

    $text = Get-Content -Raw -LiteralPath $e.V.Log -ErrorAction SilentlyContinue
    if ($text -and -not $text.Contains($token)) { Add-Case "token_not_in_output" "PASS" "$($text.Length) chars of console output scanned; token absent" }
    else { Add-Case "token_not_in_output" "FAIL" "token found in (or no) output: $($e.V.Log)" }
}

# ---- 2b. A login from the development build against a test login service on loopback (nothing leaves this machine) ----
# The test service answers get_grid_info (its own address as the login address) and any login with a refusal in its own
# words. The address typed into the session login is a grid-info address with a stray backtick, as pasted into the screen
# once. Pass: the viewer tidied and resolved it, posted the login to the service, and shows the service's own refusal
# (not a parse error). The account is a made-up one in a scratch accounts file; the service accepts nobody.
$svcPort = (New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0))
$svcPort.Start(); $portN = $svcPort.LocalEndpoint.Port; $svcPort.Stop()
$svcUrl = "http://127.0.0.1:$portN/"
$svcSeen = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
$svc = [powershell]::Create()
[void]$svc.AddScript({
    param($url, $seen)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add($url); $l.Start()
    try {
        while ($l.IsListening) {
            $c = $l.GetContext()
            if ($c.Request.Url.AbsolutePath -eq "/__stop") { $c.Response.StatusCode = 200; $c.Response.Close(); break }
            $seen.Enqueue("$($c.Request.HttpMethod) $($c.Request.Url.AbsolutePath)")
            if ($c.Request.HttpMethod -eq "GET" -and $c.Request.Url.AbsolutePath -eq "/get_grid_info") {
                $body = "<gridinfo><login>$url</login><gridname>Test Service</gridname></gridinfo>"; $type = "text/xml"
            } elseif ($c.Request.HttpMethod -eq "POST") {
                [void](New-Object System.IO.StreamReader($c.Request.InputStream)).ReadToEnd()
                $body = "<llsd><map><key>login</key><string>false</string><key>reason</key><string>key</string><key>message</key><string>Test refusal from the test login service.</string></map></llsd>"; $type = "text/xml"
            } else { $body = ""; $type = "text/plain"; $c.Response.StatusCode = 404 }
            $b = [System.Text.Encoding]::UTF8.GetBytes($body)
            $c.Response.ContentType = $type; $c.Response.ContentLength64 = $b.Length
            $c.Response.OutputStream.Write($b, 0, $b.Length); $c.Response.Close()
        }
    } finally { $l.Stop() }
}).AddArgument($svcUrl).AddArgument($svcSeen)
$svcHandle = $svc.BeginInvoke()
$acctFile = Join-Path $out "test-service-accounts.json"
@{ accounts = @(@{ alias = "tester1"; first = "Test"; last = "Nobody"; password = "not-a-real-password" }) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $acctFile -Encoding utf8
$e = Test-Enabled
if ($e) {
    $typed = $svcUrl + "get_grid_info" + [string][char]96
    $lg = Invoke-Raw $e.Port "session" $e.Token (@{ action = "login"; accounts_file = $acctFile; account = "tester1"; login_uri = $typed } | ConvertTo-Json -Compress)
    $status = ""; $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        $status = [string]((Invoke-Raw $e.Port "session" $e.Token '{"action":"state"}').Body | ConvertFrom-Json).status
        if ($status -match "refusal|didn't answer|failed") { break }
    }
    $seenList = @($svcSeen.ToArray())
    $q = Invoke-Raw $e.Port "quit" $e.Token
    $e.V.Proc.WaitForExit(40000) | Out-Null
    $ok = $lg.Status -eq 200 -and $status -match "Test refusal from the test login service" -and $status -notmatch "deserialize|unrecognized" `
        -and $seenList.Count -ge 2 -and $seenList[0] -eq "GET /get_grid_info" -and ($seenList | Where-Object { $_ -eq "POST /" }).Count -ge 1
    Add-Case "login_test_service" ($(if ($ok) { "PASS" } else { "FAIL" })) "pasted grid-info address with a backtick: the service saw [$($seenList -join '; ')]; the viewer's status: $status"
} else { Add-Case "login_test_service" "FAIL" "development build did not start with --automation" }
try { [void](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 ($svcUrl + "__stop")) } catch { }
[void]$svc.EndInvoke($svcHandle); $svc.Dispose()

# ---- 3. L6: live, read-only login with the test account ----
if ($Live) {
    . (Join-Path $PSScriptRoot "other-clients.ps1")
    $agent = @(Find-OtherClients -Pattern $OtherClientPattern)   # by executable, never by command-line text
    $gridInfo = $(& "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL)
    $loginUri = $null
    if ($gridInfo) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $gridInfo).Content).gridinfo.login } catch { $loginUri = $null } }
    if (-not $Accounts -or -not $Start -or -not $OtherClientPattern) { Add-Case "live_login" "BLOCKED" "NV_TEST_ACCOUNTS, NV_LIVE_START and NV_OTHER_CLIENT_PATTERN must all be set" }
    elseif ($agent.Count -gt 0) { Add-Case "live_login" "BLOCKED" "another test client is running ($($agent.Count) process(es)); the test account is not used by two tools" }
    elseif (-not $gridInfo) { Add-Case "live_login" "BLOCKED" "NV_GRID_INFO_URL not set" }
    elseif (-not $loginUri) { Add-Case "live_login" "BLOCKED" "grid info did not answer: grid down" }
    else {
        $e = Test-Enabled -LiveLogin
        $port = $e.Port; $token = $e.Token
        $t0 = Get-Date
        $login = Invoke-Raw $port "session" $token (@{ action = "login"; accounts_file = $Accounts; account = "tester1"; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
        $samples = New-Object System.Collections.Generic.List[object]
        $stable = 0; $last = -1; $o = $null; $inWorldAt = $null; $completeAt = $null
        $deadline = (Get-Date).AddSeconds(150)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $o = (Invoke-Raw $port "observe" $token).Body | ConvertFrom-Json
            $secs = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
            $samples.Add([pscustomobject]@{ t = $secs; stage = $o.stage; objects = $o.world.objects; avatars = $o.world.avatars; patches = $o.terrain.patches; chunks = $o.presentation.chunks; backlog = $o.presentation.backlog; age = $o.presentation.backlog_age_ms })
            if ($o.stage -eq "InWorld" -and -not $inWorldAt) { $inWorldAt = $secs }
            if ($o.terrain.complete -and -not $completeAt) { $completeAt = $secs }
            if ($o.stage -eq "InWorld" -and $o.terrain.complete -and $o.presentation.dirty_chunks -eq 0) {
                if ($o.world.objects -eq $last) { $stable++ } else { $stable = 0 }
                $last = $o.world.objects
                if ($stable -ge 3) { break }
            }
            $s = (Invoke-Raw $port "session" $token '{"action":"state"}').Body | ConvertFrom-Json
            if ($s.ended -or ($s.has_session -eq $false -and -not $s.busy -and $secs -gt 30)) { break }
        }
        $state = (Invoke-Raw $port "session" $token '{"action":"state"}').Body | ConvertFrom-Json
        $shot = Invoke-Raw $port "screenshot" $token (@{ dir = $out; name = "l6-terrain" } | ConvertTo-Json -Compress)
        $samples | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out "l6-samples.json") -Encoding utf8
        $reached = $o.stage -eq "InWorld" -and $o.terrain.complete -and $stable -ge 3
        $logout = Invoke-Raw $port "session" $token '{"action":"logout"}'
        $after = ($logout.Body | ConvertFrom-Json)
        $q = Invoke-Raw $port "quit" $token
        $e.V.Proc.WaitForExit(40000) | Out-Null
        $godotExit = if ($e.V.Proc.HasExited) { "exit=$($e.V.Proc.ExitCode) " } else { "still running" }
        $text = Get-Content -Raw -LiteralPath $e.V.Log -ErrorAction SilentlyContinue
        # The token must never appear. The login HOST does appear once, by design: the core's login.start entry logs the
        # login URI's origin with its path redacted; counted and reported, not failed.
        $leak = $text.Contains($token)
        $hostLines = @(($text -split "`n") | Where-Object { $_.Contains(([Uri]$loginUri).Host) }).Count
        $detail = "login=$($login.Status); in world at $inWorldAt s, terrain complete at $completeAt s ($($o.terrain.patches)/$($o.terrain.expected)); objects=$($o.world.objects) (stable x$stable) attachments=$($o.world.attachments) avatars=$($o.world.avatars) other avatars=$($o.world.other_avatars); chunks=$($o.presentation.chunks) backlog=$($o.presentation.backlog) frames over budget=$($o.presentation.frames_over_budget)/$($o.presentation.frames); region=$($state.region.name) $($state.region.size_x)x$($state.region.size_y); screenshot=$($shot.Status); logout=$($logout.Status) ended=$($after.ended) reason=$($after.end_reason); viewer $godotExit; token in output: $leak; lines naming the login host: $hostLines (login.start origin, by design)"
        $ok = $login.Status -eq 200 -and $reached -and $shot.Status -eq 200 -and $logout.Status -eq 200 -and $after.ended -and $after.end_reason -eq "LoggedOut" -and "$godotExit" -eq "exit=0 " -and -not $leak
        if ($ok) { Add-Case "live_login" "PASS" $detail }
        elseif ($state.status -match "presence|already logged in") { Add-Case "live_login" "GRID-KNOWN" "login refused for presence (a known grid issue: a presence left behind by an earlier session): $detail" }
        else { Add-Case "live_login" "FAIL" $detail }
    }
}

Write-JUnit
if (@($cases | Where-Object Status -eq "FAIL").Count -gt 0) { exit 1 }
exit 0
