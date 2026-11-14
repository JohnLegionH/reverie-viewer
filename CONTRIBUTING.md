# Contributing to Reverie Viewer

Thank you for wanting to help. Bug reports, testing on hardware we do not have (Linux desktops especially), and pull
requests are all welcome. Support and review are best effort.

## Licence of contributions

- Reverie Viewer is licensed under the GNU Lesser General Public License, version 2.1. **New files you add are
  LGPL-2.1-or-later.**
- Every source file carries an SPDX line near its top. A new file starts with:

  ```
  // Copyright (c) <year> <your name or organisation>
  // SPDX-License-Identifier: LGPL-2.1-or-later
  ```

  (with the comment marker of the file's language).
- Code ported, translated or adapted from another project keeps that project's copyright notice and licence exactly as
  written, at the top of the file, and stays under that licence. Never shorten or reword another holder's notice. Add
  the file to `ThirdPartyLicenses/PORTED-FILES.tsv`, and the project to `THIRD-PARTY-NOTICES.md` if it is new there.
- Only bring in code under a licence that can be combined with LGPL-2.1. Ask in an issue first if you are not sure.
- Do not copy code or comments from the Second Life viewer line by line into new files. Where the viewer should behave as
  the official viewer does, describe the behaviour first and write the code from that description, or port the file
  whole with its notice.

## Tests with every change

- Every change comes with tests. A bug fix starts with a test that fails without the fix.
- Run the suites before you send a change (see [BUILDING.md](BUILDING.md)):
  `powershell -NoProfile -ExecutionPolicy Bypass -File tools\test.ps1`.
- A test that cannot run in your setup must skip with a reason, never pass silently.
- Changes to what is drawn need the matching render cases; say in the pull request if you could not run them.

## Sending a pull request

1. Fork the repository and make a branch for your change.
2. Keep each pull request to one change, with its tests.
3. Write commit messages that say what changed and why, in plain words.
4. Open the pull request against `main`. Describe what you changed, how you tested it, and anything you could not
   test.
5. Expect questions; a change is merged when its tests pass and it has been reviewed.

## What we do not accept

- **No content-theft features**, ever: nothing that copies, exports or captures other people's objects, textures,
  meshes, animations or other content beyond what the viewer needs to show it, and no add-on access to asset data for
  that purpose.
- Nothing that presents the viewer to a grid as another viewer.
- Nothing that sends data anywhere the user did not choose, beyond what the grid they log in to requires.
- No Linden Lab or InWorldz logos or artwork.

## Reporting problems

Use the issue forms on GitHub. Security problems go through private reporting; see [SECURITY.md](SECURITY.md).
