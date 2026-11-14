# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
# =====================================================================================
# move-live.ps1: an inventory move that stays moved, on a real grid, with a test account.
#   start 1: an item (not worn, not a link) in a top-level folder is dropped on another top-level folder that is closed, as a drop in
#            the tree does; it must show there at once (the viewer does not wait for the grid or read the folders back)
#   start 2: the viewer started again and logged in; both folders are read fresh from the grid: the target holds the item, its
#            first folder does not; the item is then dropped back into its first folder
#   start 3: started again; its first folder, read fresh from the grid, holds it again (the account's inventory as it was found)
# Ids only in the log; never names. Writes only into -Sandbox.
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\move-live.ps1 -Sandbox <new folder> -LoginUri <uri> -Account <alias>
#     [-Start <uri:region&x&y&z>] [-Accounts <accounts file>]
# Needs the development export (tools\s1.ps1 makes it), a granted lane claim (claim-guard.ps1) and the avatar claim for the account,
# taken by the caller. Exit: 0 passed; 1 a step failed; 3 blocked (an input is missing).
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [string]$Exe = "",
    [string]$LoginUri = "",
    [string]$Account = "",
    [string]$Start = "",
    [string]$Accounts = $env:NV_TEST_ACCOUNTS,
    [int]$StartSeconds = 120
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Output "BLOCKED: the development export is missing: $Exe"; exit 3 }
if (-not $Accounts) { $Accounts = & (Join-Path $PSScriptRoot "live-env.ps1") NV_TEST_ACCOUNTS }
if (-not $LoginUri -or -not $Account -or -not $Accounts) { Write-Output "BLOCKED: needs -LoginUri, -Account and an accounts file (NV_TEST_ACCOUNTS)"; exit 3 }
$godot = Join-Path $PSScriptRoot "godot.ps1"
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null
$textLog = Join-Path $Sandbox "move-live.txt"
$failed = 0
function Say([string]$line) { Write-Output $line; Add-Content -LiteralPath $textLog -Value $line -Encoding utf8 }
function Record([string]$name, [bool]$pass, [string]$detail) {
    if (-not $pass) { $script:failed++ }
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
        return $all.Substring($i + 4)
    } finally { $tcp.Close() }
}
function Api($v, [string]$command, $body = '{}') { $t = Invoke-Api $v $command $body; if ($t) { try { $t | ConvertFrom-Json } catch { $null } } else { $null } }
function Wait-Until([scriptblock]$cond, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { $r = & $cond; if ($r) { return $r }; Start-Sleep -Milliseconds 400 }
    return $null
}
$appdata = Join-Path $Sandbox "appdata"
$local = Join-Path $Sandbox "localappdata"
function Start-Viewer([string]$label) {
    New-Item -ItemType Directory -Force -Path (Join-Path $appdata "ReverieViewer") | Out-Null
    $log = Join-Path $Sandbox "viewer-$label.log"
    Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue | ForEach-Object { Rename-Item -LiteralPath $_.FullName -NewName ($_.Name + ".old-$label") }
    $args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"", "-Exe", "`"$Exe`"", "-AppDataDir", "`"$appdata`"", "-LocalAppDataDir", "`"$local`"",
        "-TimeoutSeconds", "600", "-HangSnapshotSeconds", "0", "-PlaceWindow", "-DiagDir", "`"$Sandbox`"", "-LogFile", "`"$log`"", "-ArgLine", "`"-- --automation`"")
    $p = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $args2
    $v = @{ Label = $label; Proc = $p; Log = $log; Port = 0; Token = "" }
    $tf = Wait-Until { $f = @(Get-ChildItem -LiteralPath (Join-Path $local "ReverieViewer\Automation") -Filter "automation-*.json" -ErrorAction SilentlyContinue); if ($f.Count -gt 0) { $f[0].FullName } } $StartSeconds
    if (-not $tf) { Stop-Viewer $v; return $null }
    Start-Sleep -Milliseconds 500
    $j = Get-Content -Raw -LiteralPath $tf | ConvertFrom-Json
    $v.Port = [int]$j.port; $v.Token = [string]$j.token
    Start-Sleep -Seconds 2
    return $v
}
function Stop-Viewer($v) {
    try { if ($v.Port) { [void](Invoke-Api $v "quit" '{}') } } catch { }
    if (-not $v.Proc.WaitForExit(60000)) { Stop-Process -Id $v.Proc.Id -Force -ErrorAction SilentlyContinue }   # only the launcher this script started
    Start-Sleep -Milliseconds 800
}
function LogIn($v) {
    $body = @{ action = "submit"; accounts_file = $Accounts; account = $Account; login_uri = $LoginUri; remember = $false }
    if ($Start) { $body.start = $Start }
    [void](Api $v "login_screen" $body)
    $s = Wait-Until { $s = Api $v "session" '{"action":"state"}'; if ($s -and "$($s.stage)" -eq "InWorld") { $s } } 120
    if ($s) { [void](Api $v "windows" '{"action":"open","name":"inventory"}'); Start-Sleep -Seconds 2 }
    return $s
}
function Inv($v, [string]$action, [string]$item = "", [string]$folder = "") { Api $v "inventory_view" (@{ action = $action; item = $item; folder = $folder }) }

