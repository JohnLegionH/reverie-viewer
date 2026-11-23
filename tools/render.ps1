# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# render.ps1 - L4 render goldens and L3 resource lifecycle (the test strategy 3.3), against the EXPORTED shipping
# build (tools\s1.ps1 exports it first; run this after it). Every run goes through tools\godot.ps1 and writes only to
# tools\test-results\render\ (passed after "--"; ADR-019).
# Test windows: godot.ps1 opens each run's window off every monitor (beyond the right edge of all of them), at the bottom of the
# stacking order and without taking focus; the picture is drawn at its full size (NV_TEST_WINDOW_ONSCREEN=1: in the corner of the
# monitor in use instead). The opt-in measurement sections (-Residency, -Resolutions, -Memory, -Budget, -TextureServer,
# -UploadBench, -Measure) are measuring runs ($script:Measuring, passed to godot.ps1 as -MeasureRun): same window, vsync off
# and the pacing guard on. NV_TEST_WINDOW_FULL=1 (or godot.ps1 -FullSizeWindow) restores the usual window for every run.
#   golden_<backend>        synthetic 512x256 fixture (committed) vs its committed golden, backend asserted in the sidecar
#   negative_<backend>      the same with a 40 m hill raised through the core: the diff MUST fail
#   logincard_<what>_<backend>  the login card at 1152x648: SL's long refusal (card inside the window, above the release line) and the
#                           two disclosures (panel inside the window), vs their goldens; logincard_long_negative: unbounded status MUST fail
#   loginscreen_<backend>   the login screen inside the shell, with its theme and a reserved example address (fixtures/ui) vs its golden
#                           (approved after viewing); loginscreen_negative_<backend>: the pseudo-localised strings, the diff MUST fail
#   zoo_<backend>: the committed prim zoo (fixtures\prims) vs its golden (PROPOSED until approved after viewing)
#   zoo_negative_<backend>  the zoo with one prim parameter changed through the core (a twist): the diff MUST fail
#   mesh_<cam>_<backend>: the committed mesh zoo (fixtures\meshes) at a near and a far camera vs its goldens
#                           (PROPOSED); the far camera must draw every mesh at a lower LOD than the near one
#   mesh_negative_<backend> the mesh zoo with one vertex of the cube's high LOD raised 1 m: the diff MUST fail
#   detail_levels_<backend> the committed detail-at-a-distance scene (fixtures\textures\detail-distance.texzoo.json): a
#                           mesh sphere and a textured wall at 8, 24, 48 and 96 m; the spheres' levels of detail and the
#                           walls' texture levels (wanted and on the GPU) MUST be the official viewer's choices, worked
#                           from its formulas (mesh detail factor 2.0; 640x360, 60 degrees): meshes 3,3,2,1, textures 0,2,3,4
#   detail_<backend>        the same scene vs its golden (approved after viewing)
#   layered_order_<backend> the committed layered-transparency scene (fixtures\materials\layered-transparency.texzoo.json):
#                           blended panes at known depths (a wide near pane over a far one, a pane inside a blended box,
#                           a tube's two faces, a linkset): at each probe the colour MUST be the blend arithmetic's
#                           for the official viewer's order (fullbright faces exact; the lit pane read from its reference)
#   layered_water_order_<backend> the same with water (layered-water.texzoo.json): a pane crossing the water and one under
#                           it; the water's effect read from black and white references beside each probe
#   layered_<backend>, layered_water_<backend>  each scene vs its golden (approved after viewing)
#   glow_order_<sky>_<backend> the committed glow scene (fixtures\materials\glow.matzoo.json: panels of five kinds of face at
#                           glow 0, 0.1, 0.5 and 1.0, against a dark and a bright sky): at each probe the colour MUST be the
#                           glow arithmetic's (the face's displayed colour times its glow value, blurred, added), worked in
#                           GlowZoo and GlowBlur, not from the viewer's drawing code; the control row shows no halo
#   glow_<sky>_<backend>    the same scene vs its golden (approved after viewing)
#   glow_draw_calls_busy_vulkan  LOCAL-ONLY: region A v9 from its busiest viewpoint with one face forced to glow: the glow pass draws at most 40 % of
#                           the draw calls it makes with every object drawn, and its picture equals the every-object picture byte for byte
#   glow_negative_<sky>_vulkan  the scene with the glow pass off (--tr-alter=noglow): the probes MUST fail
#   blended_once_<kind>_<backend>  one object with every face blended (box6: a prim box, mesh8: a mesh of 8 faces):
#                           every frame MUST hand the engine k surfaces to draw (each blended face once)
#   tex_<backend>: the committed texture zoo (fixtures\textures) vs its goldens (PROPOSED)
#   tex_negative_<backend>  the texture zoo with one texture offset changed through the core: the diff MUST fail
#   auto_mask_<backend>     the committed cut-out scene (fixtures\textures\auto-mask.texzoo.json: four boxes with one all-or-nothing alpha texture) vs its golden
#                           (approved after viewing); a plain and a fullbright face are drawn as cut-outs (12 faces), a glowing one and a half-transparent one stay blended
#   auto_mask_negative_<backend>  the same with the automatic mask settings off (--tr-automask=off): no face is drawn as a cut-out
#   mat_<backend>: the committed material zoo (fixtures\materials: PBR materials, their textures and a glTF override) vs
#                           its approved goldens: every PBR face drawn with its material, no asset missing, every
#                           in-view detail map (normal, metallic-roughness, emissive) at its level
#   mat_negative_<backend>  the material zoo without its glTF overrides: the diff MUST fail
#   alphazoo_<backend>: the committed alpha zoo (fixtures\materials\alpha-zoo.matzoo.json: legacy materials in every
#                           diffuse alpha mode, legacy normal and specular maps, PBR blend and mask) vs its goldens: every
#                           legacy-material and PBR face drawn with its material, every in-view detail map at its level
#   alphazoo_negative_<backend>  the alpha zoo without the region's legacy materials: the diff MUST fail
#   mat_fallback_<backend>  the material zoo with the override box's base colour held back: its faces show their legacy
#                           texture until it arrives, then their PBR material (the golden matches); no extra fetch
#   mat_fallback_off_<backend>  the same with the fallback off: the faces are blank while held, so the check MUST fail
#   mat_fallback_unshared_<backend>  a legacy texture no other face uses: neither shown nor fetched by the fallback
#   region_a_v9_<backend>   LOCAL-ONLY: region A v9 (material data, PBR assets cached): its PBR faces drawn, its legacy
#                           material faces drawn, every in-view detail map at its level, vs fixtures-local\goldens\ (approved)
#   avatarprep_<backend>    LOCAL-ONLY (NV_CHARACTER_DIR): the body parts' skinned surfaces vs the engine's packing;
#                           avatarprep_negative_vulkan: one skin byte changed, every part MUST be caught
#   avatars_<backend>, avatars_baked_<backend>  LOCAL-ONLY: one avatar in its default pose and one mid-walk (bare, and
#                           wearing the avatar's own bakes) vs fixtures-local\goldens\ (approved, fixtures\avatars\manifest.json); avatars_negative_vulkan:
#                           the walk left out, the diff MUST fail
#   crowd_real_<N>_vulkan   (LOCAL-ONLY) a crowd of N avatars with looks (crowd:N,real): every avatar shaped, morphed and baked at the server sizes, about half in a
#                           mesh body, the system body hidden exactly where the alpha bakes and the mesh body say; bakes sized by the avatar's pixel area, and the
#                           farthest avatar's bakes follow the camera up to full size and back; crowd_real_negative_vulkan: no appearances sent, MUST fail;
#                           crowd_real_same_seed_vulkan: two runs of one seed give one scene digest, another seed another
#   arrival_<N>_vulkan      (LOCAL-ONLY) N such avatars arriving within one second: all finished; the longest main-thread slice is reported against the 4 ms rule
#   region_a_v2_<backend>, LOCAL-ONLY: the region A recapture (with prim shape data) replayed with the local asset cache:
#                           prims and meshes drawn, sculpts and attachments counted; vs fixtures-local\goldens\ (PROPOSED)
#   region_a_prims             LOCAL-ONLY: the region A capture's prims (skipped counts reported; a real SKIP when absent)
#   refuse_opengl3          --test-render on opengl3 must be refused (exit 3, R-1)
#   not_drawing_<backend>   a camera-path run whose window is minimised 6 s into the path (--tr-minimize-at=6, with --tr-no-restore so the viewer does not bring it back) must stop
#                           itself within 60 s with exit 5, verdict NOT_DRAWING, the window's mode and drawing state in the
#                           result and the heartbeat (frames drawn fewer than frames processed), instead of waiting for
#                           its timeout; nothing else about a normal run changes
#   window_minimized_<backend>  the same kind of path run, 12 s, with its window minimised 4 s in: the viewer must find it minimised, bring it
#                           back without focus, finish with verdict PASS, and draw the same picture (byte for byte) as an undisturbed run
#                           (-WindowOnly runs just this case and not_drawing)
#   region_b_<backend>           LOCAL-ONLY: fixtures-local\capture-region-b.jsonl.gz replayed into the core (1024x1024) vs
#                           fixtures-local\goldens\; a real SKIP when the capture is absent
#   lifecycle_<view>_<be>   20 region changes (1024 m <-> 256 m): no live resource after each reset; nothing a region made
#                           (textures, materials) outlives its change; no engine error; engine objects and video
#                           memory after every cycle exactly as after cycle 0; nodes and render buffer memory back to the baseline
#   lifecycle_release_<be>  3 cycles built with collections held off and terrain detail textures: nothing a region made
#                           outlives its change even when no finalizer has run (the deterministic check)
#   lifecycle_leak_negative_vulkan  a run whose clear keeps one texture referenced must FAIL, per object and by the counters
#   engineerrors_<be>       engine errors on the main thread and a worker thread, collections held off: the backtraces the
#                           logger is handed are released by the main thread's drains, none left for a finalizer;
#                           engineerrors_control_keep_vulkan (a logger that hands nothing over) must FAIL
#   engineerrors_optimised_<be>  the same check with every method optimised from its first call (DOTNET_TieredCompilation=0):
#                           the worker's errors must still be logged, with no crash in the engine's error call
# -Propose     writes missing goldens as PROPOSALS (committed ones only with -Propose; a person approves after viewing).
# -Measure     also runs the ADR-003 measurement (nodes vs RenderingServer, 1024x1024) on both backends.
# -Residency   also runs the camera-path residency measurements: the (local-only region A v5 / region B v3 captures) and
#              the synthetic heavy scene (committed generator) at five budgets, paced by wall-clock time.
# -Resolutions also runs the camera paths at the reference (1920x1080) and at 3840x2160 (by
#              resolution; every frame over 33 ms and every upload unit over 4 ms with what happened in it):
#              heavy scene demand (8 GiB) on both backends and 2 GiB / 614 / 256 MiB on vulkan at 1080p; demand on both and
#              4 GiB / 2 GiB on vulkan at 4K; region A v5 (LOCAL-ONLY) at both sizes on both backends at 2 GiB (the default
#              for a 16 GB card); region B v3 (LOCAL-ONLY) at 640x360 on both. 30 s wall-clock paths, seed 8.
#              -MeasureOnly skips the test cases (measurement runs only). -PathExe runs them against another exported build (e.g. a copy with an override.cfg); -PathTagSuffix names them; -PathExtra adds harness arguments (e.g. --tr-gc-latency=sustained).
# -Memory     also runs: what the viewer holds in video memory besides the residency's textures,
#              from the engine's counters, on camera paths: the heavy scene at its demand budget (8 GiB), region A v5 and region B v3
#              (LOCAL-ONLY) at 2 GiB, each at 640x360, 1920x1080 and 3840x2160 on both backends (30 s wall-clock, seed 8).
# -Budget     also runs: camera paths with the app's own budget
#              (--tr-budget=default: this card's video memory / 2, minus the larger of 512 MiB or a fifth of that, minus the
#              engine's live non-texture memory, at least 768 MiB): the heavy scene at 1920x1080 and 3840x2160 on both
#              backends, a simulated 2 GB card (--tr-vram-mib=2048) at 1920x1080 on vulkan, region A v5 (LOCAL-ONLY) at
#              3840x2160 on both, and simulated 8 GB and 4 GB cards on the heavy scene at 3840x2160 on vulkan (30 s
#              wall-clock, seed 8).
# -TextureServer also runs: Region A v5 (LOCAL-ONLY) camera path at 1920x1080 on vulkan with its cached
#              textures served over HTTP by a local test server on loopback (127.0.0.1, an ephemeral port) and fetched through
#              the real fetch path: partial (range requests) and the whole-file control (30 s wall-clock, seed 8, 2 GiB).
#              -PathExtra "--tr-texture-delay-ms=105" simulates the grid's wait per request,
#              "--tr-first-request=chunk|level1024|<n>k" picks a first-request rule to measure; -PathTagSuffix names the runs.
# -UploadBench also runs the upload benchmark (--tr-uploadbench) on both backends: the main-thread cost
#              of one upload unit by size and stage for the earlier main-thread paths and the current one; the current path must
#              keep every 1024x1024 unit under the 4 ms presentation budget.
# -HangCheck  also runs the hang check: a lifecycle run on d3d12 whose main thread is stopped for good at a chosen
#              step (--tr-hang-test=reset:2) must time out (90 s) leaving a full dump (tools\godot.ps1, in -DiagDir) whose
#              main thread's managed stack shows the stop, and a heartbeat naming that step and cycle. Opt-in: each run
#              leaves a dump of about 1 GB, which nothing deletes.
# -LiveRepeatOnly  just the live-walker repeat cases (avatars_live_repeat: the live case rendered twice must give the same picture); implies -AvatarsOnly
# -Backends    the rendering backends the cases run on (default both: vulkan, d3d12); -Backends vulkan runs the Vulkan cases only.
# -ShowPaths   prints where the local-only fixtures are read from and exits.
# Batch mode (tools\render-serve.ps1): the cases that can share a process run in one viewer process per backend, started once and handed the cases
#              one after another, instead of one process per case; a case that crashes or hangs fails alone and the next case gets a new process. Cases
#              that need a process of their own (measuring runs, timing cases, lifecycle and leak checks, warm-up and pipeline-gate checks) are never batched.
# -OneProcessPerCase  every case in a process of its own, as before the batch mode (also NV_RENDER_ONE_PER_CASE=1).
# -ParallelBackends  (also NV_RENDER_PARALLEL=1) with more than one backend: one child render.ps1 per backend runs at the same time instead of the backends one after the other.
#              Same cases, same checks, same tolerances; the pictures are the same files, Render.junit.xml is the merge of the children's, and the exit code is the one a single run
#              would give. Needs -SkipTiming (the timing cases run alone, one backend after the other); not applied to -PlanOnly, -ShowPaths or the measuring sections.
# -SkipTiming  leaves out the timing cases (a time limit is part of the check: stall, stress, crossing, arrival, crowd and the like); each is reported as a SKIP.
# -OnlyTiming  runs only the timing cases, each alone in a process of its own, one after another (the other cases are reported as SKIP).
# -OutDir      the folder the pictures, sidecars and the JUnit file go to (default tools\test-results\render).
# Build stamp: tools\s1.ps1 writes build-stamp.txt ("commit=<sha>") beside the export. A run that renders refuses (exit 2) when the stamp
#              is missing or is not the commit under test (-ExpectedCommit, default the repository HEAD), so it never draws a stale build.
#              -AllowStaleBuild is the explicit override: the run goes on, prints what it overrode and records a build_stamp case.
# -OnlyCases   a regular expression on the case's tag (the name of its picture, e.g. 'rigged|shape-look'): the other cases are left out and reported as SKIP.
#              It filters the sections given; alone it is no section (use -All -OnlyCases '<cases>' for the full set filtered).
# Exit codes: 0 every rendered case passed; 1 a case failed; 2 no section was given (nothing rendered); 3 the sections given rendered
#             no case (every case was left out or skipped), so a run that rendered nothing never reads as a pass. -PlanOnly and
#             -ShowPaths render nothing on purpose and exit 0.
# -PlanOnly    renders nothing: lists every case the selected sections would run, per backend, as batched, in a process of its own, or a timing
#              case (plan.tsv in the output folder and a count table). Every case is reported as a SKIP.
# NV_FIXTURES_LOCAL (see tools\fixtures-local.ps1) reads the local-only fixtures from another folder, read-only:
#              with it set, -Propose is refused (nothing may be written there).
#     powershell -NoProfile -File <repo>\tools\render.ps1 [-Propose] [-Measure] [-Residency] [-UploadBench] [-ShowPaths]
# =====================================================================================
[CmdletBinding()]
param([switch]$All, [switch]$CutOutOnly, [switch]$SeamsOnly, [switch]$TreesOnly, [switch]$EditLookOnly, [switch]$AvatarsOnly, [switch]$NameTagsOnly, [switch]$GlowOnly, [switch]$GateOnly, [switch]$WarmOnly, [switch]$SkyOnly, [switch]$BanOnly, [switch]$ObjectOnly, [switch]$DialogOnly, [switch]$LoginOnly, [switch]$LiveRepeatOnly, [switch]$NeighboursOnly, [switch]$TeardownOnly, [switch]$WaterSkyOnly, [switch]$BusyOnly, [switch]$ChurnOnly, [switch]$WindowOnly, [switch]$ReuseOnly, [switch]$SkyCycleOnly, [switch]$CloudsOnly, [switch]$UiOnly, [string]$UiSceneOnly = "", [switch]$UiChromeOnly, [switch]$ProportionsOnly, [switch]$HandlesOnly, [switch]$Propose, [switch]$Measure, [int]$MeasureFrames = 300, [switch]$Residency, [switch]$UploadBench, [int]$UploadRepeats = 40, [switch]$ShowPaths, [switch]$Resolutions, [string]$PathExe = "", [string]$PathTagSuffix = "", [string]$PathFilter = "", [switch]$MeasureOnly, [string]$PathExtra = "", [switch]$Memory, [switch]$Budget, [switch]$TextureServer, [string]$DiagDir = "", [switch]$HangCheck, [string[]]$Backends = @("vulkan", "d3d12"), [switch]$OneProcessPerCase, [switch]$ParallelBackends,[switch]$SkipTiming, [switch]$OnlyTiming, [string]$OutDir = "", [switch]$PlanOnly, [string]$OnlyCases = "", [string]$ExpectedCommit = "", [switch]$AllowStaleBuild)

$ErrorActionPreference = "Stop"
# The full render set is slow and takes the machine for a long time, so it never starts by accident: it needs -All. With no section
# switch (and no -All) the script lists its sections and exits without rendering anything. Switches that only change how a run is
# made (-Propose, -Backends, -DiagDir, -MeasureOnly, the -Path* options, -MeasureFrames, -UploadRepeats) are not sections.
$sectionSwitches = @((Get-Command $PSCommandPath).Parameters.Keys | Where-Object { $_ -like "*Only" -and $_ -ne "MeasureOnly" }) + @("Measure", "Residency", "Resolutions", "Memory", "Budget", "TextureServer", "UploadBench", "HangCheck", "ShowPaths")
$sectionGiven = $All -or $UiSceneOnly -or @($sectionSwitches | Where-Object { $PSBoundParameters.ContainsKey($_) -and $PSBoundParameters[$_] }).Count -gt 0
if (-not $sectionGiven) {
    Write-Output "Sections of render.ps1 (nothing was rendered; give one or more of these, or -All for the full set):"
    Write-Output "  -All                 the full render set, every case on every backend (slow: run it on a quiet machine)"
    foreach ($s in ($sectionSwitches | Sort-Object)) { Write-Output ("  -{0}" -f $s) }
    Write-Output "  -UiSceneOnly <name>  one interface scene"
    Write-Output "Options that are not sections: -Propose, -Backends, -DiagDir, -MeasureOnly, -PathExe, -PathTagSuffix, -PathFilter, -PathExtra, -MeasureFrames, -UploadRepeats, -OneProcessPerCase, -SkipTiming, -OnlyTiming, -OutDir, -PlanOnly, -OnlyCases."
    Write-Output "The header of this script says what each section runs."
    Write-Output "RENDERED NO CASE: no section was given (-OnlyCases filters a section; the full set filtered is -All -OnlyCases '<cases>')"
    exit 2
}
# A scene name alone means that scene's section.
if ($UiSceneOnly) { $UiOnly = [switch]::Present }
# Only the opt-in measurements were asked for (no -All and no avatar section): run them without the test cases.
if (-not $All -and -not $AvatarsOnly -and -not $LiveRepeatOnly -and -not $MeasureOnly) { $MeasureOnly = [switch]::Present }
# stopping this script, however it is stopped, ends everything it started (job-guard.ps1, beside this script)
. (Join-Path $PSScriptRoot "job-guard.ps1")
. (Join-Path $PSScriptRoot "own-processes.ps1")
. (Join-Path $PSScriptRoot "claim-guard.ps1")
Assert-ClaimGranted   # no viewer or engine process without a granted lane claim (claim-guard.ps1)
$repo = Split-Path -Parent $PSScriptRoot
# the avatar data folder: NV_CHARACTER_DIR when it is set, else the copy of it kept in the repository
if (-not $env:NV_CHARACTER_DIR) { $env:NV_CHARACTER_DIR = Join-Path $repo "vendor\second-life-character" }
if ($LiveRepeatOnly) { $AvatarsOnly = [switch]::Present }   # the repeat cases are part of the avatar section
. (Join-Path $PSScriptRoot "fixtures-local.ps1")
# with NV_TEMP_ROOT set, TEMP and TMP of every process this run starts point under it (tools\temp-root.ps1).
. (Join-Path $PSScriptRoot "temp-root.ps1")
$tempRoot = Use-TempRoot "render"
if ($tempRoot) { Write-Output "Temp root: $tempRoot" }
$local = Get-FixturesLocal $repo
if ($Propose -and (Test-FixturesLocalOverride)) {
    Write-Output "REFUSED: -Propose with $FixturesLocalVariable set ($local): the local-only fixtures are read-only to this run. Nothing was written."
    exit 2
}
if ($ShowPaths) { Write-Output "fixtures-local: $local"; exit 0 }
$build = Join-Path $repo "host\Viewer.Godot\build"
$exe = Join-Path $build "Reverie.console.exe"
$godot = Join-Path $PSScriptRoot "godot.ps1"
# -DiagDir is passed to every godot.ps1 run (hang-tree snapshots go there); absent, godot.ps1's
# default (tools\test-results) stands
$diagArgs = if ($DiagDir) { @("-DiagDir", $DiagDir) } else { @() }
$results = Join-Path $PSScriptRoot "test-results"
$out = if ($OutDir) { $OutDir } else { Join-Path $results "render" }
# -ParallelBackends (or NV_RENDER_PARALLEL=1): one child render.ps1 per backend, run at the same time, instead of the backends one after the other.
# Each child gets the same arguments with -Backends <one backend> and writes its own pictures (their tags carry the backend, so the files
# do not collide) and its own Render.<backend>.junit.xml; this run merges them into Render.junit.xml, prints each child's output, and
# ends with the exit code a single run would give. The cases, the checks and the tolerances are the children's, unchanged. Not used
# for -PlanOnly, -ShowPaths, the measuring sections (their numbers are timings, which a second run at the same time would disturb) or a
# single backend.
$measuringSections = @("Measure", "Residency", "Resolutions", "Memory", "Budget", "TextureServer", "UploadBench", "HangCheck", "MeasureOnly", "OnlyTiming")
$wantParallel = ($ParallelBackends -or $env:NV_RENDER_PARALLEL -eq "1") -and @($Backends).Count -gt 1 -and -not $PlanOnly -and -not $ShowPaths -and -not $env:NV_RENDER_JUNIT_NAME `
    -and -not @($measuringSections | Where-Object { $PSBoundParameters.ContainsKey($_) -and $PSBoundParameters[$_] }).Count
if ($wantParallel -and -not $SkipTiming) {
    # a timing case's time limit is part of its check, so a second backend drawing at the same time could fail it: they run one after the other
    Write-Output "NOTE: -ParallelBackends needs -SkipTiming (the timing cases keep their machine to themselves); running the backends one after the other."
    $wantParallel = $false
}
if ($wantParallel) {
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $forward = @()
    foreach ($k in $PSBoundParameters.Keys) {
        if ($k -in @("Backends", "ParallelBackends", "OutDir")) { continue }
        $v = $PSBoundParameters[$k]
        if ($v -is [System.Management.Automation.SwitchParameter]) { if ($v.IsPresent) { $forward += "-$k" } }
        else { $forward += "-$k"; $forward += [string]$v }
    }
    $jobs = @()
    foreach ($be in $Backends) {
        $stdout = Join-Path $out "render-$be.stdout.txt"
        $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath) + $forward + @("-Backends", $be, "-OutDir", $out)
        $env:NV_RENDER_JUNIT_NAME = "Render.$be.junit.xml"; $env:NV_RENDER_ONLY_DRIVER = $be
        $p = Start-Process -FilePath "powershell.exe" -ArgumentList ($argList | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) `
            -RedirectStandardOutput $stdout -NoNewWindow -PassThru
        $null = $p.Handle   # keeps the exit code readable after the wait
        Remove-Item Env:NV_RENDER_JUNIT_NAME, Env:NV_RENDER_ONLY_DRIVER -ErrorAction SilentlyContinue
        $jobs += [pscustomobject]@{ Backend = $be; Process = $p; Stdout = $stdout }
    }
    foreach ($j in $jobs) { $j.Process.WaitForExit() }
    $codes = @(); $merged = @()
    foreach ($j in $jobs) {
        $code = $j.Process.ExitCode; $codes += $code
        Write-Output "---- backend $($j.Backend) (exit $code) ----"
        Get-Content -LiteralPath $j.Stdout | ForEach-Object { Write-Output $_ }
        $jf = Join-Path $out "Render.$($j.Backend).junit.xml"
        if (Test-Path -LiteralPath $jf) { $merged += ([xml](Get-Content -Raw -LiteralPath $jf)).testsuites.testsuite.testcase }
    }
    # a case both children report (one ran it, the other left it to the first, or a check that is not a process) is one case: the one that ran
    $merged = @($merged | Group-Object { $_.name } | ForEach-Object { $ran = @($_.Group | Where-Object { -not $_.skipped }); if ($ran.Count -gt 0) { $ran[0] } else { $_.Group[0] } })
    $mfail = @($merged | Where-Object { $_.failure }).Count
    $mskip = @($merged | Where-Object { $_.skipped }).Count
    $mx = New-Object System.Text.StringBuilder
    [void]$mx.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$mx.AppendLine("<testsuites><testsuite name=""Render"" tests=""$($merged.Count)"" failures=""$mfail"" skipped=""$mskip"" errors=""0"">")
    foreach ($tc in $merged) { [void]$mx.AppendLine($tc.OuterXml) }
    [void]$mx.AppendLine("</testsuite></testsuites>")
    Set-Content -LiteralPath (Join-Path $out "Render.junit.xml") -Value $mx.ToString() -Encoding utf8
    Write-Output ("Render summary (backends run in parallel): total {0}, passed {1}, failed {2}, skipped {3}" -f $merged.Count, ($merged.Count - $mfail - $mskip), $mfail, $mskip)
    # the exit code a single run over both backends gives: 3 only when no backend rendered a case, 2 for no section, 1 for any failure
    $final = if (@($codes | Where-Object { $_ -ne 3 }).Count -eq 0) { 3 } elseif ($codes -contains 2) { 2 } elseif (@($codes | Where-Object { $_ -notin @(0, 3) }).Count -gt 0) { 1 } else { 0 }
    exit $final
}
. (Join-Path $PSScriptRoot "render-serve.ps1")
$serveOff = [bool]($OneProcessPerCase -or $env:NV_RENDER_ONE_PER_CASE -eq "1")
$script:Counts = @{ Served = 0; OwnProcess = 0; Skipped = 0 }
$script:Plan = New-Object System.Collections.Generic.List[object]
if ($PlanOnly) { $ErrorActionPreference = "Continue" }   # a plan has no results, so the checks that read them find nothing; that is not an error here
$script:SkipPending = $false
$fixture = Join-Path $repo "fixtures\terrain\synthetic-512x256.nvterrain.gz"
$regionB = Join-Path $local "capture-region-b.jsonl.gz"
$regionA = Join-Path $local "capture-region-a.jsonl.gz"
$regionA2 = Join-Path $local "capture-region-a-v2.jsonl.gz"
$assets = Join-Path $local "assets"
$zoo = Join-Path $repo "fixtures\prims\prim-zoo.primzoo.json"
$meshZoo = Join-Path $repo "fixtures\meshes\mesh-zoo.meshzoo.json"
$meshDir = Join-Path $repo "fixtures\meshes"
$texZoo = Join-Path $repo "fixtures\textures\texture-zoo.texzoo.json"
$detailZoo = Join-Path $repo "fixtures\textures\detail-distance.texzoo.json"
$matZoo = Join-Path $repo "fixtures\materials\material-zoo.matzoo.json"
$alphaZoo = Join-Path $repo "fixtures\materials\alpha-zoo.matzoo.json"
$layeredZoo = Join-Path $repo "fixtures\materials\layered-transparency.texzoo.json"
$layeredWater = Join-Path $repo "fixtures\materials\layered-water.texzoo.json"
$glowZoo = Join-Path $repo "fixtures\materials\glow.matzoo.json"
$texDir = Join-Path $repo "fixtures\textures"
$localGoldens = Join-Path $local "goldens"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$cases = New-Object System.Collections.Generic.List[object]
function Add-Case([string]$name, [string]$status, [string]$detail, [double]$seconds) {
    if ($script:SkipPending) {   # the render this case reads was left out (-SkipTiming / -OnlyTiming): a real skip with its reason, never a pass or a fail
        $status = "SKIP"
        $detail = if ($PlanOnly) { "planned only: nothing was rendered (-PlanOnly)" } elseif ($OnlyCases) { "left out by -OnlyCases" } elseif ($OnlyTiming) { "not a timing case: left out by -OnlyTiming" } else { "timing case: left out by -SkipTiming (run it alone with -OnlyTiming)" }
    }
    $cases.Add([pscustomobject]@{ Name = $name; Status = $status; Detail = $detail; Seconds = $seconds })
    Write-Output ("  {0,-7} Render.{1}  {2}" -f $status, $name, $detail)
}
function Esc([string]$s) { [System.Security.SecurityElement]::Escape($s) }
# Writes the results and ends the run with $code, or with 3 when the sections given rendered no case at all (every case left out
# by -OnlyCases, -SkipTiming or -OnlyTiming, or skipped): a run that rendered nothing must not read as a pass. -PlanOnly renders
# nothing on purpose.
function Exit-Render([int]$code) {
    Write-JUnit
    if (-not $PlanOnly -and @($cases | Where-Object Status -ne "SKIP").Count -eq 0) {
        Write-Output "RENDERED NO CASE: $($cases.Count) case(s) reported, all skipped$(if ($OnlyCases) { " (-OnlyCases '$OnlyCases' matched none of the cases these sections render)" })"
        exit 3
    }
    exit $code
}
function Write-JUnit {
    $fail = @($cases | Where-Object Status -eq "FAIL").Count
    $skip = @($cases | Where-Object Status -eq "SKIP").Count
    $xml = New-Object System.Text.StringBuilder
    [void]$xml.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$xml.AppendLine("<testsuites><testsuite name=""Render"" tests=""$($cases.Count)"" failures=""$fail"" skipped=""$skip"" errors=""0"">")
    foreach ($c in $cases) {
        [void]$xml.Append("<testcase classname=""Render"" name=""$(Esc $c.Name)"" time=""$($c.Seconds)"">")
        if ($c.Status -eq "FAIL") { [void]$xml.Append("<failure message=""$(Esc $c.Detail)"" />") }
        elseif ($c.Status -eq "SKIP") { [void]$xml.Append("<skipped message=""$(Esc $c.Detail)"" />") }
        else { [void]$xml.Append("<system-out>$(Esc $c.Detail)</system-out>") }
        [void]$xml.AppendLine("</testcase>")
    }
    [void]$xml.AppendLine("</testsuite></testsuites>")
    Stop-ServeWorkers
    if ($PlanOnly) {
        $script:Plan | ForEach-Object { "{0}`t{1}`t{2}`t{3}`t{4}" -f $_.Driver, $_.Tag, $_.Kind, $_.Timeout, $_.Why } | Set-Content -LiteralPath (Join-Path $out "plan.tsv") -Encoding utf8
        Write-Output "Plan (cases the selected sections would render):"
        $script:Plan | Group-Object Driver, Kind | Sort-Object Name | ForEach-Object { Write-Output ("  {0,-22} {1}" -f $_.Name, $_.Count) }
        Write-Output "Cases with their own process, by reason:"
        $script:Plan | Where-Object Kind -eq "own-process" | Group-Object Why | Sort-Object Count -Descending | ForEach-Object { Write-Output ("  {0,4}  {1}" -f $_.Count, $_.Name) }
    }
    Set-Content -LiteralPath (Join-Path $(if ($OutDir) { $out } else { $results }) $(if ($env:NV_RENDER_JUNIT_NAME) { $env:NV_RENDER_JUNIT_NAME } else { "Render.junit.xml" })) -Value $xml.ToString() -Encoding utf8
    Write-Output ("Render summary: total {0}, passed {1}, failed {2}, skipped {3}" -f $cases.Count, @($cases | Where-Object Status -eq "PASS").Count, $fail, $skip)
    $st = Get-ServeStats
    Write-Output ("Render batch: {0} renders in batch processes ({1} started, {2} could not start), {3} in a process of their own, {4} left out by the timing switches" -f $script:Counts.Served, $st.Starts, $st.Failures, $script:Counts.OwnProcess, $script:Counts.Skipped)
}

# One --test-render run. Returns @{ Exit; Json; Seconds }.
$script:Measuring = $false   # true while the opt-in measurement sections run: their windows keep the usual size
# Starts the viewer process that serves one backend's cases (tools\render-serve.ps1 calls this); the process runs under godot.ps1 like every other.
function Start-ServeProcess([string]$driver, [string]$dir) {
    function Q([string]$v) { return '"' + ($v -replace '"', '\"') + '"' }
    $argLine = "--rendering-driver $driver -- --test-render `"--tr-serve=$dir`" `"--tr-out=$out`" --tr-tag=serve-$driver"
    $a = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Q $godot))
    if ($DiagDir) { $a += @("-DiagDir", (Q $DiagDir)) }
    $a += @("-Exe", (Q $exe), "-AppDataDir", (Q (Join-Path $build "appdata")), "-TimeoutSeconds", "21600", "-LogFile", (Q (Join-Path $dir "worker.log")), "-ServeDir", (Q $dir), "-ArgLine", (Q $argLine))
    return Start-Process -FilePath (Get-Command powershell.exe).Source -ArgumentList ($a -join " ") -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dir "launcher.out.txt") -RedirectStandardError (Join-Path $dir "launcher.err.txt")
}
# True when the timing switches leave this case out
function Test-LeftOut([string]$tag) {
    $timing = Test-TimingCase $tag
    if ($OnlyCases -and $tag -notmatch $OnlyCases) { return $true }
    return ($SkipTiming -and $timing) -or ($OnlyTiming -and -not $timing)
}
# One case: in the batch process of its backend when it can share one, else in a process of its own (see render-serve.ps1)
function Invoke-CaseProcess([string]$driver, [string]$tag, [string]$extra, [int]$timeout, [bool]$noDump) {
    if ($PlanOnly) {
        $d = Test-CaseServable $driver $tag $extra $script:Measuring $noDump
        $kind = if (Test-TimingCase $tag) { "timing" } elseif ($d.Serve) { "batched" } else { "own-process" }
        $script:Plan.Add([pscustomobject]@{ Driver = $driver; Tag = $tag; Kind = $kind; Why = $d.Why; Timeout = $timeout })
        $script:SkipPending = $true
        return @{ Exit = -1; Json = $null; Seconds = 0; Skipped = $true }
    }
    # -ParallelBackends: a child runs only the processes of its own driver (every driver but d3d12 belongs to the vulkan child), so a case
    # that is always drawn on one driver (its tag says which) is run once, by that driver's child, not by both
    if ((Test-LeftOut $tag) -or ($env:NV_RENDER_ONLY_DRIVER -and (($driver -eq "d3d12") -ne ($env:NV_RENDER_ONLY_DRIVER -eq "d3d12")))) {
        $script:SkipPending = $true
        $script:Counts.Skipped++
        return @{ Exit = -1; Json = $null; Seconds = 0; Skipped = $true }
    }
    $script:SkipPending = $false
    if (-not $serveOff -and -not $script:Serve.Disabled) {
        $d = Test-CaseServable $driver $tag $extra $script:Measuring $noDump
        if ($d.Serve) {
            $r = Invoke-ServedCase $driver $tag $extra $timeout $out { param($drv, $dir) Start-ServeProcess $drv $dir }
            if ($r) { $script:Counts.Served++; return $r }
        }
    }
    # A case that has a process of its own runs alone: the batch processes are ended first (they start again when a later case needs one).
    # An idle batch process still draws frames in its window, which was enough to change the pictures of the pose-sensitive avatar cases.
    Stop-ServeWorkers
    $script:Counts.OwnProcess++
    return $null
}
function Invoke-Render([string]$driver, [string]$tag, [string]$extra, [int]$timeout = 240, [switch]$NoDump) {
    $served = Invoke-CaseProcess $driver $tag $extra $timeout ([bool]$NoDump)
    if ($served) { return $served }
    $t0 = Get-Date
    $json = Join-Path $out "$tag.json"
    $windowArgs = if ($script:Measuring) { @("-MeasureRun") } else { @() }
    if ($NoDump) { $windowArgs += @("-DotnetDump", (Join-Path $out "no-dump-tool.exe")) }   # a run that is expected to be stopped: no multi-GB dump
    & powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs @windowArgs -Exe $exe -AppDataDir (Join-Path $build "appdata") -TimeoutSeconds $timeout `
        -LogFile (Join-Path $out "$tag.log") -ArgLine "--rendering-driver $driver -- --test-render `"--tr-out=$out`" --tr-tag=$tag $extra" | Out-Null
    $code = $LASTEXITCODE
    $j = if ((Test-Path -LiteralPath $json) -and (Get-Item -LiteralPath $json).LastWriteTime -ge $t0.AddSeconds(-1)) { Get-Content -Raw -LiteralPath $json | ConvertFrom-Json } else { $null }
    return @{ Exit = $code; Json = $j; Seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) }
}

