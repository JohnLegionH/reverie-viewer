# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

# =====================================================================================
# package.ps1 - release packages: exports the shipping presets and packs each into a package, with one SHA-256 checksum file.
#   -Exports    the folder the exports are made in, one subfolder per platform (windows-x86_64, linux-x86_64, linux-arm64)
#   -OutDir     the folder the packages and the checksum file are written to (never inside the repository)
#   -Platforms  which platforms to make (default: all three)
#   -NoExport   pack the exports already in -Exports as they are (no import, no export)
#   -GodotExe   the portable Godot 4.7.2 .NET editor (default: as tools\s1.ps1)
#   -DiagDir    passed to every godot.ps1 run
# Package names come from one place, Get-PackageName: <name>-<version>-<platform>. The name is the identity's package name
# (ViewerIdentity.PackageName, core\Viewer.Core\Identity\ViewerIdentity.cs) and the version the core's <Version>
# (core\Viewer.Core\Viewer.Core.csproj); changing either renames every package.
# What it does, in order (any failure stops it before anything is packed; exit 1):
#   0. makes -Exports when it is missing and, before exporting, restores Viewer.sln in NuGet locked mode (a fresh clone needs no steps first);
#   1. exports each platform's shipping preset (export-release) and checks the export's outputs are fresh;
#   1b. refuses (exit 1, before anything is packed) an export that holds the viewer's saved-login file (remembered-logins.dat) or
#      any file with a login hash in it ("$1$" and 32 hex digits): a remembered password must never ship. It names the file, never the
#      hash, and runs again just before packing;
#   2. copies the avatar data folder (NV_CHARACTER_DIR) beside the executable as character\, with its NOTICE.txt, as S-1 does;
#   3. checks the physics native: Windows, every joltc*.dll is the patched build (assert-joltc.ps1); Linux, libjoltc.so is
#      byte-identical to the vendored native\joltc\<rid>\libjoltc.so and no joltc*.dll is there;
#   4. scans every export for the developer tools (ShippingExportsScanTests, NV_SHIP_EXPORTS = -Exports): 4 passed, 0 skipped;
#   4b. checks every export comes with no grid filled in (assert-no-grid-address.ps1) and carries no Linden Lab or InWorldz logo
#      (assert-no-logo.ps1; by content too when NV_CHARACTER_DIR points into the official viewer's source);
#   5. packs: Windows as a .zip, Linux as a .tar.gz with Unix modes (the executable, the launcher "reverie", createdump and every .so 0755, the rest
#      0644), each one folder <package>\ holding the export, LICENSE, THIRD-PARTY-NOTICES.md, ThirdPartyLicenses\ and FIRST-RUN.txt
#      (from tools\release\FIRST-RUN-<os>.txt);
#   6. writes <name>-<version>-SHA256SUMS.txt beside the packages (sha256sum's format: "<hash>  <file>").
# A package that already exists is never written over: the run stops (exit 1) before packing.
# =====================================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exports,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string[]]$Platforms = @("windows-x86_64", "linux-x86_64", "linux-arm64"),
    [switch]$NoExport,
    [string]$GodotExe = "",
    [string]$DiagDir = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "job-guard.ps1")
