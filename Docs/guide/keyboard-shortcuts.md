# Keyboard shortcuts

This section lists every key Reverie answers, in groups. It also says where a key works only in one place.

Reverie has **no key binding editor yet**. You cannot change a key from inside the viewer. The keys below are the ones you get.

## How the keys reach the viewer

- **Keys with Ctrl or Alt** work even while you type in a text box. For example Ctrl+I opens the inventory while you are in the chat bar.
- **Keys without Ctrl or Alt** (such as W, F, M or G) are typed into a text box if one has the keyboard. To use them in the world, press **Esc** to let go of the text box, or click the world.
- A shortcut for a menu entry does nothing while that entry is greyed out. For example Link (Ctrl+L) needs something selected.
- The shortcut for a menu entry is also shown beside the entry in the menu.

## Windows and search

| Keys | What it does |
|---|---|
| Ctrl+Shift+K | Search every window, command and setting by name |
| Ctrl+I | Inventory |
| Ctrl+O | Outfits |
| Ctrl+P | Preferences |
| Ctrl+T | Conversations |
| Ctrl+H | Nearby chat |
| Ctrl+Shift+A | People |
| Alt+Shift+N | Notifications |
| Ctrl+G | Gestures |
| Ctrl+M | World map |
| Ctrl+Shift+M | Mini-map |
| Ctrl+Shift+H | Go home |
| Ctrl+Alt+F | Switch between full screen and a window |
| Ctrl+Q | Quit |

Esc closes the window that has the focus. If you are typing in a text box, the first Esc lets go of the box, and a second one closes the window.

## Moving and the camera

These work in the world, while you are logged in. See [Moving and the camera](moving-and-the-camera.md) for what they do.

| Keys | What it does |
|---|---|
| W or Up | Walk forward |
| S or Down | Walk back |
| A or Left | Turn left |
| D or Right | Turn right |
| Shift+A or Shift+D (also Shift+Left, Shift+Right) | Slide sideways |
| E or Page Up | Jump (when flying: go up) |
| C or Page Down | Crouch (when flying: go down) |
| F or Home | Fly or land |
| Space | Stop moving |
| Ctrl+R | Always run on or off |
| M | Mouselook on or off |
| Esc | Reset the camera behind your avatar. In mouselook it leaves mouselook instead |
| Alt+arrow keys, or Alt+W A S D | Move the camera: orbit left and right, zoom in and out |
| Alt+E and Alt+C, or Alt+Page Up and Alt+Page Down | Move the camera up and down around you |
| Ctrl+Alt+W and Ctrl+Alt+S (also Ctrl+Alt+Up, Ctrl+Alt+Down) | Move the camera up and down around you |
| Ctrl+Alt+Shift+arrow keys, or Ctrl+Alt+Shift+W A S D | Pan the camera |

The mouse part of the camera (Alt+drag, Ctrl+Alt+drag, Ctrl+Alt+Shift+drag, Alt+click and the scroll wheel) is in [Moving and the camera](moving-and-the-camera.md).

Two places where the keys change:

- **In mouselook,** A, D, Left and Right slide you sideways instead of turning you.
- **While you sit,** the plain keys W, A, S, D and the arrow keys, E, C, Page Up and Page Down move the camera, not your avatar. Shift+A and Shift+D still slide. A script that has taken the controls, or a seat that has its own camera, gets the keys instead.

## Chat

These work only in a chat line (the nearby chat line, or an instant message line). Click the line first.

| Keys | What it does |
|---|---|
| Enter | Say it (in nearby chat), or send it (in an instant message) |
| Shift+Enter | Whisper. Nearby chat only |
| Ctrl+Enter | Shout. Nearby chat only |
| Esc | Let go of the chat line, so the keys move your avatar again |

A line that starts with `/5` speaks on channel 5. In the world, with no window or line holding the keyboard, **Enter** puts the keyboard in the chat bar, and the number pad's **/** does the same with a `/` already typed (it keeps what you had typed if the line is not empty), ready for a gesture trigger.

Gestures use the function keys:

| Keys | What it does |
|---|---|
| F2 to F12 | Play the active gesture that has this key. Shift, Ctrl or Alt with the key also work if the gesture says so |

An active gesture that uses F9 wins over the sky key (see below); with none, F9 steps through the sky. Set a gesture's key in the gesture editor: **Me > Gestures…** (Ctrl+G).

## The sky

| Keys | What it does |
|---|---|
| Ctrl+Shift+O | Sunrise |
| Ctrl+Shift+Y | Midday |
| Ctrl+Shift+T | Midday (Legacy) |
| Ctrl+Shift+N | Sunset |
| Ctrl+Shift+Z | Midnight |
| F9 | Step to the next choice: Use Shared Environment, Sunrise, Midday, Sunset, Midnight. It skips Midday (Legacy). An active gesture that uses F9 takes the key instead |

## Building

These are the shortcuts of the **Build** menu. Some need something selected, and the grid keys work only while build mode is on. Switch build mode on with **Build > Build** (Ctrl+B).

| Keys | What it does |
|---|---|
| Ctrl+B | Build mode on or off |
| Ctrl+1 | Focus tool |
| Ctrl+2 | Grab tool |
| Ctrl+3 | Edit tool |
| Ctrl+4 | Create tool |
| Esc | Leave build mode, clear the selection and close the Build window. Build mode only |
| Ctrl+D | Duplicate the selected objects (Build > Object > Duplicate). Greyed unless a selected object may be copied |
| Ctrl+L | Link the selected objects |
| Ctrl+Shift+L | Unlink |
| Ctrl+. | Select the next part of a linked object |
| Ctrl+, | Select the previous part |
| Ctrl+Z | Undo your last build change (greyed when there is none) |
| Ctrl+Y | Redo |
| G | Snap to Grid on or off. Build mode only |
| Shift+X | Snap the selected object's X and Y to the grid. Build mode, with a selection |
| Shift+G | Use the selection for the grid. Build mode, with a selection |
| Ctrl+Shift+B | Grid Options… |
| Ctrl+U | Upload an image (works anywhere in the world) |
| Ctrl+Alt+U | Upload a model (works anywhere in the world) |

While you drag with the Edit tool, hold **Ctrl** to show the rotate handles, or **Ctrl+Shift** to show the stretch handles.

Ctrl+Z and Ctrl+Y act on a text box first while you type in one.

## In windows

| Keys | Where | What it does |
|---|---|---|
| Up, Down, Enter | Search list (Ctrl+Shift+K) | Choose an entry and run it. Esc closes the list |
| Left, Right | Inventory | Close or open a folder, or go to its parent folder |
| Enter | Inventory | Open the chosen item |
| Up, Down, Enter | Gestures window | Move through the list, and play the gesture |
| Ctrl+S | Script editor and notecard editor | Save |
| Ctrl+F | Script editor and notecard editor | Find |
| F2 | Content tab of the Build window | Rename the chosen item |
| Delete | Content tab of the Build window | Remove the chosen items |

## Next

For everything else the viewer can do, see the [feature cheat sheet](../FEATURES.md).