# the glow scene against a dark and a bright sky: the probes' arithmetic, then each picture against its golden
function Invoke-GlowCases([string]$be) {
    foreach ($sky in @("dark", "bright")) {
        $golden = Join-Path $repo "fixtures\materials\golden-glow-$sky-$be.png"
        $hasGolden = Test-Path -LiteralPath $golden
        $goldenArg = if ($hasGolden) { "`"--tr-golden=$golden`"" } else { "" }
        $tag = "glow-$sky-$be"
        $r = Invoke-Render $be $tag "`"--tr-fixture=$glowZoo`" --tr-camera=glow --tr-sky=$sky $goldenArg"
        $pr = $r.Json.probes
        $bad = @(if ($pr) { $pr.rows | Where-Object { -not $_.pass } | ForEach-Object { "$($_.name) [$($_.rgb -join ',')] expected [$($_.expected -join ',')] ($($_.what))" } })
        $ok = $r.Json -and $r.Json.backend.driver -eq $be -and $pr -and $pr.pass -and $pr.checked_probes -gt 0
        Add-Case "glow_order_${sky}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($pr) { "$($pr.checked_probes) probes checked, tolerance $($pr.tolerance), sky [$($pr.sky -join ',')]" } else { 'no probes' })$(if ($bad.Count) { '; wrong (' + $bad.Count + '): ' + (($bad | Select-Object -First 6) -join '; ') })$(if (-not $r.Json) { '; no result, exit ' + $r.Exit })" $r.Seconds
        if (-not $hasGolden) {
            if ($Propose -and $r.Json -and $r.Json.backend.driver -eq $be -and (Test-Path -LiteralPath (Join-Path $out "$tag.png"))) {
                Copy-Item -LiteralPath (Join-Path $out "$tag.png") -Destination $golden
                Add-Case "glow_${sky}_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else {
                Add-Case "glow_${sky}_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds
            }
        } else {
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case "glow_${sky}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
    }
}
# prim lights (Docs/PROJECTOR-SPEC.md): the projector scene (fixtures\materials\projector.matzoo.json: four projector lamps throwing a patterned
# picture on a wall and a floor, at two focus values and two fields of view, one with ambiance, and a glowing panel in a beam) in a nearly dark region.
#   glow_light_<light>_vulkan  a glowing face lit only by that light (spot = projector, omni = plain point light, sun, moon, ambient): the glow
#                              picture's pixel at the face must equal the view's own (read with the glow pass off), within 3 of 255
#   glow_light_negative_<light>_vulkan  the same with the lights kept off the glow picture's layer: the picture must NOT match
#   projector_vulkan           the scene vs its golden (PROPOSED until approved after viewing)
#   projector_probes_vulkan    a patch of pixels at each of 16 points (inside each beam on the wall and the floor, and outside it above and
#                              beside it): the pattern's contrast and brightness inside, dark and flat outside
#   projector_negative_vulkan  the same probes with no lights at all (--tr-lights=none): most of them MUST fail
#   projector_shadow_probes_vulkan  two patches on the wall inside one beam: behind the scene's occluder (dark) and beside it (lit)
#   projector_shadow_negative_vulkan  the same with the projector shadow setting off (--tr-shadows=off): the probes MUST fail
#   projector_shadow_passes_vulkan  the busy view with eight projectors: with the shadow setting off no draw call goes into any shadow map in any frame; with it on, the ranked casters' passes are drawn
function Invoke-ProjectorCases {
    $zoo = Join-Path $repo "fixtures\materials\projector.matzoo.json"
    foreach ($light in @("spot", "omni", "sun", "moon", "ambient")) {
        $la = if ($light -eq "spot") { "" } else { "--tr-lights=$light" }
        $r = Invoke-Render "vulkan" "glow-light-$light-vulkan" "`"--tr-fixture=$zoo`" --tr-camera=projector $la" 120
        $g = $r.Json.glow_light
        $ok = $r.Exit -eq 0 -and $g -and $g.max_diff -ge 0 -and $g.max_diff -le 3
        Add-Case "glow_light_${light}_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($g) { "glow picture [$($g.picture -join ',')] view [$($g.view_without_glow -join ',')] max difference $($g.max_diff)" } else { 'no result, exit ' + $r.Exit })" $r.Seconds
        if ($light -eq "ambient") { continue }   # the ambient light is copied to the glow picture's environment, not carried by a layer
        $r = Invoke-Render "vulkan" "glow-light-neg-$light-vulkan" "`"--tr-fixture=$zoo`" --tr-camera=projector $la --tr-alter=nolightglow" 120
        $g = $r.Json.glow_light
        $ok = $r.Exit -eq 0 -and $g -and $g.max_diff -gt 3
        Add-Case "glow_light_negative_${light}_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($g) { "with the lights off the glow layer the picture differs by $($g.max_diff), as it must" } else { 'no result, exit ' + $r.Exit })" $r.Seconds
    }
    $golden = Join-Path $repo "fixtures\materials\golden-projector-vulkan.png"
    $hasGolden = Test-Path -LiteralPath $golden
    $goldenArg = if ($hasGolden) { "`"--tr-golden=$golden`"" } else { "" }
    $r = Invoke-Render "vulkan" "projector-vulkan" "`"--tr-fixture=$zoo`" --tr-camera=projector $goldenArg" 120
    $pj = $r.Json.projector
    $pp = $r.Json.probes
    $badRows = @(if ($pp) { $pp.rows | Where-Object { -not $_.pass } | ForEach-Object { "$($_.name) [max $($_.max), contrast $($_.contrast)]" } })
    Add-Case "projector_probes_vulkan" ($(if ($pp -and $pp.pass -and $pp.checked_probes -eq 16) { "PASS" } else { "FAIL" })) "$(if ($pp) { "$($pp.passed_probes) of $($pp.checked_probes) probes pass (patch $($pp.patch) px)" } else { 'no probes' })$(if ($badRows.Count) { '; wrong: ' + ($badRows -join '; ') })" $r.Seconds
    $n = Invoke-Render "vulkan" "projector-negative-vulkan" "`"--tr-fixture=$zoo`" --tr-camera=projector --tr-lights=none" 120
    $np = $n.Json.probes
    $nFailed = if ($np) { $np.checked_probes - $np.passed_probes } else { 0 }
    Add-Case "projector_negative_vulkan" ($(if ($np -and $nFailed -gt $np.checked_probes / 2 -and $n.Json.projector.spot_lights -eq 0) { "PASS" } else { "FAIL" })) "$(if ($np) { "$nFailed of $($np.checked_probes) probes fail with no lights, as they must ($($n.Json.projector.spot_lights) spot lights)" } else { 'no probes (exit ' + $n.Exit + ')' })" $n.Seconds
    $sp = $r.Json.shadow_probes
    $badShadow = @(if ($sp) { $sp.rows | Where-Object { -not $_.pass } | ForEach-Object { "$($_.name) [max $($_.max), contrast $($_.contrast)]" } })
    Add-Case "projector_shadow_probes_vulkan" ($(if ($sp -and $sp.pass -and $sp.checked_probes -eq 2 -and $sp.shadows) { "PASS" } else { "FAIL" })) "$(if ($sp) { "$($sp.passed_probes) of $($sp.checked_probes) shadow probes pass: the wall patch behind the occluder is dark, the one beside it is lit" } else { 'no shadow probes' })$(if ($badShadow.Count) { '; wrong: ' + ($badShadow -join '; ') })" $r.Seconds
    $so = Invoke-Render "vulkan" "projector-shadows-off-vulkan" "`"--tr-fixture=$zoo`" --tr-camera=projector --tr-shadows=off" 120
    $sop = $so.Json.shadow_probes
    Add-Case "projector_shadow_negative_vulkan" ($(if ($sop -and -not $sop.shadows -and $sop.passed_probes -lt $sop.checked_probes) { "PASS" } else { "FAIL" })) "$(if ($sop) { "with projector shadows off $($sop.checked_probes - $sop.passed_probes) of $($sop.checked_probes) shadow probes fail, as they must" } else { 'no shadow probes (exit ' + $so.Exit + ')' })" $so.Seconds
    $lit = $r.Exit -ne 5 -and $pj -and $pj.spot_lights -eq 4
    if (-not $hasGolden) {
        if ($Propose -and $r.Json -and (Test-Path -LiteralPath (Join-Path $out "projector-vulkan.png"))) {
            Copy-Item -LiteralPath (Join-Path $out "projector-vulkan.png") -Destination $golden
            Add-Case "projector_vulkan" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else { Add-Case "projector_vulkan" "FAIL" "no committed golden $golden (render exit $($r.Exit))" $r.Seconds }
    } else {
        $ok = $r.Exit -eq 0 -and $lit -and $r.Json.diff.pass
        Add-Case "projector_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$($pj.spot_lights) spot lights, $($r.Json.diff.summary)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
}
function Invoke-GlowNegative {
    # the negative control: the same scene with the glow pass off must fail its probes (the control row alone still passes)
    $r = Invoke-Render "vulkan" "glow-negative-dark-vulkan" "`"--tr-fixture=$glowZoo`" --tr-camera=glow --tr-sky=dark --tr-alter=noglow"
    $pr = $r.Json.probes
    $failed = if ($pr) { @($pr.rows | Where-Object { -not $_.pass }).Count } else { 0 }
    Add-Case "glow_negative_dark_vulkan" ($(if ($pr -and -not $pr.pass -and $failed -gt 0) { "PASS" } else { "FAIL" })) "$(if ($pr) { "$failed of $(@($pr.rows).Count) probes fail with the glow pass off, as they must" } else { 'no probes (exit ' + $r.Exit + ')' })" $r.Seconds
}

# the glow pass in a busy view (LOCAL-ONLY): region A v9 from the viewpoint with the most draw calls, one face near the view's
# target forced to glow. The pass must draw only the glowing objects and what can hide them (a small share of the scene's
# draw calls), and the picture it makes that way must be byte-for-byte the one made with every object drawn.
# the busy view (--tr-busy=objects:N,glow:percent,lights:L,projectors:K, tests\BusySceneTests.cs): a small one, drawn for a few seconds on the circling eye; the counts the
# engine layer holds must be the counts asked for (no golden: the picture moves). The measuring harness runs the same switch at large sizes.
function Invoke-BusySmoke {
    $zoo = Join-Path $repo "fixtures\materials\projector.matzoo.json"
    $r = Invoke-Render "vulkan" "busy-smoke-vulkan" "`"--tr-fixture=$zoo`" --tr-busy=objects:60,glow:25,lights:6,projectors:2 --tr-busy-seconds=3 --tr-size=640x360" 180
    $b = $r.Json.busy
    $ok = $r.Exit -eq 0 -and $b -and $b.counts.objects -eq 60 -and $b.counts.glowing -eq 15 -and $b.counts.lights -eq 6 -and $b.counts.projectors -eq 2 -and
        $b.held.glow_objects -eq 15 -and $b.held.spot_lights -eq 2 -and $b.held.omni_lights -eq 4 -and $b.frames -gt 0 -and $b.glow_choice_runs -gt 0 -and $b.pass_draw_calls.p50 -gt 0
    Add-Case "busy_smoke_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($b) { "asked 60 objects / 25 % glow / 6 lights / 2 projectors; scene $($b.counts.objects) / $($b.counts.glowing) glowing / $($b.counts.lights) / $($b.counts.projectors); engine holds $($b.held.glow_objects) glowing objects, $($b.held.omni_lights) omni and $($b.held.spot_lights) spot lights; $($b.frames) frames, object choice ran $($b.glow_choice_runs) times, glow pass draw calls p50 $($b.pass_draw_calls.p50)" } else { 'no busy result' })$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    # projector shadow passes: eight projectors in the busy view. With the projector shadow setting off no projector may render a shadow pass
    # (no draw call into any shadow map in any frame); with it on, the ranked casters still do (the passes are not simply gone).
    $off = Invoke-Render "vulkan" "projector-shadow-passes-off-vulkan" "`"--tr-fixture=$zoo`" --tr-busy=objects:1000,glow:10,lights:8,projectors:8 --tr-busy-seconds=4 --tr-size=960x540 --tr-shadows=off" 240
    $on = Invoke-Render "vulkan" "projector-shadow-passes-on-vulkan" "`"--tr-fixture=$zoo`" --tr-busy=objects:1000,glow:10,lights:8,projectors:8 --tr-busy-seconds=4 --tr-size=960x540" 240
    $so = $off.Json.busy.shadow_draw_calls; $sn = $on.Json.busy.shadow_draw_calls
    $ok2 = $off.Exit -eq 0 -and $on.Exit -eq 0 -and $so -and $sn -and $off.Json.busy.counts.projectors -eq 8 -and $so.max -eq 0 -and $sn.max -gt 0
    Add-Case "projector_shadow_passes_vulkan" ($(if ($ok2) { "PASS" } else { "FAIL" })) "$(if ($so -and $sn) { "8 projectors: shadow-map draw calls per frame with the setting off max $($so.max) (must be 0), on p95 $($sn.p95) max $($sn.max) (must be above 0)" } else { 'no busy result' })" ($off.Seconds + $on.Seconds)
    # the engine must report nothing while 6,000 objects with their own shader parameters are drawn and churned (the busy run counts the engine's errors and fails on any)
    $big = Invoke-Render "vulkan" "busy-engine-errors-vulkan" "`"--tr-fixture=$zoo`" --tr-busy=objects:6000,glow:10,lights:40,projectors:8 --tr-busy-churn --tr-busy-seconds=8 --tr-size=960x540" 400
    $okE = $big.Exit -eq 0 -and $big.Json.busy -and $big.Json.busy.counts.objects -eq 6000 -and $big.Json.engine_errors -eq 0
    Add-Case "busy_engine_errors_vulkan" ($(if ($okE) { "PASS" } else { "FAIL" })) "6,000 objects churned for 8 s: engine errors $($big.Json.engine_errors) (must be 0)$(if (-not $okE) { '; exit ' + $big.Exit + ' ' + ($big.Json.failures -join ' | ') })" $big.Seconds
}
# Still objects merged per area and material (on by default, --merge=off turns it off; Presentation\PrimLayer.Merge.cs): a still busy view (the eye held, --tr-busy-still) drawn twice,
# once with --merge=off and once with nothing set (the default, merged); merge_off_busy_vulkan checks the off run built and merged nothing. With 6,000 objects (600 glowing, 40 lights, 8 projectors) the view draws 22,590 calls unmerged (the census); the bound is 2,000 (under 9 %).
#   merge_draw_bound_busy_vulkan    merged: the view's draw calls p50 at most 2,000, at least 90 % of the objects merged; the unmerged run is the control (it must exceed the bound)
#   merge_picture_busy_vulkan       merged and unmerged pictures within the golden tolerance, and at most 0.01 % of the pixels differ at all
#   merge_update_busy_vulkan        2,000 objects, then 286 move and 286 change colour: the picture of the first frame in which every change is presented equals
#                                   the unmerged run's (cells are taken apart in the step of the change), the cells that held them are taken apart, and the main
#                                   thread's longest call into the merge stays within the 4 ms slice
# NV_TEST_MERGE_OFF=1 leaves --tr-merge off in the "merged" runs: the bound case must then fail (the red run)
function Invoke-DrawMerge {
    $proj = Join-Path $repo "fixtures\materials\projector.matzoo.json"
    $mergeArg = if ($env:NV_TEST_MERGE_OFF -eq "1") { "--merge=off --tr-merge-still=0.5" } else { "--tr-merge-still=0.5" }   # the default is merging on: no switch
    $bound = 2000
    $busy = "--tr-busy=objects:6000,glow:10,lights:40,projectors:8 --tr-size=1920x1080 --tr-busy-still --tr-busy-frames=60 --tr-census"
    $a = Invoke-Render "vulkan" "merge-busy-unmerged-vulkan" "`"--tr-fixture=$proj`" $busy --merge=off" 400
    $ref = Join-Path $out "merge-busy-unmerged-vulkan.png"
    $b = Invoke-Render "vulkan" "merge-busy-merged-vulkan" "`"--tr-fixture=$proj`" $busy $mergeArg `"--tr-golden=$ref`"" 400
    $ua = $a.Json.busy; $mb = $b.Json.busy
    $mg = if ($mb -and $mb.census) { $mb.census.layer.merge } else { $null }
    $ok = $a.Exit -eq 0 -and $b.Exit -eq 0 -and $ua -and $mb -and $mg -and $mb.view_draw_calls.p50 -le $bound -and $ua.view_draw_calls.p50 -gt $bound -and $mg.on -eq $true -and $mg.merged_objects -ge 0.9 * $mg.eligible_objects -and $mb.pick -and $mb.pick.local -ge 40000
    Add-Case "merge_draw_bound_busy_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($ua -and $mb -and $mg) { "6,000 objects: view draw calls p50 unmerged $($ua.view_draw_calls.p50), merged $($mb.view_draw_calls.p50) (bound $bound); glow pass $($ua.pass_draw_calls.p50) -> $($mb.pass_draw_calls.p50); $($mg.merged_objects) of $($mg.eligible_objects) objects in $($mg.merged_cells) cells, $($mg.merged_surfaces) surfaces; first merged triangle maps to object $($mb.pick.local) face $($mb.pick.face)" } else { 'no busy result' })$(if (-not $ok) { '; exits ' + $a.Exit + '/' + $b.Exit + ' ' + (($b.Json.failures) -join ' | ') })" ($a.Seconds + $b.Seconds)
    $mgOff = if ($ua -and $ua.census) { $ua.census.layer.merge } else { $null }
    $ok = $a.Exit -eq 0 -and $mgOff -and $mgOff.on -eq $false -and $mgOff.merged_cells -eq 0 -and $mgOff.merged_objects -eq 0 -and $mgOff.builds -eq 0 -and $ua.view_draw_calls.p50 -gt $bound
    Add-Case "merge_off_busy_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($mgOff) { "--merge=off: merging on $($mgOff.on), $($mgOff.merged_cells) cells, $($mgOff.merged_objects) objects merged, $($mgOff.builds) builds; view draw calls p50 $($ua.view_draw_calls.p50) (above the bound $bound)" } else { 'no busy result' })" 0
    $pic = if ($mb) { $mb.picture } else { $null }
    $total = 1920 * 1080
    $ok = $b.Exit -eq 0 -and $pic -and $pic.pass -and $pic.differing -le 0.0001 * $total
    Add-Case "merge_picture_busy_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($pic) { $pic.summary } else { 'no picture comparison' })" 0
    $small = "--tr-busy=objects:2000,glow:10,lights:8,projectors:8 --tr-size=960x540 --tr-busy-still --tr-busy-frames=80 --tr-busy-poke --tr-census"
    $c = Invoke-Render "vulkan" "merge-update-unmerged-vulkan" "`"--tr-fixture=$proj`" $small --merge=off" 300
    $refPoke = Join-Path $out "merge-update-unmerged-vulkan-poke.png"
    $refUpdate = Join-Path $out "merge-update-unmerged-vulkan.png"
    $d = Invoke-Render "vulkan" "merge-update-merged-vulkan" "`"--tr-fixture=$proj`" $small $mergeArg `"--tr-golden=$ref`" `"--tr-golden-poke=$refPoke`"" 300
    $mu = $d.Json.busy
    $pp = if ($mu) { $mu.picture_after_poke } else { $null }
    $pk = if ($mu) { $mu.poked } else { $null }
    $mg2 = if ($mu -and $mu.census) { $mu.census.layer.merge } else { $null }
    $ok = $c.Exit -eq 0 -and $pp -and $pp.pass -and $pp.differing -le 0.0001 * 960 * 540 -and $pk -and $pk.merged_objects_before -gt 0 -and $mg2 -and $mg2.taken_apart -gt 0 -and $mg2.worst_main_thread_ms -le 4.0
    Add-Case "merge_update_busy_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($pp -and $pk -and $mg2) { "$($pk.merged_objects_before) objects merged before; $($pk.moved) moved and $($pk.recoloured) recoloured; picture at frame $($pk.capture_frame): $($pp.summary); cells taken apart $($mg2.taken_apart); longest merge call $($mg2.worst_main_thread_ms) ms (slice 4)" } else { 'no result' })" ($c.Seconds + $d.Seconds)
    Invoke-BusyChurn
}

# Objects that keep changing (--tr-busy-churn): the 6,000-object busy view, the eye held, a tenth of the objects swaying all the time and four changing colour each second,
# for 8 s with --merge=off and again with nothing set (merging on). At two fixed steps the churn stops, every change is presented and the picture is kept;
#   merge_churn_picture_busy_vulkan  both pictures of the merged run equal those of the unmerged run (within the golden tolerance, at most 0.01 % of the pixels differ at all)
# The figures beside it (frame p50 / p95 / max, the main thread's longest iteration, cells rebuilt per second) are INDICATIVE, taken under a build claim, not a gate.
function Invoke-BusyChurn {
    $proj = Join-Path $repo "fixtures\materials\projector.matzoo.json"
    $churn = "--tr-busy=objects:6000,glow:10,lights:40,projectors:8 --tr-size=1920x1080 --tr-busy-churn --tr-busy-seconds=8 --tr-census"
    $off = Invoke-Render "vulkan" "merge-churn-off-vulkan" "`"--tr-fixture=$proj`" $churn --merge=off" 400
    $prefix = Join-Path $out "merge-churn-off-vulkan"
    $on = Invoke-Render "vulkan" "merge-churn-on-vulkan" "`"--tr-fixture=$proj`" $churn `"--tr-churn-golden=$prefix`"" 400
    $co = $off.Json.busy.churn; $cn = $on.Json.busy.churn
    $total = 1920 * 1080
    $cps = if ($cn) { @($cn.checkpoints) } else { @() }
    $ok = $off.Exit -eq 0 -and $on.Exit -eq 0 -and $co -and $cn -and $co.merging -eq $false -and $cn.merging -eq $true -and $cn.swaying_objects -eq 600 -and $cn.object_updates -gt 0 -and $cps.Count -eq 2 -and
        @($cps | Where-Object { $_.picture -and $_.picture.pass -and $_.picture.differing -le 0.0001 * $total }).Count -eq 2
    $pics = if ($cps.Count -gt 0) { ($cps | ForEach-Object { "step $($_.step): $(if ($_.picture) { $_.picture.differing } else { '?' }) pixels differ, drained in $($_.drained_frames) frames" }) -join '; ' } else { 'no checkpoints' }
    Add-Case "merge_churn_picture_busy_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$pics$(if (-not $ok) { '; exits ' + $off.Exit + '/' + $on.Exit + ' ' + (($on.Json.failures) -join ' | ') })" ($off.Seconds + $on.Seconds)
    if ($co -and $cn) {
        $fo = $off.Json.busy.frame_ms; $fn = $on.Json.busy.frame_ms
        Write-Output ("  INDICATIVE churn (6,000 objects, 8 s): merging off: frame p50 {0} p95 {1} max {2} ms, main thread longest {3} ms, {4} object updates; merging on: frame p50 {5} p95 {6} max {7} ms, main thread longest {8} ms, longest merge slice {9} ms, {10} cells rebuilt ({11} a second), {12} taken apart, {13} objects merged at the end" -f $fo.p50, $fo.p95, $fo.max, $co.main_thread_ms.max, $co.object_updates, $fn.p50, $fn.p95, $fn.max, $cn.main_thread_ms.max, $cn.worst_merge_slice_ms, $cn.cell_builds, $cn.cell_builds_per_second, $cn.cells_taken_apart, $cn.merged_objects_at_end)
    }
}

function Invoke-GlowDrawCalls {
    $cap = Join-Path $local "capture-region-a-v9.jsonl.gz"
    if (-not (Test-Path -LiteralPath $cap) -or -not (Test-Path -LiteralPath $assets)) {
        Add-Case "glow_draw_calls_busy_vulkan" "SKIP" "local-only fixtures-local\capture-region-a-v9.jsonl.gz and fixtures-local\assets\ are absent (grid content, never committed; ADR-011)" 0
        return
    }
    $look = "125.295746,238.07632,29.295631,115.471466,211.77663,18.721626"
    $r = Invoke-Render "vulkan" "glow-draws-busy-vulkan" "`"--tr-fixture=$cap`" `"--tr-assets=$assets`" --tr-seed=8 --tr-size=1920x1080 --tr-look=$look --tr-glow-force=0.5 --tr-glowtime=20" 400
    $g = $r.Json.glowtime
    $draws = if ($g) { $g.pass_draw_calls.p50 } else { -1 }
    $pic = if ($g) { $g.picture } else { $null }
    $maxShare = 0.4   # the pass may draw at most this share of the calls it makes with every object drawn
    $ok = $r.Exit -eq 0 -and $g -and $g.glowing_objects -gt 0 -and $draws -gt 0 -and $pic -and $draws -le $maxShare * $pic.all_draw_calls -and
        $pic -and $pic.all_draw_calls -ge 150 -and $pic.differing_bytes -eq 0 -and $pic.bytes -gt 0
    Add-Case "glow_draw_calls_busy_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "pass draw calls p50 $draws (limit $($maxShare * 100) % of the calls with every object drawn), $(if ($pic) { "$($pic.chosen_objects) objects chosen of $($pic.all_objects); drawn with every object: $($pic.all_draw_calls) calls; pictures differ in $($pic.differing_bytes) of $($pic.bytes) bytes" } else { 'no picture comparison in the result' })$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}
# the time-of-day choice (--tr-timeofday): the same scene, with no environment served (the viewer's default sky), at
# Midday and at Midnight. The sun must be above the horizon at one and below it at the other, and each has its golden.
function Invoke-BanLineCases {
    # the wall at the edge of a parcel that refuses the agent (a ban: the tall yellow wall), against its golden on each backend;
    # the negative control hides the wall (the ban-lines setting off) and must FAIL the same diff. Eye and target in region coordinates.
    $look = "--tr-look=70,84,62,97,98,40"
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\terrain\golden-banlines-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-banlines-$be" "`"--tr-fixture=$fixture`" --tr-banlines=ban $look"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "proposal-banlines-$be.png") -Destination $golden
                Add-Case "banlines_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "banlines_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "banlines-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`" --tr-banlines=ban $look"
        $b = if ($r.Json) { $r.Json.banlines } else { $null }
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $b.shown -and $b.pieces -gt 0 -and $b.height -eq 5000
        Add-Case "banlines_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); wall pieces $($b.pieces), height $($b.height)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "banlines-hidden-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`" --tr-banlines=ban-hidden $look"
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
            Add-Case "banlines_negative_$be" "PASS" "no wall drawn, rejected: $($n.Json.diff.summary)" $n.Seconds
        } else { Add-Case "banlines_negative_$be" "FAIL" "the picture without the wall was not rejected (exit $($n.Exit))" $n.Seconds }
    }
}

function Invoke-ObjectCases {
    # the highlight around a selected object, the floating text over one, and the Select Face marker on three faces of two boxes (faces), each
    # against its golden on each backend; the negative control
    # leaves the highlight or the text out and must FAIL the same diff. Eye and target in region coordinates (the box stands at 128,128).
    $look = ""   # the scene frames itself on the box
    foreach ($kind in @("select", "text", "faces")) {
        foreach ($be in $Backends) {
            $golden = Join-Path $repo "fixtures\terrain\golden-$kind-$be.png"
            if (-not (Test-Path -LiteralPath $golden)) {
                $r = Invoke-Render $be "proposal-$kind-$be" "`"--tr-fixture=$fixture`" --tr-interact=$kind $look"
                if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                    Copy-Item -LiteralPath (Join-Path $out "proposal-$kind-$be.png") -Destination $golden
                    Add-Case "${kind}_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
                } else { Add-Case "${kind}_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
                continue
            }
            $r = Invoke-Render $be "$kind-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`" --tr-interact=$kind $look"
            $b = if ($r.Json) { $r.Json.interact } else { $null }
            $drawn = if ($kind -eq "select") { $b.pieces -gt 0 } elseif ($kind -eq "faces") { $b.pieces -eq 3 } else { $b.shown -gt 0 }
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $drawn
            Add-Case "${kind}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); pieces $($b.pieces), texts $($b.shown), ground $($b.ground), camera $($b.camera.X),$($b.camera.Y),$($b.camera.Z)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
            $n = Invoke-Render $be "$kind-hidden-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`" --tr-interact=$kind-hidden $look"
            if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
                Add-Case "${kind}_negative_$be" "PASS" "layer left out, rejected: $($n.Json.diff.summary)" $n.Seconds
            } else { Add-Case "${kind}_negative_$be" "FAIL" "the picture without the layer was not rejected (exit $($n.Exit))" $n.Seconds }
        }
    }
}

function Invoke-DialogCases {
    # a script dialog of twelve buttons drawn over the terrain fixture (rows of three, the first button bottom-left, Ignore below), against its
    # golden on each backend; the negative control draws a dialog of four buttons and must FAIL the same diff.
    $look = "--tr-look=70,84,62,97,98,40"
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\ui\golden-dialog12-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-dialog12-$be" "`"--tr-fixture=$fixture`" --tr-dialog=12 $look"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-dialog12-$be.png") -Destination $golden
                Add-Case "dialog12_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "dialog12_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "dialog12-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`" --tr-dialog=12 $look"
        $d = if ($r.Json) { $r.Json.dialog } else { $null }
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $d.buttons -eq 12 -and $d.rows.Count -eq 4
        Add-Case "dialog12_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); buttons $($d.buttons) in $($d.rows.Count) rows$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "dialog4-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`" --tr-dialog=4 $look"
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
            Add-Case "dialog12_negative_$be" "PASS" "four buttons drawn, rejected: $($n.Json.diff.summary)" $n.Seconds
        } else { Add-Case "dialog12_negative_$be" "FAIL" "the picture of a four-button dialog was not rejected (exit $($n.Exit))" $n.Seconds }
    }
}

function Invoke-UiSceneCases {
    # every interface window and the in-world frame, each on its own, in the Classic look on each backend; the login screen and the in-world
    # frame also in High contrast. Goldens: fixtures\ui\golden-ui-<scene>-<look>-<backend>.png (-Propose writes a missing one; approved after
    # viewing). The negative control draws the in-world frame with the accent and the text colour replaced (--tr-alter=wrongtoken): the diff MUST fail.
    $scenes = @("inworld", "inventory", "outfits", "people", "conversations", "map", "minimap", "teleport", "notifications", "scriptdialog", "permission", "inspect", "land", "personal", "preferences", "preferences-general", "preferences-graphics", "preferences-chat", "preferences-moveview", "preferences-privacy", "preferences-cache", "preferences-sound", "preferences-sound-150", "preferences-sound-200", "preferences-sound-open", "preferences-sound-open-150", "preferences-sound-open-200", "reconnect", "login", "appearance", "appearance-shape", "appearance-shirt", "build", "build-look", "build-search", "build-create", "build-object", "build-150", "build-200", "build-contents-150", "build-contents-200", "build-look-150", "build-look-200", "build-placement", "build-placement-150", "build-placement-200", "build-create-150", "build-create-200", "build-object-150", "build-object-200", "colourpicker", "script", "script-error", "script-readonly", "script-engine", "script-engine-on", "script-engine-early", "script-engine-nosave", "script-engine-slua", "script-sl", "commandlist", "commandlist-search", "commandlist-setting", "notecard", "notecard-readonly", "gesture", "gestures", "newmenu", "scriptqueue", "texturepicker", "build-contents", "takefolder", "gridoptions", "objectcontents", "buy", "objectinfo", "gestures-used", "addons", "addon-ao", "addon-rlv", "upload-image", "upload-bulk", "upload-model", "upload-bulk-models", "about", "licences", "login-dialog")
    $plan = @()
    if ($UiSceneOnly) { $scenes = @($scenes | Where-Object { $_ -eq $UiSceneOnly }) }
    foreach ($sc in $scenes) { $plan += ,@($sc, "classic") }
    foreach ($sc in @("login", "inworld", "preferences-graphics")) { if (-not $UiSceneOnly -or $sc -eq $UiSceneOnly) { $plan += ,@($sc, "high-contrast") } }
    # the Reverie top bar (100 % and 200 %, and with the World menu open) and the component gallery, in the dark and the light variant
    foreach ($sc in @("reverie-topbar-100", "reverie-topbar-200", "reverie-topbar-menu", "reverie-gallery")) { foreach ($look in @("reverie-dark", "reverie-light")) { if (-not $UiSceneOnly -or $sc -eq $UiSceneOnly) { $plan += ,@($sc, $look) } } }
    foreach ($be in $Backends) {
        foreach ($item in $plan) {
            $sc = $item[0]; $look = $item[1]; $name = "ui_${sc}_${look}_$be"
            $golden = Join-Path $repo "fixtures\ui\golden-ui-$sc-$look-$be.png"
            if (-not (Test-Path -LiteralPath $golden)) {
                $r = Invoke-Render $be "proposal-ui-$sc-$look-$be" "--tr-ui=$sc --tr-theme=$look"
                if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                    Copy-Item -LiteralPath (Join-Path $out "proposal-ui-$sc-$look-$be.png") -Destination $golden
                    Add-Case $name "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
                } else { Add-Case $name "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
                continue
            }
            $r = Invoke-Render $be "ui-$sc-$look-$be" "`"--tr-golden=$golden`" --tr-ui=$sc --tr-theme=$look"
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
        $golden = Join-Path $repo "fixtures\ui\golden-ui-appearance-shape-classic-$be.png"
        if (Test-Path -LiteralPath $golden) {
            # the Appearance window's shape panel drawn with the accent and the text colour replaced: the diff MUST fail (the window uses the theme's tokens)
            $n = Invoke-Render $be "ui-appearance-wrongtoken-$be" "`"--tr-golden=$golden`" --tr-ui=appearance-shape --tr-theme=classic --tr-alter=wrongtoken"
            if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
                Add-Case "ui_appearance_negative_$be" "PASS" "the shape panel drawn with a wrong accent and text colour, rejected: $($n.Json.diff.summary)" $n.Seconds
            } else { Add-Case "ui_appearance_negative_$be" "FAIL" "the shape panel drawn with a wrong token was not rejected (exit $($n.Exit))" $n.Seconds }
        }
        $golden = Join-Path $repo "fixtures\ui\golden-ui-inworld-classic-$be.png"
        if (Test-Path -LiteralPath $golden) {
            $n = Invoke-Render $be "ui-wrongtoken-$be" "`"--tr-golden=$golden`" --tr-ui=inworld --tr-theme=classic --tr-alter=wrongtoken"
            if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
                Add-Case "ui_negative_$be" "PASS" "drawn with a wrong accent and text colour, rejected: $($n.Json.diff.summary)" $n.Seconds
            } else { Add-Case "ui_negative_$be" "FAIL" "the picture drawn with a wrong token was not rejected (exit $($n.Exit))" $n.Seconds }
        }
    }
}