. (Join-Path $PSScriptRoot "claim-guard.ps1")
$repo = Split-Path -Parent $PSScriptRoot
# the avatar data folder: NV_CHARACTER_DIR when it is set, else the copy of it kept in the repository
if (-not $env:NV_CHARACTER_DIR) { $env:NV_CHARACTER_DIR = Join-Path $repo "vendor\second-life-character" }
$project = Join-Path $repo "host\Viewer.Godot"
$godot = Join-Path $PSScriptRoot "godot.ps1"
if (-not $GodotExe) { $GodotExe = Join-Path ([System.IO.Path]::GetPathRoot($PSScriptRoot)) "tools\godot\4.7.2-mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" }
$diagArgs = if ($DiagDir) { @("-DiagDir", $DiagDir) } else { @() }
$Exports = [IO.Path]::GetFullPath($Exports).TrimEnd('\')
$OutDir = [IO.Path]::GetFullPath($OutDir).TrimEnd('\')
if ($OutDir.StartsWith($repo + "\", [StringComparison]::OrdinalIgnoreCase)) { Write-Output "package: the packages never go inside the repository"; exit 1 }
# the exports folder is made here when it is not there yet, so a fresh clone needs nothing made beforehand (the output folder is made
# just before packing)
New-Item -ItemType Directory -Force -Path $Exports | Out-Null

# ---- the one place package names come from ----
function Get-ReleaseIdentity {
    $src = Get-Content -Raw -LiteralPath (Join-Path $repo "core\Viewer.Core\Identity\ViewerIdentity.cs")
    if ($src -notmatch 'const string PackageName = "([^"]*)"') { throw "PackageName not found in ViewerIdentity.cs" }
    $name = $Matches[1].Trim()
    if ($src -notmatch 'const string ProductName = "([^"]*)"') { throw "ProductName not found in ViewerIdentity.cs" }
    $display = $Matches[1].Trim()
    $csproj = [xml](Get-Content -Raw -LiteralPath (Join-Path $repo "core\Viewer.Core\Viewer.Core.csproj"))
    $version = "$(@($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0])".Trim()
    if (-not $name -or $name -match '[^A-Za-z0-9._-]') { throw "the package name '$name' is not usable in a file name" }
    if (-not $version -or $version -match '[^0-9A-Za-z.+-]') { throw "the version '$version' is not usable in a file name" }
    [pscustomobject]@{ Name = $name; Display = $display; Version = $version }
}
function Get-PackageName([string]$platform) { $id = Get-ReleaseIdentity; "$($id.Name)-$($id.Version)-$platform" }
# the folder name the viewer keeps its settings and logs under (ViewerIdentity.DataFolderName), for the first-run text
function Get-DataName {
    $src = Get-Content -Raw -LiteralPath (Join-Path $repo "core\Viewer.Core\Identity\ViewerIdentity.cs")
    if ($src -notmatch 'const string DataFolderName = "([^"]+)"') { throw "DataFolderName not found in ViewerIdentity.cs" }
    $Matches[1]
}

$targets = @{
    "windows-x86_64" = @{ Preset = "windows-desktop"; Exe = "Reverie.exe"; Os = "windows"; Rid = "win-x64"; Arch = "x86_64" }
    "linux-x86_64"   = @{ Preset = "linux-x86_64"; Exe = "Reverie.x86_64"; Os = "linux"; Rid = "linux-x64"; Arch = "x86_64" }
    "linux-arm64"    = @{ Preset = "linux-arm64"; Exe = "Reverie.arm64"; Os = "linux"; Rid = "linux-arm64"; Arch = "arm64" }
}
foreach ($p in $Platforms) { if (-not $targets.ContainsKey($p)) { Write-Output "package: unknown platform $p"; exit 1 } }
$id = Get-ReleaseIdentity
Write-Output "package: $($id.Name) $($id.Version), platforms $($Platforms -join ', ')"
$planned = @($Platforms | ForEach-Object { Join-Path $OutDir ((Get-PackageName $_) + $(if ($targets[$_].Os -eq "windows") { ".zip" } else { ".tar.gz" })) })
$sumsFile = Join-Path $OutDir "$($id.Name)-$($id.Version)-SHA256SUMS.txt"
$taken = @(($planned + $sumsFile) | Where-Object { Test-Path -LiteralPath $_ })
if ($taken.Count -gt 0) { Write-Output "package: STOPPED, already there (never written over): $($taken -join ', ')"; exit 1 }

# ---- 1. export ----
if (-not $NoExport) {
    Assert-ClaimGranted
    if (-not (Test-Path -LiteralPath $GodotExe)) { Write-Output "package: no Godot editor at $GodotExe"; exit 1 }
    $env:RestoreLockedMode = "true"
    # a fresh clone has never been restored: the export builds the add-on projects too, and fails (NETSDK1004) without their assets
    # files, so the whole solution is restored first, in NuGet locked mode like the export's own restore
    $ErrorActionPreference = "Continue"
    $restoreOut = & dotnet restore (Join-Path $repo "Viewer.sln") --locked-mode -nodeReuse:false 2>&1
    $restoreExit = $LASTEXITCODE
    $ErrorActionPreference = "Stop"
    if ($restoreExit -ne 0) { $restoreOut | Select-Object -Last 20 | ForEach-Object { Write-Output "    $_" }; Write-Output "package: restore failed (exit $restoreExit)"; exit 1 }
    Write-Output "  restore Viewer.sln (locked mode) ok"
    & powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $GodotExe -TimeoutSeconds 300 -LogFile (Join-Path $Exports "import.log") `
        -ArgLine "--headless --path `"$project`" --import" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Output "package: import failed (exit $LASTEXITCODE)"; exit 1 }
    foreach ($p in $Platforms) {
        $t = $targets[$p]
        $dir = Join-Path $Exports $p
        if (Test-Path -LiteralPath $dir) { Write-Output "package: STOPPED, the export folder $dir is already there (an export is made into a new folder)"; exit 1 }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $start = (Get-Date).AddSeconds(-2)
        & powershell -NoProfile -ExecutionPolicy Bypass -File $godot @diagArgs -Exe $GodotExe -TimeoutSeconds 600 -LogFile (Join-Path $Exports "export-$p.log") `
            -ArgLine "--headless --path `"$project`" --export-release $($t.Preset) `"$dir\$($t.Exe)`"" | Out-Null
        $exit = $LASTEXITCODE
        $data = @(Get-ChildItem -LiteralPath $dir -Directory -Filter "data_Viewer.Godot_*")
        $fresh = @("$dir\$($t.Exe)", "$dir\Reverie.pck") + @($data | ForEach-Object { Join-Path $_.FullName "Viewer.Godot.dll" })
        $stale = @($fresh | Where-Object { -not (Test-Path -LiteralPath $_) -or (Get-Item -LiteralPath $_).LastWriteTime -lt $start })
        if ($exit -ne 0 -or $data.Count -ne 1 -or $stale.Count -gt 0) { Write-Output "package: export $p failed (exit $exit; data folders $($data.Count); missing or stale: $($stale -join ', '))"; exit 1 }
        Write-Output "  export  $p  ok"
    }
}

# ---- 1b. no saved login data in any export ----
# The viewer keeps a remembered password only as a protected login hash in the user's own settings folder; a test run that pointed
# that folder into an export, or a copied settings folder, must never reach a package.
function Assert-NoSavedLogin {
    $savedName = "remembered-logins.dat"
    $hashPattern = [regex]'\$1\$[0-9a-fA-F]{32}'
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($p in $Platforms) {
        $dir = Join-Path $Exports $p
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        foreach ($f in @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force)) {
            $rel = $f.FullName.Substring($Exports.Length + 1)
            if ($f.Name -ieq $savedName) { $found.Add("$rel (the saved-login file)"); continue }
            # read as single bytes, so a hash inside a binary file is found too; only the file's name is printed, never what matched
            $text = [System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($f.FullName))
            if ($hashPattern.IsMatch($text)) { $found.Add("$rel (holds a login hash)") }
        }
    }
    if ($found.Count -gt 0) {
        Write-Output "package: STOPPED, saved login data found in the export (a remembered password must never ship):"
        foreach ($x in $found) { Write-Output "  $x" }
        exit 1
    }
    Write-Output "  remembered password  no file and no login hash in $($Platforms -join ', ')"
}
Assert-NoSavedLogin

# ---- 2. avatar data folder ----
$charSrc = $env:NV_CHARACTER_DIR
if (-not $charSrc -or -not (Test-Path -LiteralPath $charSrc -PathType Container)) { Write-Output "package: NV_CHARACTER_DIR is unset or not a folder: no avatar data folder to ship"; exit 1 }
$charFiles = @(Get-ChildItem -LiteralPath $charSrc -File -Force)
foreach ($p in $Platforms) {
    $dest = Join-Path (Join-Path $Exports $p) "character"
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in $charFiles) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $dest $f.Name) -Force }
    Copy-Item -LiteralPath (Join-Path $repo "ThirdPartyLicenses\character-NOTICE.txt") -Destination (Join-Path $dest "NOTICE.txt") -Force
    $bad = @($charFiles | Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $dest $_.Name)).Hash })
    $extra = @(Get-ChildItem -LiteralPath $dest -Force | Where-Object { $_.Name -ne "NOTICE.txt" -and -not (Test-Path -LiteralPath (Join-Path $charSrc $_.Name)) })
    if ($bad.Count -gt 0 -or $extra.Count -gt 0) { Write-Output "package: character folder of $p differs from its source"; exit 1 }
    Write-Output "  avatar  $p  character\ holds $($charFiles.Count) files and NOTICE.txt"
}

