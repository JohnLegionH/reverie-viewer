# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# notecard-live.ps1 - the notecard window through the automation API, on the development export.
#   Headless part (always): before any login the viewer opens the notecard files fixtures\notecards\embedded.notecard and
#   plain.notecard in windows of their own and the script checks what each window shows: the text with embedded items as
#   their names, the item rows (a landmark with a teleport and Copy to inventory enabled, an object with Copy disabled), the
#   web and location links; opening embedded.notecard again brings its window to the front instead of opening a third.
#   Live part (-Live): logs in as -Account, opens a notecard of its own inventory (else one of the Library), checks that it
#   loaded and that the window shows its text and one row per embedded item, copies one embedded item out when the rules
#   allow it, and logs out. Nothing is made in-world.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\notecard-live.ps1 -Sandbox <new folder> -OutDir <existing folder>
#     [-Live] [-Credentials <accounts file>] [-Account <alias>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Run it under a build claim (headless only), or a live-login claim and an avatar claim on the account (-Live). Output:
# notecard-check.json in -OutDir. Exit: 0 pass, 1 a check failed, 3 blocked, 5 the viewer or the login did not come up.
# Live cards are described by the viewer in counts, kinds and lengths only; their names and text are never asked for.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [switch]$Live,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$Account = "viewer1",
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [int]$StartSeconds = 90
)
if ($Live) {
    if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
    if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
    if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
}
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
$fixtures = Join-Path $repo "fixtures\notecards"
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
$loginUri = $null
if ($Live) {
    if (-not $Credentials -or -not (Test-Path -LiteralPath $Credentials -PathType Leaf)) { $problems.Add("-Credentials (or NV_TEST_ACCOUNTS) is not a file") }
    if (-not $Start) { $problems.Add("-Start (or NV_LIVE_START) is required") }
    if (-not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_GRID_INFO_URL) is required") }
    if ($GridInfoUrl) { try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { } }
    if ($GridInfoUrl -and -not $loginUri) { $problems.Add("the grid's get_grid_info did not answer with a login URI") }
    $viewers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -like "Reverie*.exe" -and $_.CommandLine -match "--automation" })
    if ($viewers.Count -gt 0) { $problems.Add("a viewer that can log in is already running ($($viewers.Count))") }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Output "BLOCKED: $_" }; exit 3 }
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null