function Invoke-UiChromeCases {
    # the in-world frame at the interface sizes 100, 150 and 200 % in three window sizes, on each backend (--tr-uichrome=PCT:WxH). Every case asserts that the
    # top bar, chat bar, dock and status area are wholly inside the window, that the dock sits above the chat bar with every button shown inside it, and that
    # the open windows are inside the main view. The 1152x648 cases (the stressed size) also have a golden: fixtures\ui\golden-uichrome-<pct>-1152x648-<backend>.png.
    foreach ($be in $Backends) {
        foreach ($win in @("1152x648", "1280x720", "1920x1080")) {
            foreach ($pct in @(100, 150, 200)) {
                $name = "ui_chrome_${pct}_${win}_$be"
                $golden = Join-Path $repo "fixtures\ui\golden-uichrome-$pct-$win-$be.png"
                $wantsGolden = $win -eq "1152x648"
                if ($wantsGolden -and -not (Test-Path -LiteralPath $golden)) {
                    $r = Invoke-Render $be "proposal-uichrome-$pct-$win-$be" "--tr-uichrome=${pct}:$win"
                    $okGeometry = $r.Json -and $r.Json.backend.driver -eq $be -and $r.Json.ui_chrome -and -not @($r.Json.ui_chrome.problems).Count
                    if ($Propose -and $okGeometry) {
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                        Copy-Item -LiteralPath (Join-Path $out "proposal-uichrome-$pct-$win-$be.png") -Destination $golden
                        Add-Case $name "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
                    } else { Add-Case $name "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { (@($r.Json.failures) -join ' | ') }))" $r.Seconds }
                    continue
                }
                $goldenArg = if ($wantsGolden) { "`"--tr-golden=$golden`"" } else { "" }
                $r = Invoke-Render $be "uichrome-$pct-$win-$be" "--tr-uichrome=${pct}:$win $goldenArg"
                $c = $r.Json.ui_chrome
                $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $c -and -not @($c.problems).Count
                Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) logical $($c.logical), $($c.dock_buttons) dock buttons in $($c.dock_rows) row(s)$(if ($wantsGolden -and $r.Json.diff) { '; ' + $r.Json.diff.summary })$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + (@($r.Json.failures) -join ' | ') })" $r.Seconds
            }
        }
    }
}

function Invoke-ColourWindowCases {
    # the Build window's colour picker window at the test window's size (1152x648) at 100 and 150 % interface size, on each backend
    # (--tr-colourwindow=PCT:WxH). Every case asserts that OK and Cancel are wholly in view (no scroll area or window edge cuts them) and that a
    # pointer press on each closes the window.
    foreach ($be in $Backends) {
        foreach ($pct in @(100, 150)) {
            $name = "ui_build_colourwindow_${pct}_$be"
            $r = Invoke-Render $be $name "--tr-colourwindow=${pct}:1152x648"
            $c = $r.Json.colour_window
            $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $c -and -not @($c.problems).Count
            Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $(@($c.steps) -join ' | ')$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + (@($r.Json.failures) -join ' | ') })" $r.Seconds
        }
    }
}

function Invoke-FloaterDragCases {
    # the Inventory window resized from every edge, corner and the grip, moved by its title bar and closed, by pointer events in window pixels, in a
    # 3840x2066 window at 100 % and at 150 % interface size (--tr-floaterdrag=PCT:WxH). Every case asserts that the window follows the pointer (out grows,
    # in shrinks, never back and forth), keeps its minimum, stays in the main view, and that one click anywhere on the close button closes it.
    foreach ($be in $Backends) {
        foreach ($pct in @(100, 150)) {
            $name = "ui_floaterdrag_${pct}_$be"
            $r = Invoke-Render $be "floaterdrag-$pct-$be" "--tr-floaterdrag=${pct}:3840x2066"
            $c = $r.Json.floater_drag
            $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $c -and -not @($c.problems).Count
            Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $(@($c.steps).Count) steps$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + (@($r.Json.failures) -join ' | ') })" $r.Seconds
        }
    }
}

function Invoke-HandleCases {
    # the edit tools' handles and grid round a box (--tr-handles=MODE), each against its golden on Vulkan: Move (arrows and plane handles), Rotate
    # (rings), Stretch (corner and face boxes) and the grid while a Move arrow is dragged. Every case also checks that the lit handle shows and that
    # the arrow keeps its length on screen with the camera twice as far. The negative control draws the box without its handles and MUST fail.
    $be = "vulkan"
    foreach ($mode in @("move", "rotate", "scale", "grid")) {
        $golden = Join-Path $repo "fixtures\build\golden-handles-$mode-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-handles-$mode-$be" "--tr-handles=$mode"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-handles-$mode-$be.png") -Destination $golden
                Add-Case "handles_${mode}_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "handles_${mode}_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "handles-$mode-$be" "`"--tr-golden=$golden`" --tr-handles=$mode"
        $h = $r.Json.handles
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $h
        Add-Case "handles_${mode}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); lit pixels $($h.lit_pixels), arrow $($h.arrow_px_at_8m) px at 8 m and $($h.arrow_px_at_16m) px at 16 m$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $golden = Join-Path $repo "fixtures\build\golden-handles-move-$be.png"
    $n = Invoke-Render $be "handles-none-$be" "`"--tr-golden=$golden`" --tr-handles=move --tr-alter=nohandles"
    if ($n.Exit -eq 1 -and $n.Json -and $n.Json.backend.driver -eq $be -and $n.Json.handles.lit_pixels -lt 20) {
        Add-Case "handles_negative_$be" "PASS" "the box without its handles was rejected: $($n.Json.failures -join ' | ')" $n.Seconds
    } else { Add-Case "handles_negative_$be" "FAIL" "the box without its handles was not rejected (exit $($n.Exit))" $n.Seconds }
}

function Invoke-ProportionsCases {
    # the window's own 3D view shows a sphere and a cube as a circle and a square (--tr-proportions=PCT:STEP,...): windowed sizes of other proportions than the
    # screen's, a resize, the maximized window and the full-screen one, at 100 % and at 150 % content scale (the display scale of a 2560x1440 screen at 144 dpi).
    # The window is the usual one (not the quarter-of-the-screen test window), as the run sets its own size. The negative control stretches the scene sideways
    # (--tr-alter=stretch) and the run MUST fail.
    $prior = $env:NV_TEST_WINDOW_FULL
    $env:NV_TEST_WINDOW_FULL = "1"
    try {
        foreach ($be in $Backends) {
            foreach ($c in @(@(100, "1280x720,1920x1080,2560x1080,1000x900,720x1000,maximized,fullscreen"), @(150, "1280x720,2560x1080,maximized,fullscreen"))) {
                $name = "proportions_$($c[0])_$be"
                $r = Invoke-Render $be $name "--tr-proportions=$($c[0]):$($c[1])"
                $p = $r.Json.proportions
                $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $p -and -not @($p.problems).Count -and @($p.steps).Count -gt 0
                $steps = if ($p) { (@($p.steps | ForEach-Object { "$($_.step): picture $($_.picture), sphere $($_.sphere_width_over_height), cube $($_.cube_width_over_height)" }) -join "; ") } else { "no result" }
                Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $steps$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + (@($r.Json.failures) -join ' | ') })" $r.Seconds
            }
            $n = Invoke-Render $be "proportions_negative_$be" "--tr-proportions=100:1280x720 --tr-alter=stretch"
            if ($n.Exit -ne 0 -and @($n.Json.proportions.problems).Count -gt 0) { Add-Case "proportions_negative_$be" "PASS" "the stretched picture was rejected: $(@($n.Json.proportions.problems)[0])" $n.Seconds }
            else { Add-Case "proportions_negative_$be" "FAIL" "the stretched picture was not rejected (exit $($n.Exit))" $n.Seconds }
        }
    } finally { $env:NV_TEST_WINDOW_FULL = $prior }
}

function Invoke-NeighbourCases {
    # two regions side by side (--tr-neighbours=on), against its golden on each backend; the negative control presents the home region alone
    # (--tr-neighbours=off) and must FAIL the same diff, because the east half of the picture is empty
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\neighbours\golden-neighbours-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-neighbours-$be" "--tr-neighbours=on"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-neighbours-$be.png") -Destination $golden
                Add-Case "neighbours_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "neighbours_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "neighbours-$be" "`"--tr-golden=$golden`" --tr-neighbours=on"
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $r.Json.neighbours.east_chunks -gt 0
        Add-Case "neighbours_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); east chunks $($r.Json.neighbours.east_chunks), east instances $($r.Json.neighbours.east_instances)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "neighbours-off-$be" "`"--tr-golden=$golden`" --tr-neighbours=off"
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
            Add-Case "neighbours_negative_$be" "PASS" "the home region alone rejected: $($n.Json.diff.summary)" $n.Seconds
        } else { Add-Case "neighbours_negative_$be" "FAIL" "the picture without the neighbour was not rejected (exit $($n.Exit))" $n.Seconds }
    }
}

function Invoke-SeamCases {
    # the ground alone, seen from a low angle: no sky may show through the land below its first ground pixel in any column of the picture.
    # border: along the line where two regions meet; slope: across the lines where one region's terrain chunks meet, on a rise
    foreach ($be in $Backends) {
        foreach ($kind in @("border", "slope")) {
            $r = Invoke-Render $be "seam-$kind-$be" "--tr-neighbours=on --tr-seam=$kind"
            $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $r.Json.seam.sky_under_ground -eq 0
            Add-Case "seam_${kind}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) sky pixels under the ground: $($r.Json.seam.sky_under_ground)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
    }
}

function Invoke-TreeCases {
    # the region's built-in trees (--tr-trees=on): every prim of every tree drawn and trees in the picture, against its golden on each backend;
    # the control (--tr-trees=notrees) skips the tree updates and must FAIL the same checks
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\trees\golden-trees-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-trees-$be" "--tr-trees=on"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-trees-$be.png") -Destination $golden
                Add-Case "trees_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it); instances $($r.Json.trees.instances) of $($r.Json.trees.expected_parts)" $r.Seconds
            } else { Add-Case "trees_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }); instances $($r.Json.trees.instances) of $($r.Json.trees.expected_parts))" $r.Seconds }
        } else {
            $r = Invoke-Render $be "trees-$be" "`"--tr-golden=$golden`" --tr-trees=on"
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $r.Json.trees.instances -eq $r.Json.trees.expected_parts
            Add-Case "trees_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); prims drawn $($r.Json.trees.instances) of $($r.Json.trees.expected_parts)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
        $n = Invoke-Render $be "trees-none-$be" "--tr-trees=notrees"
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.backend.driver -eq $be -and $n.Json.trees.instances -eq 0) {
            Add-Case "trees_negative_$be" "PASS" "no tree updates rejected: $($n.Json.failures -join ' | ')" $n.Seconds
        } else { Add-Case "trees_negative_$be" "FAIL" "the scene without trees was not rejected (exit $($n.Exit))" $n.Seconds }
        # a grid that does not hold the species texture: the source answers "not found", the trees are still all drawn (on the placeholder), nothing waits for the texture and nothing is on the GPU for it
        $m = Invoke-Render $be "trees-missing-$be" "--tr-trees=missing"
        $mok = $m.Exit -eq 0 -and $m.Json -and $m.Json.backend.driver -eq $be -and $m.Json.trees.instances -eq $m.Json.trees.expected_parts -and $m.Json.trees.textures_waiting -eq 0 -and $m.Json.trees.gpu_textures -eq 0 -and $m.Json.trees.species_texture_fetches -ge 1
        Add-Case "trees_missing_texture_$be" ($(if ($mok) { "PASS" } else { "FAIL" })) "prims drawn $($m.Json.trees.instances) of $($m.Json.trees.expected_parts), waiting $($m.Json.trees.textures_waiting), on the GPU $($m.Json.trees.gpu_textures), fetches $($m.Json.trees.species_texture_fetches)$(if (-not $mok) { '; exit ' + $m.Exit + ' ' + ($m.Json.failures -join ' | ') })" $m.Seconds
    }
}

function Invoke-NeighbourWaterCases {
    # two regions with water on both, at different heights (16 m home, 29 m east: --tr-neighbours=on --tr-water=16,29), against its golden on each
    # backend; the negative control draws the one plane at the home height (--tr-water-plan=off, the way the neighbour's height was ignored before)
    # and must FAIL the same diff
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\neighbours\golden-neighbours-water-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-neighbours-water-$be" "--tr-neighbours=on --tr-water=16,29"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "proposal-neighbours-water-$be.png") -Destination $golden
                Add-Case "neighbours_water_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "neighbours_water_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "neighbours-water-$be" "`"--tr-golden=$golden`" --tr-neighbours=on --tr-water=16,29"
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $r.Json.neighbours.east_chunks -gt 0
        Add-Case "neighbours_water_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)" $r.Seconds
        $n = Invoke-Render $be "neighbours-water-oneplane-$be" "`"--tr-golden=$golden`" --tr-neighbours=on --tr-water=16,29 --tr-water-plan=off"
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
            Add-Case "neighbours_water_negative_$be" "PASS" "one plane at the home height rejected: $($n.Json.diff.summary)" $n.Seconds
        } else { Add-Case "neighbours_water_negative_$be" "FAIL" "the picture with one water plane was not rejected (exit $($n.Exit))" $n.Seconds }
        # a banned parcel in the east region: its wall stands in the east region (--tr-ban=east), against its golden; built in the east region's coordinates but placed in the
        # home region's frame, the way it was before (--tr-ban=east-at-anchor), it must FAIL the same diff
        $bgolden = Join-Path $repo "fixtures\neighbours\golden-neighbours-ban-$be.png"
        if (-not (Test-Path -LiteralPath $bgolden)) {
            $b = Invoke-Render $be "proposal-neighbours-ban-$be" "--tr-neighbours=on --tr-ban=east"
            if ($Propose -and $b.Exit -eq 0 -and $b.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "proposal-neighbours-ban-$be.png") -Destination $bgolden
                Add-Case "neighbours_ban_$be" "FAIL" "no golden: PROPOSED $bgolden (rerun to diff against it)" $b.Seconds
            } else { Add-Case "neighbours_ban_$be" "FAIL" "no committed golden $bgolden (render exit $($b.Exit): $(if ($b.Json) { $b.Json.failures -join ' | ' }))" $b.Seconds }
        } else {
            $b = Invoke-Render $be "neighbours-ban-$be" "`"--tr-golden=$bgolden`" --tr-neighbours=on --tr-ban=east"
            $ok = $b.Exit -eq 0 -and $b.Json.diff.pass -and $b.Json.backend.driver -eq $be -and $b.Json.ban.shown -and $b.Json.ban.pieces -gt 0
            Add-Case "neighbours_ban_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($b.Json.backend.driver) $($b.Json.diff.summary); wall pieces $($b.Json.ban.pieces)" $b.Seconds
            $bn = Invoke-Render $be "neighbours-ban-anchor-$be" "`"--tr-golden=$bgolden`" --tr-neighbours=on --tr-ban=east-at-anchor"
            if ($bn.Exit -eq 1 -and $bn.Json -and $bn.Json.diff -and -not $bn.Json.diff.pass -and $bn.Json.backend.driver -eq $be) {
                Add-Case "neighbours_ban_negative_$be" "PASS" "the wall in the home region's frame rejected: $($bn.Json.diff.summary)" $bn.Seconds
            } else { Add-Case "neighbours_ban_negative_$be" "FAIL" "the misplaced wall was not rejected (exit $($bn.Exit))" $bn.Seconds }
        }
        # a face that glows in the east region: the glow pass draws (--tr-glow=east); told of no other region (--tr-glow=east-unlinked, the way it was before) it does not
        $g = Invoke-Render $be "neighbours-glow-$be" "--tr-neighbours=on --tr-glow=east"
        $ok = $g.Exit -eq 0 -and $g.Json.backend.driver -eq $be -and $g.Json.glow.active -and $g.Json.glow.east_glow_objects -ge 1
        Add-Case "neighbours_glow_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($g.Json.backend.driver) glow pass active $($g.Json.glow.active), glowing objects in the east region $($g.Json.glow.east_glow_objects)" $g.Seconds
        $u = Invoke-Render $be "neighbours-glow-unlinked-$be" "--tr-neighbours=on --tr-glow=east-unlinked"
        if ($u.Exit -eq 1 -and $u.Json -and $u.Json.glow -and -not $u.Json.glow.active -and $u.Json.backend.driver -eq $be) {
            Add-Case "neighbours_glow_negative_$be" "PASS" "the pass not told of the east region does not draw: $($u.Json.failures -join ' | ')" $u.Seconds
        } else { Add-Case "neighbours_glow_negative_$be" "FAIL" "the unlinked glow pass was not rejected (exit $($u.Exit))" $u.Seconds }
    }
}

function Invoke-SharedTextureCases {
    # two regions on one texture hub (--tr-sharedtex=shared): a texture both use is fetched once and held once, and when one region leaves only what
    # no other region uses goes. The negative control gives the east region a hub of its own (--tr-sharedtex=private, the way it was before) and
    # must FAIL the same assertions.
    $img = Join-Path $repo "fixtures\colour\colour-chart.j2c"
    foreach ($be in $Backends) {
        $r = Invoke-Render $be "sharedtex-$be" "`"--tr-fixture=$img`" --tr-sharedtex=shared"
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $r.Json.sharedtex.fetches_a -eq 1 -and $r.Json.sharedtex.slots -eq 2
        Add-Case "sharedtex_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) fetches of the shared texture $($r.Json.sharedtex.fetches_a), GPU textures $($r.Json.sharedtex.slots), residency entries $($r.Json.sharedtex.residency_entries); after the east region left: shared stays $($r.Json.sharedtex_leave.shared_stays), own gone $($r.Json.sharedtex_leave.own_gone)$(if (-not $ok -and $r.Json) { '; ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "sharedtex-private-$be" "`"--tr-fixture=$img`" --tr-sharedtex=private"
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.failures.Count -gt 0 -and $n.Json.backend.driver -eq $be) {
            Add-Case "sharedtex_negative_$be" "PASS" "a hub per region rejected: $($n.Json.failures -join ' | ')" $n.Seconds
        } else { Add-Case "sharedtex_negative_$be" "FAIL" "the per-region hubs were not rejected (exit $($n.Exit))" $n.Seconds }
    }
}

function Invoke-SurfaceCases {
    # surfaces_<be>: 300 small still boxes in one merge cell, each mapped differently, so the cell has more surfaces than the engine allows one mesh (256): every face must be
    # drawn (the engine holds as many merged surfaces as the cell built) and the engine must report no error
    $img = Join-Path $repo "fixtures\colour\colour-chart.j2c"
    foreach ($be in $Backends) {
        $r = Invoke-Render $be "surfaces-$be" "`"--tr-surfaces=$img`" --tr-surfaces-n=300" 300
        $s = $r.Json.surfaces
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $s.merged_surfaces_drawn -eq $s.merged_surfaces_built -and $s.engine_errors -eq 0
        Add-Case "surfaces_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($s.objects) objects merged $($s.merged_objects); surfaces built $($s.merged_surfaces_built), held by the engine $($s.merged_surfaces_drawn) in $($s.merged_meshes) meshes; engine errors $($s.engine_errors)$(if (-not $ok) { '; exit ' + $r.Exit + ': ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    # primlod_<be>: cylinders of one size at 4, 25, 60 and 200 m are each built at the level of detail the official viewer's rule gives their distance
    foreach ($be in $Backends) {
        $p = Invoke-Render $be "primlod-$be" "--tr-primlod" 300
        $pl = $p.Json.primlod
        $pok = $p.Exit -eq 0 -and $p.Json.backend.driver -eq $be
        Add-Case "primlod_$be" ($(if ($pok) { "PASS" } else { "FAIL" })) "backend=$($p.Json.backend.driver) level the rule gives $($pl.expected -join ','), built at $($pl.drawn -join ','), $($pl.meshes) meshes$(if (-not $pok) { '; exit ' + $p.Exit + ': ' + ($p.Json.failures -join ' | ') })" $p.Seconds
    }
}

function Invoke-TeardownCases {
    Invoke-SurfaceCases
    # a home region and a neighbour with merged and blended textured objects, torn down in the logout order: the engine must report no error
    $img = Join-Path $repo "fixtures\colour\colour-chart.j2c"
    foreach ($be in $Backends) {
        $r = Invoke-Render $be "teardown-$be" "`"--tr-teardown=$img`""
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $r.Json.engine_errors -eq 0
        $b = Invoke-Render $be "teardown-big-$be" "`"--tr-teardown=$img`" --tr-teardown-objects=12000"
        $okb = $b.Exit -eq 0 -and $b.Json.backend.driver -eq $be -and $b.Json.engine_errors -eq 0 -and $b.Json.retire.longest_frame_ms -le 100
        Add-Case "teardown_big_$be" ($(if ($okb) { "PASS" } else { "FAIL" })) "backend=$($b.Json.backend.driver) engine errors $($b.Json.engine_errors); $($b.Json.retire.objects) objects in the dropped region let go of in $($b.Json.retire.frames) frames, longest frame $($b.Json.retire.longest_frame_ms) ms (the same count freed in one call: $($b.Json.retire.sync_free_ms) ms)$(if (-not $okb -and $b.Json) { '; ' + ($b.Json.failures -join ' | ') })" $b.Seconds
        Add-Case "teardown_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) engine errors $($r.Json.engine_errors); merged $($r.Json.teardown.home_merged)/$($r.Json.teardown.east_merged), blended faces $($r.Json.teardown.home_blend_faces)/$($r.Json.teardown.east_blend_faces)$(if (-not $ok -and $r.Json) { '; ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
}

function Invoke-LoginScreenCases {
    # the login screen inside the shell with its theme (--tr-loginscreen=en), against its golden on each backend; the negative control draws the
    # pseudo-localised strings (longer, accented) and must FAIL the same diff
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\ui\golden-loginscreen-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-loginscreen-$be" "--tr-loginscreen=en"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-loginscreen-$be.png") -Destination $golden
                Add-Case "loginscreen_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "loginscreen_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "loginscreen-$be" "`"--tr-golden=$golden`" --tr-loginscreen=en"
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $r.Json.loginscreen.controls -gt 0
        Add-Case "loginscreen_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); $($r.Json.loginscreen.controls) controls$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "loginscreen-pseudo-$be" "`"--tr-golden=$golden`" --tr-loginscreen=pseudo"
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
            Add-Case "loginscreen_negative_$be" "PASS" "the pseudo-localised strings rejected: $($n.Json.diff.summary)" $n.Seconds
        } else { Add-Case "loginscreen_negative_$be" "FAIL" "the pseudo-localised screen was not rejected (exit $($n.Exit))" $n.Seconds }
    }
}

function Invoke-LoginCardCases {
    # the login card at the test window's size (--tr-logincard): Second Life's long refusal on the status line (the card must lie inside the
    # window and end above the release line), and the two disclosures over the login screen (their panel must lie inside the window); each
    # against its golden. The negative control lets the status area grow with its text, as before it was bounded: its geometry check MUST fail.
    foreach ($be in $Backends) {
        foreach ($what in @("long", "disclosure_first_run", "disclosure_second_life")) {
            $name = "logincard-" + $what.Replace("_", "-")
            # the golden's file name says "sl-grid", not "second-life": the logo guard (assert-no-logo.ps1) refuses any tracked image
            # whose name says Second Life, and these are pictures of our own login card
            $golden = Join-Path $repo "fixtures\ui\golden-$($name.Replace('second-life', 'sl-grid'))-$be.png"
            if (-not (Test-Path -LiteralPath $golden)) {
                $r = Invoke-Render $be "proposal-$name-$be" "--tr-logincard=$what"
                $geometry = if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) { "geometry PASS $($r.Json.logincard | ConvertTo-Json -Compress)" } else { "geometry FAIL (exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" }
                if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $golden) | Out-Null
                    Copy-Item -LiteralPath (Join-Path $out "proposal-$name-$be.png") -Destination $golden
                    Add-Case "$($name.Replace('-', '_'))_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it); $geometry" $r.Seconds
                } else { Add-Case "$($name.Replace('-', '_'))_$be" "FAIL" "no committed golden $golden; $geometry" $r.Seconds }
                continue
            }
            $r = Invoke-Render $be "$name-$be" "`"--tr-golden=$golden`" --tr-logincard=$what"
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case "$($name.Replace('-', '_'))_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); $($r.Json.logincard | ConvertTo-Json -Compress)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
        $n = Invoke-Render $be "logincard-long-unbounded-$be" "--tr-logincard=long --tr-alter=unbounded"
        # rejected: the card leaves the window or the release line, or (the form scrolls inside the card) the status area outgrows its bound
        $statusOver = @($n.Json.failures) -match '^the status area .* is taller than its bound'
        if ($n.Exit -eq 1 -and $n.Json -and $n.Json.logincard -and (-not ($n.Json.logincard.inside -and $n.Json.logincard.above_release) -or $statusOver) -and $n.Json.backend.driver -eq $be) {
            Add-Case "logincard_long_negative_$be" "PASS" "the unbounded status area was rejected: $($n.Json.failures -join ' | ')" $n.Seconds
        } else { Add-Case "logincard_long_negative_$be" "FAIL" "the unbounded status area was not rejected (exit $($n.Exit))" $n.Seconds }
    }
}

function Invoke-TimeOfDayCases {
    $skyScene = Join-Path $repo "fixtures\environment\water-sky-default.scene.json"
    foreach ($be in $Backends) {
        foreach ($choice in @(@("midday", 0.5, $true), @("midnight", 0.0, $false))) {
            $name = $choice[0]; $wantFraction = $choice[1]; $sunUp = $choice[2]
            $golden = Join-Path $repo "fixtures\environment\golden-sky-$name-$be.png"
            if (-not (Test-Path -LiteralPath $golden)) {
                $r = Invoke-Render $be "proposal-sky-$name-$be" "`"--tr-fixture=$skyScene`" --tr-timeofday=$name"
                if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                    Copy-Item -LiteralPath (Join-Path $out "proposal-sky-$name-$be.png") -Destination $golden
                    Add-Case "sky_${name}_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
                } else { Add-Case "sky_${name}_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
                continue
            }
            $r = Invoke-Render $be "sky-$name-$be" "`"--tr-fixture=$skyScene`" `"--tr-golden=$golden`" --tr-timeofday=$name"
            $e = $r.Json.environment
            $up = [bool]($e -and $e.sun_direction -and $e.sun_direction[2] -gt 0)   # a case the timing switches left out has no result to index
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $e.water_drawn -and [math]::Abs($e.day_fraction - $wantFraction) -lt 1e-6 -and ($up -eq $sunUp)
            Add-Case "sky_${name}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); day $($e.day_fraction), sun z $(if ($e -and $e.sun_direction) { $e.sun_direction[2] })$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
    }
    # the final sky and open-water pixels against the official colours (--tr-sky-probes, --tr-water-probes): a camera outside the
    # region looking away from it, so only sky and open water are in view (no golden: the values are compared in the harness)
    foreach ($be in $Backends) {
        # the sunset sky is not the same all round (the glow follows the real angle to the sun, which sits towards -X): looking away from it,
        # across it (north) and towards it; the sky probes read three columns of each picture, so every picture covers several angles
        foreach ($pc in @(@("midday", "-200,128,48,-300,128,44", "midday"), @("sunset", "700,128,48,800,128,44", "sunset"), @("sunset-north", "128,700,48,128,800,44", "sunset"), @("sunset-sunwards", "-300,128,48,-400,128,44", "sunset"), @("midnight", "-200,128,48,-300,128,44", "midnight"), @("sunrise", "-200,128,48,-300,128,44", "sunrise"))) {
            $name = $pc[0]
            $r = Invoke-Render $be "skywater-$name-$be" "`"--tr-fixture=$skyScene`" --tr-look=$($pc[1]) --tr-size=1280x720 --tr-timeofday=$($pc[2]) --tr-sky-probes --tr-water-probes"
            $sp = $r.Json.sky_probes; $wp = $r.Json.water_probes
            $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $sp -and $sp.pass -and $wp -and $wp.pass
            Add-Case "sky_water_probes_${name}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver); sky probes in view $($sp.in_view) pass $($sp.pass); water probes in view $($wp.in_view) pass $($wp.pass)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
    }
    # a sky that sets a reflection probe ambiance is drawn through the tone mapper: the sky probes expect the tone-mapped colour, and the post pass must
    # have run; the control draws the same sky without the tone mapper (--tr-alter=nohdr) and the probes MUST fail
    $hdrScene = Join-Path $repo "fixtures\environment\hdr-sky.scene.json"
    foreach ($be in $Backends) {
        $r = Invoke-Render $be "sky-hdr-$be" "`"--tr-fixture=$hdrScene`" --tr-look=-200,128,48,-300,128,44 --tr-size=1280x720 --tr-timeofday=midday --tr-sky-probes"
        $sp = $r.Json.sky_probes; $e = $r.Json.environment
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $sp -and $sp.pass -and $sp.hdr -and $e.hdr_active -and $e.hdr_passes -gt 0
        Add-Case "sky_hdr_probes_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver); sky probes in view $($sp.in_view) pass $($sp.pass); tone mapped $($e.hdr_active), post passes $($e.hdr_passes)$(if ($e.hdr_error) { ', setup error: ' + $e.hdr_error })$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "sky-hdr-negative-$be" "`"--tr-fixture=$hdrScene`" --tr-look=-200,128,48,-300,128,44 --tr-size=1280x720 --tr-timeofday=midday --tr-sky-probes --tr-alter=nohdr"
        $okn = $n.Exit -ne 0 -and $n.Json.sky_probes -and -not $n.Json.sky_probes.pass -and -not $n.Json.environment.hdr_active
        Add-Case "sky_hdr_probes_negative_$be" ($(if ($okn) { "PASS" } else { "FAIL" })) "the sky drawn without the tone mapper fails the probes (exit $($n.Exit), probes pass $($n.Json.sky_probes.pass), tone mapped $($n.Json.environment.hdr_active))" $n.Seconds
    }
}

# the official haze pass (a sky that takes the tone-mapped path): flat fullbright faces at 100, 900 and 2500 m, looking towards and across the light, against the model of the
# official haze (--tr-haze=on); with the pass left out (--tr-haze=off) the same faces MUST fail. Then the shared environment near dusk (a thick haze, trees, land, the sun's disc)
# against its checks (the disc brighter at its centre than the sky beside it, the leaves still green) and its golden (PROPOSED until approved after viewing)
function Invoke-SharedEnvCases {
$duskSky = Join-Path $repo "fixtures\environment\shared-dusk.llsd.xml"
$duskThick = Join-Path $repo "fixtures\environment\shared-dusk-thick.llsd.xml"
foreach ($be in $Backends) {
    $r = Invoke-Render $be "haze-$be" "--tr-haze=on `"--tr-environment=$duskSky`""
    $h = $r.Json.haze
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $h -and $h.hdr_active -and $h.tonemap_ran -gt 0
    Add-Case "haze_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver); $(if ($h) { 'rows ' + $h.rows.Count + ', largest error ' + (($h.rows | Measure-Object max_error -Maximum).Maximum) + ' of ' + $h.tolerance })$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "haze-negative-$be" "--tr-haze=off `"--tr-environment=$duskSky`""
    $okn = $n.Exit -ne 0 -and $n.Json.haze -and (($n.Json.haze.rows | Measure-Object max_error -Maximum).Maximum -gt $n.Json.haze.tolerance)
    Add-Case "haze_negative_$be" ($(if ($okn) { "PASS" } else { "FAIL" })) "the faces without the haze pass fail the model (exit $($n.Exit), largest error $(if ($n.Json.haze) { ($n.Json.haze.rows | Measure-Object max_error -Maximum).Maximum }))" $n.Seconds
    $sd = Invoke-Render $be "sundisc-$be" "--tr-sundisc=on"
    $sdj = $sd.Json.sundisc
    $oksd = $sd.Exit -eq 0 -and $sd.Json.backend.driver -eq $be -and $sdj -and $sdj.texture_in_use
    Add-Case "sundisc_$be" ($(if ($oksd) { "PASS" } else { "FAIL" })) "backend=$($sd.Json.backend.driver); $(if ($sdj) { 'disc ' + $sdj.rows[0].disc_width_px + ' px of ' + $sdj.rows[0].expected_width_px + ' expected, centre ' + $sdj.rows[0].centre + ' against sky ' + $sdj.rows[0].beside } else { 'no result' })$(if (-not $oksd -and $sd.Json) { ' | ' + ($sd.Json.failures -join ' | ') })" $sd.Seconds
    $sn = Invoke-Render $be "sundisc-negative-$be" "--tr-sundisc=off"
    $oksn = $sn.Exit -ne 0 -and $sn.Json.sundisc
    Add-Case "sundisc_negative_$be" ($(if ($oksn) { "PASS" } else { "FAIL" })) "the procedural disc fails the official square (exit $($sn.Exit))" $sn.Seconds
    $x = Invoke-Render $be "haze-noexposure-$be" "--tr-haze=noexposure `"--tr-environment=$duskSky`""
    $okx = $x.Exit -ne 0 -and $x.Json.haze -and (($x.Json.haze.rows | Measure-Object max_error -Maximum).Maximum -gt $x.Json.haze.tolerance)
    Add-Case "haze_noexposure_$be" ($(if ($okx) { "PASS" } else { "FAIL" })) "the faces without the automatic exposure fail the model (exit $($x.Exit), largest error $(if ($x.Json.haze) { ($x.Json.haze.rows | Measure-Object max_error -Maximum).Maximum } else { 'none' }))" $x.Seconds
    $golden = Join-Path $repo "fixtures\environment\golden-shared-environment-dusk-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-shared-env-$be" "--tr-trees=on --tr-sharedenv=dusk `"--tr-environment=$duskThick`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-shared-env-$be.png") -Destination $golden
            Add-Case "shared_env_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else { Add-Case "shared_env_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $(if ($r.Json) { $r.Json.failures -join ' | ' }))" $r.Seconds }
    } else {
        $r = Invoke-Render $be "shared-env-$be" "`"--tr-golden=$golden`" --tr-trees=on --tr-sharedenv=dusk `"--tr-environment=$duskThick`""
        $se = $r.Json.shared_env
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
        Add-Case "shared_env_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); sun centre $($se.sun_centre_luminance) over sky $($se.sky_beside_luminance), leaf saturation $($se.leaf_saturation)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
}
}

# water and the region's environment: the synthetic water scene against its goldens (also run alone by -WaterSkyOnly, with the time-of-day cases)
function Invoke-WaterCases {
$waterScene = Join-Path $repo "fixtures\environment\water-sky.scene.json"
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\environment\golden-water-sky-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-water-$be" "`"--tr-fixture=$waterScene`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-water-$be.png") -Destination $golden
            Add-Case "water_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else { Add-Case "water_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "water-$be" "`"--tr-fixture=$waterScene`" `"--tr-golden=$golden`""
    $e = $r.Json.environment
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $e.water_drawn -and $e.source -eq "region" -and [math]::Abs($e.day_fraction - 0.3) -lt 1e-6
    Add-Case "water_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); environment $($e.source), day $($e.day_fraction), water at $($e.water_height) m$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "water-negative-$be" "`"--tr-fixture=$waterScene`" `"--tr-golden=$golden`" --tr-alter=water"
    if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) { Add-Case "water_negative_$be" "PASS" "water raised 3 m rejected: $($n.Json.diff.summary)" $n.Seconds }
    else { Add-Case "water_negative_$be" "FAIL" "raised water NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
}
}

# The water's own look: the shore seen from above and a low angle at midday against goldens, the ripples moving between two moments (a picture of
# the water rectangle a little later differs; the same view with flat water does not move: the negative control), and the sun's glint on open water
# below the sun (bright sparkles; flat water has none). The shore and low views have a water-only rectangle measured.
function Invoke-WaterLookCases {
$waterScene = Join-Path $repo "fixtures\environment\water-sky.scene.json"
$views = @(
    @{ Name = "shore"; Look = "-40,128,45,40,128,18"; Rect = "100,540,400,160" },
    @{ Name = "low"; Look = "-120,128,24,200,128,18"; Rect = "300,430,600,250" })
foreach ($be in $Backends) {
    foreach ($v in $views) {
        $golden = Join-Path $repo "fixtures\environment\golden-water-$($v.Name)-$be.png"
        $base = "`"--tr-fixture=$waterScene`" --tr-timeofday=midday --tr-look=$($v.Look) --tr-size=1280x720 --tr-water-look=$($v.Rect)"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-water-$($v.Name)-$be" $base
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "proposal-water-$($v.Name)-$be.png") -Destination $golden
                Add-Case "water_$($v.Name)_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "water_$($v.Name)_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
        } else {
            $r = Invoke-Render $be "water-$($v.Name)-$be" "$base `"--tr-golden=$golden`""
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case "water_$($v.Name)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
        # the ripples move: the same view two seconds of water time later (a quarter of the pictures' water rectangle differs by more than the 8-bit noise);
        # with flat water the view does not move at all (negative control)
        $a = Invoke-Render $be "water-$($v.Name)-t0-$be" "$base --tr-water-time=0"
        $b = Invoke-Render $be "water-$($v.Name)-t2-$be" "$base --tr-water-time=2 `"--tr-water-compare=$(Join-Path $out "water-$($v.Name)-t0-$be.png")`""
        $fa = Invoke-Render $be "water-$($v.Name)-flat0-$be" "$base --tr-water-time=0 --tr-water-flat"
        $fb = Invoke-Render $be "water-$($v.Name)-flat2-$be" "$base --tr-water-time=2 --tr-water-flat `"--tr-water-compare=$(Join-Path $out "water-$($v.Name)-flat0-$be.png")`""
        $moved = $b.Json.water_look.diff_vs_other; $flatMoved = $fb.Json.water_look.diff_vs_other
        $ok = $b.Json -and $fb.Json -and $moved -ge 0.004 -and $flatMoved -lt 0.001
        Add-Case "water_ripples_move_$($v.Name)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "water rectangle moved by $moved (needs 0.004 or more); the same view with flat water moved by $flatMoved (must be under 0.001)" ($b.Seconds + $fb.Seconds)
    }
    # the sun's glint: a camera over open water below a midday sun looking straight down; the water holds bright sparkles (its brightest 1 % at 0.6 or more and a wide spread);
    # flat water holds none
    $glint = "`"--tr-fixture=$waterScene`" --tr-timeofday=midday --tr-look=-200,128,150,-199,128,18 --tr-size=1280x720 --tr-water-look=340,160,600,400"
    $g = Invoke-Render $be "water-glint-$be" $glint
    $gf = Invoke-Render $be "water-glint-flat-$be" "$glint --tr-water-flat"
    $wl = $g.Json.water_look; $wf = $gf.Json.water_look
    $ok = $wl -and $wf -and $wl.p99 -ge 0.6 -and $wl.std -ge 0.05 -and $wf.p99 -lt 0.4
    Add-Case "water_glint_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "brightest 1 % at $($wl.p99) (needs 0.6 or more), spread $($wl.std) (needs 0.05 or more); flat water: brightest 1 % at $($wf.p99) (must be under 0.4)" ($g.Seconds + $gf.Seconds)
}
}

