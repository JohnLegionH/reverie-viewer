# Release notes draft: the next release after 0.1.3

This is a draft. It is not published, and it changes as work lands on main.

## What's new

- **Choose the script engine.** On a grid whose regions run more than one script engine and let you choose, the script editor has a "Choose engine myself" box and a list of the region's engines. Tick it, pick an engine and save: the region keeps that choice with the script. Untick it to clear the choice, and the region decides again. Unticked, the editor shows which engine runs the script and why: a saved choice, the script's first line, or the region default. Scripts in an object can also be moved to another engine from the Build window's Contents list (right-click, Engine) without opening them. That entry is greyed for a script you cannot edit, unless the region allows it.
- **Second Life's four compiler choices.** On Second Life the script editor offers LSL Legacy, LSL on Mono, LSL on Lua and Lua. The two Lua entries are greyed where the region does not allow Lua scripts.
- **Lua words.** Lua scripts get colours and tooltips for Luau's words and for the ll, ossl and other function tables.
- **Mini-map directions.** The mini-map shows N, NE, E, SE, S, SW, W and NW at its edge, and they turn with the map.
- **Ground menu.** Right-clicking the ground offers About Land, Sit Here and Build, in the official viewer's order. Build opens the Create tool.
- **A larger login card.** The login card takes 40 percent of the window's width. In a short window it becomes compact and keeps the Log in and Cancel buttons in view.
- **The AO is on by default.** On a fresh install the animation overrider starts switched on and plays its set's stand as soon as it has a set. Turn it off with the AO button; the choice is kept.

## Fixes

- **A save keeps a script's language.** Saving a Lua script, or an LSL-on-Lua script, no longer turns it into an LSL Mono script. This applies to scripts inside an object and in the inventory, on OpenSimulator and on Second Life.
- **Lists.** In People, the friends list takes Ctrl-click and Shift-click, and a double-click opens a message. A double-click on a group opens its chat. In the inventory, Shift-click selects a range. The Contents list takes several items at once.
- **Conference IMs.** Selecting several friends and starting a message opens one conference with all of them.
- **Rename.** Inventory items you may modify can be renamed. In an object's Contents list, F2 renames the chosen item.
- **Contents.** The Build window shows the Contents section first, and open. The Delete key removes the chosen items from an object, as its rights allow.
- **Dropped notecard items.** Items dropped into a notecard are kept one space apart instead of running together.
- **"Save changes?"** The buttons of the save-changes question stay in view at every interface size.
- **Build handles.** After a drag, the move handles stay where you left them until the region answers, instead of jumping back first.
- **Full screen.** On first start, with no saved window mode, the viewer opens full screen.
- **Grid alerts in the script editor.** An alert the region sends about a script save shows in the editor's status line.

## Known limits

- The engine choice needs a grid whose regions offer it. On a stock OpenSimulator region the editor shows no engine row, and a save keeps the script as it is. The tick box for Mono from earlier versions is gone.
- A script in the inventory shows its engine only where the grid sends it with the item.
