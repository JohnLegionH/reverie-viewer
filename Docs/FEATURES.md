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
- Pivot point (Build window, Placement section, "Pivot point"): the handles turn and stretch the selection about its middle (**Centre**), the root prim (**Root**) or a point you type (**Custom**: X / Y / Z as a percent of the box or in metres, counted from the box's minimum corner in the object's own turn, so 0 / 0 / 0 % is a door's hinge). Centre is now the middle of the box that holds every selected object's extents, as in the official viewer, so with objects of different sizes the handles stand where they did not before. A new selection starts at Centre; the closed Placement section adds ", pivot root" or ", pivot custom".
- Align (Build window, Arrange section, between Placement and Shape): select two or more objects, pick the axis (X, Y or Z of the grid frame) and what to line up to (**Selection box** or **Last selected**), then press **Min**, **Centre** or **Max**. Each linkset moves along that axis only, so its low edge, middle or high edge meets the same edge of the target; a linkset's box holds every one of its prims, turned ones by their turned corners, and a part picked moves its whole linkset by its root. The region gets one message for all of them, and Undo steps each back. The buttons are off, with the reason as their tooltip, for a single linkset, a worn object, or an object you may not move. The closed section reads "3 linksets: align, distribute".
- Distribute (same Arrange section, below Align): select three or more objects, pick the axis (X, Y or Z of the grid frame), then press **Centres** (equal steps between the linksets' centres) or **Gaps** (equal space between one linkset's box and the next). The linksets are taken in order along the axis, whatever order they were picked in; the two outermost stay where they are and the ones between move along that axis only. Boxes are measured as in Align. The region gets one message for the moved linksets, and Undo steps each back. The buttons are off, with the reason as their tooltip, for fewer than three linksets, a worn object, or an object you may not move.
- Mirror (same Arrange section, below Distribute): select one or more linksets (or, with Edit linked parts, some of a linkset's parts), pick the axis (X, Y or Z of the grid frame) and where the plane passes (the middle of what is mirrored, or the pivot point), then press **Mirror**. Each part's place goes to its image on the other side of the plane, and each part's turn is replaced by the proper turn that puts the part onto the image of the old one, so mirroring twice puts everything back. A prim's handed shape settings are flipped with it so that the part is the true mirror image, not only a symmetric stand-in: twist (negated about a profile plane, end for end about the path plane), top shear (negated on its own axis), taper, path cut and profile cut, and a sculpt's Mirror setting. The part is turned about whichever of its own middle planes its shape can be flipped about, which can mean a half turn for a part that is not symmetric. A mesh cannot be flipped, because that would need the asset uploaded again; a prim with a shape no setting can flip (a twisted or cut ring, torus or sphere, a flexible path, a sheared prim about its own length, a profile cut that would run past the end) is likewise only placed. A notice names those parts ("these parts keep their own handedness") and closes with **OK**. **Mirror a copy** (same section, below Mirror) copies the selected linksets where they stand and then mirrors the copies about the plane through the originals, so the originals stay and each copy lands as their image; it is off with Edit linked parts, and where Mirror or Duplicate is off. The root goes first (its group position, then its own turn), then each part in its root's frame; the region's Undo steps back one part at a time. The buttons are off, with the reason as their tooltip, for nothing selected, a worn object, or an object you may not move.
- Grid snapping: **Build > Options** (Snap to Grid G, Snap Object XY to Grid Shift+X, Use Selection for Grid Shift+G, Grid Options… Ctrl+Shift+B).
- Snap to objects (Build window, Placement section, off until you tick it): while you drag a Move arrow or plane handle, the selection catches flush against another object's face, or lines up an edge or middle, once it comes within 0.25 m or 8 pixels on screen, whichever is smaller. A pink frame with a dark edge, a little larger than the smaller of the two faces, shows what was caught; hold **Alt** to let go. It goes by each object's box along the grid's axes: exact for box prims that stand square to those axes, and only the box round turned, round and mesh shapes, not their surface. Only objects within 10 m of the selection when the drag begins are caught on.
- Recompile, reset, run and stop all scripts in a selection: **Build > Scripts**.
- Script editor (compile errors, Running, Reset, engine choice: Second Life's four-way Compile as list, or on OpenSimulator a Choose engine myself tick where the region lists engines or runs Lua, greyed where the region keeps no choice and runs no Lua) and notecard editor: double-click the item in the Inventory, or open it from the Build window's Contents section. Gesture editor: double-click the gesture in the Inventory (or Enter, or **Open**), **Edit** in the Gestures window, or a new gesture from the Inventory's Create new menu.
- Take, Take Copy, Delete: right-click an object; Take Into Folder...: **Build > Take Into Folder...**. Delete is greyed, with the reason as its tooltip, for an object you neither own nor may modify. In the Contents section, F2 renames the chosen item and Delete removes the chosen items; a name being typed is kept when you click anywhere else in the Build window.
- Edit: right-click an object in the world and choose **Edit** (above Delete). The Build window opens on the Edit tool with that object selected and its handles shown; on a part of a linked object it selects the whole linkset. It is offered on an object you wear (Delete is not), and not on avatars, land, or an object another avatar wears. After you turn several objects with the Rotate handles, the handles move to the new box as soon as you let go.
- A minimized window comes back with one click on its bar (or on its Restore button, which takes the place of Minimize), at the size and place it had when it was minimized.
- **Ctrl+W** closes the window in front.
- Selecting several objects (Edit or Move tool): Shift-click adds an object to the selection or takes it out again, also when it lies under a handle of the selection; drag a box on the world to select the objects inside it, Shift held adds them. **Build > Options > Select By Surrounding** (on, as in the official viewer) takes only objects the box surrounds; off takes any object the box touches. Objects more than 64 m away are passed over. A rectangle shows on the screen while the box is dragged. The Build window always shows how many linksets and parts are selected.
- Delete (Delete key in build mode, or Delete in the right-click menu of a selected object) removes every selected object you own or may modify, with one question when any is locked, not copyable or not yours; worn objects are left alone.
- Upload an image (Ctrl+U), a static mesh model (Ctrl+Alt+U), or many at once: **Build > Upload**.
- Touch, sit on, open, buy and pay objects; answer script dialogs: right-click an object.

## The world and land

- Teleport, go home, set home: **World > Teleport…**, **Go home** (Ctrl+Shift+H), **Set home to here**. A teleport whose destination refuses the arrival ends with the reason shown, and you stay in the region you were in.
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
- **Parcel music**: a parcel that names a music stream plays it (internet radio over http or https: MP3, AAC and Ogg Vorbis; a station's own address, a bare server address, redirects, and .pls and .m3u playlists). A music control shows in the top bar only while the parcel has music: one click turns the stream off and on, with mute and volume beside it (the speaker icon of the Sound page). Preferences > Sound has a **Music** row for the same level and mute. Off stays off across parcels and after you log in again, until you turn it on. The first time a site's stream would play, Reverie asks once, **Play** or **Don't play**, and remembers the answer for that site. A new address on the parcel (About Land) switches the music at once; an empty address, or leaving the parcel, stops it. The decoders are managed libraries (NLayer, NVorbis, SharpJaad): 0.68 MB in the export, about 0.34 MB compressed, no native library.
- Add-ons can read the world (add-on interface 0.6): the camera and avatar's place, region size, water, ground, wind, sun and sky, with events for a region change and a teleport. For add-ons that come with the viewer only, for now; on Second Life it is off unless the add-on declares Second Life. Their windows can have sliders. See [ADDONS.md](ADDONS.md#the-world-06).
- Add-ons can play sound files from their own folder (add-on interface 0.6): looped or once, around you or from a place, delayed or muffled, under the Ambient level and the master mute. Only you hear them, and they stop when the add-on stops, at logout, and when you teleport or change region. For add-ons that come with the viewer only, for now; on Second Life they are off unless the add-on declares Second Life. See [ADDONS.md](ADDONS.md#sounds-06).
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
- Voice, media on a prim and particles.
- A camera controls window and saved camera views; a key binding editor.
- Statistics and the debug settings window.
- Advanced graphics settings (shadows, anti-aliasing and the like); rigged mesh upload.
- Full RLV: only part of its commands work.
- Buying L$ and the Marketplace.