# Personal Lighting: settings laid over the sky (more haze, clouds and a scene gamma of 2) reach the picture: the sky probes read the picture against the colours of
# the changed frame bent by the gamma curve and pass; the same settings used only for the expected colours (not applied) must make the probes fail.
function Invoke-PersonalCases {
$skyScene = Join-Path $repo "fixtures\environment\water-sky-default.scene.json"
foreach ($be in $Backends) {
    $view = "`"--tr-fixture=$skyScene`" --tr-look=-200,128,48,-300,128,44 --tr-size=1280x720 --tr-timeofday=midday --tr-sky-probes"
    $set = "--tr-personal=HazeDensity=2.5,CloudCoverage=0.8,SceneGamma=2"
    $r = Invoke-Render $be "personal-probes-$be" "$view $set"
    $sp = $r.Json.sky_probes
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $sp -and $sp.pass
    Add-Case "personal_sky_probes_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver); sky probes in view $($sp.in_view) pass $($sp.pass)$(if (-not $ok) { '; exit ' + $r.Exit })" $r.Seconds
    $n = Invoke-Render $be "personal-probes-skipped-$be" "$view $set --tr-personal-skip-apply"
    $spn = $n.Json.sky_probes
    if ($n.Json -and $spn -and -not $spn.pass) { Add-Case "personal_sky_negative_$be" "PASS" "the settings left out of the picture are rejected by the probes (exit $($n.Exit))" $n.Seconds }
    else { Add-Case "personal_sky_negative_$be" "FAIL" "the picture without the settings was NOT rejected (exit $($n.Exit))" $n.Seconds }
}
}

Write-Output "Render stage (exported build)"
. (Join-Path $PSScriptRoot "test-launch.ps1")
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-ExportComplete (Split-Path -Parent $exe))) {
    Add-Case "all" "SKIP" "no complete exported build at $exe (the data folder with Viewer.Godot.dll must be beside it; run tools\s1.ps1 first)" 0
    Exit-Render 0
}

. (Join-Path $PSScriptRoot "build-stamp.ps1")
if (-not $PlanOnly) {   # a plan renders nothing, so it has no build to be stale
    $stampProblem = Test-BuildStamp (Split-Path -Parent $exe) $repo $ExpectedCommit
    if ($stampProblem -and -not $AllowStaleBuild) {
        Write-Output "REFUSED: stale or unstamped export: $stampProblem. Nothing was rendered. Re-export with tools\s1.ps1 -ExportOnly, or pass -AllowStaleBuild to render this build anyway (it is logged)."
        exit 2
    }
    if ($stampProblem) {
        Write-Output "WARNING: -AllowStaleBuild: rendering a build that does not match the commit under test: $stampProblem"
        Add-Case "build_stamp" "PASS" "overridden with -AllowStaleBuild: $stampProblem" 0
    }
}

function Invoke-WindowCases {
# A window that stops drawing while the loop runs ends the run with the "not drawing" result, not the timeout.
foreach ($be in $Backends) {
    $tag = "not-drawing-$be"
    $r = Invoke-Render $be $tag "--tr-fixture=synthetic-heavy --tr-path-seconds=30 --tr-seed=8 --tr-budget=2048m --tr-minimize-at=6 --tr-no-restore" 120 -NoDump
    $beatFile = Join-Path $out "$tag.heartbeat"
    $beat = @{}
    if (Test-Path -LiteralPath $beatFile) { foreach ($l in Get-Content -LiteralPath $beatFile) { $kv = $l -split '=', 2; if ($kv.Count -eq 2) { $beat[$kv[0]] = $kv[1] } } }
    $nd = if ($r.Json) { $r.Json.not_drawing } else { $null }
    $problems = @()
    if ($r.Exit -ne 5) { $problems += "exit $($r.Exit), expected 5" }
    if (-not $r.Json -or $r.Json.verdict -ne "NOT_DRAWING") { $problems += "verdict $(if ($r.Json) { $r.Json.verdict } else { 'none (no sidecar)' })" }
    if ($nd -and $nd.window -notmatch 'mode=Minimized can_draw=False') { $problems += "window state: $($nd.window)" }
    if ($nd -and $nd.drawn -ge $nd.processed) { $problems += "drawn $($nd.drawn) not below processed $($nd.processed)" }
    if ($beat["drawn"] -and $beat["frame"] -and [int64]$beat["drawn"] -ge [int64]$beat["frame"]) { $problems += "heartbeat drawn $($beat['drawn']) not below frame $($beat['frame'])" }
    if ($r.Seconds -gt 60) { $problems += "took $($r.Seconds) s" }
    Add-Case "not_drawing_$be" ($(if ($problems) { "FAIL" } else { "PASS" })) ($(if ($problems) { $problems -join "; " } else { "exit 5 NOT_DRAWING in $($r.Seconds) s; window $($nd.window); drawn $($nd.drawn) of $($nd.processed) frames processed" })) $r.Seconds
}

# A window minimised in the middle of a run is brought back at once without taking focus (host\Viewer.Godot\TestWindow.cs), so the
# run goes on drawing and its picture is the one an undisturbed run draws. The control and the minimised run draw the same path.
foreach ($be in $Backends) {
    $common = "--tr-fixture=synthetic-heavy --tr-path-seconds=12 --tr-seed=8 --tr-budget=2048m"
    $c = Invoke-Render $be "minimize-control-$be" $common 120
    $m = Invoke-Render $be "minimize-run-$be" "$common --tr-minimize-at=4" 120
    $problems = @()
    if ($c.Exit -ne 0) { $problems += "control exit $($c.Exit)" }
    if ($m.Exit -ne 0) { $problems += "minimised run exit $($m.Exit) verdict $(if ($m.Json) { $m.Json.verdict } else { 'none' })" }
    $k = if ($m.Json) { $m.Json.window_keeper } else { $null }
    if (-not $k -or $k.minimized_seen -lt 1) { $problems += "the window was never found minimised (window_keeper $($k | ConvertTo-Json -Compress))" }
    $pa = Join-Path $out "minimize-control-$be.png"; $pb = Join-Path $out "minimize-run-$be.png"
    if (-not (Test-Path -LiteralPath $pa) -or -not (Test-Path -LiteralPath $pb)) { $problems += "a picture is missing" }
    elseif ((Get-FileHash -LiteralPath $pa -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $pb -Algorithm SHA256).Hash) { $problems += "the minimised run's picture differs from the control's" }
    Add-Case "window_minimized_$be" ($(if ($problems) { "FAIL" } else { "PASS" })) ($(if ($problems) { $problems -join "; " } else { "minimised once, found $($k.minimized_seen) time(s) and brought back; $($k.marked_frames) frame(s) marked; picture identical to the control's" })) ($c.Seconds + $m.Seconds)
}
}

# The compile gate (--tr-compile-gate, host\Viewer.Godot\CompileWatch.cs): after the start-up warm-up, the engine must compile no pipeline that a frame
# waits for (canvas, mesh, surface or draw source) and no background (specialization) compile may land in a long frame. The scenes: the zoos, the
# projector scene and the water-sky scene at ten fixed times of day; a busy view with lights, projectors and projector shadows; the local region
# replays (the crowd's case is with the avatar cases). The negative control leaves the lights out of the warm-up (--tr-warmup=nolights): the busy view
# then compiles variants for its first point light and projector, and the gate must fail. -GateOnly runs just these.
function GateDetail($j) {
    if (-not $j -or -not $j.compile_watch) { return "no compile_watch in the result" }
    $w = $j.compile_watch
    "$($w.frames) frames after the warm-up (median $($w.median_frame_ms) ms): compiled that a frame waits for $($w.waiting_compilations), in the background $($w.background_compilations), long frames holding a compile $($w.long_frames_with_compilations)"
}
function GateClean($r, [string]$be) {
    $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $r.Json.compile_watch.frames -gt 0 -and $r.Json.compile_watch.waiting_compilations -eq 0 -and $r.Json.compile_watch.background_compilations -eq 0
}
function Invoke-PipelineGate {
    $projZoo = Join-Path $repo "fixtures\materials\projector.matzoo.json"
    $gateDays = @("0.0", "0.1", "0.2", "0.25", "0.3", "0.5", "0.7", "0.75", "0.8", "0.9")
    $zooScenes = @((Join-Path $repo "fixtures\sculpts\sculpt-zoo.sculptzoo.json"), $meshZoo, $texZoo, $matZoo, $alphaZoo, (Join-Path $repo "fixtures\environment\water-sky.scene.json"), $glowZoo, "avatars", $projZoo)
    $gateScenes = ($zooScenes + @($gateDays | ForEach-Object { "$(Join-Path $repo 'fixtures\environment\water-sky.scene.json')@$_" })) -join ';'
    foreach ($be in $Backends) {
        $g = Invoke-Render $be "pipeline-gate-statics-$be" "`"--tr-fixture=$zoo`" `"--tr-warmup-scenes=$gateScenes`" --tr-warmup --tr-compile-gate" 300
        $ok = GateClean $g $be
        Add-Case "pipeline_gate_statics_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(GateDetail $g.Json)$(if (-not $ok) { '; exit ' + $g.Exit + ' ' + ($g.Json.failures -join ' | ') })" $g.Seconds
        $busyGate = "`"--tr-fixture=$projZoo`" --tr-busy=objects:2000,glow:10,lights:8,projectors:8 --tr-size=1280x720 --tr-busy-seconds=4 --tr-compile-gate"
        $b = Invoke-Render $be "pipeline-gate-busy-$be" $busyGate 300
        $ok = GateClean $b $be
        Add-Case "pipeline_gate_busy_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(GateDetail $b.Json)$(if (-not $ok) { '; exit ' + $b.Exit + ' ' + ($b.Json.failures -join ' | ') })" $b.Seconds
        $n = Invoke-Render $be "pipeline-gate-busy-nolights-$be" "$busyGate --tr-warmup=nolights" 300
        $caught = $n.Exit -eq 1 -and ($n.Json.compile_watch.background_compilations + $n.Json.compile_watch.waiting_compilations) -gt 0
        Add-Case "pipeline_gate_negative_$be" ($(if ($caught) { "PASS" } else { "FAIL" })) "$(if ($caught) { 'caught: ' } else { "NOT caught (exit $($n.Exit)): " })warm-up without its lights: $(GateDetail $n.Json)" $n.Seconds
        foreach ($scene in @(@{ Name = "region_a_v5"; Path = (Join-Path $local "capture-region-a-v5.jsonl.gz") }, @{ Name = "region_a_v9"; Path = (Join-Path $local "capture-region-a-v9.jsonl.gz") })) {
            if (-not ((Test-Path -LiteralPath $scene.Path) -and (Test-Path -LiteralPath $assets))) {
                Add-Case "pipeline_gate_$($scene.Name)_$be" "SKIP" "local-only $(Split-Path -Leaf $scene.Path) or the asset cache is absent (grid content, never committed; ADR-011)" 0
                continue
            }
            $r = Invoke-Render $be "pipeline-gate-$($scene.Name)-$be" "`"--tr-fixture=$($scene.Path)`" `"--tr-assets=$assets`" --tr-path-seconds=10 --tr-seed=8 --tr-budget=2048m --tr-size=1920x1080 --tr-texture-server=partial --tr-texture-delay-ms=105 --tr-compile-gate" 300
            $ok = GateClean $r $be
            Add-Case "pipeline_gate_$($scene.Name)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(GateDetail $r.Json)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
    }
}

# the pipeline warm-up. The committed prim, sculpt, mesh, texture and material zoos and the water-sky scene,
# each presented after the warm-up: no pipeline may be compiled while presenting them, and what the warm-up made is freed
# (resources and nodes back to before; objects no more than the scenes leave anyway). The control (--tr-warmup=off) must
# show the first-use compilations, and the warm-up run may add no more engine objects or render buffer memory than the
# control adds by presenting the scenes alone (WarmAdds; compared as additions from each run's own count
# before the warm-up, never as absolute end counts, which differ by one between runs). --tr-warmup=leak (the warm-up keeps
# one material) is the comparison's negative control. LOCAL-ONLY: 10 s paths over region A v5 and region B v3 (the settings otherwise) compile nothing after the
# warm-up.
$projectorZoo = Join-Path $repo "fixtures\materials\projector.matzoo.json"
$warmScenes = @((Join-Path $repo "fixtures\sculpts\sculpt-zoo.sculptzoo.json"), $meshZoo, $texZoo, $matZoo, $alphaZoo, (Join-Path $repo "fixtures\environment\water-sky.scene.json"), $glowZoo, $projectorZoo, "$projectorZoo#omni", "avatars") -join ';'   # avatars: name labels from synthetic updates; the glow scene draws the glow pass; the projector scene brings the projector and, with #omni, the point light that the warm-up covers (the engine keeps its light buffers once it has drawn local lights, so the scenes must draw both kinds for the comparison to be like for like)
$warmArgs = "`"--tr-fixture=$zoo`" `"--tr-warmup-scenes=$warmScenes`""
function WarmDetail($j) {
    if (-not $j -or -not $j.scenes) { return "" }
    $sc = @($j.scenes | ForEach-Object { "$(($_.scene -split ' ')[0..1] -join ' '): $(if ($_.pipelines.PSObject.Properties.Count) { ($_.pipelines.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', ' } else { 'none' })" }) -join '; '
    $w = if ($j.warmup -is [string]) { "off" } else { "$($j.warmup.ms) ms, compiled $(($j.warmup.pipelines.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', ')" }
    "warm-up $w; compiled while presenting $($j.compiled_while_presenting) ($sc); objects $($j.lifecycle.before.objects) -> $($j.lifecycle.after.objects) (end $($j.lifecycle.end.objects)), nodes $($j.lifecycle.before.nodes) -> $($j.lifecycle.after.nodes), resources $($j.lifecycle.before.resources) -> $($j.lifecycle.after.resources)"
}
# The one-time render buffer allocation the warm-up's lights cause: the engine makes its light and shadow buffers the first time it draws
# local lights, and the warm-up is the first thing to draw them. Measured on both backends (Godot 4.7.2): with the warm-up's lights left out
# (--tr-warmup=nolights) the run ends with exactly the control's buffer memory; a second warm-up in the same process adds 0 (the
# warmup_repeat case); with a spot-projector scene added to the compared scenes the control's gap falls from 1,447,632 B to 720,896 B.
# The allowance is 1.5 MiB (1,572,864 B), about twice the largest gap seen, for that one allocation and nothing else: the object counts
# are compared as before, and warmup_repeat guards against a leak. The render buffer memory a kept material adds is far below it, which is
# why the leak control compares against the ordinary warm-up (LeakAdds).
$WarmLightBuffers = 1572864
# what the warm-up run ($w) adds against the control ($c, --tr-warmup=off): the warm-up's own additions (after
# it minus before it) and the run's whole additions (end minus before) may be no more than the control adds by presenting
# the scenes (its end minus its before), in engine objects and render buffer memory. On d3d12 the engine now and then
# makes one object of its own once, at a random point, and keeps it for the run, with no buffer or video memory (the
# lifecycle check allows the same one object): a d3d12 warm-up run may add one object more than its control, and never
# more buffer memory. The leak control's kept material adds an object and its buffer memory, so it still fails there.
function WarmAdds($w, $c, [string]$be) {
    if (-not $w -or -not $c -or -not $w.lifecycle -or -not $c.lifecycle) { return @{ Ok = $false; Text = "no lifecycle in one of the runs" } }
    $wl = $w.lifecycle; $cl = $c.lifecycle
    $wo = $wl.after.objects - $wl.before.objects; $wb = $wl.after.buffer_mem - $wl.before.buffer_mem
    $to = $wl.end.objects - $wl.before.objects; $tb = $wl.end.buffer_mem - $wl.before.buffer_mem
    $co = $cl.end.objects - $cl.before.objects; $cb = $cl.end.buffer_mem - $cl.before.buffer_mem
    $engine = if ($be -eq "d3d12") { 1 } else { 0 }
    $ok = $wo -le ($co + $engine) -and $wb -le ($cb + $WarmLightBuffers) -and $to -le ($co + $engine) -and $tb -le ($cb + $WarmLightBuffers)
    @{ Ok = $ok; Text = "added: warm-up objects $wo, buffer memory $wb; warm-up run objects $to, buffer memory $tb; control (scenes) objects $co, buffer memory $cb; the engine's one-time objects allowed $engine, one-time light buffers allowed $WarmLightBuffers B" }
}
# What the leak control must show: the run whose warm-up keeps one material ($l) ends with more objects or more render buffer memory than
# the ordinary warm-up run ($w) does. The comparison with the scenes (WarmAdds) cannot catch it, because its light-buffer allowance is far
# larger than one material; the kept material adds one object after the warm-up and 48 bytes of buffer memory by the end of the run over the ordinary warm-up (measured, both backends).
function LeakAdds($l, $w) {
    if (-not $l -or -not $w -or -not $l.lifecycle -or -not $w.lifecycle) { return @{ Caught = $false; Text = "no lifecycle in one of the runs" } }
    $lo = $l.lifecycle.after.objects - $l.lifecycle.before.objects; $lb = $l.lifecycle.after.buffer_mem - $l.lifecycle.before.buffer_mem
    $wo = $w.lifecycle.after.objects - $w.lifecycle.before.objects; $wb = $w.lifecycle.after.buffer_mem - $w.lifecycle.before.buffer_mem
    $le = $l.lifecycle.end.buffer_mem - $l.lifecycle.before.buffer_mem; $we = $w.lifecycle.end.buffer_mem - $w.lifecycle.before.buffer_mem
    @{ Caught = ($lo -gt $wo -or $lb -gt $wb -or $le -gt $we); Text = "keeping a material: warm-up adds objects $lo, buffer memory $lb (to the end of the run $le); the ordinary warm-up adds objects $wo, buffer memory $wb (to the end of the run $we)" }
}
function Invoke-WarmCases {
    foreach ($be in $Backends) {
        $wr = Invoke-Render $be "warmup-$be" "$warmArgs --tr-warmup"
        $ok = $wr.Exit -eq 0 -and $wr.Json.backend.driver -eq $be -and $wr.Json.compiled_while_presenting -eq 0 -and @($wr.Json.scenes).Count -eq 11
        Add-Case "warmup_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(WarmDetail $wr.Json)$(if (-not $ok) { '; exit ' + $wr.Exit + ' ' + ($wr.Json.failures -join ' | ') })" $wr.Seconds
        $wc = Invoke-Render $be "warmup-control-$be" "$warmArgs --tr-warmup=off"
        $caught = $wc.Exit -eq 1 -and $wc.Json.compiled_while_presenting -gt 0
        $adds = WarmAdds $wr.Json $wc.Json $be
        Add-Case "warmup_control_$be" ($(if ($caught -and $adds.Ok) { "PASS" } else { "FAIL" })) "$(if ($caught) { "caught: " } else { "NOT caught (exit $($wc.Exit)): " })$(WarmDetail $wc.Json); the warm-up adds no more than the scenes: $($adds.Ok) ($($adds.Text))" $wc.Seconds
        # a second warm-up in the same process adds no node and no render buffer memory, and video memory within 8 KiB (twice the largest run-to-run figure measured, 4 KiB on Direct3D 12, 1.5 KiB on Vulkan): what the engine keeps once is kept once
        $w2 = Invoke-Render $be "warmup-twice-$be" "$warmArgs --tr-warmup=twice"
        $rep = $w2.Json.warmup_repeat
        $repOk = $w2.Exit -eq 0 -and $rep -and $rep.nodes -eq 0 -and $rep.buffer_mem -le 0 -and $rep.video_mem -le 8192
        Add-Case "warmup_repeat_$be" ($(if ($repOk) { "PASS" } else { "FAIL" })) "$(if ($rep) { "second warm-up added: nodes $($rep.nodes), buffer memory $($rep.buffer_mem), video memory $($rep.video_mem) (objects $($rep.objects), resources $($rep.resources) reported, not gated)" } else { "no repeat figures" })$(if (-not $repOk) { "; exit " + $w2.Exit + " " + ($w2.Json.failures -join " | ") })" $w2.Seconds
        $wl = Invoke-Render $be "warmup-leak-$be" "$warmArgs --tr-warmup=leak"
        $leak = LeakAdds $wl.Json $wr.Json
        $lcaught = $wl.Json -and $wl.Json.lifecycle -and $leak.Caught
        Add-Case "warmup_leak_control_$be" ($(if ($lcaught) { "PASS" } else { "FAIL" })) "$(if ($lcaught) { "caught: " } else { "NOT caught (exit $($wl.Exit)): " })$($leak.Text)" $wl.Seconds
    }
}

if ($GateOnly) {   # -GateOnly: just the compile gate's cases (the crowd's is with the avatar cases)
    Invoke-PipelineGate
    Exit-Render 0
}

if ($WarmOnly) {   # -WarmOnly: just the warm-up cases (the warm-up, its control, the repeat and the leak control)
    Invoke-WarmCases
    Exit-Render 0
}

# one terrain view serving three presenters in turn (the lifecycle and kept-collections cases do this): each presents its chunks on the shared
# view, the view makes at most one ground material while a scene is on it and none is left once the presenter is gone, and the engine
# reports no errors. The view releases its own material when it is cleared, so a presenter's teardown never leaves the next one a disposed object.
function Invoke-ViewReuse {
foreach ($be in $Backends) {
    $v = Invoke-Render $be "viewreuse-$be" "--tr-viewreuse"
    $vr = $v.Json.viewreuse
    $ok = $v.Exit -eq 0 -and $v.Json.backend.driver -eq $be -and $vr -and @($vr.passes).Count -eq 3 -and $vr.engine_errors -eq 0
    Add-Case "viewreuse_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($vr) { "3 presenters on one view: chunks " + (@($vr.passes | ForEach-Object { $_.chunks }) -join ', ') + "; ground materials left " + (@($vr.passes | ForEach-Object { $_.materials_left }) -join ', ') + "; engine errors $($vr.engine_errors)" } else { 'no result' })$(if (-not $ok) { '; exit ' + $v.Exit + ' ' + ($v.Json.failures -join ' | ') })" $v.Seconds
}
}

if ($ReuseOnly) {   # -ReuseOnly: just the shared terrain view case
    Invoke-ViewReuse
    Exit-Render 0
}

if ($WindowOnly) {   # -WindowOnly: just the cases about the test window (stopped drawing, minimised and brought back)
    Invoke-WindowCases
    Exit-Render 0
}

if ($ChurnOnly) {   # -ChurnOnly: just the moving-objects case (merge_churn_picture_busy_vulkan and its indicative figures)
    Invoke-BusyChurn
    Exit-Render 0
}

if ($BusyOnly) {   # -BusyOnly: just the busy view's smoke case
    Invoke-BusySmoke
    Invoke-DrawMerge
    Exit-Render 0
}

# the cloud layer (--tr-clouds): the sky with clouds at a fixed scroll, midday and sunset, each picture against its golden and its pixels against the
# official colour (--tr-cloud-probes). The negative control leaves the layer off (--tr-alter=cloudsoff) while the probes still expect clouds: it must fail
# on its probes. The other cases keep the layer off, so their goldens do not move.
function Invoke-CloudCases {
    $skyScene = Join-Path $repo "fixtures\environment\water-sky-default.scene.json"
    foreach ($be in $Backends) {
        foreach ($c in @(@("midday", "-200,128,48,-300,128,118", "midday", 1500), @("sunset", "128,700,48,128,800,118", "sunset", 1500))) {
            $name = $c[0]
            $common = "`"--tr-fixture=$skyScene`" --tr-look=$($c[1]) --tr-timeofday=$($c[2]) --tr-clouds=$($c[3]) --tr-cloud-probes"
            $golden = Join-Path $repo "fixtures\environment\golden-clouds-$name-$be.png"
            if (-not (Test-Path -LiteralPath $golden)) {
                $r = Invoke-Render $be "proposal-clouds-$name-$be" $common
                $cp = $r.Json.cloud_probes
                if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                    Copy-Item -LiteralPath (Join-Path $out "proposal-clouds-$name-$be.png") -Destination $golden
                    Add-Case "clouds_${name}_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it); probes in view $($cp.probes), on cloud $($cp.on_cloud), within 0.03: $($cp.within_0_03), mean difference $($cp.mean_abs_diff)" $r.Seconds
                } else { Add-Case "clouds_${name}_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
            } else {
                $r = Invoke-Render $be "clouds-$name-$be" "$common `"--tr-golden=$golden`""
                $cp = $r.Json.cloud_probes
                $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $cp -and $cp.pass
                Add-Case "clouds_${name}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); cloud probes: $($cp.probes) in view, $($cp.on_cloud) on cloud, $($cp.on_clear_sky) on clear sky, $($cp.within_0_03) within 0.03, mean difference $($cp.mean_abs_diff) (on cloud $($cp.mean_on_cloud), on clear $($cp.mean_on_clear); the sky alone would differ by $($cp.cloud_vs_sky_only) on cloud)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
            }
            # negative control: layer off, probes expect clouds
            $n = Invoke-Render $be "clouds-$name-$be-off" "$common --tr-alter=cloudsoff"
            $failed = $n.Exit -ne 0 -and ($n.Json.failures -join ' ') -match 'cloud probes'
            Add-Case "clouds_${name}_negative_control_$be" ($(if ($failed) { "PASS" } else { "FAIL" })) "layer off, probes expecting clouds: $(if ($failed) { 'failed on its probes as it must' } else { 'DID NOT FAIL (exit ' + $n.Exit + ')' })" $n.Seconds
        }
    }
    Invoke-CloudSwitchCases
}

# switching the clouds off, and on again, while the view is up (the Preferences setting): the picture after "off" must equal a picture drawn without
# clouds, and after "off, on" the cloud picture; the control leaves the clouds on and must differ from the picture without clouds
function Invoke-CloudSwitchCases {
    $skyScene = Join-Path $repo "fixtures\environment\water-sky-default.scene.json"
    $mid = "`"--tr-fixture=$skyScene`" --tr-look=-200,128,48,-300,128,118 --tr-timeofday=midday"
    foreach ($be in $Backends) {
        $ref = Invoke-Render $be "cloudswitch-ref-$be" $mid
        $refPng = Join-Path $out "cloudswitch-ref-$be.png"
        if ($ref.Exit -ne 0 -or -not (Test-Path -LiteralPath $refPng)) { Add-Case "clouds_switch_off_$be" "FAIL" "the picture without clouds was not made (exit $($ref.Exit))" $ref.Seconds; continue }
        $off = Invoke-Render $be "cloudswitch-off-$be" "$mid --tr-clouds=1500 --tr-clouds-switch=off `"--tr-golden=$refPng`""
        $ok = $off.Exit -eq 0 -and $off.Json.diff.pass -and $off.Json.backend.driver -eq $be
        Add-Case "clouds_switch_off_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "after the switch off: $($off.Json.diff.summary)" $off.Seconds
        $ctl = Invoke-Render $be "cloudswitch-control-$be" "$mid --tr-clouds=1500 `"--tr-golden=$refPng`""
        $differs = $ctl.Exit -eq 1 -and $ctl.Json -and $ctl.Json.diff -and -not $ctl.Json.diff.pass
        Add-Case "clouds_switch_off_negative_control_$be" ($(if ($differs) { "PASS" } else { "FAIL" })) "clouds left on, compared with the picture without clouds: $(if ($differs) { 'rejected as it must be' } else { 'NOT rejected (exit ' + $ctl.Exit + ')' })" $ctl.Seconds
        $golden = Join-Path $repo "fixtures\environment\golden-clouds-midday-$be.png"
        if (Test-Path -LiteralPath $golden) {
            $back = Invoke-Render $be "cloudswitch-back-$be" "$mid --tr-clouds=1500 --tr-clouds-switch=offon `"--tr-golden=$golden`""
            $okBack = $back.Exit -eq 0 -and $back.Json.diff.pass -and $back.Json.backend.driver -eq $be
            Add-Case "clouds_switch_off_on_$be" ($(if ($okBack) { "PASS" } else { "FAIL" })) "after the switch off and on: $($back.Json.diff.summary)" $back.Seconds
        }
    }
}

# the midday cloud view's frame time and the engine's render times with the layer on and off (report only: this is a render-harness figure, not a measuring run)
function Invoke-CloudFrameTimes {
    $skyScene = Join-Path $repo "fixtures\environment\water-sky-default.scene.json"
    foreach ($be in $Backends) {
        $fig = @{}
        foreach ($mode in @("on", "off")) {
            $clouds = if ($mode -eq "on") { "--tr-clouds=1500" } else { "" }
            $r = Invoke-Render $be "cloudtime-$mode-$be" "`"--tr-fixture=$skyScene`" --tr-look=-200,128,48,-300,128,118 --tr-timeofday=midday $clouds --tr-cloud-frames=300" 300
            $fig[$mode] = $r.Json.cloud_frames
            $f = $fig[$mode]
            Add-Case "cloud_frame_time_${mode}_$be" ($(if ($r.Exit -eq 0 -and $f) { "PASS" } else { "FAIL" })) "clouds $mode, 300 frames of the midday view: frame p50/p95/max $($f.frame_ms.p50)/$($f.frame_ms.p95)/$($f.frame_ms.max) ms, render gpu p50/p95 $($f.render_gpu_ms.p50)/$($f.render_gpu_ms.p95) ms, render cpu p50 $($f.render_cpu_ms.p50) ms" $r.Seconds
        }
    }
}

if ($CloudsOnly) {   # -CloudsOnly: just the cloud cases
    Invoke-CloudCases
    Invoke-CloudFrameTimes
    Exit-Render 0
}

if ($WaterSkyOnly) {   # -WaterSkyOnly: the water scene and the time-of-day cases (the cases a sky change moves)
    Invoke-SharedEnvCases
    Invoke-WaterCases
    Invoke-WaterLookCases
    Invoke-PersonalCases
    Invoke-TimeOfDayCases
    Exit-Render 0
}

# the five time-of-day skies applied four times in a row through the environment layer: the engine allocates a sky's memory at its first use and
# applying any sky again allocates nothing, so from the end of the first cycle video memory must stay flat within 8 KiB (the allowance of
# warmup_repeat); growth in a later cycle is a leak. Then a fresh sky four times, each freed before the next: freed at once, with no collection.
function Invoke-SkyCycle {
foreach ($be in $Backends) {
    $s = Invoke-Render $be "skycycle-$be" "--tr-skycycle"
    $sc = $s.Json.skycycle
    $ok = $s.Exit -eq 0 -and $s.Json.backend.driver -eq $be -and $sc -and @($sc.samples).Count -eq 20
    Add-Case "skycycle_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($sc) { "sky $($sc.sky_material), process mode $($sc.sky_process_mode), radiance size $($sc.sky_radiance_size); video memory: start $($sc.start_video_mem), first cycle adds $($sc.first_cycle_adds), later cycles add $($sc.later_cycles_add); per sky: " + ((@($sc.samples) | ForEach-Object { $_.video_mem }) -join ', ') } else { 'no result' })$(if (-not $ok) { '; exit ' + $s.Exit + ' ' + ($s.Json.failures -join ' | ') })" $s.Seconds
}
}

if ($SkyCycleOnly) {   # -SkyCycleOnly: just the sky cycle case
    Invoke-SkyCycle
    Exit-Render 0
}

if ($SkyOnly) {   # -SkyOnly: just the time-of-day cases (to propose or check their goldens)
    Invoke-TimeOfDayCases
    Invoke-CloudCases
    Invoke-SkyCycle
    Exit-Render 0
}

if ($SeamsOnly) {   # -SeamsOnly: just the land-seam cases
    Invoke-SeamCases
    Exit-Render 0
}

if ($TreesOnly) {   # -TreesOnly: just the tree cases (to propose or check their goldens)
    Invoke-TreeCases
    Exit-Render 0
}

if ($TeardownOnly) {   # -TeardownOnly: the world torn down in the logout order raises no engine error
    Invoke-TeardownCases
    Exit-Render 0
}

if ($NeighboursOnly) {   # -NeighboursOnly: just the neighbour-region cases (to propose or check their goldens)
    Invoke-NeighbourCases
    Invoke-NeighbourWaterCases
    Invoke-SharedTextureCases
    Exit-Render 0
}

if ($LoginOnly) {   # -LoginOnly: just the login-screen cases (to propose or check their goldens)
    Invoke-LoginScreenCases
    Invoke-LoginCardCases
    Exit-Render 0
}

if ($BanOnly) {   # -BanOnly: just the ban-line cases (to propose or check their goldens)
    Invoke-BanLineCases
    Exit-Render 0
}
if ($ObjectOnly) {   # -ObjectOnly: just the selection highlight and floating text cases (to propose or check their goldens)
    Invoke-ObjectCases
    Exit-Render 0
}
# The character path may hold spaces, so these runs call godot.ps1 in this process (a nested powershell -File splits it).
function Invoke-AvatarRender([string]$driver, [string]$tag, [string]$extra, [int]$timeout = 240) {
    $served = Invoke-CaseProcess $driver $tag "`"--tr-character=$env:NV_CHARACTER_DIR`" `"--tr-assets=$assets`" $extra" $timeout $false
    if ($served) { return $served }
    $t0 = Get-Date
    $json = Join-Path $out "$tag.json"
    $argLine = "--rendering-driver $driver -- --test-render `"--tr-out=$out`" --tr-tag=$tag `"--tr-character=$env:NV_CHARACTER_DIR`" `"--tr-assets=$assets`" $extra"
    $diag = if ($DiagDir) { @{ DiagDir = $DiagDir } } else { @{} }   # named: an array splat binds by position in-process
    & $godot @diag -Exe $exe -AppDataDir (Join-Path $build "appdata") -TimeoutSeconds $timeout -LogFile (Join-Path $out "$tag.log") -ArgLine $argLine | Out-Null
    $code = $LASTEXITCODE
    $j = if ((Test-Path -LiteralPath $json) -and (Get-Item -LiteralPath $json).LastWriteTime -ge $t0.AddSeconds(-1)) { Get-Content -Raw -LiteralPath $json | ConvertFrom-Json } else { $null }
    return @{ Exit = $code; Json = $j; Seconds = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1) }
}
function Invoke-EditLookCases {
    # avatars_edit_look: the own avatar drawn as the simulator sent it, again with its Height raised through the wearable editor and the local preview
    # (the path the Appearance window uses), and a third time after the edit was thrown away (Don't save); side by side (first two) vs
    # fixtures-local\goldens\ (PROPOSED until approved), pixel checks on the body colour: as much taller as the computed heights say, back to the first
    # height after Don't save. avatars_edit_look_negative: the edit is never handed to the avatar, it does not grow, and the checks MUST fail.
    foreach ($be in $Backends) {
        $name = "avatars_edit_look_$be"
        $golden = Join-Path $localGoldens "golden-avatars-edit-height-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-AvatarRender $be "proposal-avatars-edit-height-$be" "--tr-avatars=editlook"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-avatars-edit-height-$be-edit.png") -Destination $golden
                Add-Case $name "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
            } else { Add-Case $name "FAIL" "no local golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
            continue
        }
        $r = Invoke-AvatarRender $be "avatars-edit-height-$be" "--tr-avatars=editlook `"--tr-golden=$golden`""
        $el = $r.Json.avatars_edit_look
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
        Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); computed heights $($el.computed_heights -join ','), drawn heights (px) $($el.pixel_heights -join ',')$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $n = Invoke-AvatarRender "vulkan" "avatars-edit-height-negative-vulkan" "--tr-avatars=editlook --tr-alter=nopreview"
    if ($n.Exit -eq 1 -and $n.Json.backend.driver -eq "vulkan" -and ($n.Json.failures -join ' ') -match 'only') { Add-Case "avatars_edit_look_negative_vulkan" "PASS" "the edit never handed to the avatar: it did not grow, the checks failed ($($n.Json.failures.Count) failures)" $n.Seconds }
    else { Add-Case "avatars_edit_look_negative_vulkan" "FAIL" "an edit that never reached the avatar was NOT caught (exit $($n.Exit))" $n.Seconds }
}
if ($EditLookOnly) {   # -EditLookOnly: just the height-edit avatar cases (to propose or check their goldens); they need the character folder, not the walk animation
    $editSkip = if (-not $env:NV_CHARACTER_DIR) { "local-only character data: NV_CHARACTER_DIR is not set (and the repository's copy is missing)" } elseif (-not (Test-Path -LiteralPath $env:NV_CHARACTER_DIR)) { "NV_CHARACTER_DIR names a folder that is not there" } else { $null }
    if ($editSkip) { foreach ($be in $Backends) { Add-Case "avatars_edit_look_$be" "SKIP" $editSkip 0 } }
    else { Invoke-EditLookCases }
    Exit-Render 0
}
if ($ProportionsOnly) {   # -ProportionsOnly: just the picture-proportion cases (a sphere and a cube in the window's own view)
    Invoke-ProportionsCases
    Exit-Render 0
}
if ($HandlesOnly) {   # -HandlesOnly: just the edit tools' handle and grid cases (to propose or check their goldens)
    Invoke-HandleCases
    Exit-Render 0
}
if ($UiChromeOnly) {   # -UiChromeOnly: just the interface-size cases (to propose or check their goldens)
    Invoke-UiChromeCases
    Invoke-FloaterDragCases
    Invoke-ColourWindowCases
    Exit-Render 0
}
if ($UiOnly) {   # -UiOnly: just the interface scene cases (to propose or check their goldens)
    Invoke-UiSceneCases
    Invoke-UiChromeCases
    Invoke-FloaterDragCases
    Invoke-ColourWindowCases
    Exit-Render 0
}
if ($DialogOnly) {   # -DialogOnly: just the script dialog cases (to propose or check their goldens)
    Invoke-DialogCases
    Exit-Render 0
}



# The name tag case: four stand-in avatars of very different heights with the tags the model plans for them, drawn through the rig's camera.
# The golden is tracked (it holds no avatar body or character data): fixtures\nametags\golden-nametags-<backend>.png. -Propose writes a missing one.
function Invoke-NameTagCases {
    $goldenDir = Join-Path $repo "fixtures\nametags"
    foreach ($be in $Backends) {
        $name = "nametags_$be"
        $golden = Join-Path $goldenDir "golden-nametags-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-nametags-$be" "--tr-nametags"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path $goldenDir | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-nametags-$be.png") -Destination $golden
                Add-Case $name "FAIL" "no golden: PROPOSED $golden" $r.Seconds
            } else { Add-Case $name "FAIL" "no golden $golden (render exit $($r.Exit))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "nametags-$be" "--tr-nametags `"--tr-golden=$golden`""
        $v = $r.Json.name_tags
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $v.drawn -eq 4
        Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); tags planned $($v.planned) drawn $($v.drawn)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    # tags over avatars the viewer draws: one tag each (title and name) and no engine label besides; checked on the tags and the nodes, no golden
    foreach ($be in $Backends) {
        $r = Invoke-Render $be "nametags-avatars-$be" "--tr-nametags-avatars"
        $v = $r.Json.name_tags_avatars
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $v.drawn -eq 2 -and $v.engine_labels -eq 0
        Add-Case "nametags_avatars_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) tags planned $($v.planned) drawn $($v.drawn), engine labels $($v.engine_labels)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $golden = Join-Path $goldenDir "golden-nametags-vulkan.png"
    if (Test-Path -LiteralPath $golden) {
        $n = Invoke-Render "vulkan" "nametags-negative-vulkan" "--tr-nametags --tr-alter=notags `"--tr-golden=$golden`""
        if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass) { Add-Case "nametags_negative_vulkan" "PASS" "tags switched off rejected: $($n.Json.diff.summary)" $n.Seconds }
        else { Add-Case "nametags_negative_vulkan" "FAIL" "tags switched off NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
    } else { Add-Case "nametags_negative_vulkan" "FAIL" "no golden $golden" 0 }
}
if ($NameTagsOnly) {   # -NameTagsOnly: just the name tag cases
    Invoke-NameTagCases
    Exit-Render 0
}

# The automatic cut-out (the official RenderAutoMaskAlphaDeferred / NonDeferred rule, Viewer.Core World\AutoMask): four boxes with one texture whose alpha is
# all or nothing (fixtures\textures\tex-alpha-mask.j2c): a plain face and a fullbright face are drawn as cut-outs (6 faces each, so 12), a glowing face and a
# face whose colour is half transparent stay blended. The analysis is made on each level the viewer uploads, so the count of 12 also proves the faces are drawn
# again when the texture's full level arrives after its coarse first one. Each backend: the picture against its golden (approved after viewing) and the count of cut-out faces;
# the negative control (--tr-automask=off, both settings off) draws none of them as a cut-out.
function Invoke-CutOutCases {
    $zoo = Join-Path $repo "fixtures\textures\auto-mask.texzoo.json"
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\textures\golden-auto-mask-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-auto-mask-$be" "`"--tr-fixture=$zoo`""
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "proposal-auto-mask-$be.png") -Destination $golden
                Add-Case "auto_mask_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "auto_mask_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "auto-mask-$be" "`"--tr-fixture=$zoo`" `"--tr-golden=$golden`""
        $p = $r.Json.prims
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $p.instances -eq 4 -and $p.gpu_textures -eq 1 -and $p.textures_waiting -eq 0 -and $p.auto_masked_faces -eq 12
        Add-Case "auto_mask_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); objects $($p.instances)/4, cut-out faces $($p.auto_masked_faces) (must be 12)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "auto-mask-off-$be" "`"--tr-fixture=$zoo`" --tr-automask=off"
        $np = $n.Json.prims
        $okN = $n.Exit -eq 0 -and $np -and $np.instances -eq 4 -and $np.auto_masked_faces -eq 0
        Add-Case "auto_mask_negative_$be" ($(if ($okN) { "PASS" } else { "FAIL" })) "$(if ($np) { "with the automatic mask settings off $($np.auto_masked_faces) faces are cut-outs, as it must be 0" } else { 'no result, exit ' + $n.Exit })" $n.Seconds
    }
}

