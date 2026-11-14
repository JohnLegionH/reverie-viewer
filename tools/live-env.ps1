# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# Returns the value of a live-run setting (NV_TEST_ACCOUNTS, NV_LIVE_START, NV_GRID_INFO_URL, NV_OTHER_CLIENT_PATTERN, ...).
# A variable that is set always wins. For one that is not set, the value comes from an untracked local file of
# NAME=value lines (# starts a comment), so a live script needs no setup in the shell:
#   NV_LIVE_ENV_FILE set: that file;
#   otherwise <fixtures-local>\live.env (the same folder the local-only fixtures are read from, see fixtures-local.ps1).
# The value is also put into this process's environment, so the viewer the script starts sees it. The accounts setting is
# only a path; this script never opens the file it names. Used in a script's parameter defaults:
#   [string]$Credentials = $(& (Join-Path (Split-Path -Parent $PSCommandPath) 'live-env.ps1') NV_TEST_ACCOUNTS)
# ($PSScriptRoot is empty in a parameter default in Windows PowerShell 5.1.)
param([Parameter(Mandatory = $true)][string]$Name)

$current = [Environment]::GetEnvironmentVariable($Name)
if (-not [string]::IsNullOrEmpty($current)) { return $current }

$file = $env:NV_LIVE_ENV_FILE
if ([string]::IsNullOrWhiteSpace($file)) {
    . (Join-Path $PSScriptRoot "fixtures-local.ps1")
    try { $file = Join-Path (Get-FixturesLocal (Split-Path -Parent $PSScriptRoot)) "live.env" } catch { return }
}
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return }

foreach ($line in [System.IO.File]::ReadAllLines($file)) {
    $t = $line.Trim()
    if ($t.Length -eq 0 -or $t.StartsWith("#")) { continue }
    $eq = $t.IndexOf("=")
    if ($eq -lt 1) { continue }
    if ($t.Substring(0, $eq).Trim() -ceq $Name) {
        $value = $t.Substring($eq + 1).Trim()
        [Environment]::SetEnvironmentVariable($Name, $value)
        return $value
    }
}
