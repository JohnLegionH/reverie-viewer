<!-- Copyright (c) 2026 Legion Builds -->
<!-- SPDX-License-Identifier: LGPL-2.1-or-later -->

# Add-ons: how to write, package and install one (interface 0.5)

An add-on adds a feature to the viewer: an animation overrider, a restraint system, a chat tool. Anyone can write one, swap
one for another, or improve one. Some come with the viewer (the animation overrider is the first); others are installed by
the user.

**Safety first.** Add-ons run inside the viewer, in the viewer's own process, with full access to your computer. The
permissions below limit what the viewer hands an add-on through its interface, and every call checks them, but they are
not a sandbox: a .NET library can still use anything .NET offers (files, the network). Install only add-ons from people
you trust, as you would any program. See "Security" below.

## What an add-on is

A folder holding:

- `add-on.json`, the manifest (below);
- the add-on's .NET library (a `.dll` built for .NET 10), and any libraries it needs beside it.

The library has exactly one public class that implements `Viewer.Addons.IAddon` and has a public constructor without
arguments (or the manifest names the class in `entry`). It is compiled against `Viewer.Addons.Api.dll`, the one assembly
that describes the interface; it uses nothing else of the viewer. The viewer supplies its own copy of
`Viewer.Addons.Api.dll` at run time, so the add-on does not ship one (if it does, the viewer's is used anyway).

## The manifest

```json
{
  "id": "hello-sample",
  "name": "Hello Sample",
  "version": "1.0.0",
  "author": "Someone",
  "licence": "MIT",
  "interface": "0.1",
  "job": "ao",
  "permissions": ["window", "chat.send"],
  "library": "HelloAddon.dll",
  "entry": "HelloAddon.HelloAddon"
}
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Lower-case letters and digits, with `.`, `_` or `-` between them. Unique: a second add-on with the same id is skipped. |
| `name` | yes | Shown in the Add-ons window. |
| `version` | yes | Shown in the Add-ons window. |
| `author` | yes | Shown in the Add-ons window. |
| `licence` | yes | The add-on's licence, shown in the Add-ons window. Add-ons may use any licence. |
| `interface` | yes | The interface version it was built for, `major.minor`. The viewer loads it when the major version is the same and the minor version is no newer than its own (this viewer: 0.5). |
| `job` | no | The job it fills, if any (below). Same shape as `id`. |
| `permissions` | no | What it asks for (below). An unknown permission makes the manifest unusable. |
| `library` | yes | The `.dll` file name in the add-on's folder. |
| `entry` | no | The full name of the `IAddon` class, when the library has more than one. |
| `inventory_folder` | with `inventory.write` | The one inventory folder the add-on may change, as folder names from the inventory's top joined by `/` (for example `#Firestorm/#AO`). Required when it asks for `inventory.write`. |
| `default_on` | no | `true` or `false`: whether an add-on that comes with the viewer is on before the user chooses (default `true`). RLV says `false`. |
| `switch_at` | no | `"now"` (default) or `"next-login"`: when switching the add-on on or off takes effect. With `"next-login"` the switch keeps the user's choice, the Add-ons window says "Takes effect at next login.", and the add-on starts or stops when the user next logs in. RLV says `"next-login"`, as Firestorm's RestrainedLove setting applies at the next start. |

A folder whose manifest cannot be read, or whose library is missing, is skipped and the reason is logged.

## Where add-ons live

- **Built-in add-ons** ship in the viewer's own `addons` folder, beside the viewer's libraries, one folder per add-on.
  They are on unless the user turns them off, or their manifest says `"default_on": false` (RLV does).
- **Add-ons the user installs** go in the `Addons` folder of the viewer's user folder (the folder that holds the viewer's
  settings: on Windows `%APPDATA%\<the viewer's name>\Addons`), one folder per add-on, for example
  `Addons\hello-sample\add-on.json` and `Addons\hello-sample\HelloAddon.dll`. The viewer makes the `Addons` folder at
  start. They are found when the viewer starts and are **off until the user turns them on** in the Add-ons window,
  after seeing the permissions they ask for.
- An add-on's own settings live in `AddonSettings\<id>\` in the same user folder.
- Whether each add-on is on, and the permissions the user granted, are kept in `addons-state.json` there. An installed
  add-on that later asks for more permissions than were granted stays off until the user accepts again.

## The Add-ons window

