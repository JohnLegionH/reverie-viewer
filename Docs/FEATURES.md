# Reverie Viewer: what you can do

A one-page cheat sheet for the current preview. It lists what works today and where to find it. For step-by-step help, see
the [user guide](guide/README.md). The list grows as the viewer does.

Menus are on the top bar: the **Reverie** menu (About, Preferences, Add-ons, Check for updates, Log out, Quit), then **Me**,
**Communicate**, **World**, **Build**, **Content** and **Help**. The dock at the bottom has buttons for the most-used windows.
Press **Ctrl+Shift+K** to search every window, command and setting by name, also by its Second Life or Firestorm name.

## Getting in

- Log in to Second Life or an OpenSimulator grid. Login screen: Grid, Name, Password, then **Log in**.
- Add or manage grids. Login screen: Grid list > **Add a grid…** or **Manage grids…**.
- A grid can make Reverie read like its own by setting keys in its grid information: its name, its currency symbol (`currency`) and currency name (`currencyname`), and its help, about, welcome and viewer-download pages. Prices, fees and costs carry the grid's symbol; a grid that sets `currency = none` has no balance, no Buy currency and no Pay. The Help menu gets the grid's help and about pages and **Get Reverie from <grid>**, and the login screen gets **Visit <grid>'s page**; an item shows only when the grid sets its key. Every such page opens in your own browser, after a question that names its host. The grid list always shows each grid's host beside its name; a grid named "Second Life", "SL" or anything with "Linden" in it is shown under its host, as is a grid with the same name as another you saved. Second Life is unchanged: nothing a grid supplies applies there.
- Visit another grid by Hypergrid: the top bar shows **Visiting <grid name> (<host>)**, prices there carry that grid's symbol, and your balance keeps your home grid's symbol.
- Travel to a region on another grid by typing its address in the location box or the world map's search: `hop://host:port/Region/x/y/z`, `host:port:Region`, `host:port Region` or `http://host:port/ Region`. While visiting, the location box shows the region with its grid's host; in a narrow bar the coordinates go first, then the names are shortened with "…", the host always shows, and the whole place is in the tooltip. Back and teleport history remember which grid each place was on.
- A hop:// link to another grid in chat, an IM, a notecard, a group notice or a parcel's description asks first, naming the place and its grid, and teleports only on **Teleport**. While visiting, a link back to your home grid asks the same way.
- Landmarks: the star in the location box makes one in your Landmarks folder; double-click a landmark in the inventory to see its place and the grid it is on, and **Teleport** there, also to another grid.
- **Go home** while visiting tries the teleport home first; if your home grid refuses it, a dialog offers to log out and log back in at your home location. A dialog that must be answered takes every click on itself at any interface size; nothing behind it does.
- Remember your name, or (where this computer can protect it) your password. Login screen: **Remember name**, **Remember password**.
- Choose where you arrive: last location, home, or a named region. Login screen: **Start at**.
- Reconnect after a dropped connection: the **Logged out** window has a **Reconnect** button. It does not start by itself.
- The reconnect notice, the login dialogs (terms, certificates, updates), the four upload windows, About and Licences draw in the same new look as the other windows.

## Moving and the camera

- Walk and turn: W A S D or the arrow keys. Shift+A or Shift+D slides sideways. E or Page Up jumps (flying: up); C or Page Down crouches (flying: down).
- Fly or land: F or Home. Stop moving: Space.
- Run: Ctrl+R switches always-run; tap a direction twice and hold to run (Preferences > Move & View).
- Sit on an object or the ground (right-click > **Sit Here**), and stand up (the **Stand** button, or right-click > **Stand Up**).
- Mouselook: M, or scroll in past the closest zoom. Esc or scroll out leaves it. Turn it off in Preferences > Move & View.
- Camera: Alt+drag orbits and zooms, Ctrl+Alt+drag orbits, Ctrl+Alt+Shift+drag pans. Alt+click focuses on what you click. Alt with the arrow keys also moves the camera. Scroll wheel zooms. Esc resets the view. Left-drag on your own avatar turns you the way you drag (right turns right) and tilts the view up and down; left-drag on the ground steers.
- Smoothing, inverted mouse, crosshair and camera constraints: Preferences > Move & View.

## Chat and people

