#!/usr/bin/env sh
# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# Runs every test suite in the repo. JUnit XML goes to tools/test-results/ (gitignored).
# Node reuse and the shared compiler are off, so no MSBuild or VBCSCompiler process outlives the run.
#     sh /d/native-viewer/tools/test.sh
set -e
repo="$(cd "$(dirname "$0")/.." && pwd)"
results="$repo/tools/test-results"
mkdir -p "$results"
# every restore in this run (dotnet test's own, and the S-1 export's publish, which inherits the environment)
# runs in NuGet locked mode, so a lock-file mismatch FAILS the run instead of silently rewriting packages.lock.json.
export RestoreLockedMode=true
# run from the repo root, so no global.json of the caller's directory decides how dotnet test runs.
cd "$repo"
# Under git-bash on Windows, dotnet needs a Windows path: a /c/... path is taken literally as a folder named c on the current drive
log_dir="$results"
if command -v cygpath >/dev/null 2>&1; then log_dir="$(cygpath -m "$results")"; fi
set +e
dotnet test "$repo/Viewer.sln" -nodeReuse:false -p:UseSharedCompilation=false \
    --logger "junit;LogFilePath=$log_dir/{assembly}.junit.xml;MethodFormat=Class;FailureBodyFormat=Verbose" \
    --logger "console;verbosity=normal"
code=$?
# S-1 stage (Windows only: the exported build is win-x64); tools/s1.ps1 emits its own SKIP if Godot is missing.
if command -v powershell.exe >/dev/null 2>&1; then
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$repo/tools/s1.ps1" 2>/dev/null || echo "$repo/tools/s1.ps1")"
    s1=$?
else
    echo "  SKIP    S1.all  powershell.exe not available (S-1 is a Windows export stage)"
    s1=0
fi
echo "JUnit XML: $results"
[ $code -ne 0 ] && exit $code
exit $s1