# start 1: the drop
$pick = $null
$v = Start-Viewer "move-1"
if (-not $v) { Record "start 1" $false "no automation token file"; exit 1 }
try {
    if (-not (LogIn $v)) { Record "login 1" $false "not in world"; exit 1 }
    $pick = Inv $v "move_pick"
    if (-not $pick -or $pick.refused) { Record "pick" $false "no item and folder to move: $($pick.refused)"; exit 1 }
    Say "picked: item $($pick.item) ($($pick.kind)) from folder $($pick.home) to folder $($pick.target) (closed $($pick.target_closed))"
    $d = Inv $v "move_drop" $pick.item $pick.target
    Record "move_shown_at_once" ($d.shown_at_once -eq $true) "the item shows in the target at once: $($d.shown_at_once); a refusal notice: $($d.notice)"
    Start-Sleep -Seconds 5   # the message is reliable; give it time to reach the grid before logging out
    [void](Api $v "session" '{"action":"logout"}')
    Start-Sleep -Seconds 3
} finally { Stop-Viewer $v }

# start 2: read fresh from the grid, then back
$v = Start-Viewer "move-2"
if (-not $v) { Record "start 2" $false "no automation token file"; exit 1 }
try {
    if (-not (LogIn $v)) { Record "login 2" $false "not in world"; exit 1 }
    $t = Inv $v "move_where" $pick.item $pick.target
    $h = Inv $v "move_where" $pick.item $pick.home
    Record "move_kept_after_relog" ($t.loaded -and $t.holds -and $h.loaded -and -not $h.holds) "after logging in again, read from the grid: the target holds it $($t.holds) (read $($t.loaded)), its first folder holds it $($h.holds) (read $($h.loaded))"
    $b = Inv $v "move_drop" $pick.item $pick.home
    Record "moved_back_shown" ($b.shown_at_once -eq $true) "dropped back into its first folder, shown at once: $($b.shown_at_once)"
    Start-Sleep -Seconds 5
    [void](Api $v "session" '{"action":"logout"}')
    Start-Sleep -Seconds 3
} finally { Stop-Viewer $v }

# start 3: the inventory as it was found
$v = Start-Viewer "move-3"
if (-not $v) { Record "start 3" $false "no automation token file"; exit 1 }
try {
    if (-not (LogIn $v)) { Record "login 3" $false "not in world"; exit 1 }
    $h = Inv $v "move_where" $pick.item $pick.home
    $t = Inv $v "move_where" $pick.item $pick.target
    Record "move_back_kept_after_relog" ($h.loaded -and $h.holds -and $t.loaded -and -not $t.holds) "after logging in a third time: its first folder holds it $($h.holds) (read $($h.loaded)), the target holds it $($t.holds) (read $($t.loaded))"
    [void](Api $v "session" '{"action":"logout"}')
    Start-Sleep -Seconds 3
} finally { Stop-Viewer $v }

Say ("move-live: {0} failed" -f $failed)
if ($failed -gt 0) { exit 1 }
exit 0
