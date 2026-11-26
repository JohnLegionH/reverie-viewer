# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# inventory-live.ps1 - the Worn tab and the Item Properties window on a test account, through the automation API, on the
#   development export. Each round is a new viewer with an empty sandbox, so what it reads comes from the grid:
#     1. (from round 2) the name, description and next-owner rights saved in the round before are what the grid serves;
#     2. the Worn tab's rows equal the worn set, then again after an object is put on and after it is taken off;
#     3. Properties, picked from the item's row menu through the menu's handler (the menu is not shown), shows the item's values;
#     4. a new name, description and next-owner right are typed and clicked in (engine input);
#     5. a no-modify item of the account, when it has one, shows greyed fields that a click does not change.
#   After the last round one more login checks it and puts the item back as it was found.
#
#   powershell -NoProfile -File <repo>\tools\inventory-live.ps1 -Sandbox <new folder> -OutDir <existing folder> -Account <alias>
#     [-Rounds 10] [-Credentials <accounts file>] [-GridInfoUrl <get_grid_info URL>] [-Start <start location>] [-Exe <development export>]
#
# Run it under an avatar claim on the account. Output: inventory-check.json in -OutDir. Exit: 0 pass, 1 a check failed,
# 3 blocked, 5 the viewer or the login did not come up. Only an account meant for tests: an item of it is renamed and restored.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sandbox,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Account,
    [int]$Rounds = 10,
    [string]$Credentials = $env:NV_TEST_ACCOUNTS,
    [string]$GridInfoUrl = $env:NV_GRID_INFO_URL,
    [string]$Start = $env:NV_LIVE_START,
    [string]$Exe = "",
    [switch]$ProbeOnly
)
if (-not $Credentials) { $Credentials = & "$PSScriptRoot\live-env.ps1" NV_TEST_ACCOUNTS }
if (-not $GridInfoUrl) { $GridInfoUrl = & "$PSScriptRoot\live-env.ps1" NV_GRID_INFO_URL }
if (-not $Start) { $Start = & "$PSScriptRoot\live-env.ps1" NV_LIVE_START }
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "live-driver.ps1")
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $repo "host\Viewer.Godot\build\dev\Reverie.console.exe" }
$env:NV_OUTFIT_SCRATCH_OK = "1"

if (Test-Path -LiteralPath $Sandbox) { Write-Output "BLOCKED: -Sandbox exists: $Sandbox"; exit 3 }
$loginUri = $null
try { $loginUri = ([xml](Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $GridInfoUrl).Content).gridinfo.login } catch { }
if (-not $loginUri) { Write-Output "BLOCKED: the grid's get_grid_info did not answer with a login URI"; exit 3 }
New-Item -ItemType Directory -Force -Path $Sandbox | Out-Null

$checks = New-Object System.Collections.Generic.List[object]
function Check([int]$round, [string]$name, [bool]$pass, [string]$detail) {
    $checks.Add([ordered]@{ round = $round; name = $name; pass = $pass; detail = $detail })
    Write-Output ("  r{0}  {1}  {2}  {3}" -f $round, $(if ($pass) { "PASS" } else { "FAIL" }), $name, $detail)
}
function Inv([hashtable]$body) { Invoke-LiveApi "inventory" $body }
function Props([string]$what, [string]$id = "") { Inv @{ action = "properties"; text = $what; kind = $id } }

function Start-Session([string]$name) {
    if (-not (Start-LiveViewer (Join-Path $Sandbox $name) $Exe 90 600)) { throw "the viewer did not start" }
    if (-not (Enter-LiveWorld $Credentials $Account $loginUri $Start 180)) { throw "the login did not reach the world" }
    [void](Invoke-LiveApi "windows" @{ action = "open"; name = "inventory" })
    # every folder is fetched (a search asks for all of them), then the search is cleared
    if (-not (Wait-LiveUntil { $s = Inv @{ action = "status" }; if ($s -and $s.skeleton) { $s } } 60)) { throw "no inventory" }
    [void](Inv @{ action = "search"; text = "zzzzzz-no-such-item"; tab = "all" })
    if (-not (Wait-LiveUntil { $s = Inv @{ action = "status" }; if ($s -and $s.unloaded -eq 0 -and $s.in_flight -eq 0) { $s } } 180)) { throw "the inventory did not load" }
    [void](Inv @{ action = "search"; text = ""; tab = "all" })
}