# ---- 3. physics native ----
foreach ($p in $Platforms) {
    $t = $targets[$p]
    $dir = Join-Path $Exports $p
    if ($t.Os -eq "windows") {
        $g = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "assert-joltc.ps1") -Path $dir
        if ($LASTEXITCODE -ne 0) { Write-Output "package: $p joltc guard failed: $(($g | Select-Object -Last 1))"; exit 1 }
    } else {
        $vendored = Join-Path $repo "native\joltc\$($t.Rid)\libjoltc.so"
        $libs = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter "libjoltc*.so")
        $dlls = @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter "joltc*.dll")
        $want = (Get-FileHash -LiteralPath $vendored).Hash
        $wrong = @($libs | Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -ne $want })
        if ($libs.Count -ne 1 -or $wrong.Count -gt 0 -or $dlls.Count -gt 0) {
            Write-Output "package: $p physics native: libjoltc.so copies $($libs.Count) (not the vendored build: $($wrong.Count)), joltc*.dll $($dlls.Count)"; exit 1
        }
    }
    Write-Output "  joltc   $p  ok"
}

# ---- 4. developer tools absent from every shipping export ----
$env:NV_SHIP_EXPORTS = $Exports
$ErrorActionPreference = "Continue"   # a failing test writes to stderr; its result is read from the summary and the exit code
$scan = & dotnet test (Join-Path $repo "core\Viewer.Core.Tests\Viewer.Core.Tests.csproj") -nodeReuse:false -p:UseSharedCompilation=false `
    --filter "FullyQualifiedName~ShippingExportsScanTests" 2>&1
$scanExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
$env:NV_SHIP_EXPORTS = $null
$scanLine = "$($scan | Select-String -Pattern 'Passed!|Failed!' | Select-Object -Last 1)".Trim()
if ($scanExit -ne 0 -or $scanLine -notmatch "Passed:\s+4," -or $scanLine -notmatch "Skipped:\s+0,") {
    $scan | Select-String -Pattern "Failed |Assert|Error Message|Expected|Actual" | Select-Object -First 20 | ForEach-Object { Write-Output "    $_" }
    Write-Output "package: STOPPED, the developer-tools scan did not pass: $scanLine"; exit 1
}
Write-Output "  scan    every export: no developer tools ($scanLine)"

# ---- 4b. no grid filled in, no Linden Lab or InWorldz logo ----
$official = if ($env:NV_CHARACTER_DIR) { Split-Path -Parent ([IO.Path]::GetFullPath($env:NV_CHARACTER_DIR).TrimEnd([char]92)) } else { "" }
foreach ($p in $Platforms) {
    $dir = Join-Path $Exports $p
    $g = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "assert-no-grid-address.ps1") -Path $dir
    if ($LASTEXITCODE -ne 0) { $g | ForEach-Object { Write-Output "    $_" }; Write-Output "package: STOPPED, $p comes with a grid filled in"; exit 1 }
    Write-Output "  grid    $p  $(($g | Select-Object -Last 1))"
    $logoArgs = @("-Path", $dir)
    if ($official -and (Test-Path -LiteralPath (Join-Path $official "icons"))) { $logoArgs += @("-Reference", $official) }
    $l = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "assert-no-logo.ps1") @logoArgs
    if ($LASTEXITCODE -ne 0) { $l | ForEach-Object { Write-Output "    $_" }; Write-Output "package: STOPPED, $p carries a Linden Lab or InWorldz logo"; exit 1 }
    Write-Output "  logo    $p  $(($l | Select-Object -Last 1))"
}

# ---- 5. pack ----
Assert-NoSavedLogin   # again, over everything that is about to be packed
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stageRoot = Join-Path $Exports "stage"
$dataName = Get-DataName
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Copy-Tree([string]$from, [string]$to) {
    foreach ($f in Get-ChildItem -LiteralPath $from -Recurse -File -Force) {
        $rel = $f.FullName.Substring($from.Length + 1)
        if ($rel -like "appdata\*" -or $rel -like "s1-result*") { continue }   # a run's own state is not part of the export
        $target = Join-Path $to $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $f.FullName -Destination $target -Force
    }
}
function Get-MtreePath([string]$s) {   # mtree escapes: backslash, space, and the characters it treats as separators
    ($s -replace '\\', '\134' -replace ' ', '\040' -replace "`t", '\011' -replace '#', '\043' -replace '=', '\075')
}
foreach ($p in $Platforms) {
    $t = $targets[$p]
    $pkg = Get-PackageName $p
    $stage = Join-Path $stageRoot $pkg
    if (Test-Path -LiteralPath $stage) { Write-Output "package: STOPPED, the staging folder $stage is already there"; exit 1 }
    Copy-Tree (Join-Path $Exports $p) $stage
    Copy-Item -LiteralPath (Join-Path $repo "LICENSE") -Destination (Join-Path $stage "LICENSE")
    Copy-Item -LiteralPath (Join-Path $repo "THIRD-PARTY-NOTICES.md") -Destination (Join-Path $stage "THIRD-PARTY-NOTICES.md")
    Copy-Tree (Join-Path $repo "ThirdPartyLicenses") (Join-Path $stage "ThirdPartyLicenses")
    $text = (Get-Content -LiteralPath (Join-Path $PSScriptRoot "release\FIRST-RUN-$($t.Os).txt") -Encoding UTF8 | Where-Object { $_ -notmatch '^# ' }) -join "`n"
    $text = $text.Replace("{NAME}", $id.Display).Replace("{VERSION}", $id.Version).Replace("{PACKAGE}", $pkg).Replace("{EXE}", $t.Exe).Replace("{ARCH}", $t.Arch).Replace("{DATA_NAME}", $dataName)
    if ($t.Os -eq "windows") { $text = $text.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText((Join-Path $stage "FIRST-RUN.txt"), $text + $(if ($t.Os -eq "windows") { "`r`n" } else { "`n" }), [Text.UTF8Encoding]::new($false))
    if ($t.Os -eq "linux") {
        # the launcher (tools\release\reverie-linux.sh): says what is missing when no window can open, else starts the engine
        $launcher = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "release\reverie-linux.sh")).Replace("`r`n", "`n")
        [IO.File]::WriteAllText((Join-Path $stage "reverie"), $launcher, [Text.UTF8Encoding]::new($false))
    }
    if ($t.Os -eq "windows") {
        $out = Join-Path $OutDir "$pkg.zip"
        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $out, [IO.Compression.CompressionLevel]::Optimal, $true)
    } else {
        $out = Join-Path $OutDir "$pkg.tar.gz"
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("#mtree")
        $lines.Add("$(Get-MtreePath $pkg) type=dir mode=0755 uid=0 gid=0")
        foreach ($d in Get-ChildItem -LiteralPath $stage -Recurse -Directory -Force | Sort-Object FullName) {
            $lines.Add("$(Get-MtreePath ($pkg + '/' + $d.FullName.Substring($stage.Length + 1).Replace('\', '/'))) type=dir mode=0755 uid=0 gid=0")
        }
        foreach ($f in Get-ChildItem -LiteralPath $stage -Recurse -File -Force | Sort-Object FullName) {
            $rel = $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $exec = ($rel -eq $t.Exe) -or ($rel -eq "reverie") -or ($f.Name -eq "createdump") -or ($f.Name -like "*.so") -or ($f.Name -like "*.so.*")
            $mode = if ($exec) { "0755" } else { "0644" }
            $lines.Add("$(Get-MtreePath ($pkg + '/' + $rel)) type=file mode=$mode uid=0 gid=0 contents=$(Get-MtreePath $f.FullName.Replace('\', '/'))")
        }
        $mtree = Join-Path $stageRoot "$pkg.mtree"
        [IO.File]::WriteAllLines($mtree, $lines, [Text.UTF8Encoding]::new($false))
        $tar = Join-Path $env:SystemRoot "System32\tar.exe"
        & $tar -czf $out --format=pax "@$($mtree.Replace('\', '/'))"
        if ($LASTEXITCODE -ne 0) { Write-Output "package: tar failed for $p (exit $LASTEXITCODE)"; exit 1 }
    }
    Write-Output ("  package {0}  {1:N1} MiB" -f (Split-Path -Leaf $out), ((Get-Item -LiteralPath $out).Length / 1MB))
}

# ---- 6. checksums ----
$sums = foreach ($o in $planned) { "{0}  {1}" -f (Get-FileHash -LiteralPath $o -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $o) }
[IO.File]::WriteAllText($sumsFile, (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
Write-Output "  sums    $(Split-Path -Leaf $sumsFile)"
$sums | ForEach-Object { Write-Output "    $_" }
Write-Output "package: done"
exit 0