if ($CutOutOnly) {   # -CutOutOnly: just the automatic cut-out cases
    Invoke-CutOutCases
    Exit-Render 0
}

if ($GlowOnly) {   # -GlowOnly: just the glow scene's cases (a work-in-progress check, not the whole stage)
    foreach ($be in $Backends) { Invoke-GlowCases $be }
    Invoke-ProjectorCases
    Invoke-GlowNegative
    Invoke-GlowDrawCalls
    Invoke-BusySmoke
    Exit-Render 0
}
if (-not $MeasureOnly) {   # -MeasureOnly: only the opt-in measurements below (not a test run)
if (-not $AvatarsOnly) {   # -AvatarsOnly: skip the terrain, zoo and path sections up to the avatar cases (a work-in-progress check, not the whole stage)
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\terrain\golden-synthetic-512x256-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-synthetic-$be" "`"--tr-fixture=$fixture`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-synthetic-$be.png") -Destination $golden
            Add-Case "golden_$be" "FAIL" "no golden: PROPOSED $golden (needs approval after viewing; rerun to diff against it)" $r.Seconds
        } else {
            Add-Case "golden_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit); -Propose writes a proposal)" $r.Seconds
        }
        continue
    }
    $r = Invoke-Render $be "golden-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`""
    $problems = @()
    if (-not $r.Json) { $problems += "no sidecar (exit $($r.Exit))" }
    else {
        if ($r.Json.backend.driver -ne $be) { $problems += "backend $($r.Json.backend.driver), requested $be" }
        if ($r.Exit -ne 0) { $problems += "exit $($r.Exit): $($r.Json.failures -join ' | ')" }
        if (-not $r.Json.diff.pass) { $problems += "diff: $($r.Json.diff.summary)" }
        if (-not $r.Json.region.complete) { $problems += "terrain incomplete" }
    }
    Add-Case "golden_$be" ($(if ($problems) { "FAIL" } else { "PASS" })) ($(if ($problems) { $problems -join "; " } else { "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); chunks=$($r.Json.render.chunks) build=$($r.Json.render.build_ms) ms" })) $r.Seconds

    # The test window is shown by the operating system (WS_VISIBLE), also with the no-focus flag: the engine's own visible flag
    # says visible either way, so the harness asks user32. Not asserted when the run keeps the usual window untouched or has no window.
    if ($r.Json -and $null -ne $r.Json.window_os_visible) {
        Add-Case "test_window_shown_$be" ($(if ($r.Json.window_os_visible) { "PASS" } else { "FAIL" })) ($(if ($r.Json.window_os_visible) { "the operating system shows the window" } else { "the operating system reports the test window hidden (the engine reports it visible)" })) 0
    }

    # Negative control: the same fixture with a hill raised through the core must FAIL the same diff.
    $n = Invoke-Render $be "negative-$be" "`"--tr-fixture=$fixture`" `"--tr-golden=$golden`" --tr-alter=hill"
    if ($n.Exit -eq 1 -and $n.Json -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
        Add-Case "negative_$be" "PASS" "altered terrain rejected: $($n.Json.diff.summary)" $n.Seconds
    } else {
        Add-Case "negative_$be" "FAIL" "altered terrain NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds
    }
}

# the prim zoo on both backends, and its negative control.
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\prims\golden-prim-zoo-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-zoo-$be" "`"--tr-fixture=$zoo`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-zoo-$be.png") -Destination $golden
            Add-Case "zoo_$be" "FAIL" "no golden: PROPOSED $golden (needs approval after viewing; rerun to diff against it)" $r.Seconds
        } else {
            Add-Case "zoo_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds
        }
        continue
    }
    $r = Invoke-Render $be "zoo-$be" "`"--tr-fixture=$zoo`" `"--tr-golden=$golden`""
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
    Add-Case "zoo_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); prims $($r.Json.prims.instances)/$($r.Json.prims.objects), meshes $($r.Json.prims.meshes), materials $($r.Json.prims.materials)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "zoo-negative-$be" "`"--tr-fixture=$zoo`" `"--tr-golden=$golden`" --tr-alter=prim"
    if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
        Add-Case "zoo_negative_$be" "PASS" "altered prim rejected: $($n.Json.diff.summary)" $n.Seconds
    } else {
        Add-Case "zoo_negative_$be" "FAIL" "altered prim NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds
    }
}

# the mesh zoo, near and far, on both backends, and its negative control.
foreach ($be in $Backends) {
    $lods = @{}
    foreach ($cam in @("near", "far")) {
        $golden = Join-Path $repo "fixtures\meshes\golden-mesh-zoo-$cam-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-mesh-$cam-$be" "`"--tr-fixture=$meshZoo`" --tr-camera=$cam"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "proposal-mesh-$cam-$be.png") -Destination $golden
                Add-Case "mesh_${cam}_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else {
                Add-Case "mesh_${cam}_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds
            }
            continue
        }
        $r = Invoke-Render $be "mesh-$cam-$be" "`"--tr-fixture=$meshZoo`" --tr-camera=$cam `"--tr-golden=$golden`""
        $lods[$cam] = @($r.Json.prims.mesh_lods)
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $r.Json.prims.mesh_objects -eq 3
        Add-Case "mesh_${cam}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); meshes $($r.Json.prims.mesh_objects)/3 at LODs [$($lods[$cam] -join ',')], mesh resources $($r.Json.prims.mesh_resources); camera moved in-run: [$($r.Json.camera_move.lods_before -join ',')] -> [$($r.Json.camera_move.lods_after -join ',')]$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    if ($lods.ContainsKey("near") -and $lods.ContainsKey("far")) {
        $lower = @(0..2 | Where-Object { [int]$lods["far"][$_] -lt [int]$lods["near"][$_] }).Count
        Add-Case "mesh_lod_switch_$be" ($(if ($lower -eq 3) { "PASS" } else { "FAIL" })) "near LODs [$($lods['near'] -join ',')], far LODs [$($lods['far'] -join ',')]: $lower of 3 lower when far" 0
    }
    $golden = Join-Path $repo "fixtures\meshes\golden-mesh-zoo-near-$be.png"
    if (Test-Path -LiteralPath $golden) {
        $n = Invoke-Render $be "mesh-negative-$be" "`"--tr-fixture=$meshZoo`" --tr-camera=near `"--tr-golden=$golden`" --tr-alter=vertex"
        if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) {
            Add-Case "mesh_negative_$be" "PASS" "altered vertex rejected: $($n.Json.diff.summary)" $n.Seconds
        } else {
            Add-Case "mesh_negative_$be" "FAIL" "altered vertex NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds
        }
    }
    # detail at a distance: the levels the official viewer chooses for a mesh and a textured wall at each distance (meshes at its High level, factor 1.5; the
    # expected values are worked from its formulas, not from this viewer's code), then the scene against its golden
    $golden = Join-Path $repo "fixtures\textures\golden-detail-distance-$be.png"
    $hasGolden = Test-Path -LiteralPath $golden
    $goldenArg = if ($hasGolden) { "`"--tr-golden=$golden`"" } else { "" }
    $r = Invoke-Render $be "detail-$be" "`"--tr-fixture=$detailZoo`" --tr-camera=detail $goldenArg"
    $meshLods = @($r.Json.prims.mesh_lods)
    $tl = $r.Json.prims.texture_levels
    $texWanted = @(if ($tl) { $tl.PSObject.Properties | Sort-Object Name | ForEach-Object { $_.Value[1] } })
    $texOnGpu = @(if ($tl) { $tl.PSObject.Properties | Sort-Object Name | ForEach-Object { $_.Value[0] } })
    $ok = $r.Json -and $r.Json.backend.driver -eq $be -and ($meshLods -join ',') -eq '3,2,2,1' -and ($texWanted -join ',') -eq '0,2,3,4' -and ($texOnGpu -join ',') -eq '0,2,3,4'
    Add-Case "detail_levels_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "meshes at LODs [$($meshLods -join ',')] (official 3,2,2,1 at the factor 1.5); textures wanted [$($texWanted -join ',')], on the GPU [$($texOnGpu -join ',')] (official 0,2,3,4)$(if (-not $r.Json) { '; no result, exit ' + $r.Exit })" $r.Seconds
    if (-not $hasGolden) {
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "detail-$be.png") -Destination $golden
            Add-Case "detail_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else {
            Add-Case "detail_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds
        }
    } else {
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
        Add-Case "detail_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    # layered transparency: blended faces at known depths drawn in the official viewer's order; at each probe the colour
    # must be the one the blend arithmetic gives for that order (worked out in LayeredZoo, not from this viewer's code),
    # then each scene against its golden
    foreach ($ls in @(@{ Name = "layered"; File = $layeredZoo; Cam = "layered" }, @{ Name = "layered_water"; File = $layeredWater; Cam = "layered-water" })) {
        $golden = Join-Path $repo ("fixtures\materials\golden-" + ($ls.Name -replace '_', '-') + "-$be.png")
        $hasGolden = Test-Path -LiteralPath $golden
        $goldenArg = if ($hasGolden) { "`"--tr-golden=$golden`"" } else { "" }
        $tag = ($ls.Name -replace '_', '-') + "-$be"
        $r = Invoke-Render $be $tag "`"--tr-fixture=$($ls.File)`" --tr-camera=$($ls.Cam) $goldenArg"
        # the probes' arithmetic is the flat water's (no ripples, no refraction offset, no glint), as the water cases' colour probes are:
        # a scene with water reads its probes from a second, flat run; the rippled picture above is the one vs its golden
        $rp = if ($ls.Cam -eq "layered-water") { Invoke-Render $be "$tag-probes" "`"--tr-fixture=$($ls.File)`" --tr-camera=$($ls.Cam) --tr-water-flat" } else { $r }
        $pr = $rp.Json.probes
        $bad = @(if ($pr) { $pr.rows | Where-Object { -not $_.pass } | ForEach-Object { "$($_.name) [$($_.rgb -join ',')] expected [$($_.expected -join ',')]" } })
        $ok = $r.Json -and $r.Json.backend.driver -eq $be -and $pr -and $pr.pass -and $pr.checked_probes -gt 0
        Add-Case "$($ls.Name)_order_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($pr) { "$($pr.checked_probes) probes checked, tolerance $($pr.tolerance)" } else { 'no probes' })$(if ($bad.Count) { '; wrong: ' + ($bad -join '; ') })$(if (-not $r.Json) { '; no result, exit ' + $r.Exit })" $r.Seconds
        if (-not $hasGolden) {
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "$tag.png") -Destination $golden
                Add-Case "$($ls.Name)_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else {
                Add-Case "$($ls.Name)_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds
            }
        } else {
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case "$($ls.Name)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
    }
    Invoke-GlowCases $be
    if ($be -eq "vulkan") { Invoke-ProjectorCases }
    # each blended face handed to the engine once: one object with every face blended (a prim box of 6, a mesh of 8),
    # seen by a circling camera, must give the engine k surfaces to draw in every frame, not k for each face instance
    foreach ($bc in @(@{ Kind = "box6"; Faces = 6 }, @{ Kind = "mesh8"; Faces = 8 })) {
        $r = Invoke-Render $be "blended-once-$($bc.Kind)-$be" "--tr-blendstress=$($bc.Kind):1 --tr-blendstress-seconds=1 --tr-blendstress-expect-surfaces=$($bc.Faces) --tr-size=640x360"
        $bs = $r.Json.blendstress
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $bs -and $bs.blend_places -eq $bc.Faces
        Add-Case "blended_once_$($bc.Kind)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($bs) { "$($bs.blend_places) places, $($bs.blend_face_instances) face instances; surfaces handed to the engine per frame p50 $($bs.view_objects.p50) max $($bs.view_objects.max) (expected $($bc.Faces)) over $($bs.frames) frames" })$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    # mesh LOD surface data prepared off the main thread is the engine's own, byte for byte; builds
    # are handovers only; an object switching LOD draws its previous LOD meanwhile; unwanted data is let go; a region
    # change leaves nothing in flight or held. Every other surface the viewer draws too (every
    # prim-zoo shape, every sculpt type at every LOD, terrain chunks with and without a composition, the water plane)
    $m = Invoke-Render $be "meshprep-$be" "`"--tr-fixture=$meshZoo`" --tr-meshprep"
    $mp = $m.Json.meshprep
    $kinds = @("mesh", "prim", "sculpt", "terrain", "water")
    $allKinds = $mp -and @($kinds | Where-Object { $mp.by_kind.$_ -and $mp.by_kind.$_.surfaces -gt 0 -and $mp.by_kind.$_.identical -eq $mp.by_kind.$_.surfaces }).Count -eq $kinds.Count
    $ok = $m.Exit -eq 0 -and $m.Json.backend.driver -eq $be -and $mp -and $mp.mismatches -eq 0 -and $mp.zoo_lods -gt 0 -and $allKinds
    $steps = if ($mp) { ($mp.lifecycle.steps | ForEach-Object { $_.at }) -join ' / ' } else { "" }
    $byKind = if ($mp) { ($kinds | ForEach-Object { "$_ $($mp.by_kind.$_.identical)/$($mp.by_kind.$_.surfaces)" }) -join ', ' } else { "" }
    Add-Case "meshprep_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($mp) { "$(@($mp.cases).Count) cases ($($mp.zoo_lods) mesh-zoo LODs, $($mp.prim_shapes) prim shapes, sculpt types $($mp.sculpt_types -join ',')) identical to the engine's own packing, surfaces by kind: $byKind; $($mp.mismatches) mismatches; lifecycle: $steps; dropped $($mp.lifecycle.dropped), discarded $($mp.lifecycle.discarded)" })$(if (-not $ok) { '; exit ' + $m.Exit + ' ' + ($m.Json.failures -join ' | ') })" $m.Seconds
}
Invoke-GlowNegative
Invoke-GlowDrawCalls
Invoke-BusySmoke
Invoke-DrawMerge
# the negative control of the byte check: one bit of every prepared surface changed must be caught on every kind
$nb = Invoke-Render "vulkan" "meshprep-negative-vulkan" "`"--tr-fixture=$meshZoo`" --tr-meshprep --tr-alter=prepbyte"
$nbp = $nb.Json.meshprep
$caught = $nbp -and @("mesh", "prim", "sculpt", "terrain", "water" | Where-Object { $nbp.by_kind.$_ -and $nbp.by_kind.$_.surfaces -gt 0 -and $nbp.by_kind.$_.identical -eq 0 }).Count -eq 5
if ($nb.Exit -eq 1 -and $nbp.altered -and $caught) {
    Add-Case "meshprep_negative_vulkan" "PASS" "every altered surface caught: $((@("mesh", "prim", "sculpt", "terrain", "water") | ForEach-Object { "$_ 0/$($nbp.by_kind.$_.surfaces) identical" }) -join ', ')" $nb.Seconds
} else {
    Add-Case "meshprep_negative_vulkan" "FAIL" "altered surfaces NOT all caught (exit $($nb.Exit))" $nb.Seconds
}
# one site left, the handover (every surface is prepared bytes). Every site's Godot collections are kept (the same wrapper after many calls and two region loads
# per terrain view), empty after every call, nothing carried between calls; the Variant disposal guard (GodotSharp 4.7.2,
# the disposer found). Negative controls: a new set at every use, and no clearing, must both fail.
$keptEnv = Join-Path $repo "fixtures\environment\synthetic-environment.llsd.xml"
$keptArgs = "`"--tr-fixture=$fixture`" `"--tr-textures=$texDir`" `"--tr-environment=$keptEnv`""
foreach ($be in $Backends) {
    $k = Invoke-Render $be "keptcollections-$be" "$keptArgs --tr-keptcollections"
    $kc = $k.Json.keptcollections
    $ok = $k.Exit -eq 0 -and $k.Json.backend.driver -eq $be -and $kc -and $kc.variant_disposal -eq "pinned" -and @($kc.sites).Count -eq 1
    $kq = if ($kc) { $kc.queue } else { $null }
    Add-Case "keptcollections_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($kc) { "sites $(@($kc.sites) -join ','); GodotSharp $($kc.godotsharp), Variant disposal $($kc.variant_disposal); not reused $($kc.not_same), not cleared $($kc.not_empty), wrong vertex counts $($kc.wrong_vertex_count); made at startup (frame $($kc.prepared_at_frame), $($kc.sets_at_start) sets, $($kc.made_late) late, $($kc.setup_ms) ms); disposals in a call $($kc.disposals_outside_drain); queue: $($kq.queued) queued, $($kq.disposed) disposed, $($kq.waiting) waiting, drain per frame: disposals max $($kq.frame_value_ms_max) ms, late starts $($kq.late_starts), wall max $($kq.frame_ms_max) ms p95 $($kq.frame_ms_p95) ms over $($kq.drain_frames) frames ($($kq.stalled_frames) stalled between values, $($kq.drains_with_collection) with a collection), peak $($kq.peak_values) values $($kq.peak_bytes) bytes, behind $($kq.frames_behind), forced $($kq.forced_drains)" })$(if (-not $ok) { '; exit ' + $k.Exit + ' ' + ($k.Json.failures -join ' | ') })" $k.Seconds
}
foreach ($ctl in @("fresh", "noclear")) {
    $k = Invoke-Render "vulkan" "keptcollections-control-$ctl-vulkan" "$keptArgs --tr-keptcollections=$ctl"
    $caught = $k.Exit -eq 1 -and @($k.Json.failures | Where-Object { $_ -match $(if ($ctl -eq "fresh") { "not reused" } else { "not cleared" }) }).Count -ge 1
    Add-Case "keptcollections_control_$($ctl)_vulkan" ($(if ($caught) { "PASS" } else { "FAIL" })) "$(if ($caught) { "caught: " } else { "NOT caught (exit $($k.Exit)): " })$($k.Json.failures -join ' | ')" $k.Seconds
}
# the drain's bound is what the drain controls: a slowed drain and a drain that ignores its slice must fail it; a thread
# stalled between values (which no drain can bound) must pass it, its wall time over 1 ms only reported
foreach ($ctl in @("slowdrain", "noslice", "stall")) {
    $k = Invoke-Render "vulkan" "keptcollections-control-$ctl-vulkan" "$keptArgs --tr-keptcollections=$ctl"
    $kq = if ($k.Json.keptcollections) { $k.Json.keptcollections.queue } else { $null }
    $caught = switch ($ctl) {
        "slowdrain" { $k.Exit -eq 1 -and @($k.Json.failures | Where-Object { $_ -match "disposals took" }).Count -ge 1 }
        "noslice" { $k.Exit -eq 1 -and @($k.Json.failures | Where-Object { $_ -match "after their frame's drain slice" }).Count -ge 1 }
        "stall" { $k.Exit -eq 0 -and $kq -and $kq.frame_ms_max -gt 1.0 -and $kq.stalled_frames -gt 0 }
    }
    $what = if ($kq) { "disposals max $($kq.frame_value_ms_max) ms, late starts $($kq.late_starts), wall max $($kq.frame_ms_max) ms, stalled $($kq.stalled_frames)" } else { "no queue report" }
    Add-Case "keptcollections_control_$($ctl)_vulkan" ($(if ($caught) { "PASS" } else { "FAIL" })) "$(if ($ctl -eq 'stall') { if ($caught) { 'passed with the stall seen: ' } else { "stall NOT as expected (exit $($k.Exit)): " } } elseif ($caught) { 'caught: ' } else { "NOT caught (exit $($k.Exit)): " })$what$(if ($k.Json.failures) { '; ' + ($k.Json.failures -join ' | ') })" $k.Seconds
}
# every engine error hands the logger its script backtraces; they are released on the main thread, pinned, not left to
# a finalizer: errors on the main thread and on a worker thread with collections held off, then the main thread's drain
# must leave no engine object of theirs alive, and with frames running the frame's drain releases them. Negative
# control: a logger that hands nothing over (as before) must fail.
foreach ($be in $Backends) {
    $e = Invoke-Render $be "engineerrors-$be" "--tr-engineerrors"
    $ee = $e.Json.engineerrors
    $ok = $e.Exit -eq 0 -and $e.Json.backend.driver -eq $be -and $ee -and $ee.held -and $ee.left_after_drain -le 1
    Add-Case "engineerrors_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($ee) { "$($ee.raised) errors ($($ee.handed_off_main) off the main thread), $($ee.objects_per_error) engine objects each until released, $($ee.left_after_drain) left after the drain ($($ee.drain_us_per_error) us per error); frame drains released $($ee.frame_disposed) of $($ee.frame_errors) in $($ee.frames_to_release) frames; error own time p50 $($ee.error_ms.p50) ms, mean $($ee.error_ms_mean) ms" })$(if (-not $ok) { '; exit ' + $e.Exit + ' ' + ($e.Json.failures -join ' | ') })" $e.Seconds
    # The same check with every method optimised from its first call (the runtime's tiered compilation off). GodotSharp's
    # GD.PushError names the frame above its caller (a StackTrace that skips three frames, then GetFrame(0) used without a null
    # check); once PushError and GD.ErrPrintError are optimised, PushError's call is a tail call and the helper that takes the
    # StackTrace is inlined, so on a thread whose own method calls PushError directly there is no such frame and the call throws.
    # Normally the methods reach their optimised code partway through the worker's errors, at a moment that varies, so the case
    # above crashed in some runs only; here they are optimised from the start, so a worker that calls PushError that way fails
    # every time.
    $prevTc = $env:DOTNET_TieredCompilation
    $env:DOTNET_TieredCompilation = "0"
    try { $e = Invoke-Render $be "engineerrors-optimised-$be" "--tr-engineerrors" -NoDump }
    finally { if ($null -eq $prevTc) { Remove-Item Env:\DOTNET_TieredCompilation -ErrorAction SilentlyContinue } else { $env:DOTNET_TieredCompilation = $prevTc } }
    $ee = $e.Json.engineerrors
    $ok = $e.Exit -eq 0 -and $e.Json.backend.driver -eq $be -and $ee -and $ee.held -and $ee.left_after_drain -le 1 -and $ee.handed_off_main -eq $ee.worker_errors
    Add-Case "engineerrors_optimised_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($ee) { "$($ee.raised) errors ($($ee.handed_off_main) off the main thread), every method optimised from its first call, $($ee.left_after_drain) left after the drain" } else { 'no result' })$(if (-not $ok) { '; exit ' + $e.Exit + ' ' + ($e.Json.failures -join ' | ') })" $e.Seconds
}
$e = Invoke-Render "vulkan" "engineerrors-control-keep-vulkan" "--tr-engineerrors=keep"
$caught = $e.Exit -eq 1 -and @($e.Json.failures | Where-Object { $_ -match "only a finalizer would release them" }).Count -ge 1
Add-Case "engineerrors_control_keep_vulkan" ($(if ($caught) { "PASS" } else { "FAIL" })) "$(if ($caught) { "caught: " } else { "NOT caught (exit $($e.Exit)): " })$($e.Json.failures -join ' | ')" $e.Seconds

Invoke-ViewReuse

# today's path (every mesh LOD built on the main thread when first drawn) must fail the checks
$nc = Invoke-Render "vulkan" "meshprep-control-vulkan" "`"--tr-fixture=$meshZoo`" --tr-meshprep --tr-mesh-main-thread"
if ($nc.Exit -eq 1 -and @($nc.Json.failures).Count -ge 5 -and $nc.Json.meshprep.mismatches -eq 0) {
    Add-Case "meshprep_control_vulkan" "PASS" "the main-thread path fails $(@($nc.Json.failures).Count) checks: $($nc.Json.failures -join ' | ')" $nc.Seconds
} else {
    Add-Case "meshprep_control_vulkan" "FAIL" "the main-thread path was NOT caught (exit $($nc.Exit), $(@($nc.Json.failures).Count) failures)" $nc.Seconds
}

# terrain chunks built and encoded off the main thread: every chunk handed over byte-identical to the
# main-thread build (the committed synthetic terrain on both backends; LOCAL-ONLY region B v3 and region A v5 on both), none built or
# encoded on the main thread, a patch changed while its chunk prepares (the stale result discarded, the final image equal to
# a fresh build's), a region reset while chunks prepare (nothing old handed over, counts back to zero); and the negative
# control (every chunk built on the main thread, as before) must fail
function TerrainPrepDetail($j) {
    $t = $j.terrainprep
    if (-not $t) { return "" }
    "fixture $($t.fixture.identical)/$($t.fixture.chunks) chunks identical ($($t.fixture.with_terrain) with terrain, composition $($t.fixture.composition)), prepared $($t.fixture.prep.prepared); " +
    "stale: held $($t.stale.held_at_change), discarded $($t.stale.stale), $($t.stale.identical)/$($t.stale.chunks) identical, image equal $($t.stale.image_equal_to_fresh); " +
    "reset: in flight $($t.reset.in_flight_at_reset), handed after $($t.reset.handed_over_after), resources $($t.reset.live_resources), running $($t.reset.running); " +
    "main thread: built $($t.built_on_main_thread), encoded $($t.encoded_on_main_thread)"
}
function TerrainPrepOk($r, $be) {
    $t = $r.Json.terrainprep
    return $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $t -and $t.fixture.chunks -gt 0 -and $t.fixture.identical -eq $t.fixture.chunks -and $t.fixture.prep.prepared -gt 0 -and
        $t.stale.stale -ge 1 -and $t.stale.identical -eq $t.stale.chunks -and $t.stale.image_equal_to_fresh -and $t.reset.in_flight_at_reset -ge 1 -and $t.reset.handed_over_after -eq 0 -and
        $t.reset.live_resources -eq 0 -and $t.built_on_main_thread -eq 0 -and $t.encoded_on_main_thread -eq 0
}
$regionB3 = Join-Path $local "capture-region-b-v3.jsonl.gz"
$regionA5tp = Join-Path $local "capture-region-a-v5.jsonl.gz"
foreach ($be in $Backends) {
    $t = Invoke-Render $be "terrainprep-$be" "`"--tr-fixture=$fixture`" --tr-terrainprep"
    $ok = TerrainPrepOk $t $be
    Add-Case "terrainprep_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(TerrainPrepDetail $t.Json)$(if (-not $ok) { '; exit ' + $t.Exit + ' ' + ($t.Json.failures -join ' | ') })" $t.Seconds
    foreach ($scene in @(@{ Name = "region_b_v3"; Path = $regionB3 }, @{ Name = "region_a_v5"; Path = $regionA5tp })) {
        if (-not (Test-Path -LiteralPath $scene.Path)) {
            Add-Case "terrainprep_$($scene.Name)_$be" "SKIP" "local-only $(Split-Path -Leaf $scene.Path) is absent (grid content, never committed; ADR-011)" 0
            continue
        }
        $t = Invoke-Render $be "terrainprep-$($scene.Name)-$be" "`"--tr-fixture=$($scene.Path)`" --tr-terrainprep"
        $ok = TerrainPrepOk $t $be
        Add-Case "terrainprep_$($scene.Name)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(TerrainPrepDetail $t.Json)$(if (-not $ok) { '; exit ' + $t.Exit + ' ' + ($t.Json.failures -join ' | ') })" $t.Seconds
    }
}
$tc = Invoke-Render "vulkan" "terrainprep-control-vulkan" "`"--tr-fixture=$fixture`" --tr-terrainprep --tr-terrain-main-thread"
$caught = $tc.Exit -eq 1 -and $tc.Json.terrainprep -and $tc.Json.terrainprep.built_on_main_thread -gt 0 -and $tc.Json.terrainprep.encoded_on_main_thread -gt 0
Add-Case "terrainprep_control_vulkan" ($(if ($caught) { "PASS" } else { "FAIL" })) "$(if ($caught) { "caught: " } else { "NOT caught (exit $($tc.Exit)): " })$($tc.Json.failures -join ' | ')" $tc.Seconds

# the settle gate on LOCAL-ONLY region B v3 (a camera path with the texture server, as measured):
# every chunk with terrain handed over once (one more only for a chunk the cap started early), none built again because
# the detail textures arrived after it was built; and the negative control (no settle window, the earlier behaviour) must fail
function SettleDetail($j) { $s = $j.settle_check; $g = $j.path.terrain_settle; if (-not $s) { return "" }; "handovers $($s.handovers) for $($s.chunks_with_terrain) chunks with terrain (most for one chunk $($s.most_per_chunk)); started by the cap $($s.started_by_cap); settle delay p50 $($g.settle_delay_ms.p50) max $($g.settle_delay_ms.max) ms; complete $($j.path.terrain_complete_ms) ms" }
$settleArgs = "`"--tr-assets=$(Join-Path $local "assets")`" --tr-path-seconds=5 --tr-seed=8 --tr-budget=2048m --tr-size=1920x1080 --tr-texture-server=partial --tr-texture-delay-ms=105 --tr-content-store --tr-settle-check"
if (-not (Test-Path -LiteralPath $regionB3)) {
    Add-Case "terrain_settle_region_b_v3_vulkan" "SKIP" "local-only capture-region-b-v3.jsonl.gz is absent (grid content, never committed; ADR-011)" 0
    Add-Case "terrain_settle_control_vulkan" "SKIP" "local-only capture-region-b-v3.jsonl.gz is absent (grid content, never committed; ADR-011)" 0
} else {
    $st = Invoke-Render "vulkan" "terrain-settle-region-b-v3-vulkan" "`"--tr-fixture=$regionB3`" $settleArgs" 300
    $ok = $st.Exit -eq 0 -and $st.Json.settle_check -and $st.Json.settle_check.chunks_with_terrain -gt 0 -and $st.Json.settle_check.handovers -le ($st.Json.settle_check.chunks_with_terrain + $st.Json.settle_check.started_by_cap)
    Add-Case "terrain_settle_region_b_v3_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(SettleDetail $st.Json)$(if (-not $ok) { '; exit ' + $st.Exit + ' ' + ($st.Json.failures -join ' | ') })" $st.Seconds
    $sc = Invoke-Render "vulkan" "terrain-settle-control-vulkan" "`"--tr-fixture=$regionB3`" $settleArgs --tr-terrain-settle-ms=0" 300
    $caught = $sc.Exit -eq 1 -and $sc.Json.settle_check -and $sc.Json.settle_check.handovers -gt ($sc.Json.settle_check.chunks_with_terrain + $sc.Json.settle_check.started_by_cap)
    Add-Case "terrain_settle_control_vulkan" ($(if ($caught) { "PASS" } else { "FAIL" })) "$(if ($caught) { "caught: " } else { "NOT caught (exit $($sc.Exit)): " })$(SettleDetail $sc.Json)" $sc.Seconds
}

Invoke-WarmCases
foreach ($be in $Backends) {
    foreach ($scene in @(@{ Name = "region_a_v5"; Path = $regionA5tp }, @{ Name = "region_b_v3"; Path = $regionB3 }, @{ Name = "region_a_v9"; Path = (Join-Path $local "capture-region-a-v9.jsonl.gz") })) {
        if (-not ((Test-Path -LiteralPath $scene.Path) -and (Test-Path -LiteralPath $assets))) {
            Add-Case "warmup_path_$($scene.Name)_$be" "SKIP" "local-only $(Split-Path -Leaf $scene.Path) or the asset cache is absent (grid content, never committed; ADR-011)" 0
            continue
        }
        $wp = Invoke-Render $be "warmup-path-$($scene.Name)-$be" "`"--tr-fixture=$($scene.Path)`" `"--tr-assets=$assets`" --tr-path-seconds=10 --tr-seed=8 --tr-budget=2048m --tr-size=1920x1080 --tr-texture-server=partial --tr-texture-delay-ms=105" 300
        $fl = $wp.Json.path.frames_log
        $n = if ($fl) { ($fl.pipeline_compilations.PSObject.Properties | Measure-Object -Property Value -Sum).Sum } else { -1 }
        if ($null -eq $n) { $n = 0 }
        $ok = $wp.Exit -eq 0 -and $wp.Json.backend.driver -eq $be -and $fl -and $n -eq 0 -and $wp.Json.warmup.ms -gt 0
        Add-Case "warmup_path_$($scene.Name)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($fl) { "warm-up $($wp.Json.warmup.ms) ms; pipelines compiled in the path $n; hitch total $($wp.Json.path.hitch_ms_total) ms in $($wp.Json.path.hitch_frames) frames" })$(if (-not $ok) { '; exit ' + $wp.Exit + ' ' + ($wp.Json.failures -join ' | ') })" $wp.Seconds
    }
}

Invoke-PipelineGate

# a short camera path over the committed synthetic terrain: every terrain chunk the presenter builds
# is recorded as an upload unit of kind terrain (the path run's own check), and split out in the path report.
#every chunk handed over is a terrain unit, every one prepared off the main thread (none built or encoded on it)
$tp = Invoke-Render "vulkan" "path-terrain-units-vulkan" "--tr-fixture=synthetic-256 `"--tr-assets=$texDir`" --tr-path=90 --tr-seed=8 --tr-budget=2048m" 300
$tpp = $tp.Json.path
$ok = $tp.Exit -eq 0 -and $tpp -and $tpp.terrain_chunks_built -gt 0 -and $tpp.terrain_upload_units -eq $tpp.terrain_chunks_built -and $tpp.upload_units_by_kind.terrain.units -eq $tpp.terrain_chunks_built -and
    $tpp.terrain_prep -and $tpp.terrain_prep.prepared + $tpp.terrain_prep.failed -eq $tpp.terrain_chunks_built -and $tpp.terrain_built_on_main_thread -eq 0 -and $tpp.terrain_encoded_on_main_thread -eq 0
Add-Case "path_terrain_units_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(if ($tpp) { "chunks handed over $($tpp.terrain_chunks_built), terrain units $($tpp.terrain_upload_units) (over 4 ms $($tpp.terrain_upload_units_over_4ms), worst $($tpp.terrain_worst_ms) ms); prepared off the main thread $($tpp.terrain_prep.prepared), stale $($tpp.terrain_prep.stale), built / encoded on the main thread $($tpp.terrain_built_on_main_thread) / $($tpp.terrain_encoded_on_main_thread); complete $($tpp.terrain_complete_ms) ms" })$(if (-not $ok) { '; exit ' + $tp.Exit + ' ' + ($tp.Json.failures -join ' | ') })" $tp.Seconds

# the bound on decoded mesh geometry with the mesh zoo and the content store, on both
# backends; with a 0-byte shared asset store, so every re-decode reads the content store; and the negative control (the
# bound off in the pass under test) must fail the 10.5 s step
function BoundDetail($j) {
    $b = $j.decodedbound
    if (-not $b) { return "" }
    $src = if ($b.re_decode_sources) { ($b.re_decode_sources.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', ' } else { "none" }
    $t = @($b.steps | Where-Object { $_.pass -eq "test" })
    $at = ($t | ForEach-Object { "$($_.at): decoded $($_.decoded_entries) ($($_.decoded_bytes) B), held $($_.held), objects $($_.objects)" }) -join '; '
    "re-decodes $($b.re_decodes) (within 10 s $($b.re_decodes_within_10s), network $($b.network_re_decodes)), from $src; let go $($b.let_go), at reset $($b.let_go_at_reset); $at"
}
foreach ($be in $Backends) {
    $d = Invoke-Render $be "decodedbound-$be" "`"--tr-fixture=$meshZoo`" --tr-decodedbound --tr-content-store"
    $ok = $d.Exit -eq 0 -and $d.Json.decodedbound.re_decodes -gt 0 -and $d.Json.decodedbound.network_re_decodes -eq 0
    Add-Case "decodedbound_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(BoundDetail $d.Json)$(if (-not $ok) { '; exit ' + $d.Exit + ' ' + ($d.Json.failures -join ' | ') })" $d.Seconds
}
$d = Invoke-Render "vulkan" "decodedbound-contentstore-vulkan" "`"--tr-fixture=$meshZoo`" --tr-decodedbound --tr-content-store --tr-asset-store-mib=0"
$srcs = @($d.Json.decodedbound.re_decode_sources | ForEach-Object { $_.PSObject.Properties.Name })
$ok = $d.Exit -eq 0 -and $d.Json.decodedbound.re_decodes -gt 0 -and $d.Json.decodedbound.network_re_decodes -eq 0 -and $srcs.Count -eq 1 -and $srcs[0] -eq "content"
Add-Case "decodedbound_contentstore_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "shared store 0 bytes: $(BoundDetail $d.Json)$(if (-not $ok) { '; exit ' + $d.Exit + ' ' + ($d.Json.failures -join ' | ') })" $d.Seconds
$d = Invoke-Render "vulkan" "decodedbound-control-vulkan" "`"--tr-fixture=$meshZoo`" --tr-decodedbound=off --tr-content-store"
$caught = $d.Exit -eq 1 -and (@($d.Json.failures) -match "test 10.5 s: .* not let go").Count -gt 0
Add-Case "decodedbound_control_vulkan" ($(if ($caught) { "PASS" } else { "FAIL" })) "$(if ($caught) { "caught: " } else { "NOT caught (exit $($d.Exit)): " })$($d.Json.failures -join ' | ')" $d.Seconds

# the texture zoo on both backends, and its negative control.
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\textures\golden-texture-zoo-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-tex-$be" "`"--tr-fixture=$texZoo`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-tex-$be.png") -Destination $golden
            Add-Case "tex_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else { Add-Case "tex_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "tex-$be" "`"--tr-fixture=$texZoo`" `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $p.instances -eq 10 -and $p.gpu_textures -eq 3 -and $p.textures_waiting -eq 0
    Add-Case "tex_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); objects $($p.instances)/10, GPU textures $($p.gpu_textures), textured materials $($p.textured_materials)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "tex-negative-$be" "`"--tr-fixture=$texZoo`" `"--tr-golden=$golden`" --tr-alter=offset"
    if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) { Add-Case "tex_negative_$be" "PASS" "altered offset rejected: $($n.Json.diff.summary)" $n.Seconds }
    else { Add-Case "tex_negative_$be" "FAIL" "altered offset NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
}

# the automatic cut-out cases (the texture of all-or-nothing alpha drawn as a mask)
Invoke-CutOutCases

# the material zoo on both backends (every PBR face drawn with its material), and its negative control.
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\materials\golden-material-zoo-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-mat-$be" "`"--tr-fixture=$matZoo`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-mat-$be.png") -Destination $golden
            Add-Case "mat_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else { Add-Case "mat_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "mat-$be" "`"--tr-fixture=$matZoo`" `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $p.instances -eq 10 -and $p.pbr_faces -eq 41 -and $p.pbr_faces_not_opaque -eq 0 -and $p.material_assets_unavailable -eq 0 -and $p.materials_waiting -eq 0 -and $p.textures_waiting -eq 0 -and $p.gpu_textures -eq 5 -and $p.pbr_detail_maps_in_view -eq 3 -and $p.pbr_detail_maps_below_level -eq 0
    Add-Case "mat_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); objects $($p.instances)/10, PBR faces $($p.pbr_faces)/41 (not opaque $($p.pbr_faces_not_opaque)), PBR materials $($p.pbr_materials), assets read $($p.material_assets_ready) unavailable $($p.material_assets_unavailable), GPU textures $($p.gpu_textures)/5, detail maps in view $($p.pbr_detail_maps_in_view)/3 (below their level $($p.pbr_detail_maps_below_level))$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "mat-negative-$be" "`"--tr-fixture=$matZoo`" `"--tr-golden=$golden`" --tr-alter=override"
    if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) { Add-Case "mat_negative_$be" "PASS" "overrides left out rejected: $($n.Json.diff.summary)" $n.Seconds }
    else { Add-Case "mat_negative_$be" "FAIL" "overrides left out NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
}

# the alpha zoo on both backends: legacy materials (every diffuse alpha mode, normal and specular maps) and PBR alpha modes,
# each face drawn with its material; its negative control leaves the region's legacy materials out.
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\materials\golden-alpha-zoo-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-alphazoo-$be" "`"--tr-fixture=$alphaZoo`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-alphazoo-$be.png") -Destination $golden
            Add-Case "alphazoo_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else { Add-Case "alphazoo_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "alphazoo-$be" "`"--tr-fixture=$alphaZoo`" `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $p.instances -eq 12 -and $p.legacy_material_faces -eq 43 -and $p.legacy_materials_waiting -eq 0 -and $p.pbr_faces -eq 18 -and $p.pbr_faces_not_opaque -eq 18 -and $p.material_assets_unavailable -eq 0 -and $p.materials_waiting -eq 0 -and $p.textures_waiting -eq 0 -and $p.gpu_textures -eq 4 -and $p.pbr_detail_maps_in_view -eq 2 -and $p.pbr_detail_maps_below_level -eq 0
    Add-Case "alphazoo_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); objects $($p.instances)/12, legacy material faces $($p.legacy_material_faces)/43 (materials $($p.legacy_materials), waiting $($p.legacy_materials_waiting)), PBR faces $($p.pbr_faces)/18 (not opaque $($p.pbr_faces_not_opaque)), GPU textures $($p.gpu_textures)/4, detail maps in view $($p.pbr_detail_maps_in_view)/2 (below their level $($p.pbr_detail_maps_below_level))$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "alphazoo-negative-$be" "`"--tr-fixture=$alphaZoo`" `"--tr-golden=$golden`" --tr-alter=nolegacy"
    if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be -and $n.Json.prims.legacy_material_faces -eq 0) { Add-Case "alphazoo_negative_$be" "PASS" "legacy materials left out rejected: $($n.Json.diff.summary)" $n.Seconds }
    else { Add-Case "alphazoo_negative_$be" "FAIL" "legacy materials left out NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
}

# The PBR faces' legacy fallback on the material zoo. The override box's base colour (a texture only its 6 faces use) is
# held back until everything else has settled. While it is held those faces must show their legacy texture (already on
# screen for the other boxes); once it arrives they switch to their PBR material and the image matches the approved
# golden. The fixture textures must be fetched exactly as often as with the fallback off (the fallback asks for nothing).
# Controls: with the fallback off the faces are blank while held, so the fallback check MUST fail; with a legacy texture
# no other face uses, the fallback neither shows nor fetches it.
$heldBase = "4e560009-0000-4000-8000-000000000005"
$unsharedLegacy = "4e560009-0000-4000-8000-0000000000f1"
function FallbackShown($r) { $h = $r.Json.prims.fallback.held; return [bool]($h -and $h.others_settled -and $h.state -ne "Resident" -and $h.fallback_faces -eq 6 -and $h.waiting_faces -eq 0) }
function FallbackDetail($r) { $fb = $r.Json.prims.fallback; $h = $fb.held; return "held $($h.state): legacy $($h.fallback_faces), blank $($h.waiting_faces) (others settled $($h.others_settled)); end: switches $($fb.switches), legacy $($fb.faces_end), blank $($fb.waiting_end); $($r.Json.diff.summary)" }
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\materials\golden-material-zoo-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) { Add-Case "mat_fallback_$be" "FAIL" "no committed golden $golden" 0; continue }
    $base = "`"--tr-fixture=$matZoo`" `"--tr-golden=$golden`" --tr-hold-texture=$heldBase"
    $on = Invoke-Render $be "mat-fallback-$be" $base
    $off = Invoke-Render $be "mat-fallback-off-$be" "$base --tr-legacy-fallback=off"
    $un = Invoke-Render $be "mat-fallback-unshared-$be" "$base --tr-alter=fallback-unshared"
    $settledOk = { param($r) $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $r.Json.prims.pbr_faces -eq 41 -and $r.Json.prims.fallback.faces_end -eq 0 -and $r.Json.prims.fallback.waiting_end -eq 0 -and $r.Json.prims.textures_waiting -eq 0 }
    $fOn = $on.Json.prims.fallback.fetches | ConvertTo-Json -Compress
    $fOff = $off.Json.prims.fallback.fetches | ConvertTo-Json -Compress
    $ok = (& $settledOk $on) -and (FallbackShown $on) -and $on.Json.prims.fallback.switches -eq 6 -and $fOn -eq $fOff -and $on.Json.prims.fallback.fetches.$heldBase -eq 1
    Add-Case "mat_fallback_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(FallbackDetail $on); fetches $fOn (fallback off $fOff)$(if (-not $ok) { '; exit ' + $on.Exit + ' ' + ($on.Json.failures -join ' | ') })" $on.Seconds
    $offOk = (& $settledOk $off) -and -not (FallbackShown $off) -and $off.Json.prims.fallback.held.waiting_faces -eq 6 -and $off.Json.prims.fallback.held.fallback_faces -eq 0
    Add-Case "mat_fallback_off_$be" ($(if ($offOk) { "PASS" } else { "FAIL" })) "fallback off, the fallback check $(if (FallbackShown $off) { 'NOT ' })rejected: $(FallbackDetail $off)" $off.Seconds
    $unOk = (& $settledOk $un) -and $un.Json.prims.fallback.held.fallback_faces -eq 0 -and $un.Json.prims.fallback.held.waiting_faces -eq 6 -and $un.Json.prims.fallback.switches -eq 0 -and -not $un.Json.prims.fallback.fetches.$unsharedLegacy
    Add-Case "mat_fallback_unshared_$be" ($(if ($unOk) { "PASS" } else { "FAIL" })) "legacy texture nobody else uses, fetches $([int]$un.Json.prims.fallback.fetches.$unsharedLegacy): $(FallbackDetail $un)" $un.Seconds
}

