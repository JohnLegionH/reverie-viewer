# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# stock-settings.ps1 - dot-sourced by stock-smoke.ps1 and stock-server.ps1. The stock smoke needs a handful of machine-specific
# settings (the two account aliases, the region name, the accounts file, the stock server's grid-info address and control script).
# They can be given as parameters, as environment variables, or as lines of one settings file kept outside the repository.
# Order of precedence: parameter, then environment variable, then the settings file.
#
# The settings file is named by -Settings or by the environment variable NV_STOCK_SETTINGS. Its lines are NAME=VALUE with the
# names the scripts already use (NV_TEST_ACCOUNTS, NV_STOCK_ACCOUNT_A, NV_STOCK_ACCOUNT_B, NV_STOCK_REGION, NV_STOCK_GRID_INFO_URL,
# NV_STOCK_SERVER_CONTROL, and optionally NV_CLAIM_WRAPPER, NV_CLAIM_LANE, NV_CLAIM_ORDER, NV_STOCK_LANE, and NV_AVATAR_POOL: the
# test-account pool file whose aliases the viewer may log in with, when the stock accounts are not in the usual one). Blank lines and lines
# starting with # are ignored. The values are names and paths. A password is never a setting: the accounts file is passed by path
# and only the viewer reads it.
# =====================================================================================

# Where the settings file is looked for, in order: the -Settings parameter, then NV_STOCK_SETTINGS.
function Get-StockSettingsCandidates([string]$Explicit) {
    $list = New-Object System.Collections.Generic.List[object]
    if ($Explicit) { $list.Add(@{ Source = "-Settings"; Path = $Explicit }) }
    if ($env:NV_STOCK_SETTINGS) { $list.Add(@{ Source = "NV_STOCK_SETTINGS"; Path = $env:NV_STOCK_SETTINGS }) }
    return $list
}

# Reads the first settings file that exists. Returns @{ Values = <hashtable>; File = <path or $null>; Looked = <text> }.
function Read-StockSettings([string]$Explicit) {
    $values = @{}
    $found = $null
    $looked = New-Object System.Collections.Generic.List[string]
    foreach ($c in (Get-StockSettingsCandidates $Explicit)) {
        if (Test-Path -LiteralPath $c.Path -PathType Leaf) { $found = $c.Path; break }
        $looked.Add("$($c.Source) = $($c.Path) (not a file)")
    }
    if ($found) {
        foreach ($line in (Get-Content -LiteralPath $found -Encoding utf8)) {
            $t = $line.Trim()
            if (-not $t -or $t.StartsWith("#")) { continue }
            $i = $t.IndexOf("=")
            if ($i -lt 1) { continue }
            $values[$t.Substring(0, $i).Trim()] = $t.Substring($i + 1).Trim()
        }
    } elseif ($looked.Count -eq 0) {
        $looked.Add("no settings file named: pass -Settings or set NV_STOCK_SETTINGS")
    }
    return @{ Values = $values; File = $found; Looked = ($looked -join "; ") }
}

# A setting's value: the parameter's, else the environment variable's, else the settings file's, else "".
function Resolve-StockSetting([string]$Current, [string]$Name, $Loaded) {
    if ($Current) { return $Current }
    $e = [Environment]::GetEnvironmentVariable($Name)
    if ($e) { return $e }
    if ($Loaded.Values.ContainsKey($Name)) { return [string]$Loaded.Values[$Name] }
    return ""
}

# Optional settings that other scripts read straight from the environment (the claim wrapper and its lane and order, the claim
# kinds the viewer guard accepts, the claim wait, the viewer's test-account pool file): set from the settings file when the environment does not already have them.
function Set-StockOptionalEnvironment($Loaded) {
    foreach ($n in @("NV_CLAIM_WRAPPER", "NV_CLAIM_LANE", "NV_CLAIM_ORDER", "NV_CLAIM_KINDS", "NV_STOCK_LANE", "CLAIM_WAIT", "NV_CHARACTER_DIR", "NV_FIXTURES_LOCAL", "NV_AVATAR_POOL")) {
        if (-not [Environment]::GetEnvironmentVariable($n) -and $Loaded.Values.ContainsKey($n)) { [Environment]::SetEnvironmentVariable($n, [string]$Loaded.Values[$n]) }
    }
}
