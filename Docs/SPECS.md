<!-- Copyright (c) 2026 Legion Builds -->
<!-- SPDX-License-Identifier: LGPL-2.1-or-later -->

# System specs

What Reverie Viewer needs to run, and what it runs well on. Each figure is marked:

- **Floor:** enforced by the viewer's own start check. Below it the viewer does not start.
- **Measured:** the developers measured it on real hardware. The machine is named.
- **Expected:** worked out from the viewer's own rules or a measurement, but not yet measured on a machine like that.

This is an early preview, measured on few machines. Reports from other hardware are welcome on the issues page.

## The floor (enforced)

| | What the viewer needs |
|---|---|
| Graphics API | **Vulkan 1.3** or newer, or **Direct3D 12 at feature level 12_0** or newer |
| OpenGL | Not supported. There is no OpenGL mode. |

At start the viewer reads what the graphics driver reports and holds it to this floor. On Windows it prefers Vulkan; if
the Vulkan driver is older than 1.3 and Direct3D 12 meets the floor, it starts on Direct3D 12 instead, by itself. When
neither meets the floor it does not start, and it says which version the computer offers and that updating the graphics
driver may help.

An old driver can be the whole problem. One measured laptop (NVIDIA GeForce GTX 1660 Ti, driver from 2021) reported
Vulkan 1.2.175, below the floor, and ran on Direct3D 12. Vulkan 1.3 support often comes with a newer driver for the
same card.

## Operating systems

| System | Status |
|---|---|
| Windows 11, 64-bit (x86_64) | **Measured:** all of the measurements below. |
| Windows 10, 64-bit (x86_64) | **Expected:** same program as on Windows 11; not yet measured. |
| Ubuntu 26.04 LTS and 26.10, x86_64 | **Measured, software graphics only:** the package starts and draws its login screen with Mesa's software Vulkan (llvmpipe) on an X11 virtual display. Not yet run on a real graphics card or on a Wayland desktop. |
| Other Linux distributions, x86_64 | **Expected:** glibc 2.34 or newer, a Vulkan 1.3 driver, and the libraries the README lists. Not yet tried. |
| Linux ARM64 (aarch64) | **Packaged, not yet started** on a display. |
| macOS | Not available. |

## Memory (RAM)

| | Figure | Status |
|---|---|---|
| Minimum | 8 GB | **Expected:** in the scenes measured below the viewer's peak working set was at most 2.0 GiB and its private bytes at most 2.9 GiB, which leaves room for the system on an 8 GB computer. Not yet tried on one. |
| Suggested | 16 GB | **Measured:** a laptop with 15.4 GiB of usable memory ran every scene below, with the viewer's peak well under half of it. |

What the viewer used (Windows 11, 1920x1080, earlier builds of this preview):

| Scene | Peak working set | Peak private bytes | Machine |
|---|---|---|---|
| A furnished region, flown through for 30 s (Direct3D 12) | 1.0 to 1.2 GiB | 1.4 to 1.6 GiB | laptop and desktop below |
| 2,000 to 6,000 objects in view (Direct3D 12) | 0.9 GiB | 0.9 to 1.3 GiB | laptop and desktop below |
| 25 avatars standing around (Direct3D 12) | 1.8 to 1.9 GiB | 2.6 to 2.9 GiB | laptop and desktop below |
| 100 avatars (Vulkan, worst run) | 2.0 GiB | 2.8 GiB | desktop below |
| A live region over 100 minutes, logging out and in every 10 minutes (Vulkan) | | 1.3 to 1.7 GiB; no steady growth the run could tell apart from noise | desktop below |

## Graphics memory (video memory)

| | Figure | Status |
|---|---|---|
| Minimum | 2 GB | **Expected:** the viewer keeps its textures within a budget that never goes below 768 MiB; its other video memory measured 156 to 199 MiB in a live region at 1920x1080 and at most 459 MiB in a test scene at 3840x2160. |
| Suggested | 4 GB or more | **Expected:** the texture budget is half the card's memory, less a reserve for other programs (512 MiB, or a fifth of that half if larger) and less the viewer's other video memory, so a 4 GB card gives it room above the 768 MiB minimum. |

On Linux the viewer cannot read the card's memory yet and uses a texture budget of 768 MiB. On any system the budget
can be set by hand: start the viewer with `-- --texture-budget-mib=<MiB>` after the program's name (the lone `--`
comes first).

## Processor

No minimum is set yet. **Measured:** in steady viewing (a region, busy views, 25 standing avatars), 95 % of frames took
under 16.7 ms (60 frames a second) on both machines below. On the laptop, 25 avatars arriving at once took longer per frame
than that (35 ms at the 95th percentile), so a crowd arriving is where a slower processor shows first.

## Machines measured

| | Desktop | Laptop |
|---|---|---|
| Processor | Intel Core i7-12700KF (12 cores, 20 threads) | AMD Ryzen 7 4800H (8 cores, 16 threads) |
| Memory | 64 GB | 15.4 GiB usable |
| Graphics | NVIDIA GeForce RTX 5060 Ti, 16 GB | NVIDIA GeForce GTX 1660 Ti (laptop) |
| Backends | Vulkan 1.4 and Direct3D 12 | Direct3D 12 only (its driver offered Vulkan 1.2) |
| System | Windows 11 | Windows 11 |

## Graphics cards by family

The floor is about the driver as much as the card: install the newest driver your card's maker offers. The tables
below are what the makers publish. The viewer checks the real figure at every start, so if in doubt, try it: it says
plainly when a card is below the floor.

**Vulkan 1.3 or newer, as the makers list it:**

| Maker | Families listed with Vulkan 1.3 or newer | Source |
|---|---|---|
| NVIDIA | Turing and every later architecture (GeForce GTX 16 series, RTX 20 series and newer) are listed for Vulkan 1.4. | [NVIDIA Vulkan driver support](https://developer.nvidia.com/vulkan-driver) |
| AMD | The first Vulkan 1.3 driver (Adrenalin 22.1.2, Windows) lists Radeon RX 500, RX Vega, Radeon VII, RX 5000 and RX 6000 series; later series came after it. | [AMD Adrenalin 22.1.2 with Vulkan 1.3](https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-22-1-2-VULKAN-1-3.html) |
| Intel | Arc A and B series, Iris Xe and Iris Xe MAX, Iris Plus, UHD Graphics of 10th generation Core processors and newer, UHD Graphics 610/620/630, HD Graphics 520/530 and Iris Plus 540/550/580. Intel lists some other 6th to 8th generation parts (HD Graphics 500 to 515 and 610 to 630, Iris Plus 640 to 655) at Vulkan 1.2, below the floor, and HD Graphics 5500/6000 and older without Vulkan. | [Intel graphics API support](https://www.intel.com/content/www/us/en/support/articles/000005524/graphics-drivers.html) |

**Direct3D 12:** the makers' tables say "DirectX 12" without the feature level the viewer needs (12_0). Some parts
listed for DirectX 12 run it at a lower level (11_0 or 11_1) and are below the floor. The viewer's start check reads the
level the card reports.

On Linux, the Mesa drivers (`mesa-vulkan-drivers` on Ubuntu) provide Vulkan for AMD and Intel graphics, and NVIDIA's
proprietary driver for NVIDIA cards. `vulkaninfo --summary` (package `vulkan-tools`) shows the Vulkan version a driver
offers.