**Me > Add-ons** (also **Add-ons...** in the viewer's own menu at the left of the top bar) lists every add-on found: its name, version, author, licence, job and permissions, whether it comes
with the viewer, and a switch. For each job it shows which add-on fills it, and lets the user choose another (or none).
When an add-on was switched off after an error, the window says why.

## Running, stopping and errors

- Switching an add-on on loads its library in a load context of its own (apart from the viewer's libraries and every
  other add-on's), makes its class and calls `Start(context)` once. Its libraries are read into memory, so its files
  are not held open: an add-on can be updated by replacing its folder, and the new version loads the next time it is
  switched on.
- Switching it off calls `Stop()` once, then the viewer removes its windows, menu items, filters and event handlers,
  stops any animation it started and did not stop, and unloads its code (the load context is collectible; no restart is
  needed). It stops at once.
- An exception thrown by an add-on (in `Start`, an event handler, a filter, a button, `Tick`) is caught and logged, the
  add-on is switched off, and the Add-ons window shows a note with the error. It never takes the viewer down. Using
  something that was not granted counts as such an error.
- Everything the viewer calls runs on the viewer's main thread, one call at a time. Keep calls short: the viewer waits
  for them before drawing the next frame.

## Jobs

A job is a role only one add-on should play at a time: `ao` (the animation overrider) and `rlv` (the restraint system). An add-on names
the job it fills in its manifest. Only one add-on per job is on at a time: turning one on turns the other off. A built-in
add-on that fills a job is on by default; when the user chooses another for that job, the choice is kept.

## Interface 0.5

Version 0.5 adds, to everything 0.4 had: buttons on the viewer's dock (`AddDockItem`, see "The dock" below). An add-on
built for 0.1, 0.2, 0.3 or 0.4 loads unchanged.

Version 0.4 adds, to everything 0.3 had: the restraints handle (`IAddonContext.Restraints`, permission `restraints`), which
is **not yet public**: only add-ons that come with the viewer are given it, and for any other add-on it throws
`AddonPermissionException`. It is how the RLV add-on hands the viewer the restrictions to enforce (see "RLV" below). An add-on
built for 0.1, 0.2 or 0.3 loads unchanged.

Version 0.3 adds, to everything 0.2 had: the text of a notecard the user handed to the add-on (`ReadNotecardTextAsync`, see
Inventory), and `IsNotecard` on inventory items. An add-on built for 0.1 or 0.2 loads unchanged.

Version 0.2 adds, to everything 0.1 had: changing the inventory inside one folder, a local preview of an animation, drop
zones and text fields in windows, and (for add-ons that come with the viewer only, for now) controls in the quick bar.
An add-on built for 0.1 loads unchanged.

`Start` receives an `IAddonContext`. Through it an add-on can do the following, each with the permission it needs.

### Events

| Member | Permission |
|---|---|
| `LoggedIn`, `LoggedOut`, `LoggedInNow`; `RegionChanged` (the region's name) | `session` |
| `Motion`, `MotionChanged`: the user's own avatar standing, walking, running, turning, crouching, jumping, flying, hovering, falling, landing, standing up, sitting on an object or on the ground; `Mouselook`, `MouselookChanged` | `motion` |
| `Playing`, `PlayingChanged`: the animations the region says the user's own avatar plays | `animations.read` |
| `ChatReceived`: local chat, and owner-say from the user's own objects | `chat.read` |
| `Tick`: once a frame; `Now`: seconds since the viewer started | none |

### Actions

| Member | Permission |
|---|---|
| `PlayAnimation`, `StopAnimation`: on the user's own avatar. Requests made in one frame go to the region together, in order. | `animations.play` |
| `Say`: a line in local chat as the user, through the same checks as the user's own chat; answers whether it was sent | `chat.send` |
| `PlayLocally(animation)`, `StopLocally()`: a preview on the user's own avatar in this viewer only. Nothing is sent to the region, so nobody else sees it; playing another ends the one before, and the viewer ends it when the add-on stops. | `animations.play` |

### Refusals

`AddFilter(kind, filter)` asks to be consulted before every action of that kind the user takes: `Sit` (on an object or
on the ground), `Stand`, `Teleport`. The filter answers `AddonVerdict.Allow` or `AddonVerdict.Refuse(reason)`; the first
refusal wins, nothing is sent, and the reason is shown to the user as a notice with the add-on's name. Disposing the
returned object removes the filter. Needs `filter`. Filters sit in the viewer's action pipeline, the single path every
user action takes to the grid, so no other way round them exists inside the viewer.

### The screen

`AddWindow(id, title)` adds a window (closed at first; `Open()` and `Close()` show and hide it) made of controls the
viewer draws in its own style: `AddonLabel`, `AddonButton` (`Pressed`), `AddonToggle` (`Toggled`), `AddonChoice` (a list
to pick from, `Chosen`), `AddonButtonRow`, `AddonTextField` (a one-line text box; `Value` is what the user typed) and
`AddonDropZone` (a place the user drops rows dragged from the inventory window on; `Dropped` gives each animation or
folder dropped, as `AddonDropped`: its id, whether it is a folder, and its name). A text field also raises `Submitted`
when the user presses Enter in it and `Cancelled` on Esc, and its `Focus()` asks for the keyboard. Changing a control's
`Text`, `Enabled`, `On`, `Items`, `Selected` or `Value` redraws it; `Insert`, `Remove` and `SetControls` change a
window's controls. A long window scrolls. `AddMenuItem(label, action)` adds an item to the Me menu; a label of the form
`"Group > Item"` puts the item in a submenu named `Group` (RLV adds `RLV > Restrictions…` this way). Both need `window`.

`AddQuickControl(control)` puts a switch or a choice list at the end of the viewer's toolbar row. **Not yet public:**
only add-ons that come with the viewer may use it; for any other add-on it throws `AddonPermissionException`. Needs
`window`.

### The dock (0.5)

`AddDockItem(id, label, kind, icon, tooltip, menu)` puts a button on the viewer's dock, in its Make group after Build, in
the order the add-ons ask. An add-on cannot draw its own controls on the main screen; this is how it gets a place there.
The animation overrider uses it for its AO button.

- `kind`: `Action` (a click runs something), `Toggle` (on and off) or `ToggleWithMenu` (on and off, with a menu on the
  button's chevron and on a right-click).
- `label`: the name under the button, ten characters at most; a longer one is cut to nine and an ellipsis.
- `icon`: the name of an icon from the viewer's set (the Lucide names in `host/Viewer.Shell/Ui/Reverie/LucideIcons.cs`, for example
  `person-standing`), or the text of a single-colour SVG starting with `<svg` (drawn in the button's text colour where it
  uses `currentColor`). Anything else, or none, shows the `plug` icon.
- `menu`: `AddonDockMenuEntry` rows (a label, an optional icon name, an optional tick, or a separator).

It answers null when refused: a third item from the same add-on, or an id it already used. The item it returns is the
add-on's handle: `SetOn`, `SetAttention(on, count)` (a ring that pulses, with an optional count in the name), `SetMenu`
and `Remove`; `Clicked` and `MenuChosen` (the entry's index) say what the user did. The add-on's items go the moment it
is turned off. Needs `window`.

### Settings

`GetSetting(key)` and `SetSetting(key, value)` keep strings in the add-on's own `settings.json`; `SettingsFolder` is its
own folder for anything else. No permission is needed: an add-on's folder is its own.

### Inventory

`Inventory` reads the user's inventory: folders, item names and descriptions, links, and the asset id of animations (so
an animation overrider can play the user's own animations). Read only. Needs `inventory.read`.

`ReadNotecardTextAsync` (0.3) gives the text of a notecard the user handed to the add-on: one the user dropped on one of
its drop zones, or one inside a folder the user dropped there, and only when the user may read it (the viewer's own notecard
window would show it). Items embedded in the card stand in the text as their names. Any other notecard, or one that could
not be read, answers null. An add-on never gets the notecard's file or any other asset. Needs `inventory.read`.

`InventoryWriter` changes the inventory inside the one folder the manifest names in `inventory_folder`, and nowhere
else: `CreateFolderAsync` (inside the folder, or the folders of its own path, one after another, when they are missing),
`LinkAsync` (a link inside the folder to any of the user's items: making it changes nothing of the item),
`RenameFolderAsync` and `MoveToTrashAsync` (inside the folder, never the folder itself). Moving to the Trash never purges.
A change that would fall outside answers false (or an empty id) and changes nothing. Needs `inventory.write`.

### Restraints (not yet public)

`Restraints` gives a built-in add-on the viewer's side of a restraint engine: `Publish` replaces the restrictions the viewer
enforces (`AddonRestriction`: the object, its name, the behaviour and its option, whether it is enforced, when it was set),
`Reply` says a line on a chat channel as the user past every restriction, `ForceSit`, `ForceStand` and `ForceTeleport` act
through the action pipeline passing over only the issuing object's own restrictions, and `ObjectPresent` says whether an
object is still in the viewer's object list. Everything published is dropped the moment the add-on stops, for any reason,
so an add-on that fails lets every action go ahead rather than trapping anyone. Needs `restraints`.

### Diagnostics

An add-on may also implement `IAddonDiagnostics`; only development builds of the viewer call it, for automated tests.

## Permissions

| Permission | What the user is told it may do |
|---|---|
| `session` | know when you log in or out and which region you are in |
| `motion` | know your avatar's motion and mouse-look |
| `animations.read` | see the animations your avatar plays |
| `animations.play` | play and stop animations on your avatar |
| `chat.read` | read local chat and your own objects' messages to you |
| `chat.send` | say things in local chat as you |
| `filter` | allow or refuse your sit, stand and teleport |
| `window` | add windows and menu items |
| `inventory.read` | read the names and folders of your inventory |
| `inventory.write` | make folders and links, rename folders and move things to the Trash, inside the one folder it names |
| `restraints` | (no text of its own yet: the Add-ons window lists it by its name) restrict what you can do, as RLV devices you wear ask; built-in add-ons only, for now |

Built-in add-ons are granted what they ask for. An installed add-on is granted its permissions only when the user turns
it on and accepts them, all together.

## RLV

RLV (the RestrainedLove API) comes with the viewer as the add-on filling the `rlv` job. It is **off** until the user ticks
it in **Me > Add-ons**, and the switch takes effect at the next login, both ways. While it is off it is not loaded at all,
and owner-say lines starting with `@` show in chat as in a viewer without RLV.

The protocol lives in the add-on: it reads the `@` command lines the user's own objects say to the user (several commands
per line, separated by commas; `behaviour[:option]=n|y|add|rem|force|<channel>`), keeps each object's restrictions apart,
drops an object's restrictions when the object is detached, deleted or gone from the region (checked every 30 seconds; an
object never seen goes after 20 checks), and answers `@version`, `@versionnew`, `@versionnum`, `@versionnumbl`,
`@getstatus`, `@getstatusall` and `@notify`. The version replies name this viewer and its own RLV version in RLVa's form,
for example `RestrainedLove viewer v3.4.3 (<the viewer's name> RLV 1.0.0)`. Command lines are kept out of the chat history
while RLV is on.

The enforcement lives in the viewer, in its action pipeline: locks (`@detach`, `@detach:<attachment point>`: Take Off and
Detach are greyed for a locked object, and taking it off is refused with a notice), sitting and standing (`@sit`, `@unsit`,
`@sittp`, `@standtp`), teleports (`@tplm`, `@tploc`, `@tplure` with its exceptions, `@accepttp`), chat (`@sendchat`,
`@recvchat`, `@recvchat_sec`, `@redirchat`, `@emote`, `@chatshout`, `@chatwhisper`, `@chatnormal`, `@sendchannel`) and IMs
(`@sendim`, `@recvim`, `@startim`), each with its exceptions, as RLVa behaves; and the forced actions `@sit:<key>=force`,
`@unsit=force` and `@tpto:<x>/<y>/<z>=force`. Any other behaviour RLVa knows is accepted, logged and listed as "not
enforced yet", and never claimed as enforced.

**Me > RLV > Restrictions…** lists every object holding restrictions, by name, with each behaviour, whether it is enforced
and when it was set. It is read only: there is no button that lifts a restriction. The way out is RLVa's: switch RLV off
in **Me > Add-ons** and log in again. `samples/rlv-test` holds a small script to try it with.

## What add-ons cannot do

Through the interface an add-on never gets texture, mesh or sound data, any asset's contents, or network messages, and it
cannot send anything to the grid except the actions above. It gets nothing beyond the permissions the user granted.
Content theft has no place here: no add-on may copy, export or capture other people's content.

## Writing one: the sample

`samples/HelloAddon` in the viewer's repository is a complete add-on and a template to copy: a menu item opens a small
window with a button, and the button says hello in local chat. It asks for `window` and `chat.send` only.

1. Copy the folder and rename the project, the class and the manifest's `id` and `name`.
2. Build it: `dotnet build -c Release` (outside the viewer's repository, add
   `-p:ViewerAddonsApi=<the viewer's folder>/Viewer.Addons.Api.dll`).
3. Copy `add-on.json` and your `.dll` from `bin/Release/net10.0/` into `Addons/<your id>/` in the viewer's user folder.
4. Start the viewer, open **Me > Add-ons**, turn your add-on on and accept its permissions.

## Security

Permissions guard the interface, not the computer. An add-on is a .NET library that runs in the viewer's own process, so
it can do anything the viewer can: read and write files, use the network, start programs. The permissions only decide
what the viewer itself hands it (your avatar's motion, chat, your inventory's names). Until the viewer has a sandbox for
add-ons, an add-on you install is like any program you install: turn on only add-ons from people you trust.

The viewer says this when you turn on an add-on you installed: the box reads that it "runs with full access to your
computer, like any program you install — only turn on add-ons you trust", and lists its permissions as what it says it
uses. Add-ons you installed are off until you turn them on. Add-ons that come with the viewer are on by default (RLV is
off until you turn it on) and show no box.

## Licences

- Add-ons of any licence, open or closed, free or sold, may be built against the interface (`Viewer.Addons.Api`) and
  loaded by the viewer. Building against the interface puts no licence on your add-on.
- The interface itself is part of the viewer and is licensed LGPL-2.1-or-later: changes to the interface are LGPL.
- The sample (`samples/HelloAddon`) is licensed 0BSD: copy it, change it and use it for anything, with no conditions.

## Versions of the interface

The interface version is `0.5`. Additions that do not break existing add-ons raise the minor version; anything else
raises the major version, and an add-on built for another major version is not loaded.