function Start-Viewer([string]$dir) {
    $appdata = Join-Path $dir "appdata"; $local = Join-Path $dir "localappdata"; $log = Join-Path $dir "run.log"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $argLine = "--rendering-driver vulkan -- --automation"
    $script:proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
        "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0",
        "-DiagDir", "`"$dir`"", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
    $tokDir = Join-Path $local "ReverieViewer\Automation"
    $tf = $null
    for ($i = 0; $i -lt ($StartSeconds * 4) -and -not $tf; $i++) { Start-Sleep -Milliseconds 250; $tf = @(Get-ChildItem -LiteralPath $tokDir -Filter "automation-*.json" -ErrorAction SilentlyContinue) | Select-Object -First 1 }
    if (-not $tf) { return $null }
    Start-Sleep -Milliseconds 300
    $j = Get-Content -Raw -LiteralPath $tf.FullName | ConvertFrom-Json
    return @{ port = [int]$j.port; token = [string]$j.token }
}
# polls a call until the condition holds, with a cap; answers the last reply
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

$exit = 0
$V = $null
$result = [ordered]@{}
try {
    $V = Start-Viewer (Join-Path $Sandbox "viewer")
    if (-not $V) { Note "no automation token file"; $exit = 5; throw "the viewer did not start" }
    Note "viewer up"

    # ---------------- headless: the fixtures ----------------
    $emb = Join-Path $fixtures "embedded.notecard"; $plain = Join-Path $fixtures "plain.notecard"
    $s1 = Until $V "notecard" @{ action = "fixture"; text = $emb } { param($r) $r.open -ge 1 -and @($r.windows)[0].loaded } 30
    $w1 = @($s1.windows) | Select-Object -Last 1
    # the fixture's third line, by character code: Windows PowerShell 5 reads a script file without a byte-order mark as ANSI
    $third = "Gr" + [char]0xFC + [char]0xDF + "e, caf" + [char]0xE9 + " " + [char]0x2014 + " " + [char]0x65E5 + [char]0x672C + [char]0x8A9E + " " + [char]0x2713
    $expectEmb = "Landmark: Beach`nThe box: Gift box`n" + $third + "`n"
    Check "fixture.embedded.text" ([string]$w1.text -eq $expectEmb) ("shown {0} chars" -f ([string]$w1.text).Length)
    Check "fixture.embedded.title" ([string]$w1.title -eq "embedded") ([string]$w1.title)
    $kinds = (@($w1.items) | ForEach-Object { [string]$_.kind }) -join ","
    $offers = (@($w1.items) | ForEach-Object { "{0}/{1}" -f $_.teleport, $_.copy }) -join ","
    Check "fixture.embedded.items" ($kinds -eq "Landmark,Object" -and $offers -eq "True/True,False/False" -and [int]$w1.item_rows -eq 2) "kinds $kinds, teleport/copy $offers, rows $($w1.item_rows)"
    $s2 = Until $V "notecard" @{ action = "fixture"; text = $plain } { param($r) $r.open -ge 2 -and (@($r.windows) | Select-Object -Last 1).loaded } 30
    $w2 = @($s2.windows) | Select-Object -Last 1
    Check "fixture.plain.links" ([int]$w2.links -eq 2 -and [int]$w2.places -eq 2 -and [int]$w2.item_rows -eq 0) "web $($w2.links), location $($w2.places)"
    Check "fixture.plain.text" (([string]$w2.text).StartsWith("Welcome to the example shop.") -and ([string]$w2.text).Contains("https://example.org/help")) ("shown {0} chars" -f ([string]$w2.text).Length)
    Check "fixture.two_windows" ([int]$s2.open -eq 2) "open $($s2.open)"
    $s3 = Call $V "notecard" @{ action = "fixture"; text = $emb }
    Check "fixture.open_again_comes_to_front" ([int]$s3.open -eq 2 -and [string]$s3.front -eq [string]$w1.id) "open $($s3.open), front is the first card: $([string]$s3.front -eq [string]$w1.id)"
    $result.fixtures = @($s1, $s2)

    # ---------------- live ----------------
    if ($Live) {
        $r = Invoke-Raw $V.port "session" $V.token (@{ action = "login"; accounts_file = $Credentials; account = $Account; login_uri = $loginUri; start = $Start } | ConvertTo-Json -Compress)
        Note "login requested, $($r.Status)"
        $o = Until $V "observe" $null { param($x) $x.stage -eq "InWorld" -and $x.self } 180
        if (-not $o -or $o.stage -ne "InWorld") { $exit = 5; throw "the login did not reach the world" }
        Note "in world"
        $list = Until $V "notecard" @{ action = "list"; text = "load" } { param($x) @($x.own).Count + @($x.library).Count -gt 0 -and $x.folders_unloaded -eq 0 } 120
        $own = @($list.own); $lib = @($list.library)
        Note ("notecards: own {0}, library {1}, folders unloaded {2}" -f $own.Count, $lib.Count, $list.folders_unloaded)
        Check "live.found" (($own.Count + $lib.Count) -gt 0) "own $($own.Count), library $($lib.Count)"
        $pick = if ($own.Count -gt 0) { [string]$own[0] } elseif ($lib.Count -gt 0) { [string]$lib[0] } else { $null }
        $result.live = [ordered]@{ own = $own.Count; library = $lib.Count; from = $(if ($own.Count -gt 0) { "own" } else { "library" }) }
        if ($pick) {
            [void](Call $V "notecard" @{ action = "open"; id = $pick })
            $st = Until $V "notecard" @{ action = "state" } { param($x) @($x.windows | Where-Object { [string]$_.id -eq $pick -and $_.loaded }).Count -eq 1 } 60
            $w = @($st.windows | Where-Object { [string]$_.id -eq $pick }) | Select-Object -First 1
            $result.live.window = $w
            Check "live.opened" ($w -and $w.loaded -and [string]$w.status -eq "Ok") ("status {0}, text {1} chars, shown {2}, items {3}, rows {4}" -f $w.status, $w.text_chars, $w.shown_chars, @($w.items).Count, $w.item_rows)
            Check "live.text_shown" ($w -and [int]$w.shown_chars -ge [int]$w.text_chars -and [int]$w.text_chars -ge 0) "text $($w.text_chars), shown $($w.shown_chars)"
            Check "live.item_rows" ($w -and [int]$w.item_rows -eq @($w.items).Count) "items $(@($w.items).Count), rows $($w.item_rows)"
        }
        # every other card is opened too (own and Library, at most 16), each must load; the first with an item the rules let out is used to copy one
        $opened = 0; $failed = 0; $copied = $null
        foreach ($card in @($own + $lib | Select-Object -First 16)) {
            [void](Call $V "notecard" @{ action = "open"; id = [string]$card })
            $st = Until $V "notecard" @{ action = "state" } { param($x) @($x.windows | Where-Object { [string]$_.id -eq [string]$card -and $_.loaded }).Count -eq 1 } 30
            $cw = @($st.windows | Where-Object { [string]$_.id -eq [string]$card }) | Select-Object -First 1
            if ($cw -and [string]$cw.status -eq "Ok") { $opened++ } else { $failed++; Note ("card {0}: status {1}" -f ($opened + $failed), $(if ($cw) { [string]$cw.status } else { "none" })) }
            $copyable = @($cw.items | Where-Object { $_.copy })
            if ($null -eq $copied -and $copyable.Count -gt 0) {
                $c = Call $V "notecard" @{ action = "copy"; index = [int]$copyable[0].index }
                $copied = [bool]$c.copied
                Check "live.copy" $copied "copied $copied (item kind $($copyable[0].kind))"
            }
        }
        Check "live.every_card_opens" ($failed -eq 0 -and $opened -gt 0) "opened $opened, failed $failed"
        $result.live.cards_opened = $opened
        $result.live.copied = $copied
        if ($null -eq $copied) { Note "no card of this account holds an item the rules let out: nothing copied" }
        [void](Invoke-Raw $V.port "session" $V.token '{"action":"logout"}')
        Start-Sleep -Seconds 3
    }
} catch {
    Note "stopped: $($_.Exception.Message)"
    if ($exit -eq 0) { $exit = 1 }
} finally {
    if ($V) { try { [void](Invoke-Raw $V.port "quit" $V.token) } catch { } }
    if ($script:proc) { try { $script:proc.WaitForExit(60000) | Out-Null } catch { } }
}
$result.checks = $checks
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutDir "notecard-check.json") -Encoding utf8
if ($exit -eq 0 -and @($checks | Where-Object { -not $_.pass }).Count -gt 0) { $exit = 1 }
Write-Output ("notecard check: {0} checks, {1} failed, exit {2}" -f $checks.Count, @($checks | Where-Object { -not $_.pass }).Count, $exit)
exit $exit
