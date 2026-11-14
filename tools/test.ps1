# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# Runs every test suite in the repo. JUnit XML goes to tools\test-results\ (gitignored).
# Node reuse and the shared compiler are off, so no MSBuild or VBCSCompiler process outlives the run.
#     powershell -NoProfile -File <repo>\tools\test.ps1 [-DiagDir <folder>]
# -DiagDir: passed to s1.ps1 and from there to every godot.ps1 run (hang-tree snapshots go there);
# absent, godot.ps1's default (tools\test-results) stands.
# The S-1 runs' windows open at a quarter of the monitor in use, in its bottom-right corner, at the bottom of the stacking order, without focus (see render.ps1); NV_TEST_WINDOW_FULL=1 keeps the full-size window.
param([string]$DiagDir = "")
$ErrorActionPreference = "Stop"
# stopping this script, however it is stopped, ends everything it started (job-guard.ps1, beside this script)
. (Join-Path $PSScriptRoot "job-guard.ps1")
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$repo = Split-Path -Parent $PSScriptRoot
# the avatar data folder: NV_CHARACTER_DIR when it is set, else the copy of it kept in the repository
if (-not $env:NV_CHARACTER_DIR) { $env:NV_CHARACTER_DIR = Join-Path $repo "vendor\second-life-character" }
$results = Join-Path $PSScriptRoot "test-results"
New-Item -ItemType Directory -Force -Path $results | Out-Null
# every restore in this run (dotnet test's own, and the S-1 export's publish, which inherits the environment)
# runs in NuGet locked mode, so a lock-file mismatch FAILS the run instead of silently rewriting packages.lock.json.
# Environment variables are MSBuild properties, so this reaches every project and every child dotnet process.
$env:RestoreLockedMode = "true"
# run from the repo root, so no global.json of whatever directory the caller is in (another lane's, e.g. one
# pinning Microsoft.Testing.Platform) decides how dotnet test runs (never cd-dependent).
Set-Location -LiteralPath $repo
# with NV_TEMP_ROOT set, TEMP and TMP of every process this run starts point under it (tools\temp-root.ps1).
. (Join-Path $PSScriptRoot "temp-root.ps1")
$tempRoot = Use-TempRoot "test"
if ($tempRoot) { Write-Output "Temp root: $tempRoot" }

& dotnet test (Join-Path $repo "Viewer.sln") -nodeReuse:false -p:UseSharedCompilation=false `
    --logger "junit;LogFilePath=$results\{assembly}.junit.xml;MethodFormat=Class;FailureBodyFormat=Verbose" `
    --logger "console;verbosity=normal"
$code = $LASTEXITCODE

$s1Args = if ($DiagDir) { @("-DiagDir", $DiagDir) } else { @() }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "s1.ps1") @s1Args
$s1 = $LASTEXITCODE

Write-Output "JUnit XML: $results"
if ($code -ne 0) { exit $code }
exit $s1