# a texture fully evicted comes back from the content store, not the network, with
# the same image (texture zoo, loopback texture server, partial fetch, shared store and fetcher hold at 256 bytes); and the
# negative control: without the content store the same check must see network requests (exit 1).
foreach ($be in $Backends) {
    $r = Invoke-Render $be "readback-$be" "`"--tr-fixture=$texZoo`" --tr-readback"
    $rb = $r.Json.readback
    $ok = $r.Exit -eq 0 -and $r.Json.mode -eq "readback" -and $r.Json.backend.driver -eq $be -and $rb.images_identical -and $rb.back.content_store_reads -gt 0 -and $rb.back.network_requests -eq 0 -and $rb.evicted.evicted -eq $rb.first.textures -and $rb.first.textures -gt 0
    Add-Case "readback_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "textures $($rb.first.textures), evicted $($rb.evicted.evicted), reloads $($rb.back.reloads); content-store reads $($rb.back.content_store_reads) (whole $($rb.back.whole_reads), partial fetcher $($rb.back.partial_fetcher_reads)); network requests after eviction $($rb.back.network_requests); images identical $($rb.images_identical)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "readback-nostore-$be" "`"--tr-fixture=$texZoo`" --tr-readback=nostore"
    $nb = $n.Json.readback
    if ($n.Exit -eq 1 -and $n.Json.backend.driver -eq $be -and $nb.back.network_requests -gt 0) { Add-Case "readback_negative_$be" "PASS" "without the content store: network requests after eviction $($nb.back.network_requests), content-store reads $($nb.back.content_store_reads)" $n.Seconds }
    else { Add-Case "readback_negative_$be" "FAIL" "without the content store the check did not see network requests (exit $($n.Exit), requests $($nb.back.network_requests))" $n.Seconds }
}