# the Worn tab's rows equal the worn set; with an id, that item is (or is not) in them
function Wait-Worn([string]$id, [bool]$present) {
    $script:lastWorn = $null
    $ok = Wait-LiveUntil {
        $w = Inv @{ action = "worn" }
        $script:lastWorn = $w
        if (-not $w -or $w.status -ne "inventory.status.worn") { return $null }
        if ((@($w.rows) -join ",") -ne (@($w.expected) -join ",")) { return $null }
        if ($id -and ((@($w.rows) -contains $id) -ne $present)) { return $null }
        $w
    } 90
    return [bool]$ok
}

# Properties from the item's row menu, picked through the menu's handler. The menu is not shown: it is a window of its own that
# would open at the real pointer, so the right-click itself is not part of this check
function Open-Properties([string]$id) {
    [void](Invoke-LiveApi "windows" @{ action = "close"; name = "item_properties" })
    [void](Invoke-LiveApi "windows" @{ action = "open"; name = "inventory" })
    [void](Inv @{ action = "status"; tab = "all" })
    $c = Props "choose" $id
    if (-not $c -or -not $c.enabled -or $c.selected -ne $id) { Write-LiveNote "menu: $($c | ConvertTo-Json -Compress)"; return $null }
    return Wait-LiveUntil { $p = Props "state"; if ($p -and $p.shown -and $p.item -eq $id -and $p.creator -ne "(loading)" -and $p.owner -ne "(loading)") { $p } } 30
}

function Bits([long]$mask) { return "{0},{1},{2}" -f [bool]($mask -band 0x4000), [bool]($mask -band 0x8000), [bool]($mask -band 0x2000) }

function Set-Text([string]$control, [string]$text) {
    $r = Invoke-LiveClick $control
    if (-not $r.clicked) { Write-LiveNote $r.detail; return $false }
    Invoke-LiveKey "A" -Ctrl
    [void](Invoke-LiveApi "input" @{ kind = "text"; text = $text })
    Start-Sleep -Milliseconds 200
    Invoke-LiveKey "Enter"
    Start-Sleep -Milliseconds 400
    return $true
}

