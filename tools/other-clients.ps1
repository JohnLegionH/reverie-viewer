# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
# Finds running processes of another client that may be logged in with the test account, so a live login never uses an
# account two clients hold at once. A process counts when its executable (its file name or full path) matches the
# pattern, a regex; for a process run by the dotnet host, the application it runs (the first .dll or .exe named after
# the host on its command line) counts as its executable. Nothing else on a command line is looked at, so a shell, an editor or a
# script whose command line merely names the client (its folder, a file of its) is not taken for it.
#
#   . <repo>\tools\other-clients.ps1      then  Find-OtherClients -Pattern <regex> [-Processes <objects>]
#   powershell -NoProfile -File <repo>\tools\other-clients.ps1 -Pattern <regex> [-ProcessesJson <file>]
#
# -Processes / -ProcessesJson: process objects with Name, ExecutablePath and CommandLine (tests pass their own);
# absent, the running processes. The command form prints "OTHER CLIENTS <n>" and one line per process found.
# A pattern of "none" (or empty) finds nothing.
param([string]$Pattern = "", [string]$ProcessesJson = "")

function Get-HostedApp([string]$commandLine) {
    # the dotnet host's command line: dotnet [host options and their values] <app.dll> [app arguments]
    $tokens = [regex]::Matches($commandLine, '"[^"]*"|\S+') | ForEach-Object { $_.Value.Trim('"') }
    foreach ($t in @($tokens | Select-Object -Skip 1)) {
        if ($t -match '\.(dll|exe)$') { return $t }
    }
    return $null
}

function Find-OtherClients([string]$Pattern, $Processes = $null) {
    if (-not $Pattern -or $Pattern -eq "none") { return @() }
    if ($null -eq $Processes) { $Processes = @(Get-CimInstance Win32_Process | Select-Object ProcessId, Name, ExecutablePath, CommandLine) }
    @($Processes | Where-Object {
        $p = $_
        $exe = @(@($p.Name, $p.ExecutablePath) | Where-Object { $_ })
        if ($p.Name -match '^dotnet(\.exe)?$' -and $p.CommandLine) { $app = Get-HostedApp ([string]$p.CommandLine); if ($app) { $exe += $app } }
        @($exe | Where-Object { $_ -match $Pattern }).Count -gt 0
    })
}

if ($MyInvocation.InvocationName -ne ".") {
    $procs = if ($ProcessesJson) { @(Get-Content -Raw -LiteralPath $ProcessesJson | ConvertFrom-Json) } else { $null }
    $found = @(Find-OtherClients -Pattern $Pattern -Processes $procs)
    Write-Output "OTHER CLIENTS $($found.Count)"
    foreach ($p in $found) { Write-Output "  $($p.Name)" }
}
