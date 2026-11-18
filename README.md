# Reverie Viewer

**Your dreams, in view.**

Reverie Viewer is a modern, open-source viewer for OpenSimulator grids and Second Life. It is a desktop
program built on the Godot engine with a .NET core, and it draws with Vulkan or Direct3D 12.

> **Early preview.** Version 0.1.3 is an early public release. Expect missing features and rough edges, and keep a
> second viewer at hand for anything you rely on.

## Install and first start

### Windows 10 or 11 (64-bit)

1. Download `reverie-viewer-0.1.3-windows-x86_64.zip` from the release page and, if you like, check it against the
   `SHA256SUMS` file published beside it.
2. Unpack the whole zip into a folder of its own. Keep the files together.
3. Double-click `Reverie.exe`.
4. The release is not code-signed yet, so Windows may show **"Windows protected your PC"**. Click **More info**, then
   **Run anyway**. Windows asks this once for each new version.
5. On the login screen, add your grid (see [Add your grid](#add-your-grid) below), then type your name and your
   password.

To watch the viewer's log while it runs, start `Reverie.console.exe` instead.

### Ubuntu 26.04 (and other Linux distributions)

Packages are built for x86_64 (`reverie-viewer-0.1.3-linux-x86_64.tar.gz`) and ARM64
(`reverie-viewer-0.1.3-linux-arm64.tar.gz`).

```
tar -xzf reverie-viewer-0.1.3-linux-x86_64.tar.gz
./reverie-viewer-0.1.3-linux-x86_64/reverie
```

`reverie` checks that a window can open and then starts the viewer (`Reverie.x86_64`, or `Reverie.arm64` on ARM64)
with the same options. If there is no desktop session, or display libraries are missing, it says which and what to
install, instead of starting. On a system that keeps its libraries outside the usual places, set
`REVERIE_SKIP_DISPLAY_CHECK=1` to skip that check.

- You need a graphics driver with **Vulkan 1.3**. On Ubuntu, the Mesa drivers (`mesa-vulkan-drivers`) cover AMD and Intel
  graphics; for NVIDIA, use the proprietary driver from "Additional Drivers". `vulkaninfo --summary` (package
  `vulkan-tools`) shows what your driver offers.
- glibc 2.34 or newer, and the ICU and OpenSSL 3 libraries, which desktop installs normally have.
- A desktop install also has the display libraries the viewer loads. On a minimal install (a server image with a
  desktop added later, for example), install what it needs to start and draw on Ubuntu 26.04 or 26.10 with:

  ```
  sudo apt install mesa-vulkan-drivers libicu78 libssl3t64 libx11-6 libxcursor1 libxext6 libxi6 libxinerama1 libxrandr2 libxrender1 libxkbcommon0 libwayland-client0 libwayland-cursor0 libfontconfig1
  ```

  On other Ubuntu releases the ICU package has another number (`apt-cache search '^libicu[0-9]'` names it). For sound
  the viewer uses PulseAudio (`libpulse0`) or ALSA, which a desktop has; without either it runs silent.
- The viewer runs natively on Wayland and falls back to X11. To force X11, add `--display-driver x11`.
- **Testers wanted:** the Linux builds have not yet been run on a real Linux desktop by the developers. If you try one,
  please report what happened, good or bad, on the issues page.

### Add your grid

Second Life is built in: it is always on the grid list on the login screen. To add an OpenSimulator grid:

1. On the login screen, open the **Grid** list and choose **Add a grid…**.
2. Type the grid's login address (your grid's website gives it, for example `https://grid.example.org:8002/`) and
   choose **Look up**. The viewer asks the grid for its name, sign-up and password pages, and whether it has hypergrid
   and a currency, and shows what it found.
3. If the address starts with `http://`, the window warns that the connection isn't encrypted. Use the `https://`
   address when your grid offers one.
4. Change the name shown if you like, then choose **Add grid**. The grid is now in the list, and the viewer remembers
   it. With **Remember name** ticked at login, it also remembers the names you log in with on that grid.

To change or remove a grid later, open the **Grid** list and choose **Manage grids…**: there you can refresh a grid's
information, edit it or remove it.

### Where it keeps its files

| | Windows | Linux |
|---|---|---|
| Settings | `%APPDATA%\ReverieViewer` | `~/.config/ReverieViewer` |
| Cache | `%LOCALAPPDATA%\ReverieViewer\Cache` | `~/.cache/ReverieViewer` |
| Logs | `%LOCALAPPDATA%\ReverieViewer\Logs` | `~/.local/state/ReverieViewer/logs` |

The viewer writes nothing into the folder it runs from.

## Hardware

The viewer needs a graphics card and driver with **Vulkan 1.3** or **Direct3D 12** (feature level 12_0). There is no
OpenGL mode. On older hardware the viewer refuses to start and says why; on Windows, if Vulkan is too old but Direct3D
12 is new enough, it uses Direct3D 12 by itself. Memory, graphics cards by family and what has been measured:
[Docs/SPECS.md](Docs/SPECS.md).

| | Minimum | Suggested |
|---|---|---|
| System | Windows 10 or 11 (64-bit), or 64-bit Linux (x86_64 or ARM64) with glibc 2.34 or newer | Windows 11 (64-bit) |
| Graphics | Vulkan 1.3, or Direct3D 12 at feature level 12_0 | The newest driver for your card |
| Video memory | 2 GB | 4 GB or more |
| Memory (RAM) | 8 GB | 16 GB |

## What works today

- Logging in to Second Life and to OpenSimulator grids from your own list of grids, with an optional remembered password (kept protected
  for your user account, never as the plain password), the grid's name, colours and links, and its login messages.
  If the connection drops, the viewer reconnects.
- The world: terrain, water, sky and environment settings, prims, sculpties and mesh objects, glow, projected lights,
  and the regions around the one you are in.
- Avatars, your own and others', with their shape, clothing, attachments, mesh bodies and animations, on grids with or
  without server-side baking.
- Walking, flying, sitting, mouselook, the camera controls, teleporting, home, the world map and the mini-map.
- Nearby chat, instant messages, group chat, the friends list, teleport offers and notifications.
- Inventory: browsing, searching, renaming, moving and moving to the trash; making new folders, scripts, notecards,
  gestures, clothing and body parts; outfits, and the appearance editors for shape, skin, hair, eyes and clothing.
- Building: the Build window with its General, Object, Texture, Features and Content tabs, the move, rotate and stretch
  handles, linking and unlinking, PBR materials, a colour picker, undo and redo, and grid snapping. The Content tab
  shows what is inside an object; items can be dragged in and out, and scripts and notecards opened from it.
- The script editor (compile errors, Running, Reset and Mono), the notecard editor (with embedded items) and the
  gesture editor, with gestures played from chat triggers and function keys.
- A command list on Ctrl+Shift+K that finds any window, command or setting, also by its Second Life or Firestorm
  name.
- Uploading images and static mesh models.
- Touching, sitting on, buying and paying objects, and script dialogs.
- World sounds, with interface sounds and a mono option.
- Preferences, including the graphics backend, and windows that remember their places.
- Keyboard shortcuts for the windows that exist (for example Ctrl+I for the inventory).

## Known gaps

What you will notice is missing in this preview:

- Inventory is not complete: no emptying the trash, no copy and paste, no item properties, no Worn tab and no filters.
- No giving or paying people, no profiles and no list of people nearby.
- No landmarks yet (making one, a places list, teleport history).
- No saved chat logs, no snapshots and no particles.
- No rigged mesh, sound or animation upload.
- No land, parcel or estate tools, and no search.
- No groups beyond group chat, notices and your active group and title.
- No music, media or voice.
- No hypergrid travel.
- RLV is only partly supported, and off until you turn it on.
- The Linux builds have started and drawn their login screen with software graphics only; they have not been run on a
  real Linux desktop yet.

## Add-ons

An **animation overrider (AO)** and **RLV** are built in. You can turn each one on or off in the Add-ons window.

Add-ons from outside the viewer are **off by default**. An outside add-on runs with your full permissions on your
computer, so the viewer warns you before you turn one on: only install add-ons from people you trust. Writing an add-on:
`Docs/ADDONS.md`.

## Reporting a problem

Support is best effort. Report problems on the
[issues page](https://github.com/JohnLegionH/reverie-viewer/issues): the bug report form asks for the viewer's version,
your operating system and graphics card, the grid type, what you did, and the newest log file. Read a log before you
attach it: logs leave out passwords, but they name the grid and the regions you visited.

Security problems are reported privately; see [SECURITY.md](SECURITY.md).

## Building from source

See [BUILDING.md](BUILDING.md). Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## Licence

The viewer is licensed under the GNU Lesser General Public License, version 2.1 (LGPL-2.1); the full text is in `LICENSE`.

- The project's own code is licensed LGPL-2.1-or-later: version 2.1 of the licence or, at your option, any later version.
- Code ported from other projects keeps its upstream copyright and licence notice exactly as written, at the top of the
  file, under the licence that notice gives (for example LGPL-2.1-only for code from the Second Life viewer and the
  Firestorm viewer, BSD-3-Clause, MPL-2.0 or MIT). `ThirdPartyLicenses/PORTED-FILES.tsv` lists those files.
- The sample add-on in `samples/HelloAddon` is a template to copy and is licensed 0BSD (`samples/HelloAddon/LICENSE`):
  it may be copied, changed and used for any purpose, with no conditions. The add-on interface it builds against,
  `core/Viewer.Addons.Api`, stays LGPL-2.1-or-later like the rest of the viewer; see `Docs/ADDONS.md`, "Licences".
- Every source file states its licence on an `SPDX-License-Identifier` line.
- The components the viewer bundles or links, and the avatar data folder the shipped build carries, are listed with their
  licences and copyright lines in `THIRD-PARTY-NOTICES.md`; the licence texts are in `ThirdPartyLicenses/`.

This software is not provided or supported by Linden Lab, the makers of Second Life. Second Life and Linden Lab are
trademarks of Linden Research, Inc.
