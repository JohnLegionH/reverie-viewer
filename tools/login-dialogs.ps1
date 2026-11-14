# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# login-dialogs.ps1 - a picture of each login-flow dialog (terms, critical message, update, multifactor token, certificate trust,
# certificate error, unrecognised failure), taken through the automation API of the EXPORTED DEVELOPMENT build (tools\s1.ps1 exports it).
# The viewer shows each dialog with example content (the automation's login_dialog command), the script takes a screenshot of the
# window, and closes the dialog. The pictures are for looking at: they are not goldens and are not committed. Needs no grid and no
# account. The viewer starts through tools\godot.ps1 with APPDATA and LOCALAPPDATA sandboxed, as automation.ps1 does.
#     powershell -NoProfile -File <repo>\tools\login-dialogs.ps1 -OutDir <existing absolute folder>
# =====================================================================================
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutDir, [string]$DiagDir = "")

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
if (-not [System.IO.Path]::IsPathRooted($OutDir) -or -not (Test-Path -LiteralPath $OutDir -PathType Container)) { throw "OutDir must be an existing absolute folder" }
$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo "host\Viewer.Godot\build"
$exe = Join-Path $build "dev\Reverie.console.exe"
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not (Test-Path -LiteralPath $exe)) { throw "no development export at $exe (run tools\s1.ps1 first)" }
$work = Join-Path $repo "tools\test-results\login-dialogs"
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Invoke-Api([int]$port, [string]$command, [string]$token, [string]$body = "{}") {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $port)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($body)
        $h = "POST /v1/$command HTTP/1.1`r`nHost: 127.0.0.1:$port`r`nAuthorization: Bearer $token`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $all = (New-Object System.IO.StreamReader($s)).ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}

$local = Join-Path $work "localappdata-$([DateTime]::UtcNow.ToString('HHmmss'))"
$log = Join-Path $work "run.log"
$argLine = "--rendering-driver vulkan -- --automation"
$godotArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$godot`"",
    "-Exe", "`"$exe`"", "-AppDataDir", "`"$(Join-Path $build 'appdata')`"", "-LocalAppDataDir", "`"$local`"", "-TimeoutSeconds", "120", "-LogFile", "`"$log`"", "-ArgLine", "`"$argLine`"")
if ($DiagDir) { $godotArgs += @("-DiagDir", "`"$DiagDir`"") }
$proc = Start-Process -FilePath "powershell" -PassThru -WindowStyle Hidden -ArgumentList $godotArgs

$dir = Join-Path $local "ReverieViewer\Automation"
$tokenFile = $null
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and -not $tokenFile) {
    $f = @(Get-ChildItem -LiteralPath $dir -Filter "automation-*.json" -ErrorAction SilentlyContinue)
    if ($f.Count -gt 0) { Start-Sleep -Milliseconds 300; $tokenFile = $f[0].FullName }
    elseif ($proc.HasExited) { break }
    else { Start-Sleep -Milliseconds 250 }
}
if (-not $tokenFile) { throw "no automation token file within 60 s (see $log)" }
$j = Get-Content -Raw -LiteralPath $tokenFile | ConvertFrom-Json
$port = [int]$j.port; $token = [string]$j.token

$failed = 0
try {
    foreach ($kind in @("terms", "terms_page", "critical", "update", "mfa", "trust", "certificate_error", "certificate_unreadable", "unknown")) {
        $shown = Invoke-Api $port "login_dialog" $token (@{ action = "show"; kind = $kind } | ConvertTo-Json -Compress)
        Start-Sleep -Milliseconds 600   # the dialog is built on the next frame and drawn on the one after
        $state = Invoke-Api $port "login_dialog" $token '{"action":"state"}'
        $shot = Invoke-Api $port "screenshot" $token (@{ dir = $OutDir; name = "dialog-$kind" } | ConvertTo-Json -Compress)
        $file = Join-Path $OutDir "dialog-$kind.png"
        $ok = $shown.Status -eq 200 -and $state.Status -eq 200 -and ($state.Body | ConvertFrom-Json).open -eq $true -and $shot.Status -eq 200 -and (Test-Path -LiteralPath $file)
        Write-Output ("  {0,-5} {1,-24} {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $kind, $state.Body)
        if (-not $ok) { $failed++ }
        [void](Invoke-Api $port "login_dialog" $token '{"action":"close"}')
    }
} finally {
    [void](Invoke-Api $port "quit" $token)
    $proc.WaitForExit(40000) | Out-Null
}
Write-Output "login dialog pictures: $OutDir (failed: $failed)"
if ($failed -gt 0) { exit 1 }
