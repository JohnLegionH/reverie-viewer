# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# aim-lib.ps1 - dot-sourced by the live scripts that take evidence pictures of a person or an object. It puts the viewer's camera
# where the aim command (development builds) is told to, holds it, takes the picture, saves the aim's report beside it and has
# aim-check.ps1 judge the picture: the target must fill enough of the frame and sit in the middle.
#
#   . (Join-Path $PSScriptRoot "aim-lib.ps1")
#   $shot = Shoot-Aimed -Port $c.port -Token $c.token -Dir $OutDir -Name "face-front" -Spec @{ id = $agent; point = "head"; distance = 1; around_deg = 0 }
#   if (-not $shot.Pass) { ... $shot.Reasons ... }
#   Stop-Aim -Port $c.port -Token $c.token      # lets the camera follow the avatar again
#
# A spec holds the aim command's values (all but the target have defaults): id or name (an avatar) or target = "object" with local or id;
# point = head | chest | body | centre; distance (metres); around_deg (0 in front of the target, 90 on its left, 180 behind it);
# above_deg; fov_deg (the vertical one, default 40); min_fill (default 0.25 for a head, 0.5 otherwise); max_offset (default 0.1).
# =====================================================================================

function Invoke-AimRaw([int]$Port, [string]$Token, [string]$Command, [string]$Body = "{}") {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect([System.Net.IPAddress]::Loopback, $Port)
    try {
        $s = $tcp.GetStream()
        $b = [System.Text.Encoding]::UTF8.GetBytes($Body)
        $h = "POST /v1/$Command HTTP/1.1`r`nHost: 127.0.0.1:$Port`r`nAuthorization: Bearer $Token`r`nContent-Type: application/json`r`nContent-Length: $($b.Length)`r`n`r`n"
        $hb = [System.Text.Encoding]::ASCII.GetBytes($h)
        $s.Write($hb, 0, $hb.Length); $s.Write($b, 0, $b.Length)
        $all = (New-Object System.IO.StreamReader($s)).ReadToEnd()
        $i = $all.IndexOf("`r`n`r`n")
        return @{ Status = [int]($all.Split(' ')[1]); Body = $all.Substring($i + 4) }
    } finally { $tcp.Close() }
}
function ConvertFrom-AimBody([string]$Json) { try { return $Json | ConvertFrom-Json } catch { return $null } }

# aims the camera and returns the viewer's answer (the pose, the target's box and the framing check), or $null
function Send-Aim([int]$Port, [string]$Token, [hashtable]$Spec) {
    $body = @{ op = "aim" }
    foreach ($k in $Spec.Keys) { $body[$k] = $Spec[$k] }
    $r = Invoke-AimRaw $Port $Token "aim" ($body | ConvertTo-Json -Compress)
    return ConvertFrom-AimBody $r.Body
}
function Get-AimState([int]$Port, [string]$Token) {
    return ConvertFrom-AimBody (Invoke-AimRaw $Port $Token "aim" '{"op":"state"}').Body
}
# waits until the engine's camera has taken the aimed pose; returns the last state read
function Wait-AimApplied([int]$Port, [string]$Token, [int]$Seconds = 10) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $st = $null
    while ((Get-Date) -lt $deadline) {
        $st = Get-AimState $Port $Token
        if ($st -and $st.aimed -and $st.engine.applied) { return $st }
        Start-Sleep -Milliseconds 250
    }
    return $st
}
function Stop-Aim([int]$Port, [string]$Token) {
    [void](Invoke-AimRaw $Port $Token "aim" '{"op":"release"}')
}

# aims, takes a picture named -Name in -Dir (letters, digits, - and _), saves <Name>.aim.json beside it and judges it with aim-check.ps1.
# Returns Name, Pass, Reasons (an array), Detail (the check's line), Picture and ReportPath.
function Shoot-Aimed([int]$Port, [string]$Token, [string]$Dir, [string]$Name, [hashtable]$Spec, [int]$SettleMs = 1500) {
    $result = [ordered]@{ Name = $Name; Pass = $false; Reasons = @(); Detail = ""; Picture = (Join-Path $Dir "$Name.png"); ReportPath = (Join-Path $Dir "$Name.aim.json") }
    $a = Send-Aim $Port $Token $Spec
    if (-not $a -or -not $a.aimed) {
        $why = if ($a -and $a.error) { [string]$a.error } else { "no_answer" }
        $result.Reasons = @("aim_refused:$why"); $result.Detail = "AIM CHECK FAIL aim_refused:$why"
        return [pscustomobject]$result
    }
    $st = Wait-AimApplied $Port $Token
    Start-Sleep -Milliseconds $SettleMs   # textures and bakes settle; the target may still be moving a little
    $st = Get-AimState $Port $Token
    ($st | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $result.ReportPath -Encoding utf8
    [void](Invoke-AimRaw $Port $Token "screenshot" (@{ dir = $Dir; name = $Name } | ConvertTo-Json -Compress))
    $out = & (Join-Path $PSScriptRoot "aim-check.ps1") -Report $result.ReportPath -Picture $result.Picture
    $code = $LASTEXITCODE
    $line = @($out) | Select-Object -Last 1
    $result.Detail = [string]$line
    $result.Pass = ($code -eq 0)
    if (-not $result.Pass) { $result.Reasons = @(([string]$line -replace '^AIM CHECK (FAIL|ERROR):?\s*', '' -split '\s+')[0] -split ',') }
    return [pscustomobject]$result
}