$exit = 0
$item = $null; $original = $null; $saved = $null; $noModify = $null
try {
    for ($round = 1; $round -le $Rounds + 1; $round++) {
        $last = $round -gt $Rounds
        Start-Session "r$round"
        if (-not $item) {
            $c = Props "candidates"
            Write-LiveNote ("candidates: full-rights notecards {0}, no-modify items {1}" -f @($c.full).Count, @($c.no_modify).Count)
            if ($ProbeOnly) { break }
            if (@($c.full).Count -eq 0) { throw "the account has no notecard it may fully change" }
            $item = [string]@($c.full)[0].id
            if (@($c.no_modify).Count -gt 0) { $noModify = [string]@($c.no_modify)[0] }
        }

        $p = Open-Properties $item
        if (-not $p) { throw "Properties did not open for the test item" }
        if ($saved) {
            $same = $p.name -ceq $saved.name -and $p.description -ceq $saved.description -and [long]$p.item_next_mask -eq $saved.mask -and (@($p.next) -join ",") -eq (Bits $saved.mask)
            Check ($round - 1) "survives_relog" $same ("name kept {0}, description kept {1}, next mask {2} expected {3}" -f ($p.name -ceq $saved.name), ($p.description -ceq $saved.description), $p.item_next_mask, $saved.mask)
        }
        if ($last) {
            # put the item back as it was found
            if ($original) {
                [void](Set-Text "ip_name" $original.name)
                if ($original.description) { [void](Set-Text "ip_description" $original.description) }
                $q = Props "state"
                if ([bool]([long]$q.item_next_mask -band 0x4000) -ne [bool]($original.mask -band 0x4000)) { [void](Invoke-LiveClick "ip_next_modify") }
                Start-Sleep -Seconds 3
            }
            Stop-LiveViewer
            break
        }

        # the window shows the item's values as this new session got them from the grid
        $acquired = [DateTimeOffset]::FromUnixTimeSeconds([long]$p.item_created).ToLocalTime().ToString("ddd MMM d HH:mm:ss yyyy", [Globalization.CultureInfo]::InvariantCulture)
        $shownOk = $p.creator -and $p.owner -and $p.creator -ne "(unknown)" -and $p.owner -ne "(unknown)" -and $p.acquired -ceq $acquired -and
            (@($p.held) -join ",") -eq (Bits ([long]$p.item_owner_mask)) -and (@($p.next) -join ",") -eq (Bits ([long]$p.item_next_mask)) -and $p.held_label -eq "You can:" -and
            $p.name_editable -and $p.description_editable   # the control for the no-modify check: an item that may be changed is open
        Check $round "properties_values" ([bool]$shownOk) ("creator and owner named {0}, acquired matches {1}, held {2}, next {3}" -f [bool]($p.creator -and $p.owner), ($p.acquired -ceq $acquired), (@($p.held) -join "/"), (@($p.next) -join "/"))
        if (-not $original) { $original = @{ name = [string]$p.name; description = [string]$p.description; mask = [long]$p.item_next_mask } }

        # a new name, description and next-owner modify, by pointer and keys
        $newName = "Inventory check $round"; $newText = "round $round"
        $before = [long]$p.item_next_mask
        $a = Set-Text "ip_name" $newName
        $b = Set-Text "ip_description" $newText
        $click = Invoke-LiveClick "ip_next_modify"
        $want = $before -bxor 0x4000
        $q = Wait-LiveUntil { $s = Props "state"; if ($s -and $s.name -ceq $newName -and $s.description -ceq $newText -and [long]$s.item_next_mask -eq $want) { $s } } 20
        Check $round "change_made" ([bool]($a -and $b -and $click.clicked -and $q)) "typed and clicked; next mask $before to $want"
        if (-not $q) { throw "the change did not show" }
        $saved = @{ name = $newName; description = $newText; mask = $want }
        Start-Sleep -Seconds 3   # the update has no reply to wait for; the next round's login is what proves it

        if ($noModify) {
            $n = Open-Properties $noModify
            if ($n) {
                $nextBefore = (@($n.next) -join ",")
                [void](Invoke-LiveClick "ip_next_modify")
                $after = Props "state"
                $grey = $n.item -eq $noModify -and -not ([long]$n.item_owner_mask -band 0x4000) -and -not $n.name_editable -and -not $n.description_editable -and @($n.next_disabled)[0] -and (@($after.next) -join ",") -eq $nextBefore
                Check $round "no_modify_greyed" ([bool]$grey) ("name editable {0}, description editable {1}, next greyed {2}, unchanged by a click {3}" -f $n.name_editable, $n.description_editable, (@($n.next_disabled) -join "/"), ((@($after.next) -join ",") -eq $nextBefore))
            } else { Check $round "no_modify_greyed" $false "Properties did not open" }
        }
        [void](Invoke-LiveApi "windows" @{ action = "close"; name = "item_properties" })

        # the Worn tab: as logged in, after an object is put on, and after it is taken off
        [void](Invoke-LiveApi "windows" @{ action = "open"; name = "inventory" })
        $first = (Wait-Worn "" $true) -and @($script:lastWorn.rows).Count -gt 0
        Check $round "worn_matches" $first ("rows {0}, worn {1}" -f @($script:lastWorn.rows).Count, @($script:lastWorn.expected).Count)
        $o = Invoke-LiveApi "inventory" @{ action = "outfit" }
        $spare = [string]@($o.spare_objects)[0]
        if ($spare) {
            $w = Invoke-LiveApi "inventory" @{ action = "wear"; id = $spare }
            $on = Wait-Worn $spare $true
            Check $round "worn_after_wear" ([bool]($w.submitted -and $on)) ("rows {0}, worn {1}, the item listed {2}" -f @($script:lastWorn.rows).Count, @($script:lastWorn.expected).Count, (@($script:lastWorn.rows) -contains $spare))
            Start-Sleep -Seconds 3
            $t = Invoke-LiveApi "inventory" @{ action = "detach"; id = $spare }
            $off = Wait-Worn $spare $false
            Check $round "worn_after_takeoff" ([bool]($t.submitted -and $off)) ("rows {0}, worn {1}, the item gone {2}" -f @($script:lastWorn.rows).Count, @($script:lastWorn.expected).Count, -not (@($script:lastWorn.rows) -contains $spare))
            Start-Sleep -Seconds 2
        } else { Check $round "worn_after_wear" $false "the account has no spare object to wear" }
        Stop-LiveViewer
    }
} catch {
    Write-LiveNote "stopped: $($_.Exception.Message)"
    if ($exit -eq 0) { $exit = 1 }
} finally {
    Stop-LiveViewer
}
@{ account = $Account; exe = $Exe; no_modify_item = [bool]$noModify; checks = $checks } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutDir "inventory-check.json") -Encoding utf8
if ($ProbeOnly) { exit $exit }
if ($exit -eq 0 -and @($checks | Where-Object { -not $_.pass }).Count -gt 0) { $exit = 1 }
foreach ($g in ($checks | Group-Object { $_.name })) { Write-Output ("{0}: {1} of {2} passed" -f $g.Name, @($g.Group | Where-Object { $_.pass }).Count, $g.Count) }
Write-Output ("inventory check: {0} checks, {1} failed, exit {2}" -f $checks.Count, @($checks | Where-Object { -not $_.pass }).Count, $exit)
exit $exit
