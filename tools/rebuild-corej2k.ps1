# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# Rebuilds the CoreJ2K package in nuget-feed\ from the fork commit it was made from, and compares the result with the
# package in the repository.
#     powershell -NoProfile -File <repo>\tools\rebuild-corej2k.ps1 -Work <empty folder> [-Out <folder>] [-ForkUrl <url or folder>]
# The package is the CoreJ2K library (JJ2000 / CSJ2K lineage, BSD-3-Clause) from https://github.com/JohnLegionH/CoreJ2K,
# a fork of https://github.com/cinderblocks/CoreJ2K, at the commit named below. The pack is deterministic: a fixed
# version, the commit stamped as the repository commit, the clone folder mapped to /_/ in the build output, the entry
# times fixed by nupkg-fixed-time.ps1, no symbols package. -Work must be an empty (or missing) folder; the fork is
# cloned into it (from -ForkUrl: the fork's address by default, or a clone of it on disk) and nothing there is deleted.
# Exit 0: the rebuilt package is byte-identical to nuget-feed\ (the SHA-256 of both is printed). Exit 1: it differs
# (both hashes and the entries that differ are printed). -Create adds the build to nuget-feed\ when it is not there yet. Nothing is installed and nothing is left running.
param(
    [Parameter(Mandatory = $true)][string]$Work,
    [string]$Out = "",
    [string]$ForkUrl = "https://github.com/JohnLegionH/CoreJ2K.git",
    [switch]$Create
)
$ErrorActionPreference = "Stop"

$Commit = "15c219da2e63c6f6fa87a5e2206b73349384c474"
$Version = "2.4.1.109-alloc.1"
$AssemblyVersion = "2.4.1.109"
$repo = Split-Path -Parent $PSScriptRoot
$feed = Join-Path $repo "nuget-feed"
$packed = Join-Path $feed "CoreJ2K.$Version.nupkg"
if (-not (Test-Path -LiteralPath $packed) -and -not $Create) { throw "missing $packed (use -Create to add the first build to nuget-feed\)" }
if ((Test-Path -LiteralPath $Work) -and (Get-ChildItem -LiteralPath $Work -Force | Select-Object -First 1)) {
    throw "-Work '$Work' must be empty: this script never deletes anything"
}
$Work = [IO.Path]::GetFullPath($Work)
if (-not $Out) { $Out = Join-Path $Work "out" }
New-Item -ItemType Directory -Force -Path $Work, $Out | Out-Null

$src = Join-Path $Work "CoreJ2K"
& git clone --quiet $ForkUrl $src
if ($LASTEXITCODE -ne 0) { throw "clone failed" }
& git -C $src checkout --quiet $Commit
if ($LASTEXITCODE -ne 0) { throw "checkout $Commit failed" }
$head = (& git -C $src rev-parse HEAD).Trim()
if ($head -ne $Commit) { throw "checked out $head, expected $Commit" }

& dotnet pack (Join-Path $src "CoreJ2K\CoreJ2K.csproj") -c Release -o $Out -nodeReuse:false -p:UseSharedCompilation=false `
    "-p:Version=$Version" "-p:PackageVersion=$Version" "-p:AssemblyVersion=$AssemblyVersion" "-p:FileVersion=$AssemblyVersion" `
    "-p:RepositoryUrl=https://github.com/JohnLegionH/CoreJ2K" "-p:RepositoryCommit=$Commit" "-p:SourceRevisionId=$Commit" `
    "-p:RepositoryBranch=alloc-on-2.4.1" `
    -p:Deterministic=true -p:ContinuousIntegrationBuild=true -p:IncludeSymbols=false -p:EmbedUntrackedSources=false `
    ("-p:PathMap=" + $src + "\=/_/")
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

$built = Join-Path $Out "CoreJ2K.$Version.nupkg"
# NuGet stamps each entry with the time of the pack; the package in nuget-feed\ has the fixed time, so set it here too
& (Join-Path $PSScriptRoot "nupkg-fixed-time.ps1") -Path $built | Out-Null
if ($Create -and -not (Test-Path -LiteralPath $packed)) { Copy-Item -LiteralPath $built -Destination $packed }
$a = (Get-FileHash -Algorithm SHA256 -LiteralPath $packed).Hash
$b = (Get-FileHash -Algorithm SHA256 -LiteralPath $built).Hash
Write-Output "committed: $a  $packed"
Write-Output "rebuilt:   $b  $built"
if ($a -eq $b) { Write-Output "IDENTICAL"; exit 0 }
Add-Type -AssemblyName System.IO.Compression.FileSystem
function EntryHash($entry) {
    $s = $entry.Open()
    try { return [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)) } finally { $s.Dispose() }
}
$za = [IO.Compression.ZipFile]::OpenRead($packed); $zb = [IO.Compression.ZipFile]::OpenRead($built)
try {
    $ea = @{}; foreach ($e in $za.Entries) { $ea[$e.FullName] = $e }
    foreach ($e in $zb.Entries) {
        if (-not $ea.ContainsKey($e.FullName)) { Write-Output "only in rebuilt: $($e.FullName)"; continue }
        $o = $ea[$e.FullName]
        if ($o.Length -ne $e.Length -or (EntryHash $o) -ne (EntryHash $e)) { Write-Output "content differs: $($e.FullName)" }
        elseif ($o.LastWriteTime -ne $e.LastWriteTime) { Write-Output "timestamp differs: $($e.FullName)" }
    }
    foreach ($k in $ea.Keys) { if (-not ($zb.Entries | Where-Object FullName -eq $k)) { Write-Output "only in committed: $k" } }
} finally { $za.Dispose(); $zb.Dispose() }
exit 1
