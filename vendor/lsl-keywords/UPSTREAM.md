<!-- Copyright (c) 2026 Legion Builds -->
<!-- SPDX-License-Identifier: LGPL-2.1-or-later -->
# Script editor keyword files (vendored)

The two data files in this folder are kept unchanged, byte for byte. The script editor reads them (embedded in the
Viewer.Shell assembly) to colour LSL and OSSL words and to show their tooltips.

- `keywords_lsl_default.xml`: the file `indra/newview/app_settings/keywords_lsl_default.xml` of the Second Life viewer by
  Linden Research, Inc., release 26.4 (its `indra/newview/VIEWER_VERSION.txt` reads 7.2.3), upstream repository
  <https://github.com/secondlife/viewer>. It is the LSL syntax file that viewer loads by default
  (`indra/newview/llsyntaxid.cpp`). Licence: Second Life Viewer Source Code, GNU Lesser General Public License, version
  2.1 only (`ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt`). The file carries no notice of its own.
- `scriptlibrary_ossl.xml`: the file `indra/newview/app_settings/scriptlibrary_ossl.xml` of the Firestorm viewer by The
  Phoenix Firestorm Project, Inc., at commit `895f65ab43`, upstream repository <https://github.com/FirestormViewer/phoenix-firestorm>.
  It is the OSSL keyword list Firestorm loads on OpenSim grids (`indra/newview/llpreviewscript.cpp`). Licence: GNU Lesser
  General Public License, version 2.1 only, as the rest of that viewer's source (`ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt`,
  the same text as its `doc/LGPL-license.txt`). The file carries no notice of its own.
- Changes: none. This file was added beside the data files.
