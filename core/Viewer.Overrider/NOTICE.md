# Viewer.Overrider notice

This assembly is the animation overrider: it reads the animation sets a user keeps in the inventory the way the Firestorm viewer stores them (the `#Firestorm` folder, its `#AO` folder, a folder for each set, a folder for each state, and links to animations) and plays them as Firestorm's overrider does. It is ported from the Firestorm viewer source (commit 895f65ab43, after the release tag Firestorm_Beta_7.2.5.81383) and is licensed under the GNU Lesser General Public License, version 2.1 only, the licence of that source. Nothing else in this repository changes licence. The licence text is `ThirdPartyLicenses/LGPL-2.1-secondlife-viewer.txt` (the same text as the Firestorm source's `doc/LGPL-license.txt`).

The rest of the repository is licensed under the same licence (`LICENSE`), the project's own code under version 2.1 or any later version; the sentence above means that this assembly's licence does not change the licence of any other file.

Every ported file keeps the source files' notices ("Copyright (C) 2011, Zi Ree @ Second Life") word for word at its top, followed by the line "Adapted 2026 by Legion Builds" and what changed.

| File here | Firestorm viewer source files |
|---|---|
| `AoEngine.cs` | `indra/newview/aoengine.cpp`; the calls that feed it follow `indra/newview/llvoavatar.cpp` (`startMotion`, `stopMotion`, `processAnimationStateChanges`) |
| `AoSet.cs` | `indra/newview/aoset.h`, `indra/newview/aoset.cpp` |
| `AoInterfaces.cs` | none (new interfaces) |

What differs from the source: the inventory is only read (the folders are not made when they are missing, items that are not animation links are passed over instead of being moved to Lost and Found, sort orders are not written back); sets are not edited, imported from notecards or saved; the step each state played last is not kept between runs; an animation is stopped by asking the region, not also on the avatar directly; timers are values read against a clock the caller passes in.
