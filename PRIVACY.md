<!-- Copyright (c) 2026 Legion Builds. SPDX-License-Identifier: LGPL-2.1-or-later -->
# Privacy policy

Reverie Viewer is a program on your computer. Its developers run no server for it: the viewer sends nothing to them, and they
receive nothing from it. It talks only to the grid you log into, and, when you ask, to GitHub.

## What goes to the grid you log into

- **When you choose a grid** (other than Second Life), the viewer asks that grid for its public grid information, and the logo,
  picture and news it names.
- **When you log in:** your account name; your password, sent as a hash (MD5), never as typed; the start location; the
  viewer's name ("Reverie Viewer") and version; your operating system's name and version; `mac`, a hash (MD5) of one of this
  computer's network card addresses; `id0`, a hashed device id; and the two-factor token or the grid's two-factor hash when
  the grid asks for one. These are the same fields the official Second Life viewer sends.
- **While you are logged in:** what any viewer sends to use the world: your movements, chat, messages, inventory and object
  requests, uploads you choose to make, and requests for textures, meshes, sounds, map tiles and other content.

## What goes to Linden Lab on Second Life

On Second Life, the grid is run by Linden Lab, so everything in the section above goes to Linden Lab, including the hashed
device ids (`mac` and `id0`).

The viewer also sends Linden Lab the **viewer statistics** the official viewer sends, as Linden Lab's Third-Party Viewer Policy
requires. They go to the region's `ViewerStats` capability when you arrive, every 5 minutes, and when you log out. They
are sent on Second Life only, never to other grids. They hold:

- how long the viewer has run and been in the foreground, frames per second, the viewer's version and language, the
  region's frame rate, how many avatars are in view, network ping, distance travelled, regions visited and memory use;
- this computer's memory, operating system, processor, address size and number of processor cores; `mac_address` and
  `serial_number`, which are the same two hashes the login sends as `mac` and `id0` (never a network card address itself);
  and the graphics card's name, maker and driver;
- how much was downloaded, network packet counts and failures, whether the inventory loaded, and your display-name settings;
- the id of your current session, which the grid gave the viewer at login.

Linden Lab's own privacy policy covers what it does with this.

## Anything else

- **Check for updates**, only when you choose it from the menu, asks `api.github.com` for the project's latest release.
  GitHub sees your IP address and the request.
- **Links** (a grid's help, terms, sign-up or password page, a link in chat or a notecard, this policy) open in your web
  browser only when you click them.
- Nothing else. No analytics, no crash reports, no background checks. Saved passwords and two-factor hashes stay on this
  computer, protected for your user account.
- **Add-ons you install yourself** can do more than Reverie itself does, including sending data elsewhere; this policy covers
  Reverie only, not add-ons.

## Support

Questions and problems: https://github.com/JohnLegionH/reverie-viewer/issues
