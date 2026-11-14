# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# claim-guard.ps1 - dot-sourced by every script that starts the viewer or the engine (godot.ps1 and the scripts that call it).
#
# Optional guard for a machine where several people or jobs share the viewer and must not overlap (a measuring run is disturbed by
# another build). It is off unless the machine says how to verify a claim, through two environment variables:
#     NV_CLAIM_CHECK_SCRIPT  path of a bash script that is called as   bash <script> check <token> <kind>...
#                            and ends with exit code 0 when <token> is a granted, unreleased, unexpired claim of one of the kinds
#                            (any other exit code, with a reason on its output, means no)
#     NV_CLAIM_KINDS         the claim kinds that allow a viewer to start, separated by spaces or commas
# With NV_CLAIM_CHECK_SCRIPT unset or empty, Assert-ClaimGranted does nothing and the launchers behave as if the guard did not exist.
# With it set, Assert-ClaimGranted ends the script with exit code 125 unless the claim token in CLAIM_TOKEN is accepted by the script
# for one of the kinds. A missing token, a script or bash that cannot be found, and an empty kind list are refused as well: a claim
# that cannot be verified is not a claim. The token is whatever the wrapper that grants claims puts in CLAIM_TOKEN for the command it runs.
# =====================================================================================

function Assert-ClaimGranted {
    $checkScript = $env:NV_CLAIM_CHECK_SCRIPT
    if (-not $checkScript) { return }   # guard not configured on this machine
    $kinds = @(($env:NV_CLAIM_KINDS -split '[\s,]+') | Where-Object { $_ })
    $token = $env:CLAIM_TOKEN
    $refuse = {
        param([string]$why)
        [Console]::Error.WriteLine("CLAIM: refused. A viewer starts only under a granted claim (CLAIM_TOKEN). $why")
        Write-Output "CLAIM: refused. $why"
        exit 125
    }
    if (-not $token) { & $refuse "No claim token: run this script through the claim wrapper." }
    if ($kinds.Count -eq 0) { & $refuse "NV_CLAIM_KINDS names no claim kind, so the claim cannot be verified." }
    if (-not (Test-Path -LiteralPath $checkScript)) { & $refuse "The claim check script was not found at $checkScript, so the claim cannot be verified." }
    $bash = $null
    foreach ($candidate in @((Get-Command bash.exe -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }),
                             (Join-Path $env:ProgramFiles "Git\bin\bash.exe"), (Join-Path $env:ProgramFiles "Git\usr\bin\bash.exe"))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { $bash = $candidate; break }
    }
    if (-not $bash) { & $refuse "bash not found, so the claim cannot be verified." }
    $scriptUnix = ((Resolve-Path -LiteralPath $checkScript).ProviderPath -replace '\\', '/')
    if ($scriptUnix -match '^([A-Za-z]):(.*)$') { $scriptUnix = "/" + $Matches[1].ToLower() + $Matches[2] }
    # the script path, token and kinds are passed in the environment, never spliced into a command line
    $env:NV_CLAIM_CHECK_UNIX = $scriptUnix
    $env:NV_CLAIM_KINDS_LIST = ($kinds -join ' ')
    # $NV_CLAIM_KINDS_LIST is left unquoted on purpose: it splits into one argument per kind
    $out = & $bash -c 'bash "$NV_CLAIM_CHECK_UNIX" check "$CLAIM_TOKEN" $NV_CLAIM_KINDS_LIST 2>&1' 2>&1
    $code = $LASTEXITCODE
    Remove-Item Env:NV_CLAIM_CHECK_UNIX -ErrorAction SilentlyContinue
    Remove-Item Env:NV_CLAIM_KINDS_LIST -ErrorAction SilentlyContinue
    if ($code -ne 0) {
        $reason = (($out | ForEach-Object { "$_" }) -join " ")
        & $refuse $reason.Trim()
    }
}
