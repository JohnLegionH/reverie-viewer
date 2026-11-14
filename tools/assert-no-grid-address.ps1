# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# assert-no-grid-address.ps1 - asserts that a shipping export comes with no grid already filled in.
#
# A shipping build starts with an empty login address: the person types their grid's address. This fails on ANY of:
#   - a remembered login address or settings file inside the export (last-login-address.txt, settings.json,
#     remembered-logins.dat), which would pre-fill the login screen;
#   - a login-service address in the export's own files: a URL with an explicit port (grid login addresses carry one,
#     for example http://grid.example.org:8002/) or an IPv4 address with a port.
# Hosts reserved for examples and tests (RFC 2606 and RFC 6761: example.org, example.com, example.net, and names under
# .example, .invalid, .test and localhost) are not grids and pass.
# Scanned: the pack file, the viewer's own assemblies (Viewer.*.dll) and every text file. Third-party assemblies and the
# avatar data folder are not the viewer's own text and are left out (counted in the summary line).
#
# Usage:
#     powershell -NoProfile -File <repo>\tools\assert-no-grid-address.ps1 -Path <export folder> [-Path <export folder>...] (or one -Path of folders separated by ";")
# Exit 0 = every folder exists, holds files, and carries none of the above. Exit 1 = anything found, or a folder is
# missing or empty (a guard that checks nothing proves nothing).
# =====================================================================================
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string[]]$Path)

$ErrorActionPreference = "Stop"
$remembered = @("last-login-address.txt", "settings.json", "remembered-logins.dat")
$textExt = @(".txt", ".json", ".cfg", ".md", ".godot", ".tscn", ".tres", ".import", ".xml", ".ini", ".csv", ".gdshader", ".runtimeconfig.json")
$urlWithPort = [regex]'(?i)\b[a-z][a-z0-9+.-]{1,15}://[a-z0-9.-]+:\d{2,5}(?=[/\s"''<>]|$)'
$ipWithPort = [regex]'\b(?:\d{1,3}\.){3}\d{1,3}:\d{2,5}\b'
$reserved = [regex]'(?i)://(?:[a-z0-9-]+\.)*(?:example\.(?:org|com|net)|[a-z0-9-]+\.(?:example|invalid|test|localhost)|localhost):'
$latin1 = [Text.Encoding]::GetEncoding(28591)
$bad = 0; $scanned = 0; $skipped = 0

# -Path takes several folders, or one argument with folders separated by ";" (powershell -File passes no arrays)
$Path = @($Path | ForEach-Object { $_ -split ';' } | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
foreach ($dir in $Path) {
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) { Write-Output "GRID ADDRESS GUARD FAIL: folder not found: $dir"; exit 1 }
    $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force)
    if ($files.Count -eq 0) { Write-Output "GRID ADDRESS GUARD FAIL: no files under $dir (a guard that checks nothing proves nothing)"; exit 1 }
    $root = [IO.Path]::GetFullPath($dir).TrimEnd([char]92) + [char]92
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($root.Length)
        if ($remembered -contains $f.Name.ToLowerInvariant()) { Write-Output "GRID ADDRESS GUARD: remembered login data in the export: $rel"; $bad++; continue }
        $isOwnDll = $f.Extension -ieq ".dll" -and $f.Name -like "Viewer.*"
        $isPack = $f.Extension -ieq ".pck"
        $isText = $textExt -contains $f.Extension.ToLowerInvariant()
        if ($rel -like "character\*" -or $rel -like "*\character\*" -or -not ($isOwnDll -or $isPack -or $isText)) { $skipped++; continue }
        $scanned++
        $bytes = [IO.File]::ReadAllBytes($f.FullName)
        foreach ($text in @($latin1.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
            foreach ($m in @($urlWithPort.Matches($text) | Where-Object { -not $reserved.IsMatch($_.Value) }) + @($ipWithPort.Matches($text))) {
                Write-Output "GRID ADDRESS GUARD: $rel holds an address: $($m.Value)"
                $bad++
            }
        }
    }
}
if ($bad -gt 0) { Write-Output "GRID ADDRESS GUARD FAIL: $bad finding(s)"; exit 1 }
Write-Output "GRID ADDRESS GUARD OK: $scanned files scanned, $skipped third-party or avatar data files not scanned"
exit 0
