<!-- Copyright (c) 2026 Legion Builds -->
<!-- SPDX-License-Identifier: LGPL-2.1-or-later -->
# Avatar data folder (vendored)

The 96 data files in this folder are the files of `indra/newview/character/` of the Second Life viewer by Linden
Research, Inc., unchanged, byte for byte: the avatar definitions (`avatar_lad.xml`, `avatar_skeleton.xml` and the
other `*.xml`), the body meshes (`*.llm`) and the pictures the bakes are drawn with (`*.tga`). The viewer reads them to
draw avatar bodies; the build scripts copy them beside the exported program as `character/`.

- Source: the Second Life viewer source, release 26.4 (its `indra/newview/VIEWER_VERSION.txt` reads 7.2.3), upstream
  repository <https://github.com/secondlife/viewer>, commit `e960123230` (abbreviated, as recorded for the other files
  this project ports from that release).
- Licence: see `NOTICE.txt` in this folder. The definitions and meshes are Second Life Viewer Source Code under the GNU
  Lesser General Public License, version 2.1 only; the pictures are Second Life viewer artwork under the Creative
  Commons Attribution-Share Alike 3.0 notice reproduced there. Both are also listed in `THIRD-PARTY-NOTICES.md`.
- Changes: none. `NOTICE.txt` and this file were added beside the data files.
