# Building Reverie Viewer from source

Everything a build needs is in this repository, apart from three tools you install yourself: Git, the .NET SDK and the
Godot engine (.NET edition) with its export templates. The avatar data folder the viewer draws bodies from is included,
in `vendor/second-life-character/` (see the `UPSTREAM.md` there).

## What you need

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0 (10.0.400 or later) | <https://dotnet.microsoft.com/download> |
| Godot, .NET edition | **4.7.2** exactly | "Godot Engine - .NET" from <https://godotengine.org/download/archive/4.7.2-stable/> |
| Godot export templates | 4.7.2 .NET | the `.tpz` file on the same download page |
| Git | any recent | |
| Windows PowerShell | 5.1 (part of Windows) | for the scripts in `tools/` |

A graphics card with Vulkan 1.3 or Direct3D 12 is needed to run the viewer and its start-up check, as for users.

## Windows

### 1. Get the source

```
git clone https://github.com/JohnLegionH/reverie-viewer.git
```

The commands below are run from the folder the clone made (`reverie-viewer`).

### 2. Put Godot where the scripts look for it

The scripts use a portable Godot install in the folder `\tools\godot\4.7.2-mono\` at the top of **the drive the
repository is on**:

1. Unpack `Godot_v4.7.2-stable_mono_win64.zip` into `\tools\godot\4.7.2-mono\` on that drive, so that the editor is at
   `\tools\godot\4.7.2-mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe`.
2. In that `Godot_v4.7.2-stable_mono_win64` folder, create an empty file named `_sc_`. This puts Godot in
   self-contained mode: it keeps its settings and templates in `editor_data\` beside the editor instead of in your
   profile.
3. The export templates file (`Godot_v4.7.2-stable_mono_export_templates.tpz`) is a zip archive. Unpack the files inside
   its `templates` folder into `editor_data\export_templates\4.7.2.stable.mono\` in the same folder.

To keep Godot somewhere else, pass its console executable to the scripts with `-GodotExe <path>`
(`tools\s1.ps1`, `tools\package.ps1`) or `-Exe <path>` (`tools\godot.ps1`).

### 3. Build and run the unit tests

```
dotnet test core\Viewer.Core.Tests\Viewer.Core.Tests.csproj
```

Some tests need inputs that are not in the repository (captures from a running grid, reference renders); they report
themselves as skipped, with the reason.

### 4. Run every suite

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\test.ps1
```

This runs the .NET suites and **S-1**, the export check: it builds the Godot project, exports the shipping build
(`host\Viewer.Godot\build\Reverie.exe`) and a development build (`host\Viewer.Godot\build\dev\`), and starts the exported
build to check it. Results go to `tools\test-results\` (JUnit XML). While S-1 runs, viewer windows open and close by
themselves; leave them alone.

S-1 on its own, once the solution has been restored and built (step 4 does that, or `dotnet build Viewer.sln`):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\s1.ps1
```

### 5. Run the viewer you built

```
host\Viewer.Godot\build\Reverie.exe
```

### 6. Release packages

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\package.ps1 -Exports <empty folder for the exports> -OutDir <folder for the packages>
```

Both folders must be outside the repository. The script exports the shipping build for Windows (x86_64) and Linux
(x86_64 and ARM64), checks each export (no developer tools, no saved login, no grid filled in, no third-party logos, the
physics library), packs each into `reverie-viewer-<version>-<platform>` (`.zip` for Windows, `.tar.gz` for Linux) and
writes a SHA-256 checksum file beside them.

## Linux

The viewer's code builds and its unit tests run with the .NET SDK on Linux, and Godot 4.7.2 exports the Linux presets
from a Linux editor. These steps have not yet been checked on a Linux machine by the developers; reports are welcome.

```
git clone https://github.com/JohnLegionH/reverie-viewer.git
dotnet test reverie-viewer/core/Viewer.Core.Tests/Viewer.Core.Tests.csproj
```

Tests that drive Windows PowerShell scripts or Windows-only features fail or skip on Linux.

To export the Linux build, install the Godot 4.7.2 .NET editor for Linux and its export templates (from the editor:
Editor > Manage Export Templates > Install From File, choosing the `.tpz`), then:

```
godot --headless --path reverie-viewer/host/Viewer.Godot --export-release linux-x86_64 build/linux-x86_64/Reverie.x86_64
cp -r reverie-viewer/vendor/second-life-character reverie-viewer/host/Viewer.Godot/build/linux-x86_64/character
```

(`godot` is the editor's executable; use the preset `linux-arm64` and the file `build/linux-arm64/Reverie.arm64` on
ARM64.) The second line copies the avatar data folder beside the program, where the viewer looks for it.

## Settings for the scripts

| Variable | What it does |
|---|---|
| `NV_CHARACTER_DIR` | the avatar data folder; default `vendor\second-life-character` |
| `NV_FIXTURES_LOCAL` | a folder of local test inputs (captures, references); tests that need them skip without it |
| `NV_TEMP_ROOT` | a folder for the scripts' temporary files instead of `%TEMP%` |