- Nearby chat: **Communicate > Nearby chat** (Ctrl+H), or the dock's Chat button. Press Enter in the world (or click the chat bar) to type; the number pad's / starts a line with a / typed. In a chat line, Enter says, Shift+Enter whispers, Ctrl+Enter shouts, `/5 text` speaks on channel 5.
- Instant messages and group chat, in tabs with unread counts: **Communicate > Conversations** (Ctrl+T). Select several friends to start one conference.
- People window: **Communicate > People** (Ctrl+Shift+A), with Friends, Groups, Notices and Blocked tabs. Friend offers, your active group and title, group notices.
- Click a resident's name in nearby chat or in a message for the name menu (IM, Offer Teleport, Request Teleport, Add Friend, Copy Name and the rest; what is not built is greyed with the reason). A friend coming online or going offline shows a notice (press it to open a conversation) and a line in nearby chat; both can be switched off in Preferences > Chat. A friend already online when you log in is announced the same way. People from other grids (Hypergrid) keep their "First.Last @host:port" name in the list, the name tag and chat.
- Return, Delete (for a locked, no-copy or not-yours object), Drop (for a worn object) and giving a friend the right to edit your objects each ask in one dialog: OK or Yes with Enter, Cancel or No with Esc.
- Friend offers, teleport offers and teleport requests: right-click an avatar (**Add Friend**, **Offer Teleport**, **Request Teleport**), or **World > Teleport…** for a friend. Answer incoming ones in the notice that appears, or in People > Friends and the Teleport window.
- Notifications and offers: **Communicate > Notifications** (Alt+Shift+N), or the dock's Notices button.
- Gestures, with chat triggers and function keys (F2 to F12): **Me > Gestures…** (Ctrl+G).

## Inventory and appearance

- Browse, search, rename, make folders, drag to move, move to Trash: **Content > Inventory** (Ctrl+I). Tabs: All items, Recent items. Recent items lists what is already loaded (the Hypergrid suitcase too) by the official viewer's rules: since your last logout by default, or newer or older than a number of hours and days, and only the kinds you choose under **Show**; **Reset** goes back to Since logoff.
- Make new scripts, notecards, gestures, clothing and body parts: Inventory window.
- Open notecards and scripts; wear, add, take off, attach and detach items: Inventory window.
- Outfits: **Content > Outfits** (Ctrl+O).
- Edit your shape, skin, hair, eyes and clothing: **Me > Appearance**, or the dock's Appearance button.
- Animation overrider (AO): built in, on by default; switch it with the **AO** button on the dock (a click turns it on or off; its arrow or a right-click lists your sets and **Edit sets...**), or turn the whole add-on off in **Me > Add-ons**. It reads sets from the `#AO` folder inside the `#Firestorm` folder of your inventory.

## Building and scripting

