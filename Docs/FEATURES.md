# Reverie Viewer: what you can do

A one-page cheat sheet for the current preview. It lists what works today and where to find it. For step-by-step help, see
the [user guide](guide/README.md). The list grows as the viewer does.

Menus are on the top bar: the **Reverie** menu (About, Preferences, Add-ons, Check for updates, Log out, Quit), then **Me**,
**Communicate**, **World**, **Build**, **Content** and **Help**. The dock at the bottom has buttons for the most-used windows.
Press **Ctrl+Shift+K** to search every window, command and setting by name, also by its Second Life or Firestorm name.

## Getting in

- Log in to Second Life or an OpenSimulator grid. Login screen: Grid, Name, Password, then **Log in**.
- Add or manage grids. Login screen: Grid list > **Add a grid…** or **Manage grids…**.
- Remember your name, or (where this computer can protect it) your password. Login screen: **Remember name**, **Remember password**.
- Choose where you arrive: last location, home, or a named region. Login screen: **Start at**.
- Reconnect after a dropped connection: the **Logged out** window has a **Reconnect** button. It does not start by itself.
- The reconnect notice, the login dialogs (terms, certificates, updates), the four upload windows, the Sound Tuner, About and Licences draw in the same new look as the other windows.

## Moving and the camera

- Walk and turn: W A S D or the arrow keys. Shift+A or Shift+D slides sideways. E or Page Up jumps (flying: up); C or Page Down crouches (flying: down).
- Fly or land: F or Home. Stop moving: Space.
- Run: Ctrl+R switches always-run; tap a direction twice and hold to run (Preferences > Move & View).
- Sit on an object or the ground (right-click > **Sit Here**), and stand up (the **Stand** button, or right-click > **Stand Up**).
- Mouselook: M, or scroll in past the closest zoom. Esc or scroll out leaves it. Turn it off in Preferences > Move & View.
- Camera: Alt+drag orbits and zooms, Ctrl+Alt+drag orbits, Ctrl+Alt+Shift+drag pans. Alt+click focuses on what you click. Alt with the arrow keys also moves the camera. Scroll wheel zooms. Esc resets the view.
- Smoothing, inverted mouse, crosshair and camera constraints: Preferences > Move & View.

## Chat and people

- Nearby chat: **Communicate > Nearby chat** (Ctrl+H), or the dock's Chat button. Press Enter in the world (or click the chat bar) to type; the number pad's / starts a line with a / typed. In a chat line, Enter says, Shift+Enter whispers, Ctrl+Enter shouts, `/5 text` speaks on channel 5.
- Instant messages and group chat, in tabs with unread counts: **Communicate > Conversations** (Ctrl+T). Select several friends to start one conference.
- People window: **Communicate > People** (Ctrl+Shift+A), with Friends, Groups, Notices and Blocked tabs. Friend offers, your active group and title, group notices.
- Friend offers, teleport offers and teleport requests: right-click an avatar (**Add Friend**, **Offer Teleport**, **Request Teleport**), or **World > Teleport…** for a friend. Answer incoming ones in the notice that appears, or in People > Friends and the Teleport window.
- Notifications and offers: **Communicate > Notifications** (Alt+Shift+N), or the dock's Notices button.
- Gestures, with chat triggers and function keys (F2 to F12): **Me > Gestures…** (Ctrl+G).

## Inventory and appearance

- Browse, search, rename, make folders, drag to move, move to Trash: **Content > Inventory** (Ctrl+I). Tabs: All items, Recent items.
- Make new scripts, notecards, gestures, clothing and body parts: Inventory window.
- Open notecards and scripts; wear, add, take off, attach and detach items: Inventory window.
- Outfits: **Content > Outfits** (Ctrl+O).
- Edit your shape, skin, hair, eyes and clothing: **Me > Appearance**, or the dock's Appearance button.
- Animation overrider (AO): built in, on by default; switch it with the **AO** button on the dock (a click turns it on or off; its arrow or a right-click lists your sets and **Edit sets...**), or turn the whole add-on off in **Me > Add-ons**. It reads sets from the `#AO` folder inside the `#Firestorm` folder of your inventory.

## Building and scripting

