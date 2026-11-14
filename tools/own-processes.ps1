# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# own-processes.ps1 - the one place the scripts in this folder stop a process tree (dot-sourced).
#
# Several people run test viewers on one machine, so a script stops only a process it started, by process id, never by
# image name. taskkill prints an error when the tree has already ended; under a script's stop-on-error setting that error
# would end the whole run with no summary line, so the setting is lifted around the call and an ended tree counts as done.
# Every id stopped is appended to the file named by NV_STOPPED_PIDS_FILE (when set), one line: time, id, whether it was still running.
# =====================================================================================

function Stop-OwnedTree([int]$id) {
    if ($id -le 0) { return $false }
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $running = $null -ne (Get-Process -Id $id -ErrorAction SilentlyContinue)
        if ($running) { & taskkill.exe /T /F /PID $id 2>&1 | Out-Null }
        if ($env:NV_STOPPED_PIDS_FILE) {
            Add-Content -LiteralPath $env:NV_STOPPED_PIDS_FILE -Value ("{0:o} {1} {2}" -f (Get-Date).ToUniversalTime(), $id, $(if ($running) { "stopped" } else { "already ended" })) -ErrorAction SilentlyContinue
        }
        return $running
    }
    finally { $ErrorActionPreference = $old }
}
