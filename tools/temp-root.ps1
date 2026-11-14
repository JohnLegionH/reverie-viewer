# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# where the processes a suite script starts put their temporary files.
#   NV_TEMP_ROOT unset or empty: nothing changes (TEMP and TMP stay as they are; other machines are unaffected).
#   NV_TEMP_ROOT set: an absolute folder. Use-TempRoot <name> creates <NV_TEMP_ROOT>\<name> and points TEMP and TMP of
#   this PowerShell process at it, so every process started afterwards (dotnet and its workload logs, NuGet's scratch,
#   the test host's temp paths, Godot's publish) inherits it and nothing of the run lands in the user's %TEMP%.
#   Nothing is ever deleted here: the folder is the caller's.
# Dot-source it, then call Use-TempRoot before starting any process:
#     . (Join-Path $PSScriptRoot "temp-root.ps1")
#     Use-TempRoot "test" | Out-Null
$script:TempRootVariable = "NV_TEMP_ROOT"

function Use-TempRoot([string]$name) {
    $root = [Environment]::GetEnvironmentVariable($script:TempRootVariable)
    if ([string]::IsNullOrWhiteSpace($root)) { return $null }
    if (-not [System.IO.Path]::IsPathRooted($root)) { throw "$($script:TempRootVariable) must be an absolute path (got '$root')" }
    $dir = Join-Path $root $name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $env:TEMP = $dir
    $env:TMP = $dir
    return $dir
}
