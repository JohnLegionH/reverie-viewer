# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later
#
# build-stamp.ps1 - dot-sourced by tools\s1.ps1 (writes the stamp) and tools\render.ps1 (checks it).
# The exported build carries build-stamp.txt, one line "commit=<full sha>": the commit the export was made from. A render run
# compares it with the commit under test, so a render never draws a build made from other code.

$BuildStampFile = "build-stamp.txt"

# The commit of the repository's checked-out HEAD, or $null when it cannot be read.
function Get-HeadCommit([string]$Repo) {
    $sha = & git -C $Repo rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $sha) { return $null }
    return ([string]$sha).Trim().ToLowerInvariant()
}

# Writes the stamp beside the export. Returns the commit written, or $null when the commit cannot be read (no stamp is written).
function Write-BuildStamp([string]$BuildDir, [string]$Repo) {
    $sha = Get-HeadCommit $Repo
    if (-not $sha) { return $null }
    [IO.File]::WriteAllText((Join-Path $BuildDir $BuildStampFile), "commit=$sha`n")
    return $sha
}

# "" when the export's stamp matches the commit under test (-ExpectedCommit, else the repository's HEAD); otherwise one line saying
# why it does not (stale, no stamp, or no commit to compare with). Fail closed: anything that cannot be compared is a mismatch.
function Test-BuildStamp([string]$BuildDir, [string]$Repo, [string]$ExpectedCommit = "") {
    $want = if ($ExpectedCommit) { $ExpectedCommit.Trim().ToLowerInvariant() } else { Get-HeadCommit $Repo }
    if (-not $want) { return "the commit under test cannot be read from $Repo (pass -ExpectedCommit)" }
    $file = Join-Path $BuildDir $BuildStampFile
    if (-not (Test-Path -LiteralPath $file)) { return "no $BuildStampFile beside the export in $BuildDir (tools\s1.ps1 writes it); commit under test $want" }
    $have = $null
    foreach ($l in @(Get-Content -LiteralPath $file)) { if ($l -match '^commit=([0-9A-Fa-f]+)\s*$') { $have = $Matches[1].ToLowerInvariant(); break } }
    if (-not $have) { return "$BuildStampFile in $BuildDir holds no 'commit=<sha>' line; commit under test $want" }
    if ($have -ne $want) { return "the export was made from commit $have, the commit under test is $want" }
    return ""
}