# the mip chain built off the main thread is byte-identical to the engine's own, on each backend.
foreach ($be in $Backends) {
    $r = Invoke-Render $be "mipcheck-$be" "--tr-fixture=none --tr-mipcheck" 120
    $c = @($r.Json.mipcheck.cases)
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $c.Count -gt 0 -and @($c | Where-Object { -not $_.identical }).Count -eq 0 -and $r.Json.mipcheck.negative_control_rejected
    Add-Case "mipcheck_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(@($c | Where-Object identical).Count) of $($c.Count) sizes byte-identical ($(($c | ForEach-Object { "$($_.w)x$($_.h)" }) -join ', ')); changed chain rejected: $($r.Json.mipcheck.negative_control_rejected)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

# the texture zoo under forced budgets. 48 KiB: with discard levels the lowest-priority texture now
# sits at a lower level (not on its placeholder; 2 x 21,845 + 5,461 B = 49,151 B). 12 KiB: only coarsest levels fit and
# one texture must still fall to its placeholder. Checks: resident bytes within the budget; every resident texture
# outranks every texture left on its placeholder; levels coarsen with priority. Goldens PROPOSED. Negative controls: the
# other budget must FAIL each diff (not the default budget: at 48 KiB only the smooth gradient sits one level lower,
# which differs from full resolution by at most 3/255, below the diff's threshold).
$budgetCases = @(
    @{ Name = "tex_budget"; Budget = "48k"; Golden = "golden-texture-zoo-budget"; Other = "--tr-budget=12k"; Expect = "lower-level" },
    @{ Name = "tex_budget_12k"; Budget = "12k"; Golden = "golden-texture-zoo-budget-12k"; Other = "--tr-budget=48k"; Expect = "placeholder" }
)
foreach ($bc in $budgetCases) {
    foreach ($be in $Backends) {
        $golden = Join-Path $repo "fixtures\textures\$($bc.Golden)-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-Render $be "proposal-$($bc.Name)-$be" "`"--tr-fixture=$texZoo`" --tr-budget=$($bc.Budget)"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                Copy-Item -LiteralPath (Join-Path $out "proposal-$($bc.Name)-$be.png") -Destination $golden
                Add-Case "$($bc.Name)_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
            } else { Add-Case "$($bc.Name)_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
            continue
        }
        $r = Invoke-Render $be "$($bc.Name)-$be" "`"--tr-fixture=$texZoo`" --tr-budget=$($bc.Budget) `"--tr-golden=$golden`""
        $rs = $r.Json.residency
        $resident = @($rs.textures | Where-Object state -eq "Resident")
        $left = @($rs.textures | Where-Object state -ne "Resident")
        $ranked = $left.Count -eq 0 -or ($resident.Count -gt 0 -and (($resident | Measure-Object priority -Minimum).Minimum -gt ($left | Measure-Object priority -Maximum).Maximum))
        $levels = @($resident | Sort-Object priority -Descending | ForEach-Object level)
        $monotone = $true; for ($i = 1; $i -lt $levels.Count; $i++) { if ($levels[$i] -lt $levels[$i - 1]) { $monotone = $false } }
        $lowest = @($rs.textures | Sort-Object priority)[0]
        $expectOk = if ($bc.Expect -eq "lower-level") { $left.Count -eq 0 -and $lowest.state -eq "Resident" -and $lowest.level -gt 0 } else { $left.Count -ge 1 -and $resident.Count -ge 1 }
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $rs.resident_bytes -le $rs.budget_bytes -and $ranked -and $monotone -and $expectOk
        Add-Case "$($bc.Name)_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); budget $($rs.budget_bytes) B, resident $($rs.resident_bytes) B; resident (priority:level) $(($resident | ForEach-Object { "$($_.priority):$($_.level)" }) -join ', '); on placeholders $($left.Count) (priorities $(($left | ForEach-Object priority) -join ',')); lowest priority: $($lowest.state) at level $($lowest.level)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "$($bc.Name)-negative-$be" "`"--tr-fixture=$texZoo`" $($bc.Other) `"--tr-golden=$golden`""
        if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) { Add-Case "$($bc.Name)_negative_$be" "PASS" "a different budget rejected: $($n.Json.diff.summary)" $n.Seconds }
        else { Add-Case "$($bc.Name)_negative_$be" "FAIL" "a different budget NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
    }
}

# colour accuracy on both backends: the fullbright chart and the tints within +/-2 of the source sRGB values.
$colourChart = Join-Path $repo "fixtures\colour\colour-chart.colourchart.json"
foreach ($be in $Backends) {
    $r = Invoke-Render $be "colour-$be" "`"--tr-fixture=$colourChart`""
    $c = $r.Json.colour
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $c -and $r.Json.prims.textures_waiting -eq 0
    Add-Case "colour_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) chart max error $($c.chart_max_error)/255 ($($c.orientation)); white x tint $($c.tint_got -join ',') (want $($c.tint_expected -join ','), error $($c.tint_error)); grey x grey $($c.grey_x_grey_got -join ',') (want $($c.grey_x_grey_expected), error $($c.grey_x_grey_error)); lit mean $($c.lit_mean)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

# end-to-end texture orientation from the rendered u/v (three views), and a lighting check.
$orientZoo = Join-Path $repo "fixtures\orientation\orientation-zoo.orientation.json"
foreach ($be in $Backends) {
    $r = Invoke-Render $be "orient-$be" "`"--tr-fixture=$orientZoo`""
    $checks = @($r.Json.orientation)
    $bad = @($checks | Where-Object { -not $_.pass })
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $checks.Count -ge 17 -and $bad.Count -eq 0
    Add-Case "orient_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($checks.Count - $bad.Count)/$($checks.Count) orientation and lighting checks pass$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

Invoke-SharedEnvCases
Invoke-WaterCases
Invoke-WaterLookCases
Invoke-PersonalCases
Invoke-TimeOfDayCases
Invoke-CloudCases
Invoke-CloudFrameTimes
Invoke-SkyCycle
Invoke-BanLineCases
Invoke-DialogCases
Invoke-ObjectCases
Invoke-NeighbourCases
Invoke-SeamCases
Invoke-TreeCases
Invoke-NeighbourWaterCases
Invoke-SharedTextureCases
Invoke-TeardownCases
Invoke-LoginScreenCases
Invoke-LoginCardCases
Invoke-UiSceneCases
Invoke-UiChromeCases
Invoke-FloaterDragCases
Invoke-ColourWindowCases
Invoke-HandleCases
Invoke-ProportionsCases

# the sculpt zoo on both backends (the official sampler), and its negative control.
$sculptZoo = Join-Path $repo "fixtures\sculpts\sculpt-zoo.sculptzoo.json"
foreach ($be in $Backends) {
    $golden = Join-Path $repo "fixtures\sculpts\golden-sculpt-zoo-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-sculpt-$be" "`"--tr-fixture=$sculptZoo`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            Copy-Item -LiteralPath (Join-Path $out "proposal-sculpt-$be.png") -Destination $golden
            Add-Case "sculpt_$be" "FAIL" "no golden: PROPOSED $golden (rerun to diff against it)" $r.Seconds
        } else { Add-Case "sculpt_$be" "FAIL" "no committed golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "sculpt-$be" "`"--tr-fixture=$sculptZoo`" `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $p.instances -eq 8 -and $p.sculpt_objects -eq 8 -and $p.sculpt_placeholders -eq 1 -and $p.textures_waiting -eq 0
    Add-Case "sculpt_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); sculpts $($p.sculpt_objects)/8 (stand-ins $($p.sculpt_placeholders)), sculpt meshes $($p.sculpt_resources), LODs $($p.sculpt_lods -join ',')$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-Render $be "sculpt-negative-$be" "`"--tr-fixture=$sculptZoo`" `"--tr-golden=$golden`" --tr-alter=sculptmap"
    if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq $be) { Add-Case "sculpt_negative_$be" "PASS" "altered map region rejected: $($n.Json.diff.summary)" $n.Seconds }
    else { Add-Case "sculpt_negative_$be" "FAIL" "altered map region NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
}

#Local only: the real region A scene from the core (prims + meshes from the local asset cache).
foreach ($be in $Backends) {
    if (-not ((Test-Path -LiteralPath $regionA2) -and (Test-Path -LiteralPath $assets))) {
        Add-Case "region_a_v2_$be" "SKIP" "local-only fixtures-local\capture-region-a-v2.jsonl.gz and fixtures-local\assets\ are absent (grid content, never committed; ADR-011)" 0
        continue
    }
    $golden = Join-Path $localGoldens "golden-region-a-v2-$be.png"
    $args2 = "`"--tr-fixture=$regionA2`" `"--tr-assets=$assets`""
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-region-a-v2-$be" $args2
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
            Copy-Item -LiteralPath (Join-Path $out "proposal-region-a-v2-$be.png") -Destination $golden
            Add-Case "region_a_v2_$be" "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
        } else { Add-Case "region_a_v2_$be" "FAIL" "no local golden $golden (render exit $($r.Exit))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "region-a-v2-$be" "$args2 `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $p.skipped_mesh_unavailable -eq 0
    Add-Case "region_a_v2_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); objects $($p.objects): drawn $($p.instances) ($($p.mesh_objects) meshes), sculpts $($p.sculpt_objects) (stand-ins $($p.sculpt_placeholders), empty $($p.skipped_sculpt_empty)), skipped sculpt/mesh $($p.skipped_sculpt_or_mesh), attachments $($p.skipped_attachments), mesh unavailable $($p.skipped_mesh_unavailable)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

#Local only: the region A scene WITH textures (v3 capture carries texture entries; textures from the local cache).
$regionA3 = Join-Path $local "capture-region-a-v3.jsonl.gz"
foreach ($be in $Backends) {
    if (-not ((Test-Path -LiteralPath $regionA3) -and (Test-Path -LiteralPath $assets))) {
        Add-Case "region_a_v3_$be" "SKIP" "local-only fixtures-local\capture-region-a-v3.jsonl.gz and fixtures-local\assets\ are absent (grid content, never committed; ADR-011)" 0
        continue
    }
    $golden = Join-Path $localGoldens "golden-region-a-v3-sculpts-$be.png"   # sculpts drawn (the v3 goldens predate them)
    $args3 = "`"--tr-fixture=$regionA3`" `"--tr-assets=$assets`""
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-region-a-v3-sculpts-$be" $args3
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
            Copy-Item -LiteralPath (Join-Path $out "proposal-region-a-v3-sculpts-$be.png") -Destination $golden
            Add-Case "region_a_v3_$be" "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
        } else { Add-Case "region_a_v3_$be" "FAIL" "no local golden $golden (render exit $($r.Exit))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "region-a-v3-$be" "$args3 `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $p.textures_waiting -eq 0
    Add-Case "region_a_v3_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); objects $($p.objects): drawn $($p.instances) ($($p.mesh_objects) meshes, $($p.sculpt_objects) sculpts, stand-ins $($p.sculpt_placeholders), empty $($p.skipped_sculpt_empty)), GPU textures $($p.gpu_textures), skipped sculpt/mesh $($p.skipped_sculpt_or_mesh), attachments $($p.skipped_attachments)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

#Local only: Region A v5 (water height and the region's EEP environment in the capture), fixed day fraction 0.3.
$regionA5 = Join-Path $local "capture-region-a-v5.jsonl.gz"
foreach ($be in $Backends) {
    if (-not ((Test-Path -LiteralPath $regionA5) -and (Test-Path -LiteralPath $assets))) {
        Add-Case "region_a_v5_$be" "SKIP" "local-only fixtures-local\capture-region-a-v5.jsonl.gz and fixtures-local\assets\ are absent (grid content, never committed; ADR-011)" 0
        continue
    }
    $golden = Join-Path $localGoldens "golden-region-a-v5-$be.png"
    $args5 = "`"--tr-fixture=$regionA5`" `"--tr-assets=$assets`" --tr-max-clipped=0.01"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-region-a-v5-$be" $args5
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
            Copy-Item -LiteralPath (Join-Path $out "proposal-region-a-v5-$be.png") -Destination $golden
            Add-Case "region_a_v5_$be" "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
        } else { Add-Case "region_a_v5_$be" "FAIL" "no local golden $golden (render exit $($r.Exit))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "region-a-v5-$be" "$args5 `"--tr-golden=$golden`""
    $e = $r.Json.environment
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $e.water_drawn -and $e.source -eq "region"
    Add-Case "region_a_v5_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); environment $($e.source), day $($e.day_fraction), water at $($e.water_height) m; drawn $($r.Json.prims.instances) of $($r.Json.prims.objects)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

# Local only: Region A v9 (material data and its PBR material assets cached) with its PBR faces drawn: every PBR face
# entry drawn with its material or on a face number the object's geometry lacks, and no material asset missing.
$regionA9 = Join-Path $local "capture-region-a-v9.jsonl.gz"
foreach ($be in $Backends) {
    if (-not ((Test-Path -LiteralPath $regionA9) -and (Test-Path -LiteralPath $assets))) {
        Add-Case "region_a_v9_$be" "SKIP" "local-only fixtures-local\capture-region-a-v9.jsonl.gz and fixtures-local\assets\ are absent (grid content, never committed; ADR-011)" 0
        continue
    }
    $golden = Join-Path $localGoldens "golden-region-a-v9-$be.png"
    $args9 = "`"--tr-fixture=$regionA9`" `"--tr-assets=$assets`" --tr-max-clipped=0.01"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-region-a-v9-$be" $args9
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
            Copy-Item -LiteralPath (Join-Path $out "proposal-region-a-v9-$be.png") -Destination $golden
            Add-Case "region_a_v9_$be" "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
        } else { Add-Case "region_a_v9_$be" "FAIL" "no local golden $golden (render exit $($r.Exit))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "region-a-v9-$be" "$args9 `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and ($p.pbr_faces + $p.pbr_faces_beyond_geometry) -eq 27 -and $p.pbr_faces -gt 0 -and $p.material_assets_unavailable -eq 0 -and $p.material_assets_ready -eq 7 -and $p.materials_waiting -eq 0 -and $p.pbr_detail_maps_in_view -gt 0 -and $p.pbr_detail_maps_below_level -eq 0 -and $p.legacy_material_faces -gt 0
    Add-Case "region_a_v9_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); legacy material faces $($p.legacy_material_faces) (objects waiting for a material the region does not serve $($p.legacy_materials_waiting)); PBR faces drawn $($p.pbr_faces), on face numbers the geometry lacks $($p.pbr_faces_beyond_geometry) (27 entries), not opaque $($p.pbr_faces_not_opaque); material assets read $($p.material_assets_ready)/7, unavailable $($p.material_assets_unavailable); detail maps in view $($p.pbr_detail_maps_in_view) (below their level $($p.pbr_detail_maps_below_level)); drawn $($p.instances) of $($p.objects)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

# Local only: avatars from the character folder NV_CHARACTER_DIR names (read in place; never in the repo) and the grid's
# walk and the avatar's own bakes from the local asset cache. avatarprep: every body part's skinned surface byte for byte
# against the engine's packing (the negative control: one skin byte changed, every part caught); avatars: one avatar in
# its default pose and one mid-walk vs fixtures-local\goldens\ (approved, fixtures\avatars\manifest.json); avatars_baked: the same wearing
# the bakes; avatars_negative: the walking one left in its default pose, the diff MUST fail. avatars_live: two avatars
# from synthetic region updates through the viewer's own path (the world state, the avatar presenter with the grid's stand
# and walk, the follow camera, name labels) vs fixtures-local\goldens\ (PROPOSED until approved); avatars_live_negative:
# the walker reported with no animation, the diff MUST fail. lifecycle_avatars: the lifecycle's region changes with three
# avatars in every region and one leaving mid-region, nothing left after each change; its negative control keeps the
# departed avatar's label and MUST fail.
}   # end of the sections -AvatarsOnly skips
# skinlight_<sky>_<be>: flat skin-coloured patches on the avatar shader at several angles to a 60 degree sun, each pixel against the official classic lighting worked out on the CPU
# (Viewer.Core SkyLighting.ClassicDiffuse and ClassicSpecular, checked against the 26.4 shaders by ClassicLightTests); legacy sky: classic response, a shiny legacy-material patch too;
# eep sky (tone-mapped path): the avatar patch equals the plain face shader's at normal incidence
function Invoke-SkinLightCases {
    foreach ($be in $Backends) {
        foreach ($sky in @("legacy", "eep")) {
            $r = Invoke-Render $be "skinlight-$sky-$be" "--tr-skinlight=$sky" 180
            $rows = $r.Json.skinlight.rows
            $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $rows
            $worst = if ($rows) { ($rows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
            Add-Case "skinlight_${sky}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $(@($rows).Count) angles, worst channel error $worst of $($r.Json.skinlight.tolerance) levels$(if (-not $ok) { '; exit ' + $r.Exit + ': ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
        # the negative control: with the classic switch put off the engine's own light draws the patches, and the check must fail
        $n = Invoke-Render $be "skinlight-off-$be" "--tr-skinlight=legacy-off" 180
        $nrows = $n.Json.skinlight.rows
        $nworst = if ($nrows) { ($nrows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
        Add-Case "skinlight_off_$be" ($(if ($n.Exit -eq 1 -and $nworst -gt $n.Json.skinlight.tolerance) { "PASS" } else { "FAIL" })) "backend=$($n.Json.backend.driver) negative control: exit $($n.Exit), worst channel error $nworst (must be over $($n.Json.skinlight.tolerance))" $n.Seconds
    }
}
Invoke-SkinLightCases
# skinnormal_<be>: a skinned normal through a joint scaled 2.5 times along one axis, on the body's own surface and skeleton path, must come out turned by the joint matrix
# itself (the official avatar shaders' rule), not its inverse transpose; skinnormal_negative_<be>: the joint not scaled, which the reading MUST reject
function Invoke-SkinNormalCases {
    foreach ($be in $Backends) {
        $r = Invoke-Render $be "skinnormal-$be" "--tr-skinnormal" 120
        $sn = $r.Json.skin_normal
        Add-Case "skinnormal_$be" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $sn.reading -eq "forward") { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) reading $($sn.reading), drawn normal y over x $($sn.drawn_normal_y_over_x) (official $($sn.official_y_over_x), inverse transpose $($sn.inverse_transpose_y_over_x))$(if ($r.Exit -ne 0) { '; exit ' + $r.Exit + ': ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $n = Invoke-Render $be "skinnormal-negative-$be" "--tr-skinnormal --tr-alter=unscaled" 120
        Add-Case "skinnormal_negative_$be" ($(if ($n.Exit -eq 1 -and $n.Json.skin_normal.reading -ne "forward") { "PASS" } else { "FAIL" })) "backend=$($n.Json.backend.driver) negative control: exit $($n.Exit), reading $($n.Json.skin_normal.reading) (must not be forward)" $n.Seconds
    }
}
Invoke-SkinNormalCases
# worldlight_<sky>_<be>: flat patches on the world face shaders (plain and legacy material) and the avatar shader at eight angles to a 60 degree sun, each pixel against the official
# lighting worked out on the CPU: a legacy sky (classic response) and a sky with a probe ambiance (the non-classic response, then the post pass's tone mapping), within 1 level;
# the -off kinds are the negative controls (the engine's own light) and MUST fail; the tone-mapped kind's model includes a flat ground drawn into the sky probe's capture
function Invoke-WorldLightCases {
    foreach ($be in $Backends) {
        foreach ($sky in @("legacy", "eep")) {
            $r = Invoke-Render $be "worldlight-$sky-$be" "--tr-worldlight=$sky" 180
            $rows = $r.Json.worldlight.rows
            $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $rows
            $worst = if ($rows) { ($rows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
            Add-Case "worldlight_${sky}_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $(@($rows).Count) angles, worst channel error $worst of $($r.Json.worldlight.tolerance) levels, tone mapping ran $($r.Json.worldlight.tonemap_ran)$(if (-not $ok) { '; exit ' + $r.Exit + ': ' + ($r.Json.failures -join ' | ') })" $r.Seconds
            $n = Invoke-Render $be "worldlight-$sky-off-$be" "--tr-worldlight=$sky-off" 180
            $nrows = $n.Json.worldlight.rows
            $nworst = if ($nrows) { ($nrows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
            Add-Case "worldlight_${sky}_off_$be" ($(if ($n.Exit -eq 1 -and $nworst -gt $n.Json.worldlight.tolerance) { "PASS" } else { "FAIL" })) "backend=$($n.Json.backend.driver) negative control: exit $($n.Exit), worst channel error $nworst (must be over $($n.Json.worldlight.tolerance))" $n.Seconds
        }
        # the sky probe's negative control: the tone-mapped path with the probe's ambient switched off (the grey ambient alone) MUST fail
        $p = Invoke-Render $be "worldlight-eep-noprobe-$be" "--tr-worldlight=eep-noprobe" 180
        $prows = $p.Json.worldlight.rows
        $pworst = if ($prows) { ($prows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
        Add-Case "worldlight_eep_noprobe_$be" ($(if ($p.Exit -eq 1 -and $pworst -gt $p.Json.worldlight.tolerance) { "PASS" } else { "FAIL" })) "backend=$($p.Json.backend.driver) negative control (no probe ambient): exit $($p.Exit), worst channel error $pworst (must be over $($p.Json.worldlight.tolerance))" $p.Seconds
        # the ground bounce's negative control: the model holds a flat ground that the engine's capture lacks (no ground drawn into it) MUST fail
        $g = Invoke-Render $be "worldlight-eep-noground-$be" "--tr-worldlight=eep-noground" 180
        $grows = $g.Json.worldlight.rows
        $gworst = if ($grows) { ($grows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
        Add-Case "worldlight_eep_noground_$be" ($(if ($g.Exit -eq 1 -and $gworst -gt $g.Json.worldlight.tolerance) { "PASS" } else { "FAIL" })) "backend=$($g.Json.backend.driver) negative control (the ground left out of the capture): exit $($g.Exit), worst channel error $gworst (must be over $($g.Json.worldlight.tolerance))" $g.Seconds
        # the same chain under the thick-haze dusk sky (the sun a few degrees above the horizon): the faces turned from the sun through the zenith to its far side
        $duskThickSky = Join-Path $repo "fixtures\environment\shared-dusk-thick.llsd.xml"
        $d = Invoke-Render $be "worldlight-eep-dusk-$be" "--tr-worldlight=eep-dusk `"--tr-environment=$duskThickSky`"" 180
        $drows = $d.Json.worldlight.rows
        $dworst = if ($drows) { ($drows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
        Add-Case "worldlight_eep_dusk_$be" ($(if ($d.Exit -eq 0 -and $d.Json.backend.driver -eq $be -and $drows) { "PASS" } else { "FAIL" })) "backend=$($d.Json.backend.driver) dusk sky, worst channel error $dworst of $($d.Json.worldlight.tolerance) levels$(if ($d.Exit -ne 0) { '; exit ' + $d.Exit + ': ' + ($d.Json.failures -join ' | ') })" $d.Seconds
        # and under the built-in Sunset sky, which takes the classic path
        $l = Invoke-Render $be "worldlight-legacy-dusk-$be" "--tr-worldlight=legacy-dusk" 180
        $lrows = $l.Json.worldlight.rows
        $lworst = if ($lrows) { ($lrows | ForEach-Object { $_.patches } | ForEach-Object { $_.max_error } | Measure-Object -Maximum).Maximum } else { -1 }
        Add-Case "worldlight_legacy_dusk_$be" ($(if ($l.Exit -eq 0 -and $l.Json.backend.driver -eq $be -and $lrows) { "PASS" } else { "FAIL" })) "backend=$($l.Json.backend.driver) built-in Sunset sky, worst channel error $lworst of $($l.Json.worldlight.tolerance) levels$(if ($l.Exit -ne 0) { '; exit ' + $l.Exit + ': ' + ($l.Json.failures -join ' | ') })" $l.Seconds
    }
}
Invoke-WorldLightCases
$walkAnim = Join-Path $assets "6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0.anim"
$avatarSkip = if (-not $env:NV_CHARACTER_DIR) { "local-only character data: NV_CHARACTER_DIR is not set (and the repository's copy is missing)" }
    elseif (-not (Test-Path -LiteralPath $env:NV_CHARACTER_DIR)) { $null }
    elseif (-not (Test-Path -LiteralPath $walkAnim)) { "local-only: the grid's walk animation is not in fixtures-local\assets\ (grid content, never committed)" }
    else { $null }
# avatars_live_repeat: the live walker case rendered twice in a row must give byte-identical pictures. The case holds its clock still,
# so nothing in it may depend on how many frames the settling took (the walk-rate state once did).
function Invoke-LiveRepeatCases {
    foreach ($be in $Backends) {
        $a = Invoke-AvatarRender $be "avatars-live-repeat-a-$be" "--tr-avatars=live"
        $b = Invoke-AvatarRender $be "avatars-live-repeat-b-$be" "--tr-avatars=live"
        $pa = Join-Path $out "avatars-live-repeat-a-$be.png"; $pb = Join-Path $out "avatars-live-repeat-b-$be.png"
        $ok = $a.Exit -eq 0 -and $b.Exit -eq 0 -and $a.Json.backend.driver -eq $be -and (Test-Path -LiteralPath $pa) -and (Test-Path -LiteralPath $pb)
        $same = $ok -and ((Get-FileHash -Algorithm SHA256 -LiteralPath $pa).Hash -eq (Get-FileHash -Algorithm SHA256 -LiteralPath $pb).Hash)
        Add-Case "avatars_live_repeat_$be" ($(if ($same) { "PASS" } else { "FAIL" })) "backend=$be two renders of the live case: $(if (-not $ok) { 'a render failed (exits ' + $a.Exit + ', ' + $b.Exit + ')' } elseif ($same) { 'identical pictures' } else { 'the pictures differ' })" ($a.Seconds + $b.Seconds)
    }
}
if ($LiveRepeatOnly) {   # -LiveRepeatOnly: just the repeat cases
    if ($env:NV_CHARACTER_DIR -and -not $avatarSkip) { Invoke-LiveRepeatCases } else { foreach ($be in $Backends) { Add-Case "avatars_live_repeat_$be" "SKIP" $(if ($avatarSkip) { $avatarSkip } else { "NV_CHARACTER_DIR is not set" }) 0 } }
    Exit-Render 0
}
if ($env:NV_CHARACTER_DIR -and -not (Test-Path -LiteralPath $env:NV_CHARACTER_DIR)) {
    Add-Case "avatars" "FAIL" "NV_CHARACTER_DIR names a folder that does not exist: $env:NV_CHARACTER_DIR" 0
} elseif ($avatarSkip) {
    foreach ($c in @("avatarprep_vulkan", "avatarprep_d3d12", "avatarprep_negative_vulkan", "avatars_vulkan", "avatars_d3d12", "avatars_baked_vulkan", "avatars_baked_d3d12", "avatars_negative_vulkan",
        "avatars_live_vulkan", "avatars_live_d3d12", "avatars_live_negative_vulkan", "avatars_attach_vulkan", "avatars_attach_d3d12", "avatars_attach_negative_vulkan", "avatars_rigged_vulkan", "avatars_rigged_d3d12", "avatars_rigged_negative_vulkan", "avatars_rigged_textures_vulkan", "avatars_bake_look_vulkan", "avatars_bake_look_negative_vulkan", "avatars_shape_look_vulkan", "avatars_shape_look_negative_vulkan", "avatars_morph_look_vulkan", "avatars_morph_look_negative_vulkan", "avatars_rigged_stress_vulkan", "avatars_rigged_stress_d3d12", "avatars_rigged_stall_vulkan", "avatars_rigged_stall_d3d12", "avatars_rigged_crossing_vulkan", "avatars_rigged_crossing_d3d12", "avatars_rigged_blend_vulkan", "avatars_rigged_blend_d3d12", "avatars_rigged_blend_negative_vulkan", "crowd_smoke_vulkan", "crowd_smoke_negative_vulkan", "pipeline_gate_crowd_vulkan", "pipeline_gate_crowd_d3d12", "crowd_distinct_100_vulkan", "crowd_real_25_vulkan", "crowd_real_negative_vulkan", "crowd_real_held_twice_negative_vulkan", "crowd_real_keep_pixels_negative_vulkan", "crowd_real_same_seed_vulkan", "arrival_25_vulkan", "arrival_50_vulkan", "arrival_100_vulkan", "lifecycle_avatars_vulkan", "lifecycle_avatars_negative_vulkan", "lifecycle_avatars_slowdetach_vulkan")) { Add-Case $c "SKIP" $avatarSkip 0 }
} else {
    foreach ($be in $Backends) {
        $r = Invoke-AvatarRender $be "avatarprep-$be" "--tr-avatars=prep"
        $a = $r.Json.avatarprep
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $a.parts -eq 6 -and $a.identical -eq 6
        Add-Case "avatarprep_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) parts identical to the engine's packing $($a.identical)/$($a.parts); palette $($r.Json.avatar_body.palette), vertices $($r.Json.avatar_body.vertices)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $n = Invoke-AvatarRender "vulkan" "avatarprep-negative-vulkan" "--tr-avatars=prep --tr-alter=skinbyte"
    if ($n.Exit -eq 0 -and $n.Json.avatarprep.identical -eq 0 -and $n.Json.avatarprep.parts -eq 6) { Add-Case "avatarprep_negative_vulkan" "PASS" "one skin byte changed: caught in $($n.Json.avatarprep.parts) of 6 parts" $n.Seconds }
    else { Add-Case "avatarprep_negative_vulkan" "FAIL" "changed skin bytes NOT caught (exit $($n.Exit), identical $($n.Json.avatarprep.identical))" $n.Seconds }
    foreach ($case in @(@{ Name = "avatars"; File = "avatars"; Extra = "" }, @{ Name = "avatars_baked"; File = "avatars-baked"; Extra = "--tr-bakes" })) {
        foreach ($be in $Backends) {
            $name = "$($case.Name)_$be"
            $golden = Join-Path $localGoldens "golden-$($case.File)-$be.png"
            if (-not (Test-Path -LiteralPath $golden)) {
                $r = Invoke-AvatarRender $be "proposal-$($case.File)-$be" "--tr-avatars=render $($case.Extra)"
                if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                    New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
                    Copy-Item -LiteralPath (Join-Path $out "proposal-$($case.File)-$be.png") -Destination $golden
                    Add-Case $name "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
                } else { Add-Case $name "FAIL" "no local golden $golden (render exit $($r.Exit))" $r.Seconds }
                continue
            }
            $r = Invoke-AvatarRender $be "$($case.File)-$be" "--tr-avatars=render $($case.Extra) `"--tr-golden=$golden`""
            $v = $r.Json.avatars
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $v.count -eq 2 -and $v.engine_calls_last_frame -eq 2 * ($v.palette + 1) -and (($case.Extra -eq "") -or $v.bakes.Count -ge 3)
            Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); avatars $($v.count), palette $($v.palette), engine calls $($v.engine_calls_last_frame), walk at $($v.walk.time) s of $($v.walk.duration) s; bakes $($v.bakes -join ',')$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        }
    }
    # the default avatar's face from the front, close up: before any bake the eyes are white, not skin-coloured (a pixel check); wearing the
    # avatar's own bakes, against a local golden when there is one (otherwise the picture is only reported, and the case fails as PROPOSED)
    foreach ($be in $Backends) {
        $r = Invoke-AvatarRender $be "avatars-face-nobake-$be" "--tr-avatars=face"
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be
        Add-Case "avatars_face_nobake_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) eye blue/red $($r.Json.avatar_face.eye_blue_over_red -join ', ')$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
        $golden = Join-Path $localGoldens "golden-avatars-face-$be.png"
        if (Test-Path -LiteralPath $golden) {
            $r = Invoke-AvatarRender $be "avatars-face-$be" "--tr-avatars=face --tr-bakes `"--tr-golden=$golden`""
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case "avatars_face_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)" $r.Seconds
        } else {
            $r = Invoke-AvatarRender $be "proposal-avatars-face-$be" "--tr-avatars=face --tr-bakes"
            Add-Case "avatars_face_$be" "FAIL" "no golden ${golden}: picture proposed as $(Join-Path $out "proposal-avatars-face-$be.png")" $r.Seconds
        }
    }
    # the same face under the stock Midday (Legacy) sky, so the official classic lighting is held by a picture of an avatar (a local golden; absent, the picture is proposed and the case fails as PROPOSED)
    foreach ($be in $Backends) {
        $golden = Join-Path $localGoldens "golden-avatars-face-legacy-$be.png"
        if (Test-Path -LiteralPath $golden) {
            $r = Invoke-AvatarRender $be "avatars-face-legacy-$be" "--tr-avatars=face --tr-bakes --tr-alter=legacysky `"--tr-golden=$golden`""
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case "avatars_face_legacy_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)" $r.Seconds
        } else {
            $r = Invoke-AvatarRender $be "proposal-avatars-face-legacy-$be" "--tr-avatars=face --tr-bakes --tr-alter=legacysky"
            Add-Case "avatars_face_legacy_$be" "FAIL" "no golden ${golden}: picture proposed as $(Join-Path $out "proposal-avatars-face-legacy-$be.png")" $r.Seconds
        }
    }
    # and under a stock Midday sky that sets a reflection probe ambiance (the tone-mapped path with the sky probe's ambient), so that ambient is held by a picture of an avatar (a local golden, proposed when absent)
    foreach ($be in $Backends) {
        $golden = Join-Path $localGoldens "golden-avatars-face-eep-$be.png"
        if (Test-Path -LiteralPath $golden) {
            $r = Invoke-AvatarRender $be "avatars-face-eep-$be" "--tr-avatars=face --tr-bakes --tr-alter=eepsky `"--tr-golden=$golden`""
            $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be
            Add-Case "avatars_face_eep_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary)" $r.Seconds
        } else {
            $r = Invoke-AvatarRender $be "proposal-avatars-face-eep-$be" "--tr-avatars=face --tr-bakes --tr-alter=eepsky"
            Add-Case "avatars_face_eep_$be" "FAIL" "no golden ${golden}: picture proposed as $(Join-Path $out "proposal-avatars-face-eep-$be.png")" $r.Seconds
        }
    }
    $golden = Join-Path $localGoldens "golden-avatars-vulkan.png"
    if (Test-Path -LiteralPath $golden) {
        $n = Invoke-AvatarRender "vulkan" "avatars-negative-vulkan" "--tr-avatars=render --tr-alter=still `"--tr-golden=$golden`""
        if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq "vulkan") { Add-Case "avatars_negative_vulkan" "PASS" "the walk left out rejected: $($n.Json.diff.summary)" $n.Seconds }
        else { Add-Case "avatars_negative_vulkan" "FAIL" "the walk left out NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
    } else { Add-Case "avatars_negative_vulkan" "FAIL" "no local golden $golden" 0 }
    foreach ($be in $Backends) {
        $name = "avatars_live_$be"
        $golden = Join-Path $localGoldens "golden-avatars-live-$be.png"
        if (-not (Test-Path -LiteralPath $golden)) {
            $r = Invoke-AvatarRender $be "proposal-avatars-live-$be" "--tr-avatars=live"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-avatars-live-$be.png") -Destination $golden
                Add-Case $name "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
            } else { Add-Case $name "FAIL" "no local golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
            continue
        }
        $r = Invoke-AvatarRender $be "avatars-live-$be" "--tr-avatars=live `"--tr-golden=$golden`""
        $v = $r.Json.avatars_live
        $clips = @($v.presenter.each | ForEach-Object { $_.clip }) -join ','
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $v.drawn -eq 2 -and $v.bake_textures -ge $v.body_bake_slots -and $clips -match '2408fe9e' -and $clips -match '6ed24bd8'
        Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); drawn $($v.drawn), bake textures $($v.bake_textures), clips $clips$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $golden = Join-Path $localGoldens "golden-avatars-live-vulkan.png"
    if (Test-Path -LiteralPath $golden) {
        $n = Invoke-AvatarRender "vulkan" "avatars-live-negative-vulkan" "--tr-avatars=live --tr-alter=still `"--tr-golden=$golden`""
        if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq "vulkan") { Add-Case "avatars_live_negative_vulkan" "PASS" "the walker standing still, with no velocity or animation, rejected: $($n.Json.diff.summary)" $n.Seconds }
        else { Add-Case "avatars_live_negative_vulkan" "FAIL" "the walker standing still, with no velocity or animation, NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
    } else { Add-Case "avatars_live_negative_vulkan" "FAIL" "no local golden $golden" 0 }
    Invoke-EditLookCases
    Invoke-LiveRepeatCases
    # avatars_attach: a standing and a walking avatar each wearing ten boxes on seven points (two on one point, a turned offset,
    # a three-prim link set, a Bento point), from the front and from behind, through the viewer's own path, vs fixtures-local\goldens\
    # (PROPOSED until approved); avatars_attach_negative: the walker left standing, the diff MUST fail.
    foreach ($be in $Backends) {
        $name = "avatars_attach_$be"
        $golden = Join-Path $localGoldens "golden-avatars-attach-$be.png"
        $goldenBack = Join-Path $localGoldens "golden-avatars-attach-back-$be.png"
        if (-not ((Test-Path -LiteralPath $golden) -and (Test-Path -LiteralPath $goldenBack))) {
            $r = Invoke-AvatarRender $be "proposal-avatars-attach-$be" "--tr-avatars=attach"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-avatars-attach-$be.png") -Destination $golden
                Copy-Item -LiteralPath (Join-Path $out "proposal-avatars-attach-$be-back.png") -Destination $goldenBack
                Add-Case $name "FAIL" "no local golden: PROPOSED $golden and $goldenBack" $r.Seconds
            } else { Add-Case $name "FAIL" "no local golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
            continue
        }
        $r = Invoke-AvatarRender $be "avatars-attach-$be" "--tr-avatars=attach `"--tr-golden=$golden`" `"--tr-golden-back=$goldenBack`""
        $v = $r.Json.avatars_attach
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.diff_back.pass -and $r.Json.backend.driver -eq $be -and $v.avatars_drawn -eq 2 -and $v.boxes_drawn -eq $v.expected_boxes
        Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) front: $($r.Json.diff.summary); back: $($r.Json.diff_back.summary); avatars $($v.avatars_drawn), boxes drawn $($v.boxes_drawn) of $($v.expected_boxes)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $golden = Join-Path $localGoldens "golden-avatars-attach-vulkan.png"
    if (Test-Path -LiteralPath $golden) {
        $n = Invoke-AvatarRender "vulkan" "avatars-attach-negative-vulkan" "--tr-avatars=attach --tr-alter=still `"--tr-golden=$golden`""
        if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq "vulkan") { Add-Case "avatars_attach_negative_vulkan" "PASS" "the walker standing, its boxes with it, rejected: $($n.Json.diff.summary)" $n.Seconds }
        else { Add-Case "avatars_attach_negative_vulkan" "FAIL" "the walker standing NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
    } else { Add-Case "avatars_attach_negative_vulkan" "FAIL" "no local golden $golden" 0 }
    # avatars_rigged: two standing and two walking avatars wearing rigged meshes skinned in the face shader (a shirt, Bento face joints,
    # collision volumes, and sleeves with joint position overrides and a pelvis offset), from the front, from behind and the first
    # avatar's face, vs fixtures-local\goldens\ (PROPOSED until approved); avatars_rigged_negative: the meshes carry no full skin (as
    # when they are not skinned), none is drawn, the diff MUST fail; avatars_rigged_textures: a textured rigged shirt;
    # avatars_rigged_stress: rigged meshes attached and detached while drawing, a full collection after every change.
    foreach ($be in $Backends) {
        $name = "avatars_rigged_$be"
        $golden = Join-Path $localGoldens "golden-avatars-rigged-$be.png"
        $goldenBack = Join-Path $localGoldens "golden-avatars-rigged-back-$be.png"
        $goldenFace = Join-Path $localGoldens "golden-avatars-rigged-face-$be.png"
        if (-not ((Test-Path -LiteralPath $golden) -and (Test-Path -LiteralPath $goldenBack) -and (Test-Path -LiteralPath $goldenFace))) {
            $r = Invoke-AvatarRender $be "proposal-avatars-rigged-$be" "--tr-avatars=rigged"
            if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
                New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
                Copy-Item -LiteralPath (Join-Path $out "proposal-avatars-rigged-$be.png") -Destination $golden
                Copy-Item -LiteralPath (Join-Path $out "proposal-avatars-rigged-$be-back.png") -Destination $goldenBack
                Copy-Item -LiteralPath (Join-Path $out "proposal-avatars-rigged-$be-face.png") -Destination $goldenFace
                Add-Case $name "FAIL" "no local golden: PROPOSED $golden, $goldenBack and $goldenFace" $r.Seconds
            } else { Add-Case $name "FAIL" "no local golden $golden (render exit $($r.Exit): $($r.Json.failures -join ' | '))" $r.Seconds }
            continue
        }
        $r = Invoke-AvatarRender $be "avatars-rigged-$be" "--tr-avatars=rigged `"--tr-golden=$golden`" `"--tr-golden-back=$goldenBack`" `"--tr-golden-face=$goldenFace`""
        $v = $r.Json.avatars_rigged
        $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.diff_back.pass -and $r.Json.diff_face.pass -and $r.Json.backend.driver -eq $be -and $v.avatars_drawn -eq 4 -and $v.meshes_drawn -eq $v.expected_meshes
        Add-Case $name ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) front: $($r.Json.diff.summary); back: $($r.Json.diff_back.summary); face: $($r.Json.diff_face.summary); avatars $($v.avatars_drawn), rigged meshes drawn $($v.meshes_drawn) of $($v.expected_meshes), arm out $($v.arm_out_m) m, lifted $($v.lifted_m) m$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $golden = Join-Path $localGoldens "golden-avatars-rigged-vulkan.png"
    if (Test-Path -LiteralPath $golden) {
        $n = Invoke-AvatarRender "vulkan" "avatars-rigged-negative-vulkan" "--tr-avatars=rigged --tr-alter=norigged `"--tr-golden=$golden`""
        if ($n.Exit -eq 1 -and $n.Json.diff -and -not $n.Json.diff.pass -and $n.Json.backend.driver -eq "vulkan") { Add-Case "avatars_rigged_negative_vulkan" "PASS" "rigged meshes left undrawn rejected: $($n.Json.diff.summary)" $n.Seconds }
        else { Add-Case "avatars_rigged_negative_vulkan" "FAIL" "rigged meshes left undrawn NOT rejected (exit $($n.Exit)): $($n.Json.diff.summary)" $n.Seconds }
    } else { Add-Case "avatars_rigged_negative_vulkan" "FAIL" "no local golden $golden" 0 }
    $r = Invoke-AvatarRender "vulkan" "avatars-rigged-textures-vulkan" "--tr-avatars=riggedtex `"--tr-textures=$texDir`""
    $rt = $r.Json.avatars_rigged_textures
    Add-Case "avatars_rigged_textures_vulkan" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan") { "PASS" } else { "FAIL" })) "textured rigged shirt: skinned materials $($rt.skinned_materials), GPU textures $($rt.gpu_textures), differs from the colour-only shirt in $($rt.differing_fraction)$(if ($r.Exit -ne 0) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    # avatars_bake_look: the viewer's own avatar and another, each in a rigged shirt whose faces name the upper-body bake placeholder (bake on
    # mesh): the default texture while no bake is there, the wearer's upper bake once it arrives; then bare bodies with four bakes each, one
    # with transparent corners (the alpha cut). Pixel checks, no golden. avatars_bake_look_negative: the faces name an ordinary texture, not the
    # placeholder, so the wearer's bake changes nothing, and the checks MUST fail.
    $r = Invoke-AvatarRender "vulkan" "avatars-bake-look-vulkan" "--tr-avatars=bakelook `"--tr-textures=$texDir`""
    $bl = $r.Json.avatars_bake_look
    Add-Case "avatars_bake_look_vulkan" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan") { "PASS" } else { "FAIL" })) "bake on mesh and alpha cut: default texture asked $($bl.fetched_default_texture), shirt pixels changed when the upper bake arrived left $($bl.stage2_changed_left) right $($bl.stage2_changed_right), scene through the cut body left $($bl.stage3_backdrop_left) right $($bl.stage3_backdrop_right)$(if ($r.Exit -ne 0) { '; ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-AvatarRender "vulkan" "avatars-bake-look-negative-vulkan" "--tr-avatars=bakelook --tr-alter=nobom `"--tr-textures=$texDir`""
    if ($n.Exit -eq 1 -and $n.Json.backend.driver -eq "vulkan" -and ($n.Json.failures -join ' ') -match 'default texture') { Add-Case "avatars_bake_look_negative_vulkan" "PASS" "faces naming an ordinary texture: the bake-on-mesh checks failed ($($n.Json.failures.Count) failures)" $n.Seconds }
    else { Add-Case "avatars_bake_look_negative_vulkan" "FAIL" "faces naming an ordinary texture were NOT caught (exit $($n.Exit))" $n.Seconds }
    # avatars_shape_look: four bare avatars (default female, default male, tall long-legged male, female hovering half a metre) on one ground; pixel
    # checks on the body colour: feet on one line, the tall one as much taller as its computed body height says, the male taller than the female,
    # the hover lifting the feet. No golden. avatars_shape_look_negative: no shape parameters in the appearances, so the tall one is the female, and the checks MUST fail.
    $r = Invoke-AvatarRender "vulkan" "avatars-shape-look-vulkan" "--tr-avatars=shapelook"
    $sl = $r.Json.avatars_shape_look
    Add-Case "avatars_shape_look_vulkan" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan") { "PASS" } else { "FAIL" })) "shaped skeleton on the default body: computed heights $($sl.computed_heights -join ','), drawn heights (px) $($sl.pixel_heights -join ','), feet rows $($sl.bottoms -join ',')$(if ($r.Exit -ne 0) { '; ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-AvatarRender "vulkan" "avatars-shape-look-negative-vulkan" "--tr-avatars=shapelook --tr-alter=noshape"
    if ($n.Exit -eq 1 -and $n.Json.backend.driver -eq "vulkan" -and ($n.Json.failures -join ' ') -match 'tall avatar') { Add-Case "avatars_shape_look_negative_vulkan" "PASS" "no shape parameters: the tall avatar was not taller, the checks failed ($($n.Json.failures.Count) failures)" $n.Seconds }
    else { Add-Case "avatars_shape_look_negative_vulkan" "FAIL" "avatars without shape parameters were NOT caught (exit $($n.Exit))" $n.Seconds }
    # avatars_morph_look: four bare avatars from the front (default female, female with Body Fat and Torso Muscles at the top, default male, male with the same):
    # every avatar draws a body of its own (its morph targets applied), the heavy ones are broader at the belly than the default ones, the height does not change.
    # No golden. avatars_morph_look_negative: the heavy avatars carry the default parameters, and the checks MUST fail.
    $r = Invoke-AvatarRender "vulkan" "avatars-morph-look-vulkan" "--tr-avatars=morphlook"
    $ml = $r.Json.avatars_morph_look
    Add-Case "avatars_morph_look_vulkan" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan") { "PASS" } else { "FAIL" })) "morph targets on the default body: bodies of their own $($ml.morphed), belly breadth (px) $($ml.belly_px -join ','), chest breadth (px) $($ml.chest_px -join ','), heights (px) $($ml.heights_px -join ',')$(if ($r.Exit -ne 0) { '; ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-AvatarRender "vulkan" "avatars-morph-look-negative-vulkan" "--tr-avatars=morphlook --tr-alter=nomorph"
    if ($n.Exit -eq 1 -and $n.Json.backend.driver -eq "vulkan" -and ($n.Json.failures -join ' ') -match 'not broader') { Add-Case "avatars_morph_look_negative_vulkan" "PASS" "heavy avatars with default parameters: not broader, the checks failed ($($n.Json.failures.Count) failures)" $n.Seconds }
    else { Add-Case "avatars_morph_look_negative_vulkan" "FAIL" "heavy avatars with default parameters were NOT caught (exit $($n.Exit))" $n.Seconds }
    foreach ($be in $Backends) {
        $r = Invoke-AvatarRender $be "avatars-rigged-stress-$be" "--tr-avatars=riggedstress --tr-cycles=20" 300
        $rs = $r.Json.avatars_rigged_stress
        Add-Case "avatars_rigged_stress_$be" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($rs.cycles) cycles of three rigged meshes attached and detached under forced collections, peak instances $($rs.peak_instances), instances at the end $($rs.instances_at_end), rows in use $($rs.rows_in_use), tables $($rs.tables_in_use)$(if ($r.Exit -ne 0) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    # avatars_rigged_stall: one avatar wears 24 different dense rigged meshes (about 33 000 vertices each) arriving in one frame; the longest
    # main-thread step of the prim layer must stay within the frame budget (4 ms): their data work is done off the main thread, and
# the engine mesh is given one surface per step, with the bind table copied in a step of its own.
    foreach ($be in $Backends) {
        $r = Invoke-AvatarRender $be "avatars-rigged-stall-$be" "--tr-avatars=riggedstall" 300
        $rs = $r.Json.avatars_rigged_stall
        Add-Case "avatars_rigged_stall_$be" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $rs.rigged_drawn -eq $rs.meshes) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($rs.meshes) rigged meshes of $($rs.vertices_each) vertices arriving at once, longest main-thread step $($rs.worst_step_ms) ms (limit $($rs.limit_ms) ms), steps over the frame budget $($rs.steps_over_budget), most parts of a rigged mesh's build in one step $($rs.most_rigged_parts_in_a_step), drawn $($rs.rigged_drawn)$(if ($r.Exit -ne 0) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    # avatars_rigged_crossing: an outfit of rigged and unrigged pieces made in a region, then sent again the way a crossing sends it (the pieces before the avatar, then the avatar before the pieces); every piece is drawn after each leg.
    foreach ($be in $Backends) {
        $r = Invoke-AvatarRender $be "avatars-rigged-crossing-$be" "--tr-avatars=riggedcrossing" 300
        Add-Case "avatars_rigged_crossing_$be" ($(if ($r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) leg 1 drawn $($r.Json.crossing_leg_1.pieces_drawn) of $($r.Json.crossing_leg_1.expected), leg 2 drawn $($r.Json.crossing_leg_2.pieces_drawn) of $($r.Json.crossing_leg_2.expected), handed over: fewest pieces in a frame $($r.Json.crossing_leg_4.fewest_pieces_in_a_frame) of $($r.Json.crossing_leg_4.expected), fewest avatars $($r.Json.crossing_leg_4.fewest_avatars_in_a_frame)$(if ($r.Exit -ne 0) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    # avatars_rigged_blend: three avatars, two mid-walk, wear a rigged shirt whose sleeves are blended (90 % opaque) and whose body is opaque, so
    # one instance draws two variants of the face shader; the picture must match the one with opaque sleeves (the sleeves posed by their own
    # avatar's bones) and the engine must not warn that the instance's materials disagree about the bone-row uniforms. The negative control
    # leaves the meshes unskinned (none drawn): it must fail.
    foreach ($be in $Backends) {
        $r = Invoke-AvatarRender $be "avatars-rigged-blend-$be" "--tr-avatars=riggedblend" 300
        $rb = $r.Json.avatars_rigged_blend
        $warnings = @(Select-String -LiteralPath (Join-Path $out "avatars-rigged-blend-$be.log") -Pattern "same instance shader uniform" -ErrorAction SilentlyContinue).Count
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $rb.meshes_drawn -eq 3 -and $rb.differing_fraction -le $rb.limit_fraction -and $warnings -eq 0
        Add-Case "avatars_rigged_blend_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) rigged meshes drawn $($rb.meshes_drawn) of 3, blended against opaque sleeves: $($rb.differing_pixels) pixels differ ($($rb.differing_fraction), limit $($rb.limit_fraction)), instance-uniform warnings in the log: $warnings" $r.Seconds
    }
    $n = Invoke-AvatarRender "vulkan" "avatars-rigged-blend-negative-vulkan" "--tr-avatars=riggedblend --tr-alter=norigged" 300
    if ($n.Exit -eq 1 -and $n.Json.avatars_rigged_blend.rigged_shown -eq 0 -and $n.Json.backend.driver -eq "vulkan") { Add-Case "avatars_rigged_blend_negative_vulkan" "PASS" "meshes with no readable skin are drawn as plain meshes and not skinned, and the check rejects it: skinned $($n.Json.avatars_rigged_blend.rigged_shown) of 3 (plain meshes drawn $($n.Json.avatars_rigged_blend.meshes_drawn))" $n.Seconds }
    else { Add-Case "avatars_rigged_blend_negative_vulkan" "FAIL" "rigged meshes left undrawn NOT rejected (exit $($n.Exit), drawn $($n.Json.avatars_rigged_blend.meshes_drawn))" $n.Seconds }
    # crowd_smoke: 25 avatars in synthetic outfits of 20 rigged meshes each (a mix of standing and walking), every one drawn through the
    # viewer's own path; once the scene settles every avatar and every rigged mesh is drawn, none is counted and left undrawn. Counts only,
    # no golden image. Vulkan only.
    $r = Invoke-AvatarRender "vulkan" "crowd-smoke-vulkan" "--tr-avatars=crowd:25 --tr-crowd-seconds=1" 300
    $cs = $r.Json.avatars_crowd
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan" -and $cs.avatars -eq 25 -and $cs.avatars_drawn -eq 25 -and $cs.rigged_expected -eq 500 -and $cs.rigged_drawn -eq 500 -and $cs.rigged_skipped -eq 0 -and $cs.process_memory.peak_working_set_mib -gt 0 -and $cs.process_memory.peak_private_mib -gt 0
    Add-Case "crowd_smoke_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) avatars drawn $($cs.avatars_drawn) of $($cs.avatars) ($($cs.walking) walking), rigged meshes drawn $($cs.rigged_drawn) of $($cs.rigged_expected), counted and not drawn $($cs.rigged_skipped), draw calls p50 $($cs.draw_calls.p50), peak working set $($cs.process_memory.peak_working_set_mib) MiB, peak private $($cs.process_memory.peak_private_mib) MiB$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    # negative control: the meshes carry no full skin (as when they are not skinned), none is drawn, and the same counts MUST fail (exit 1)
    # crowd_distinct_100: 100 avatars in 100 different outfits of 20 rigged meshes (2,000 bind tables, more than the atlas first held): every avatar and
    # every rigged mesh is drawn, none counted and left undrawn, and the atlas holds them all. Counts only, no golden image. Vulkan only.
    $r = Invoke-AvatarRender "vulkan" "crowd-distinct-100-vulkan" "--tr-avatars=crowd:100 --tr-crowd-outfits=100 --tr-crowd-seconds=1" 600
    $cs = $r.Json.avatars_crowd
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan" -and $cs.outfits -eq 100 -and $cs.avatars_drawn -eq 100 -and $cs.rigged_expected -eq 2000 -and $cs.rigged_drawn -eq 2000 -and $cs.rigged_skipped -eq 0 -and $cs.tables_in_use -eq 2000
    Add-Case "crowd_distinct_100_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) avatars drawn $($cs.avatars_drawn) of $($cs.avatars) in $($cs.outfits) outfits, rigged meshes drawn $($cs.rigged_drawn) of $($cs.rigged_expected), counted and not drawn $($cs.rigged_skipped), tables in use $($cs.tables_in_use) of limit $($cs.table_limit), atlas $($cs.atlas_mib) MiB, peak working set $($cs.process_memory.peak_working_set_mib) MiB$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    # crowd_real: 25 avatars with looks (crowd:25,real): every one shaped, morphed and baked, about half in a mesh body, the system body hidden
    # exactly where the looks say; the bakes sized by each avatar's pixel area, and the farthest avatar's bakes go up to full size and back.
    $r = Invoke-AvatarRender "vulkan" "crowd-real-25-vulkan" "--tr-avatars=crowd:25,real --tr-crowd-seconds=1 --tr-bake-phase" 600
    $cs = $r.Json.avatars_crowd; $rl = $cs.real
    $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan" -and $cs.avatars_drawn -eq 25 -and $rl.shaped -eq 25 -and $rl.morphed -eq 25 -and $rl.avatars_with_all_body_bakes -eq 25 -and $rl.hidden_as_looked -eq 25 -and $rl.transparent_as_looked -eq 25 -and $rl.mesh_bodies -ge 6 -and $rl.mesh_bodies -le 19 -and $rl.males -ge 5 -and $rl.females -ge 5 -and $rl.bake_held_twice -eq 0 -and $cs.held_levels -eq 0 -and $cs.held_pixel_mib -eq 0
    Add-Case "crowd_real_25_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) drawn $($cs.avatars_drawn) of 25, shaped $($rl.shaped), morphed $($rl.morphed), all body bakes $($rl.avatars_with_all_body_bakes), hidden as looked $($rl.hidden_as_looked), transparent as looked $($rl.transparent_as_looked), mesh bodies $($rl.mesh_bodies), males $($rl.males), bake GPU $($rl.bake_gpu_mib) MiB, digest $($rl.scene_digest)$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    # negative control: the appearances are not sent, the avatars stay default, and the same checks MUST fail (exit 1)
    $n = Invoke-AvatarRender "vulkan" "crowd-real-negative-vulkan" "--tr-avatars=crowd:25,real --tr-crowd-seconds=1 --tr-alter=nolook" 300
    if ($n.Exit -eq 1 -and $n.Json.verdict -eq "FAIL" -and $n.Json.avatars_crowd.real.shaped -eq 0) { Add-Case "crowd_real_negative_vulkan" "PASS" "no appearances sent rejected: shaped $($n.Json.avatars_crowd.real.shaped) of 25, morphed $($n.Json.avatars_crowd.real.morphed)" $n.Seconds }
    else { Add-Case "crowd_real_negative_vulkan" "FAIL" "avatars left default NOT rejected (exit $($n.Exit), verdict $($n.Json.verdict), shaped $($n.Json.avatars_crowd.real.shaped))" $n.Seconds }
    # negative controls for the memory checks: a bake the prim layer already holds also given a texture by the presenter (as before slots a worn mesh or their
    # own alpha hides were left without one) MUST be seen as held twice; a decoded level kept after its upload MUST be seen as held on the CPU side
    $n = Invoke-AvatarRender "vulkan" "crowd-real-held-twice-negative-vulkan" "--tr-avatars=crowd:25,real --tr-crowd-seconds=1 --tr-alter=hiddentextures" 300
    if ($n.Exit -eq 1 -and $n.Json.verdict -eq "FAIL" -and $n.Json.avatars_crowd.real.bake_held_twice -gt 0) { Add-Case "crowd_real_held_twice_negative_vulkan" "PASS" "bakes held by both the prim layer and the presenter rejected: $($n.Json.avatars_crowd.real.bake_held_twice) held twice" $n.Seconds }
    else { Add-Case "crowd_real_held_twice_negative_vulkan" "FAIL" "bakes held twice NOT rejected (exit $($n.Exit), verdict $($n.Json.verdict), held twice $($n.Json.avatars_crowd.real.bake_held_twice))" $n.Seconds }
    $n = Invoke-AvatarRender "vulkan" "crowd-real-keep-pixels-negative-vulkan" "--tr-avatars=crowd:25,real --tr-crowd-seconds=1 --tr-alter=keeppixels" 300
    if ($n.Exit -eq 1 -and $n.Json.verdict -eq "FAIL" -and $n.Json.avatars_crowd.held_levels -gt 0) { Add-Case "crowd_real_keep_pixels_negative_vulkan" "PASS" "a decoded level kept after its upload rejected: $($n.Json.avatars_crowd.held_levels) held" $n.Seconds }
    else { Add-Case "crowd_real_keep_pixels_negative_vulkan" "FAIL" "a kept decoded level NOT rejected (exit $($n.Exit), verdict $($n.Json.verdict), held levels $($n.Json.avatars_crowd.held_levels))" $n.Seconds }
    # the same seed gives the same scene; another seed another
    $a1 = Invoke-AvatarRender "vulkan" "crowd-real-seed-a1-vulkan" "--tr-avatars=crowd:10,real --tr-crowd-seconds=1 --tr-seed=8" 600
    $a2 = Invoke-AvatarRender "vulkan" "crowd-real-seed-a2-vulkan" "--tr-avatars=crowd:10,real --tr-crowd-seconds=1 --tr-seed=8" 600
    $b1 = Invoke-AvatarRender "vulkan" "crowd-real-seed-b1-vulkan" "--tr-avatars=crowd:10,real --tr-crowd-seconds=1 --tr-seed=9" 600
    $d1 = $a1.Json.avatars_crowd.real.scene_digest; $d2 = $a2.Json.avatars_crowd.real.scene_digest; $d3 = $b1.Json.avatars_crowd.real.scene_digest
    $ok = $a1.Exit -eq 0 -and $a2.Exit -eq 0 -and $b1.Exit -eq 0 -and $d1 -and $d1 -eq $d2 -and $d1 -ne $d3
    Add-Case "crowd_real_same_seed_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "seed 8 twice: $d1 and $d2; seed 9: $d3" ($a1.Seconds + $a2.Seconds + $b1.Seconds)
    # arrival: N such avatars arriving within one second; every one finished. The longest main-thread slice is reported against the 4 ms rule, not gated: it does not meet it yet
    foreach ($na in @(25, 50, 100)) {
        $r = Invoke-AvatarRender "vulkan" "arrival-$na-vulkan" "--tr-avatars=arrive:$na --tr-crowd-seconds=1" 900
        $ar = $r.Json.avatars_crowd.arrival; $rl = $r.Json.avatars_crowd.real
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq "vulkan" -and $r.Json.avatars_crowd.avatars_drawn -eq $na -and $rl.shaped -eq $na -and $rl.morphed -eq $na -and $rl.hidden_as_looked -eq $na
        Add-Case "arrival_${na}_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) finished in $($ar.time_to_finished_s) s (indicative), longest main-thread slice $($ar.slice_max_ms) ms (limit $($ar.slice_limit_ms)), slices over $($ar.slices_over_limit), frame p95 $($ar.frame_ms.p95) ms, ingest max $($ar.ingest_ms.max) ms$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
    $n = Invoke-AvatarRender "vulkan" "crowd-smoke-negative-vulkan" "--tr-avatars=crowd:25 --tr-crowd-seconds=1 --tr-alter=norigged" 300
    $ns = $n.Json.avatars_crowd
    if ($n.Exit -eq 1 -and $n.Json.verdict -eq "FAIL" -and $ns.rigged_drawn -eq 0 -and $ns.rigged_skipped -eq 0) { Add-Case "crowd_smoke_negative_vulkan" "PASS" "meshes with no readable skin are drawn as plain meshes and not skinned, and the check rejects it: skinned $($ns.rigged_drawn) of $($ns.rigged_expected), counted and not drawn $($ns.rigged_skipped)" $n.Seconds }
    else { Add-Case "crowd_smoke_negative_vulkan" "FAIL" "rigged meshes left undrawn NOT rejected (exit $($n.Exit), verdict $($n.Json.verdict), drawn $($ns.rigged_drawn))" $n.Seconds }
    # the compile gate over a crowd arriving (the bodies, rigged meshes and name labels of 25 avatars): nothing may compile after the warm-up
    foreach ($be in $Backends) {
        $cg = Invoke-AvatarRender $be "pipeline-gate-crowd-$be" "--tr-avatars=crowd:25 --tr-crowd-seconds=3 --tr-compile-gate" 300
        $ok = $cg.Exit -eq 0 -and $cg.Json.backend.driver -eq $be -and $cg.Json.compile_watch.frames -gt 0 -and $cg.Json.compile_watch.waiting_compilations -eq 0 -and $cg.Json.compile_watch.background_compilations -eq 0
        Add-Case "pipeline_gate_crowd_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(GateDetail $cg.Json)$(if (-not $ok) { '; exit ' + $cg.Exit + ' ' + ($cg.Json.failures -join ' | ') })" $cg.Seconds
    }
    $lifeArgs = "--tr-fixture=synthetic-256 --tr-lifecycle=3 --tr-view=server `"--tr-meshes=$meshDir`" `"--tr-textures=$texDir`""
    $r = Invoke-AvatarRender "vulkan" "lifecycle-avatars-vulkan" $lifeArgs 400
    $la = $r.Json.avatars
    $ok = $r.Exit -eq 0 -and $la.regions -eq 6 -and $la.departures -eq 6
    Add-Case "lifecycle_avatars_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "$($la.regions) regions with three avatars each, $($la.departures) left mid-region, peak avatar resources $($la.peak_resources); nothing left after each change$(if (-not $ok) { '; exit ' + $r.Exit + ': ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    $n = Invoke-AvatarRender "vulkan" "lifecycle-avatars-negative-vulkan" "$lifeArgs --tr-alter=leakavatar" 400
    $caught = @($n.Json.failures | Where-Object { $_ -match 'objects \d+, with the view made' }).Count
    if ($n.Json.verdict -eq "FAIL" -and $caught -gt 0) { Add-Case "lifecycle_avatars_negative_vulkan" "PASS" "a kept bake texture rejected: $caught lifecycle checks saw the extra engine object" $n.Seconds }
    else { Add-Case "lifecycle_avatars_negative_vulkan" "FAIL" "a kept bake texture NOT rejected (exit $($n.Exit), verdict $($n.Json.verdict))" $n.Seconds }
    # a detach is presented by the frame after it, however little time that frame has: the same lifecycle with the frame after each detach limited to one unit of work
    $s = Invoke-AvatarRender "vulkan" "lifecycle-avatars-slowdetach-vulkan" "$lifeArgs --tr-alter=slowdetach" 400
    $ok = $s.Exit -eq 0 -and $s.Json.avatars.regions -eq 6 -and $s.Json.avatars.departures -eq 6
    Add-Case "lifecycle_avatars_slowdetach_vulkan" ($(if ($ok) { "PASS" } else { "FAIL" })) "a detach presented by a frame with no spare time$(if (-not $ok) { ": exit " + $s.Exit + ": " + ($s.Json.failures -join " | ") })" $s.Seconds
}

#Local only: Region A v4 (terrain settings in the capture) with its textured ground.
$regionA4 = Join-Path $local "capture-region-a-v4.jsonl.gz"
foreach ($be in $Backends) {
    if (-not ((Test-Path -LiteralPath $regionA4) -and (Test-Path -LiteralPath $assets))) {
        Add-Case "region_a_v4_$be" "SKIP" "local-only fixtures-local\capture-region-a-v4.jsonl.gz and fixtures-local\assets\ are absent (grid content, never committed; ADR-011)" 0
        continue
    }
    $golden = Join-Path $localGoldens "golden-region-a-v4-$be.png"
    $args4 = "`"--tr-fixture=$regionA4`" `"--tr-assets=$assets`""
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-region-a-v4-$be" $args4
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
            Copy-Item -LiteralPath (Join-Path $out "proposal-region-a-v4-$be.png") -Destination $golden
            Add-Case "region_a_v4_$be" "FAIL" "no local golden: PROPOSED $golden" $r.Seconds
        } else { Add-Case "region_a_v4_$be" "FAIL" "no local golden $golden (render exit $($r.Exit))" $r.Seconds }
        continue
    }
    $r = Invoke-Render $be "region-a-v4-$be" "$args4 `"--tr-golden=$golden`""
    $p = $r.Json.prims
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and ($r.Json.terrain_textures.layers -join ',') -eq 'region,region,region,region'
    Add-Case "region_a_v4_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) $($r.Json.diff.summary); drawn $($p.instances) of $($p.objects); terrain layers $($r.Json.terrain_textures.layers -join ',')$(if (-not $ok) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

#Local only: the region A capture's prims (sculpts/meshes are not drawn yet; captures before carry no shape data).
if (-not (Test-Path -LiteralPath $regionA)) {
    Add-Case "region_a_prims" "SKIP" "local-only capture fixtures-local\capture-region-a.jsonl.gz is absent (grid content, never committed; ADR-011)" 0
} else {
    $r = Invoke-Render "vulkan" "region-a-prims-vulkan" "`"--tr-fixture=$regionA`""
    $pr = $r.Json.prims
    $d = "objects=$($pr.objects) drawn=$($pr.instances) skipped: no shape data=$($pr.skipped_no_shape), sculpt/mesh=$($pr.skipped_sculpt_or_mesh), attachments=$($pr.skipped_attachments)"
    Add-Case "region_a_prims" ($(if ($r.Exit -eq 0) { "PASS" } else { "FAIL" })) "$d$(if ($r.Exit -ne 0) { '; exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

# R-1: a render run on OpenGL is refused.
$g = Invoke-Render "opengl3" "refuse-opengl3" "`"--tr-fixture=$fixture`""
if ($g.Exit -eq 3 -and $g.Json.verdict -eq "REFUSED") { Add-Case "refuse_opengl3" "PASS" "exit 3, backend $($g.Json.backend.driver) refused" $g.Seconds }
else { Add-Case "refuse_opengl3" "FAIL" "exit $($g.Exit), verdict $($g.Json.verdict)" $g.Seconds }

Invoke-WindowCases

# Local-only region B golden (1024x1024 from the capture, replayed into the core).
foreach ($be in $Backends) {
    if (-not (Test-Path -LiteralPath $regionB)) {
        Add-Case "region_b_$be" "SKIP" "local-only capture fixtures-local\capture-region-b.jsonl.gz is absent (grid content, never committed; ADR-011)" 0
        continue
    }
    $golden = Join-Path $localGoldens "golden-region-b-terrain-$be.png"
    if (-not (Test-Path -LiteralPath $golden)) {
        $r = Invoke-Render $be "proposal-region-b-$be" "`"--tr-fixture=$regionB`""
        if ($Propose -and $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be) {
            New-Item -ItemType Directory -Force -Path $localGoldens | Out-Null
            Copy-Item -LiteralPath (Join-Path $out "proposal-region-b-$be.png") -Destination $golden
            Add-Case "region_b_$be" "FAIL" "no local golden: PROPOSED $golden (local only; needs approval after viewing)" $r.Seconds
        } else {
            Add-Case "region_b_$be" "FAIL" "no local golden $golden (render exit $($r.Exit))" $r.Seconds
        }
        continue
    }
    $r = Invoke-Render $be "region-b-$be" "`"--tr-fixture=$regionB`" `"--tr-golden=$golden`""
    $ok = $r.Exit -eq 0 -and $r.Json.diff.pass -and $r.Json.backend.driver -eq $be -and $r.Json.region.size_x -eq 1024 -and $r.Json.region.complete
    Add-Case "region_b_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "backend=$($r.Json.backend.driver) region=$($r.Json.region.size_x)x$($r.Json.region.size_y) patches=$($r.Json.region.patches)/$($r.Json.region.expected) $($r.Json.diff.summary) build=$($r.Json.render.build_ms) ms$(if (-not $ok) { ' exit ' + $r.Exit + ' ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}

# L3 resource lifecycle across region changes.
foreach ($c in @(@("server", "vulkan"), @("nodes", "vulkan"), @("server", "d3d12")) | Where-Object { $Backends -contains $_[1] }) {
    $view, $be = $c
    # every region also runs 3 eviction / reload cycles of all its textures (texture memory must drop and come back);
    # each cycle also steps every texture down to its coarsest level in place and back up (same slots, same memory)
    $r = Invoke-Render $be "lifecycle-$view-$be" "--tr-fixture=synthetic-256 --tr-lifecycle=20 --tr-view=$view `"--tr-meshes=$meshDir`" `"--tr-textures=$texDir`" --tr-evict-cycles=3" 400
    $end = $r.Json.samples | Where-Object at -eq "end"
    $base = $r.Json.samples | Where-Object at -eq "baseline"
    $d = "one-time engine objects $($r.Json.one_time_engine_objects); peak live=$($r.Json.peak_live_resources) (prims $($r.Json.peak_prim_resources), mesh RIDs $($r.Json.peak_mesh_resources)); objects $($base.r.Objects)->$($end.r.Objects), nodes $($base.r.Nodes)->$($end.r.Nodes), buffer mem $($base.r.BufferMem)->$($end.r.BufferMem), video mem $($base.r.VideoMem)->$($end.r.VideoMem); eviction cycles $($r.Json.eviction.cycles), reloads $($r.Json.eviction.reloads), texture memory freed per eviction $($r.Json.eviction.max_texture_mem_freed) B; level cycles (down to the coarsest and back) $($r.Json.eviction.level_cycles), freed by levels $($r.Json.eviction.max_texture_mem_freed_by_levels) B"
    Add-Case "lifecycle_${view}_$be" ($(if ($r.Exit -eq 0) { "PASS" } else { "FAIL" })) ($(if ($r.Exit -eq 0) { $d } else { "exit $($r.Exit): $($r.Json.failures -join ' | '); $d" })) $r.Seconds
}

# the release at a region change, deterministically: each region is built with collections held off (a no-GC
# region), so a texture or material that only a finalizer would release is still alive at the check; nothing the region
# made may outlive its change. The terrain detail textures and material are in it too. A region the runtime cannot hold
# off for fails as inconclusive, never passes.
foreach ($be in $Backends) {
    $r = Invoke-Render $be "lifecycle-release-$be" "--tr-fixture=synthetic-256 --tr-lifecycle=3 --tr-view=server `"--tr-textures=$texDir`" --tr-lifecycle-gc=hold --tr-lifecycle-details" 300
    $rc = $r.Json.region_changes
    $made = if ($rc) { (@($rc.each)[0].made.PSObject.Properties | ForEach-Object { "$($_.Value) $($_.Name)" }) -join ", " } else { "" }
    $ok = $r.Exit -eq 0 -and $rc -and $rc.gc -eq "hold" -and $rc.holds_broken -eq 0 -and @($rc.each).Count -eq 6
    Add-Case "lifecycle_release_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) "$(@($rc.each).Count) region changes held off, $($rc.holds_broken) broken; each region made: $made; region change ms p50 $($rc.change_ms.p50) max $($rc.change_ms.max)$(if (-not $ok) { '; exit ' + $r.Exit + ': ' + ($r.Json.failures -join ' | ') })" $r.Seconds
}
# negative control: a clear that keeps one texture referenced must be caught per object and by the counters
$n = Invoke-Render "vulkan" "lifecycle-leak-negative-vulkan" "--tr-fixture=synthetic-256 --tr-lifecycle=3 --tr-view=server `"--tr-textures=$texDir`" --tr-alter=leaktexture" 300
$perObject = (@($n.Json.failures) -match "texture of the region's own outlived").Count
$byCounters = (@($n.Json.failures) -match "^after cycle").Count
if ($n.Json.verdict -eq "FAIL" -and $perObject -gt 0 -and $byCounters -gt 0) { Add-Case "lifecycle_leak_negative_vulkan" "PASS" "leaked texture rejected: $perObject region changes per object, $byCounters cycles by the counters" $n.Seconds }
else { Add-Case "lifecycle_leak_negative_vulkan" "FAIL" "leaked texture NOT rejected (exit $($n.Exit), per object $perObject, counters $byCounters)" $n.Seconds }

# the lifecycle's water case. Every region has a water height and the synthetic environment; the cycles
# alternate region resets and region switches; each must free the water mesh, instance and material and the sky, and
# objects / buffer / video memory must not grow. Negative control: a run whose clear forgets the water without freeing
# it (--tr-alter=keepwater) must FAIL.
$envLlsd = Join-Path $repo "fixtures\environment\synthetic-environment.llsd.xml"
foreach ($be in $Backends) {
    $r = Invoke-Render $be "lifecycle-water-$be" "--tr-fixture=synthetic-256 --tr-lifecycle=12 --tr-view=server `"--tr-environment=$envLlsd`"" 300
    $end = $r.Json.samples | Where-Object at -eq "end"
    $base = $r.Json.samples | Where-Object at -eq "baseline"
    $d = "resets $($r.Json.water.resets), switches $($r.Json.water.switches), peak water/sky resources $($r.Json.water.peak_environment_resources); objects $($base.r.Objects)->$($end.r.Objects), buffer mem $($base.r.BufferMem)->$($end.r.BufferMem), video mem $($base.r.VideoMem)->$($end.r.VideoMem)"
    $ok = $r.Exit -eq 0 -and $r.Json.water.resets -gt 0 -and $r.Json.water.switches -gt 0
    Add-Case "lifecycle_water_$be" ($(if ($ok) { "PASS" } else { "FAIL" })) ($(if ($ok) { $d } else { "exit $($r.Exit): $($r.Json.failures -join ' | '); $d" })) $r.Seconds
}
# keepwater-release leaks the same way and frees what it leaked after the checks, so the process can quit cleanly: the engine's
# own quit with live leaked resources ends in heap corruption (0xC0000374, or 0xC0000005) on both backends, every time. The
# run must end with exit 1 (the failed verdict) and nothing else: any other exit code, a crash included, fails the case.
foreach ($be in $Backends) {
    $n = Invoke-Render $be "lifecycle-water-negative-$be" "--tr-fixture=synthetic-256 --tr-lifecycle=12 --tr-view=server `"--tr-environment=$envLlsd`" --tr-alter=keepwater-release" 300
    if ($n.Json.verdict -eq "FAIL" -and (@($n.Json.failures) -match "still drawn").Count -gt 0 -and $n.Exit -eq 1) { Add-Case "lifecycle_water_negative_$be" "PASS" "unfreed water rejected: $(@($n.Json.failures)[0..1] -join ' | ')" $n.Seconds }
    else { Add-Case "lifecycle_water_negative_$be" "FAIL" "unfreed water NOT rejected, or the run did not end with exit 1 (exit $($n.Exit))" $n.Seconds }
}

# The check at quit. Every engine resource the viewer makes is recorded; a run lists what is still live just before it
# quits and fails with that list (the engine corrupts its heap when it quits with such resources live). Negative
# control: --tr-leak-at-quit makes one and never frees it; the run must end with exit 1 (the failed verdict) naming it.
foreach ($be in $Backends) {
    $n = Invoke-Render $be "quit-check-negative-$be" "`"--tr-fixture=$fixture`" --tr-leak-at-quit"
    if ($n.Json.verdict -eq "FAIL" -and (@($n.Json.failures) -match "still live at quit.*quit-check negative control").Count -gt 0 -and $n.Exit -eq 1) { Add-Case "quit_check_negative_$be" "PASS" "resource left live at quit rejected: $(@($n.Json.failures)[0])" $n.Seconds }
    else { Add-Case "quit_check_negative_$be" "FAIL" "resource left live at quit NOT rejected, or the run did not end with exit 1 (exit $($n.Exit), verdict $($n.Json.verdict))" $n.Seconds }
}

Invoke-NameTagCases
}   # end of the test cases skipped by -MeasureOnly
$script:Measuring = $true   # everything below except the hang check is a measurement (timings are the result)

# -Residency (opt-in). (LOCAL-ONLY captures): a seeded camera path (1200 frames, seed 8, as) through
# Region A v5 and region B v3 at the default for a 16 GB card (2 GiB) and at a tight budget that forces eviction, on both
# backends. The committed synthetic heavy scene (600 texture ids, ~2.3 GiB at full resolution),
# paced by wall-clock time (30 s), at 2 GiB, 4 GiB and the floor-class values 614, 384 and 256 MiB, on both backends;
# plus the upload path (--tr-legacy-upload: mipmaps on the main thread) at 2 GiB for the upload comparison.
function PathDetail($r) {
    $p = $r.Json.path
    $sizes = if ($p.upload_unit_ms_by_size) { ($p.upload_unit_ms_by_size.PSObject.Properties | ForEach-Object { "$($_.Name): p95 $($_.Value.p95) max $($_.Value.max) (n $($_.Value.n))" }) -join '; ' } else { "" }
    "$($p.paced_by) $($p.path_seconds) s, mipmaps $($p.mipmaps); frame p50/p95/max $($p.frame_ms.p50)/$($p.frame_ms.p95)/$($p.frame_ms.max) ms; upload/frame p95/max $($p.upload_ms.p95)/$($p.upload_ms.max) ms; upload units over 4 ms $($p.upload_units_over_4ms) of $($p.upload_units), worst $($p.worst_upload.w)x$($p.worst_upload.h) $($p.worst_upload.ms) ms; by size [$sizes]; budget $($p.budget_mib) MiB, peak $($p.peak_resident_mib); steps down $($p.steps_down), up $($p.steps_up), evictions $($p.evictions), reloads $($p.reloads), thrash $($p.thrash); final pose: $($p.final_pose_in_view) in view, below desired $($p.final_pose_below_desired_at_arrival) / placeholders $($p.final_pose_placeholders_at_arrival) at arrival, $($p.final_pose_below_desired_end) / $($p.final_pose_placeholders_end) at the end; all resident after $($p.final_pose_all_visible_resident_ms) ms, all at desired after $($p.final_pose_all_visible_at_desired_ms) ms; priority p95 $($p.priority_ms.p95) ms$(if ($r.Exit -ne 0) { '; ' + ($r.Json.failures -join ' | ') })"
}
if ($Residency) {
    $pathScenes = @(
        @{ Scene = "region-a-v5"; Fixture = (Join-Path $local "capture-region-a-v5.jsonl.gz"); Tight = "96m" },
        @{ Scene = "region-b-v3"; Fixture = (Join-Path $local "capture-region-b-v3.jsonl.gz"); Tight = "23m" }
    )
    foreach ($ps in $pathScenes) {
        foreach ($be in $Backends) {
            foreach ($kind in @("default", "tight")) {
                $tag = "path-$($ps.Scene)-$kind-$be"
                if (-not ((Test-Path -LiteralPath $ps.Fixture) -and (Test-Path -LiteralPath $assets))) { Add-Case $tag "SKIP" "local-only capture or assets absent (ADR-011)" 0; continue }
                $r = Invoke-Render $be $tag "`"--tr-fixture=$($ps.Fixture)`" `"--tr-assets=$assets`" --tr-path=1200 --tr-seed=8 --tr-budget=$(if ($kind -eq 'default') { '2048m' } else { $ps.Tight })" 400
                Add-Case $tag ($(if ($r.Exit -eq 0) { "PASS" } else { "FAIL" })) (PathDetail $r) $r.Seconds
            }
        }
    }
    foreach ($be in $Backends) {
        foreach ($budget in @("2048m", "4096m", "614m", "384m", "256m", "legacy-2048m")) {
            $legacy = $budget.StartsWith("legacy-")
            $b = $budget -replace "^legacy-", ""
            $tag = "path-heavy-$budget-$be"
            $r = Invoke-Render $be $tag "--tr-fixture=synthetic-heavy --tr-path-seconds=30 --tr-seed=8 --tr-budget=$b$(if ($legacy) { ' --tr-legacy-upload' })" 400
            $p = $r.Json.path
            $ok = $r.Exit -eq 0 -and $p.paced_by -eq "wall_clock" -and $p.textures -ge 600 -and $p.peak_resident_mib -le ($p.budget_mib + 16)
            Add-Case $tag ($(if ($ok) { "PASS" } else { "FAIL" })) (PathDetail $r) $r.Seconds
        }
    }
}

if ($Resolutions) {
    # (scene, size, budget, backend); scene "heavy" is the committed generator, the others LOCAL-ONLY captures
    $resRuns = @(
        @("heavy", "1920x1080", "8192m", "vulkan"), @("heavy", "1920x1080", "8192m", "d3d12"),
        @("heavy", "1920x1080", "2048m", "vulkan"), @("heavy", "1920x1080", "614m", "vulkan"), @("heavy", "1920x1080", "256m", "vulkan"),
        @("heavy", "3840x2160", "8192m", "vulkan"), @("heavy", "3840x2160", "8192m", "d3d12"),
        @("heavy", "3840x2160", "4096m", "vulkan"), @("heavy", "3840x2160", "2048m", "vulkan"),
        @("region-a-v5", "1920x1080", "2048m", "vulkan"), @("region-a-v5", "1920x1080", "2048m", "d3d12"),
        @("region-a-v5", "3840x2160", "2048m", "vulkan"), @("region-a-v5", "3840x2160", "2048m", "d3d12"),
        @("region-b-v3", "640x360", "2048m", "vulkan"), @("region-b-v3", "640x360", "2048m", "d3d12")
    )
    $saveExe = $exe; $saveBuild = $build
    if ($PathExe) { $exe = $PathExe; $build = Split-Path -Parent $PathExe }   # its own appdata\ beside it (caches land there)
    foreach ($rr in $resRuns) {
        $scene, $size, $b, $be = $rr
        $tag = "res-$scene-$size-$b-$be$PathTagSuffix"
        if ($PathFilter -and $tag -notmatch $PathFilter) { continue }
        if ($scene -eq "heavy") { $fx = "--tr-fixture=synthetic-heavy" }
        else {
            $cap = Join-Path $local "capture-$scene.jsonl.gz"
            if (-not ((Test-Path -LiteralPath $cap) -and (Test-Path -LiteralPath $assets))) { Add-Case $tag "SKIP" "local-only capture or assets absent (ADR-011)" 0; continue }
            $fx = "`"--tr-fixture=$cap`" `"--tr-assets=$assets`""
        }
        $r = Invoke-Render $be $tag "$fx --tr-path-seconds=30 --tr-seed=8 --tr-budget=$b --tr-size=$size $PathExtra" 600
        $p = $r.Json.path
        $fl = $p.frames_log
        $ok = $r.Exit -eq 0 -and $p.paced_by -eq "wall_clock" -and $r.Json.render_size.width -eq [int]($size -split 'x')[0] -and $r.Json.render_size.residency_viewport_height -eq [int]($size -split 'x')[1] -and $p.peak_resident_mib -le ($p.budget_mib + 16)
        Add-Case $tag ($(if ($ok) { "PASS" } else { "FAIL" })) "size $($r.Json.render_size.width)x$($r.Json.render_size.height) (residency height $($r.Json.render_size.residency_viewport_height)); $(PathDetail $r); long frames $($fl.long_frames) of $($fl.frames) (with compilations $($fl.long_with_compilations), GC $($fl.long_with_gc), first use $($fl.long_with_first_use), unexplained $($fl.long_unexplained)); compilations $(($fl.pipeline_compilations.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', ')" $r.Seconds
    }
    $exe = $saveExe; $build = $saveBuild
}

function MemDetail($r) {
    $m = $r.Json.path.memory
    if (-not $m -or -not $m.samples) { return "no memory samples" }
    $pk = $m.at_peak_video; $e = $m.end
    "memory at peak video: video $($pk.video_mib) = resident textures $($pk.resident_textures_mib) + other texture $($pk.other_texture_mib) + buffers $($pk.buffer_mib) + remainder $($pk.remainder_mib) MiB (non-texture $($pk.non_texture_mib); device total/textures/buffers $($pk.rd_total_mib)/$($pk.rd_textures_mib)/$($pk.rd_buffers_mib)); end: video $($e.video_mib), resident $($e.resident_textures_mib), non-texture $($e.non_texture_mib); non-texture range $($m.non_texture_range.min)..$($m.non_texture_range.max) (p50 $($m.non_texture_range.p50)); other texture $($m.other_texture_range.min)..$($m.other_texture_range.max); buffers $($m.buffer_range.min)..$($m.buffer_range.max); remainder $($m.remainder_range.min)..$($m.remainder_range.max) ($($m.samples) samples)"
}

if ($Memory) {
    foreach ($scene in @("heavy", "region-a-v5", "region-b-v3")) {
        foreach ($size in @("640x360", "1920x1080", "3840x2160")) {
            foreach ($be in $Backends) {
                $tag = "mem-$scene-$size-$be"
                if ($PathFilter -and $tag -notmatch $PathFilter) { continue }
                if ($scene -eq "heavy") { $fx = "--tr-fixture=synthetic-heavy"; $b = "8192m" }
                else {
                    $cap = Join-Path $local "capture-$scene.jsonl.gz"
                    if (-not ((Test-Path -LiteralPath $cap) -and (Test-Path -LiteralPath $assets))) { Add-Case $tag "SKIP" "local-only capture or assets absent (ADR-011)" 0; continue }
                    $fx = "`"--tr-fixture=$cap`" `"--tr-assets=$assets`""; $b = "2048m"
                }
                $r = Invoke-Render $be $tag "$fx --tr-path-seconds=30 --tr-seed=8 --tr-budget=$b --tr-size=$size $PathExtra" 600
                $m = $r.Json.path.memory
                $ok = $r.Exit -eq 0 -and $r.Json.render_size.width -eq [int]($size -split 'x')[0] -and $m.samples -gt 0 -and $m.at_peak_video.video_mib -gt 0
                Add-Case $tag ($(if ($ok) { "PASS" } else { "FAIL" })) "$(MemDetail $r); budget $($r.Json.path.budget_mib) MiB, peak resident $($r.Json.path.peak_resident_mib)" $r.Seconds
            }
        }
    }
}

if ($Budget) {
    $budgetRuns = @(
        @("heavy", "1920x1080", "", "vulkan"), @("heavy", "1920x1080", "", "d3d12"),
        @("heavy", "3840x2160", "", "vulkan"), @("heavy", "3840x2160", "", "d3d12"),
        @("heavy", "1920x1080", "2048", "vulkan"),
        @("region-a-v5", "3840x2160", "", "vulkan"), @("region-a-v5", "3840x2160", "", "d3d12"),
        @("heavy", "3840x2160", "8192", "vulkan"), @("heavy", "3840x2160", "4096", "vulkan")
    )
    foreach ($br in $budgetRuns) {
        $scene, $size, $vram, $be = $br
        $tag = "budget-$scene-$size$(if ($vram) { "-vram$vram" })-$be"
        if ($PathFilter -and $tag -notmatch $PathFilter) { continue }
        if ($scene -eq "heavy") { $fx = "--tr-fixture=synthetic-heavy" }
        else {
            $cap = Join-Path $local "capture-$scene.jsonl.gz"
            if (-not ((Test-Path -LiteralPath $cap) -and (Test-Path -LiteralPath $assets))) { Add-Case $tag "SKIP" "local-only capture or assets absent (ADR-011)" 0; continue }
            $fx = "`"--tr-fixture=$cap`" `"--tr-assets=$assets`""
        }
        $r = Invoke-Render $be $tag "$fx --tr-path-seconds=30 --tr-seed=8 --tr-budget=default$(if ($vram) { " --tr-vram-mib=$vram" }) --tr-size=$size $PathExtra" 600
        $p = $r.Json.path; $bg = $p.budget
        $ok = $r.Exit -eq 0 -and $bg -and $bg.source -eq "video memory, live" -and $bg.min_mib -ge 768 -and $p.peak_resident_mib -le ($p.budget_mib + 16)
        Add-Case $tag ($(if ($ok) { "PASS" } else { "FAIL" })) "video memory $($r.Json.video_memory_mib) MiB$(if ($r.Json.video_memory_simulated) { ' (simulated)' }); budget $($bg.source): initial $($bg.initial_mib), min $($bg.min_mib), max $($bg.max_mib), final $($bg.final_mib) MiB, $($bg.changes) changes, non-texture applied $($bg.non_texture_applied_mib) MiB; $(PathDetail $r); $(MemDetail $r)" $r.Seconds
    }
}

if ($TextureServer) {
    $cap = Join-Path $local "capture-region-a-v5.jsonl.gz"
    foreach ($mode in @("partial", "whole")) {
        $tag = "texserver-region-a-v5-1920x1080-$mode-vulkan$PathTagSuffix"
        if ($PathFilter -and $tag -notmatch $PathFilter) { continue }
        if (-not ((Test-Path -LiteralPath $cap) -and (Test-Path -LiteralPath $assets))) { Add-Case $tag "SKIP" "local-only capture or assets absent (ADR-011)" 0; continue }
        $r = Invoke-Render "vulkan" $tag "`"--tr-fixture=$cap`" `"--tr-assets=$assets`" --tr-path-seconds=30 --tr-seed=8 --tr-budget=2048m --tr-size=1920x1080 --tr-texture-server=$mode $PathExtra" 600
        $ts = $r.Json.texture_server
        $ok = $r.Exit -eq 0 -and $ts -and $ts.mode -eq $mode -and $ts.body_bytes -gt 0
        Add-Case $tag ($(if ($ok) { "PASS" } else { "FAIL" })) "server: $($ts.requests) requests ($($ts.range_requests) range), $($ts.body_bytes) body bytes for $($ts.textures) textures, bytes/texture p50/p95/max $($ts.bytes_per_texture.p50)/$($ts.bytes_per_texture.p95)/$($ts.bytes_per_texture.max); first image ms p50/p95/max $($ts.first_image_ms.p50)/$($ts.first_image_ms.p95)/$($ts.first_image_ms.max); prefix decodes $($ts.prefix_decodes), more fetches $($ts.more_fetches) (by reason $($ts.prefix_failures | ConvertTo-Json -Compress)); delay $($ts.delay_ms) ms, first request $($ts.first_request), server max concurrent $($ts.max_concurrent); timeline first image p50/p95/max $($ts.timeline.first_image.P50)/$($ts.timeline.first_image.P95)/$($ts.timeline.first_image.Max) ms, requests before first image $($ts.timeline.requests_before_first_image | ConvertTo-Json -Compress) (more than one: $($ts.timeline.more_than_one)); fetcher $($ts.fetcher | ConvertTo-Json -Compress); memory $($ts.memory | ConvertTo-Json -Compress -Depth 4); $(PathDetail $r)" $r.Seconds
    }
}

if ($UploadBench) {
    foreach ($be in $Backends) {
        $tag = "uploadbench-$be"
        $r = Invoke-Render $be $tag "--tr-fixture=none --tr-uploadbench=$UploadRepeats" 600
        $u = $r.Json.uploadbench
        $rows = @($u.sizes | ForEach-Object { $sz = $_; ($sz.paths.PSObject.Properties | ForEach-Object { "$($sz.w)x$($sz.h) $($_.Name) unit p50/p95/max $($_.Value.main_thread.unit.p50)/$($_.Value.main_thread.unit.p95)/$($_.Value.main_thread.unit.max)" }) -join '; ' })
        $ok = $r.Exit -eq 0 -and $r.Json.backend.driver -eq $be -and $u.now_1024_worst_unit_ms -gt 0 -and $u.now_1024_worst_unit_ms -le $u.aim_ms
        Add-Case $tag ($(if ($ok) { "PASS" } else { "FAIL" })) "1024 worst unit (current path) $($u.now_1024_worst_unit_ms) ms (aim $($u.aim_ms)); idle frame p50 $($u.idle_frame_ms.p50) ms; $($rows -join ' || ')$(if ($r.Exit -ne 0) { '; ' + ($r.Json.failures -join ' | ') })" $r.Seconds
    }
}

$script:Measuring = $false   # the hang check is a test
if ($HangCheck) {
    $tag = "hangcheck-d3d12"
    $r = Invoke-Render "d3d12" $tag "--tr-fixture=synthetic-256 --tr-lifecycle=20 --tr-view=server `"--tr-meshes=$meshDir`" `"--tr-textures=$texDir`" --tr-evict-cycles=3 --tr-hang-test=reset:2" 90
    $log = Get-Content -Raw -LiteralPath (Join-Path $out "$tag.log") -ErrorAction SilentlyContinue
    $beatFile = Join-Path $out "$tag.heartbeat"
    $beat = @{}
    if (Test-Path -LiteralPath $beatFile) { foreach ($l in Get-Content -LiteralPath $beatFile) { $kv = $l -split '=', 2; if ($kv.Count -eq 2) { $beat[$kv[0]] = $kv[1] } } }
    $dump = if ($log -match 'GODOT: dump of \S+ pid=\d+ \([^)]*\) -> (.+\.dmp)') { $Matches[1].Trim() } else { $null }
    $frozen = $false
    if ($dump -and (Test-Path -LiteralPath $dump)) {
        $tool = (Get-Command dotnet-dump -ErrorAction SilentlyContinue).Source
        if (-not $tool) { $tool = Join-Path $env:USERPROFILE ".dotnet\tools\dotnet-dump.exe" }
        $stacks = & $tool analyze $dump -c "clrstack -all" -c "exit" 2>&1 | Out-String
        # the stopped call, under the lifecycle's own step, on one thread
        $frozen = ($stacks -split 'OS Thread Id:' | Where-Object { $_ -match 'TestRender\.FreezeForHangTest' -and $_ -match 'TestRender\.Mark' -and $_ -match '<Lifecycle>' }).Count -eq 1
    }
    $ok = $r.Exit -eq 124 -and -not $r.Json -and $dump -and $frozen -and $beat["step"] -eq "reset" -and $beat["cycle"] -eq "2" -and $beat["phase"] -eq "lifecycle"
    Add-Case $tag ($(if ($ok) { "PASS" } else { "FAIL" })) "exit $($r.Exit); dump $(if ($dump) { $dump } else { 'none' }); main thread stopped in FreezeForHangTest under the lifecycle: $frozen; heartbeat phase $($beat['phase']) cycle $($beat['cycle']) step $($beat['step']) frame $($beat['frame'])" $r.Seconds
}

$script:Measuring = $true
if ($Measure) {
    foreach ($be in $Backends) {
        foreach ($f in @("synthetic-1024") + $(if (Test-Path -LiteralPath $regionB) { @($regionB) } else { @() })) {
            $tag = "measure-$(if ($f -eq $regionB) { 'region-b' } else { $f })-$be"
            $r = Invoke-Render $be $tag "`"--tr-fixture=$f`" --tr-measure=$MeasureFrames" 600
            $s = ($r.Json.runs | ForEach-Object { "$($_.view): frame p50/p95/p99 $($_.frame_ms.p50)/$($_.frame_ms.p95)/$($_.frame_ms.p99) ms, gpu p50 $($_.render_gpu_ms.p50), cpu p50 $($_.render_cpu_ms.p50), draws $($_.draw_calls.p50), build $($_.build_ms) ms, rebuild-all $($_.rebuild_all_ms) ms" }) -join " || "
            Add-Case $tag ($(if ($r.Exit -eq 0) { "PASS" } else { "FAIL" })) $s $r.Seconds
        }
    }
}

Exit-Render $(if (@($cases | Where-Object Status -eq "FAIL").Count -gt 0) { 1 } else { 0 })