- Build mode: **Build > Build** (Ctrl+B). Tools: Focus (Ctrl+1), Grab (Ctrl+2), Edit (Ctrl+3), Create (Ctrl+4).
- The Build window has a tool row (Focus, Grab, Edit, Create; Land is greyed), the Move, Rotate, Stretch and Select Face modes with Undo and Redo, and folding sections: Contents, Placement, Shape, Look, Object and Behaviour. Move, rotate and stretch handles; PBR materials (Look); the colour picker from any colour swatch.
- Link (Ctrl+L), Unlink (Ctrl+Shift+L), Edit Linked Parts, Select Next Part (Ctrl+.) and Previous Part (Ctrl+,), Select Only My Objects: **Build** menu.
- Undo (Ctrl+Z) and Redo (Ctrl+Y): **Build** menu.
- Grid snapping: **Build > Options** (Snap to Grid G, Snap Object XY to Grid Shift+X, Use Selection for Grid Shift+G, Grid Options… Ctrl+Shift+B).
- Recompile, reset, run and stop all scripts in a selection: **Build > Scripts**.
- Script editor (compile errors, Running, Reset, engine choice: Second Life's four-way Compile as list, or on OpenSimulator a Choose engine myself tick where the region lists engines or runs Lua, greyed where the region keeps no choice and runs no Lua) and notecard editor: double-click the item in the Inventory, or open it from the Build window's Contents section. Gesture editor: **Edit** in the Gestures window, or a new gesture from the Inventory's Create new menu (a double-click on a gesture in the Inventory does not open it).
- Take, Take Copy, Delete: right-click an object; Take Into Folder...: **Build > Take Into Folder...**. In the Contents section, F2 renames the chosen item and Delete removes the chosen items.
- Upload an image (Ctrl+U), a static mesh model (Ctrl+Alt+U), or many at once: **Build > Upload**.
- Touch, sit on, open, buy and pay objects; answer script dialogs: right-click an object.

## The world and land

- Teleport, go home, set home: **World > Teleport…**, **Go home** (Ctrl+Shift+H), **Set home to here**.
- World map (Ctrl+M) with region search and Mini-map (Ctrl+Shift+M): **World** menu, or the dock.
- Sun, sky and water: **World > Environment**. Sunrise (Ctrl+Shift+O), Midday (Ctrl+Shift+Y), Midday (Legacy) (Ctrl+Shift+T), Sunset (Ctrl+Shift+N), Midnight (Ctrl+Shift+Z), Use Shared Environment, Personal Lighting…, Pause Clouds. F9 steps through Use Shared Environment, Sunrise, Midday, Sunset and Midnight (it skips Midday (Legacy)); an active gesture on F9 takes the key instead. Preferences > Graphics can keep the choice for the next login and put the region's sky back after a teleport (both off).
- About Land: **World > About Land**, or right-click the ground.
- Region and parcel sky settings, terrain, water, prims, sculpties, mesh, glow, projected lights and the neighbouring regions are drawn as the grid sends them.
- Parcel ban lines (a wall at the edge of a parcel that refuses you; no setting for it yet) and region restart notices (with the Region restart sound).

## Settings and add-ons

- **Preferences** (Ctrl+P): General, Graphics, Sound, Chat, Move & View, Privacy, Cache, Building, and Interface (theme: Reverie dark, Reverie light, Classic, Classic light, Power user or High contrast; accent colour, window opacity, interface size, theme import and export).
- The cache folder (Preferences > Cache) has a **Choose...** button that opens a folder picker.
- Graphics presets, draw distance, texture size, frame-rate limit, vertical sync, clouds, neighbour regions, graphics backend (Vulkan or Direct3D 12), window mode: Preferences > Graphics.
- Sounds and interface sounds, with levels, mute and a mono option: Preferences > Sound.
- Add-ons window: **Me > Add-ons**. The animation overrider and RLV are built in; add-ons from outside are off until you turn them on. RLV is off until you turn it on, and its switch takes effect at your next login. An add-on from outside goes in the `Addons` folder of the viewer's settings folder, and asks you to accept what it may do when you turn it on.
- Check for updates, About: Reverie menu, **Help > About this viewer**.

## Keyboard shortcuts

| Keys | What |
|---|---|
| Ctrl+Shift+K | Search windows, commands and settings |
| Ctrl+I | Inventory |
| Ctrl+O | Outfits |
| Ctrl+P | Preferences |
| Ctrl+Q | Quit |
| Ctrl+T | Conversations |
| Ctrl+H | Nearby chat |
| Ctrl+Shift+A | People |
| Alt+Shift+N | Notifications |
| Ctrl+G | Gestures |
| Ctrl+M | World map |
| Ctrl+Shift+M | Mini-map |
| Ctrl+Shift+H | Go home |
| Ctrl+Shift+O, Y, T, N, Z | Sunrise, Midday, Midday (Legacy), Sunset, Midnight |
| F9 | Next environment choice (an active gesture on F9 wins) |
| Ctrl+B | Build mode |
| Ctrl+1, 2, 3, 4 | Focus, Grab, Edit, Create |
| Ctrl+L, Ctrl+Shift+L | Link, Unlink |
| Ctrl+. and Ctrl+, | Select next part, previous part |
| Ctrl+Z, Ctrl+Y | Undo, Redo |
| G, Shift+X, Shift+G, Ctrl+Shift+B | Snap to Grid, Snap Object XY, Use Selection for Grid, Grid Options |
| Ctrl+U, Ctrl+Alt+U | Upload image, Upload model |
| W A S D or arrows | Walk and turn |
| F or Home | Fly or land |
| Space | Stop moving |
| Ctrl+R | Always run |
| M | Mouselook |
| Esc | Reset the view (leave mouselook) |
| Enter, Shift+Enter, Ctrl+Enter | In the nearby chat line: say, whisper, shout (in an instant message line, Enter sends) |
| F2 to F12 | Gesture keys |
| Enter, number pad / | In the world: start a chat line (/ types a slash first) |

Every key is listed in the [keyboard shortcuts](guide/keyboard-shortcuts.md) section of the guide.

## Not in Reverie yet

Things people coming from other viewers look for. They are not in this preview:

- Snapshots; search; landmarks and a places list; teleport history.
- Profiles; a list of people nearby; paying or giving items to people; groups beyond chat, notices and your active group.
- Land, parcel and estate tools; the Land build tool; hypergrid travel.
- Saved chat logs; away and do-not-disturb replies; emoji, spell check and translation.
- Inventory: emptying the Trash, copy and paste, item properties, a Worn tab and filters.
- Voice, parcel music, media on a prim and particles.
- A camera controls window and saved camera views; a key binding editor.
- Statistics and the debug settings window.
- Advanced graphics settings (shadows, anti-aliasing and the like); rigged mesh upload.
- Full RLV: only part of its commands work.
- Buying L$ and the Marketplace.
