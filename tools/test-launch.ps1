# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# Where a test or measurement run of an exported build starts from, so no run writes into the build's own folder.
# Dot-sourced by godot.ps1 (and by the unit tests, which run these functions on a made-up build folder).
#
# A test window needs an override.cfg beside the executable (the engine reads the no-focus setting from it before any
# code runs). Writing it into the build folder meant a run that was stopped from outside left it there, and the next
# normal start of that build then never took focus. Instead a run starts from its own launch folder under the
# diagnostics folder: real folders and NTFS hard links to the build's files (no copy, no space), and the override.cfg.
# It holds no junction or symbolic link, so a recursive delete of a launch folder can never follow a link out of it.
# The build's folder is only read. The launch folder is made once per version of the build (its name is a
# hash of the build's file names, sizes and write times) and reused by every run of that version; it is never removed
# by a run.

Set-StrictMode -Off

# The name of the launch folder for a build folder: a short hash of what is in it (files, not the sandbox or the dev export).
function Get-TestLaunchKey([string]$ExeDir) {
    $lines = Get-ChildItem -LiteralPath $ExeDir -File -Force |
        Where-Object { $_.Name -ne "override.cfg" -and $_.Name -notlike "*~RF*.TMP" -and $_.Name -notlike "s1-result*" } |
        Sort-Object Name | ForEach-Object { "$($_.Name)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
    $data = Get-ChildItem -LiteralPath $ExeDir -Directory -Force | Where-Object { $_.Name -like "data_*" } |
        ForEach-Object { $d = $_; Get-ChildItem -LiteralPath $d.FullName -File -Recurse -Force | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($d.FullName.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" } }
    $text = (@("layout:hard-linked-data") + @($lines) + @($data)) -join "`n"
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
    return (($hash[0..5] | ForEach-Object { $_.ToString("x2") }) -join "")
}

# Recreates the folder $Source at $Target as real folders holding hard links to its files. A link inside the source
# (junction or symbolic link) is not followed or copied.
function Copy-LinkedTree([string]$Source, [string]$Target) {
    New-Item -ItemType Directory -Force -Path $Target | Out-Null
    foreach ($e in Get-ChildItem -LiteralPath $Source -Force) {
        if ($e.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }
        if ($e.PSIsContainer) { Copy-LinkedTree $e.FullName (Join-Path $Target $e.Name) }
        else { New-Item -ItemType HardLink -Path (Join-Path $Target $e.Name) -Value $e.FullName | Out-Null }
    }
}

# Whether the exported build in $ExeDir is whole: a data_* folder beside the executable with the host assembly (Viewer.Godot.dll)
# in it. Without them the .NET host cannot start and shows a dialog on the desktop instead, so such a build is never launched.
function Test-ExportComplete([string]$ExeDir) {
    foreach ($d in @(Get-ChildItem -LiteralPath $ExeDir -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "data_*" })) {
        if (Test-Path -LiteralPath (Join-Path $d.FullName "Viewer.Godot.dll")) { return $true }
    }
    return $false
}

# Makes (or finds) the launch folder for the build in $ExeDir under $Root and returns its path. The build's folder is not
# written. Returns $null when the links cannot be made (another volume, say): the caller then opens the window as it is.
function New-TestLaunchFolder([string]$ExeDir, [string]$Root) {
    $key = Get-TestLaunchKey $ExeDir
    $folder = Join-Path $Root "launch-$key"
    if (Test-Path -LiteralPath (Join-Path $folder "launch.complete")) { return $folder }
    $making = Join-Path $Root "launch-$key.making-$PID-$([DateTime]::UtcNow.Ticks)"
    try {
        New-Item -ItemType Directory -Force -Path $making | Out-Null
        foreach ($f in Get-ChildItem -LiteralPath $ExeDir -File -Force) {
            if ($f.Name -eq "override.cfg" -or $f.Name -like "*~RF*.TMP" -or $f.Name -like "s1-result*") { continue }
            New-Item -ItemType HardLink -Path (Join-Path $making $f.Name) -Value $f.FullName | Out-Null
        }
        foreach ($d in Get-ChildItem -LiteralPath $ExeDir -Directory -Force | Where-Object { $_.Name -like "data_*" }) {
            Copy-LinkedTree $d.FullName (Join-Path $making $d.Name)
        }
        Set-Content -LiteralPath (Join-Path $making "override.cfg") -Value "[display]`r`n`r`nwindow/size/no_focus=true`r`n`r`n[rendering]`r`n`r`nshader_compiler/shader_cache/enabled=false`r`nshader_compiler/shader_cache/enabled.shipping=false`r`nrendering_device/pipeline_cache/enable=false`r`nrendering_device/pipeline_cache/enable.shipping=false`r`n" -Encoding ascii
        Set-Content -LiteralPath (Join-Path $making "launch.complete") -Value $ExeDir -Encoding ascii
    } catch {
        return $null
    }
    try { Move-Item -LiteralPath $making -Destination $folder -ErrorAction Stop } catch { }   # a run that got there first wins; ours is left aside, empty of meaning
    if (Test-Path -LiteralPath (Join-Path $folder "launch.complete")) { return $folder }
    return $null
}

# The sandbox APPDATA for an exported build's run. A sandbox that lies inside the build's folder is moved to the
# diagnostics folder, one per build folder (a hash of its path), so the build folder is not written and two builds
# never share one. The development export (the build's "dev" folder) counts as part of the build it sits in.
# Anything else is returned as given.
function Get-RunAppDataDir([string]$AppDataDir, [string]$ExeDir, [string]$DiagDir) {
    $app = [System.IO.Path]::GetFullPath($AppDataDir).TrimEnd('\')
    $exe = [System.IO.Path]::GetFullPath($ExeDir).TrimEnd('\')
    if ((Split-Path -Leaf $exe) -eq "dev") { $exe = Split-Path -Parent $exe }   # the development export sits in the shipping build's folder and shares its sandbox
    if ($app.StartsWith($exe + "\", [System.StringComparison]::OrdinalIgnoreCase)) {
        $sha = [System.Security.Cryptography.SHA1]::Create()
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($exe.ToLowerInvariant()))
        return (Join-Path (Join-Path $DiagDir "appdata") (($hash[0..5] | ForEach-Object { $_.ToString("x2") }) -join ""))
    }
    return $AppDataDir
}
