# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# stock-server.ps1 - start, check and stop an upstream OpenSim standalone (the "stock" server) that exists only while a test uses
# it. The viewer must work on an unmodified OpenSim, not only on the grid it is developed against; this is the server the stock
# smoke test (tools\stock-smoke.ps1) runs against.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\tools\stock-server.ps1 -Action start|stop|status
#     [-Control <bash script>] [-GridInfoUrl <url>] [-Lane <name>] [-ReadySeconds <n>] [-DryRun]
#
# How the server is run is not this script's business. A control script does that, and is named by -Control or NV_STOCK_SERVER_CONTROL:
#   bash <control> start <lane>   starts the server and waits until it is ready. Exit 0 = up. Exit 1 = refused, nothing was started
#                                 (held by someone else, or already up). Any other exit = it may have started something.
#   bash <control> stop           stops it cleanly, never by killing. Exit 0 = gone.
#   bash <control> status         exit 0 = up, anything else = down.
# This script adds the readiness check (the server's get_grid_info answer, from -GridInfoUrl or NV_STOCK_GRID_INFO_URL, has a
# login address) and the cleanup rule: a start that did not end ready is stopped again before the script returns, so a caller that
# sees a failure never has a server left behind. A start the control script refused is not followed by a stop: that server is
# someone else's.
#
# Exit: 0 ok (start: ready; stop: gone; status: up); 1 refused or failed (stop: the server may still be up; status: down);
#       2 start did not end ready (it was stopped again); 3 blocked (an input is missing).
# Every script that starts the server must also run `-Action stop` on every way out, including a failure.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("start", "stop", "status")][string]$Action,
    [string]$Control = $env:NV_STOCK_SERVER_CONTROL,
    [string]$GridInfoUrl = $env:NV_STOCK_GRID_INFO_URL,
    [string]$Lane = $env:NV_STOCK_LANE,
    [int]$ReadySeconds = 120,
    [string]$LogFolder = "",
    [string]$Settings = "",   # a settings file (NAME=VALUE lines) for the control script, grid-info address and lane when not given; default NV_STOCK_SETTINGS (tools\stock-settings.ps1)
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "stock-settings.ps1")
$loaded = Read-StockSettings $Settings
$Control = Resolve-StockSetting $Control "NV_STOCK_SERVER_CONTROL" $loaded
$GridInfoUrl = Resolve-StockSetting $GridInfoUrl "NV_STOCK_GRID_INFO_URL" $loaded
$Lane = Resolve-StockSetting $Lane "NV_STOCK_LANE" $loaded
if (-not $Lane) { $Lane = "viewer" }

function Find-Bash {
    foreach ($candidate in @((Get-Command bash.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
                             (Join-Path $env:ProgramFiles "Git\bin\bash.exe"), (Join-Path $env:ProgramFiles "Git\usr\bin\bash.exe"))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    return $null
}
function ToUnixPath([string]$p) { $u = $p -replace '\\', '/'; if ($u -match '^([A-Za-z]):(.*)$') { $u = "/" + $Matches[1].ToLower() + $Matches[2] }; return $u }
function Say([string]$line) { Write-Output "[stock-server] $line" }

# the server's get_grid_info answer: the login address, or $null while it does not answer
function Get-LoginUri([string]$url) {
    try {
        $req = [System.Net.WebRequest]::Create($url); $req.Timeout = 5000
        $resp = $req.GetResponse()
        try { return ([xml](New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd()).gridinfo.login } finally { $resp.Close() }
    } catch { return $null }
}

$problems = New-Object System.Collections.Generic.List[string]
if (-not $Control -or -not (Test-Path -LiteralPath $Control -PathType Leaf)) { $problems.Add("-Control (or NV_STOCK_SERVER_CONTROL) is not a file") }
if ($Action -eq "start" -and -not $GridInfoUrl) { $problems.Add("-GridInfoUrl (or NV_STOCK_GRID_INFO_URL) is required to start: it is how readiness is checked") }
if ($Lane -notmatch '^[A-Za-z0-9_-]{1,40}$') { $problems.Add("-Lane must be a plain name") }
$bash = Find-Bash
if (-not $bash) { $problems.Add("bash not found") }
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Say "BLOCKED: $_" }
    Say ("settings file: " + $(if ($loaded.File) { "read $($loaded.File)" } else { "none found; looked at: $($loaded.Looked)" }) + ". A setting comes from its parameter, its environment variable (NV_...) or a NAME=VALUE line of the settings file.")
    exit 3
}
$controlUnix = ToUnixPath (Resolve-Path -LiteralPath $Control).ProviderPath

if ($DryRun) { Say "preconditions ok: action $Action, lane $Lane; nothing was called"; exit 0 }

# The control script's output goes to files, not through a pipe: the server it starts keeps running after the start returns and
# inherits that output, so a pipe would stay open (and this call with it) until the server ended. The files are then printed.
# They are written to -LogFolder (default: the temp folder), one pair per control word, and overwritten by the next call.
function Invoke-Control([string[]]$words) {
    $folder = if ($LogFolder) { $LogFolder } else { [System.IO.Path]::GetTempPath() }
    $outFile = Join-Path $folder ("stock-server-{0}.out" -f $words[0]); $errFile = Join-Path $folder ("stock-server-{0}.err" -f $words[0])
    $argList = @("`"$controlUnix`"") + @($words | ForEach-Object { "`"$_`"" })
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process -FilePath $bash -ArgumentList $argList -NoNewWindow -PassThru -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    $null = $p.Handle   # keeps the exit code readable after the wait
    $p.WaitForExit()
    foreach ($f in @($outFile, $errFile)) {
        if (Test-Path -LiteralPath $f) { Get-Content -LiteralPath $f -ErrorAction SilentlyContinue | ForEach-Object { Write-Output "$_" } }
    }
    Say ("control {0} exited {1} after {2} s" -f $words[0], $p.ExitCode, [math]::Round($clock.Elapsed.TotalSeconds, 1))
    $script:controlExit = $p.ExitCode   # a function's return value would be mixed into its output
}

switch ($Action) {
    "status" {
        Invoke-Control @("status"); $code = $script:controlExit
        if ($code -eq 0) { Say "UP"; exit 0 } else { Say "DOWN"; exit 1 }
    }
    "stop" {
        Invoke-Control @("stop"); $code = $script:controlExit
        if ($code -eq 0) { Say "STOPPED"; exit 0 }
        Say "STOP FAILED (control exit $code): the server may still be up; do not leave it"; exit 1
    }
    "start" {
        Invoke-Control @("start", $Lane); $code = $script:controlExit
        if ($code -eq 1) { Say "REFUSED: the control script started nothing (held by someone else, or already up); not stopping it"; exit 1 }
        $ready = $null
        if ($code -eq 0) {
            $deadline = (Get-Date).AddSeconds($ReadySeconds)
            do {
                $ready = Get-LoginUri $GridInfoUrl
                if ($ready) { break }
                Start-Sleep -Seconds 1
            } while ((Get-Date) -lt $deadline)
        }
        if ($ready) { Say "READY: login address $ready"; exit 0 }
        Say "NOT READY (control exit $code, no login address from the grid info within $ReadySeconds s): stopping it again"
        Invoke-Control @("stop"); $stopCode = $script:controlExit
        if ($stopCode -ne 0) { Say "STOP FAILED (control exit $stopCode): the server may still be up" }
        exit 2
    }
}