- Build mode: **Build > Build** (Ctrl+B). Tools: Focus (Ctrl+1), Grab (Ctrl+2), Edit (Ctrl+3), Create (Ctrl+4).
- The Build window has a tool row (Focus, Grab, Edit, Create; Land is greyed), the Move, Rotate, Stretch and Select Face modes with Undo and Redo, and folding sections: Contents, Placement, Shape, Look, Object and Behaviour. Move, rotate and stretch handles; PBR materials (Look); the colour picker from any colour swatch.
- Create places one of fifteen shapes (cube, prism, pyramid, tetrahedron, cylinder, hemicylinder, cone, hemicone, sphere, hemisphere, torus, tube, ring, tree, grass), with Keep Tool selected, Copy selection, Center Copy and Rotate Copy.
- Duplicate (Ctrl+D): **Build > Object > Duplicate**. A Shift-drag of a move handle leaves a copy behind.
- Object section: For sale (Copy, Contents or Original, and the price), When left-clicked (Touch, Sit, Buy, Pay, Open, Zoom, Ignore or None). The selection card shows the land impact.
- Return: right-click an object (a question asks first).
- Link (Ctrl+L), Unlink (Ctrl+Shift+L), Edit Linked Parts, Select Next Part (Ctrl+.) and Previous Part (Ctrl+,), Select Only My Objects: **Build** menu.
- Undo (Ctrl+Z) and Redo (Ctrl+Y): **Build** menu.
- Grid snapping: **Build > Options** (Snap to Grid G, Snap Object XY to Grid Shift+X, Use Selection for Grid Shift+G, Grid Options… Ctrl+Shift+B).
- Recompile, reset, run and stop all scripts in a selection: **Build > Scripts**.
- Script editor (compile errors, Running, Reset, engine choice: Second Life's four-way Compile as list, or on OpenSimulator a Choose engine myself tick where the region lists engines or runs Lua, greyed where the region keeps no choice and runs no Lua) and notecard editor: double-click the item in the Inventory, or open it from the Build window's Contents section. Gesture editor: double-click the gesture in the Inventory (or Enter, or **Open**), **Edit** in the Gestures window, or a new gesture from the Inventory's Create new menu.
- Take, Take Copy, Delete: right-click an object; Take Into Folder...: **Build > Take Into Folder...**. In the Contents section, F2 renames the chosen item and Delete removes the chosen items.
- Upload an image (Ctrl+U), a static mesh model (Ctrl+Alt+U), or many at once: **Build > Upload**.
- Touch, sit on, open, buy and pay objects; answer script dialogs: right-click an object.

## The world and land

- Teleport, go home, set home: **World > Teleport…**, **Go home** (Ctrl+Shift+H), **Set home to here**.
- World map (Ctrl+M) with region search and Mini-map (Ctrl+Shift+M): **World** menu, or the dock.
- Sun, sky and water: **World > Environment**. Sunrise (Ctrl+Shift+O), Midday (Ctrl+Shift+Y), Midday (Legacy) (Ctrl+Shift+T), Sunset (Ctrl+Shift+N), Midnight (Ctrl+Shift+Z), Use Shared Environment, Personal Lighting…, Pause Clouds. F9 steps through Use Shared Environment, Sunrise, Midday, Sunset and Midnight (it skips Midday (Legacy)); an active gesture on F9 takes the key instead. Only the sky in use has a check mark in the menu. Your choice is kept for the next login until you choose Use Shared Environment (Preferences > Graphics > Keep my sky choice for the next login turns that off); Preferences > Graphics can also put the region's sky back after a teleport (off).
- About Land: **World > About Land**, or right-click the ground.
- Region and parcel sky settings, terrain, water, prims, sculpties, mesh, glow, projected lights and the neighbouring regions are drawn as the grid sends them.
- Other people are drawn as the official viewer draws them: a person appears once their look has arrived (about three seconds after it is complete), the parts of their body their outfit hides (alpha layers, a mesh body) are never drawn, and the last finished look stays while a new one arrives. When someone leaves, what they wear goes with them instead of coming off piece by piece.
- Parcel ban lines (a wall at the edge of a parcel that refuses you; no setting for it yet) and region restart notices (with the Region restart sound).

## Settings and add-ons

- **Preferences** (Ctrl+P): General, Graphics, Sound, Chat, Move & View, Privacy, Cache, Building, and Interface (theme: Reverie dark, Reverie light, Classic, Classic light, Power user or High contrast; accent colour, window opacity, interface size, theme import and export).
- The cache folder (Preferences > Cache) has a **Choose...** button that opens a folder picker.
- Graphics presets, draw distance, texture size, frame-rate limit, vertical sync, clouds, neighbour regions, graphics backend (Vulkan or Direct3D 12), window mode: Preferences > Graphics.
- **How the window opens**: Preferences > Graphics > **Window at startup** is **Maximized window** (the default), **Last size and position** or **Full screen**. The window is a normal one with a title bar and a taskbar button, and can be minimized at any time, on the login screen too. **Me > Full screen** (Ctrl+Alt+F) switches between full screen and a window, and **Me > Minimize** sends the window to the taskbar. The viewer remembers whether the window was maximized, its size, its position and its screen; if that screen is gone, it opens maximized on the main screen.
- Sounds and interface sounds, with levels, mute and a mono option: Preferences > Sound. Master, Sounds, Ambient and Interface sounds each have one row (slider, value, Mute); the rarely changed money and pain thresholds and the per-sound table are in two collapsed sections, each with a one-line summary. Reverie ships no interface sounds of its own: it plays the sounds of the grid it is connected to, by the sound ids the official viewer uses, so each grid plays its own defaults. A sound the grid does not have is silent. On the login screen the sounds of the grid chosen there play if you connected to it before; otherwise the login screen is silent.
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
| Ctrl+D | Duplicate the selection |
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

- Snapshots; search; a places list and a teleport history window (Back works).
- Profiles; a list of people nearby; paying or giving items to people; groups beyond chat, notices and your active group.
- Land, parcel and estate tools; the Land build tool; the object weights window (Reverie shows the land impact only).
- Saved chat logs; away and do-not-disturb replies; emoji, spell check and translation.
- Inventory: emptying the Trash, copy and paste, item properties, a Worn tab and filters.
- Voice, parcel music, media on a prim and particles.
- A camera controls window and saved camera views; a key binding editor.
- Statistics and the debug settings window.
- Advanced graphics settings (shadows, anti-aliasing and the like); rigged mesh upload.
- Full RLV: only part of its commands work.
- Buying L$ and the Marketplace.
