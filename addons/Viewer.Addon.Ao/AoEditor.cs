// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addons;
using Viewer.Overrider;

namespace Viewer.Addon.Ao;

/// <summary>
/// The overrider's set editor: building a set needs no notecard and no naming rules. Animations or a folder dropped on the
/// editor open a review list where each animation shows the state its name suggests (or Unsorted), in a list the user can
/// change, with Skip; nothing is written until Add. Animations dragged onto a state's row are linked there directly. A state's
/// animations are listed: clicking one plays it on the user's own avatar in this viewer only, and Remove moves its link to the
/// Trash. Sets are made, copied, renamed and removed (to the Trash, after a confirm). Everything is written in the layout the
/// Firestorm viewer reads (<see cref="AoLibrary"/>), inside #Firestorm/#AO only.
/// </summary>
public sealed partial class AoAddon
{
    private const string Unsorted = "Unsorted", Skip = "Skip";

    private static readonly AoStateId[] States = Enum.GetValues<AoStateId>();
    private static readonly AoSet StateNames = new(Guid.Empty);

    private AddonWindow? _editor;
    private AddonLabel? _setLabel, _editorStatus;
    private AddonTextField? _newName;
    private AddonButtonRow? _newRow;
    private AddonButton? _make;
    private AddonChoice? _editSet;
    private Prompt _prompt;
    private bool _askingName => _prompt != Prompt.None;
    private Guid _awaiting;          // a set just made or copied that the inventory has not shown yet
    private double _awaitUntil, _nextLook;

    /// <summary>What the name prompt is asking for.</summary>
    private enum Prompt { None, New, Copy, Rename }
    private string? _laidOut;
    private AddonButtonRow? _setRow;
    private AddonDropZone? _drop;
    private AddonChoice? _show;
    private AddonDockItem? _dockItem;

    /// <summary>The dock button's menu: the sets, the one in use checked, then "Edit sets..." (the spec's set picker).</summary>
    private IReadOnlyList<AddonDockMenuEntry> DockMenu()
    {
        var current = _engine?.CurrentSet?.Name;
        var entries = _names.Select(n => new AddonDockMenuEntry(n, Checked: n == current)).ToList();
        if (entries.Count > 0)
            entries.Add(new AddonDockMenuEntry("", Separator: true));
        entries.Add(new AddonDockMenuEntry("Edit sets..."));
        return entries;
    }
    private IReadOnlyList<AoReviewRow>? _review;
    private Guid _confirmRemove;
    private AoStateId? _shown;
    private Guid _previewing;

    private static string StateName(AoStateId id) => StateNames.GetState(id).Name;

    /// <summary>The editor window, its menu entry and the quick-bar controls; called from <see cref="Start"/>.</summary>
    private void StartEditor(IAddonContext context)
    {
        _editor = context.AddWindow("editor", "Animation Overrider: sets and animations");
        _setLabel = new AddonLabel("set_label");
        // the sets, to pick the one being edited; a set just made, copied or renamed is picked here
        _editSet = new AddonChoice("edit_set") { Text = "Set:" };
        _editSet.Chosen += index =>
        {
            var sets = Library()?.SetFolders() ?? [];
            if (index >= 0 && index < sets.Count)
                _editing = sets[index].Id;
            Layout();
        };
        var newSet = new AddonButton("set_new") { Text = "New set" };
        var copy = new AddonButton("set_copy") { Text = "Copy" };
        var rename = new AddonButton("set_rename") { Text = "Rename" };
        var remove = new AddonButton("set_remove") { Text = "Remove set" };
        // New set, Copy and Rename ask for the name in a small prompt at the top of the editor, with the keyboard in its field:
        // Enter or the first button does it, Esc or Cancel closes the prompt
        _newName = new AddonTextField("new_set_name") { Text = "Name of the new set:" };
        _make = new AddonButton("new_set_make") { Text = "Make set" };
        var cancelNew = new AddonButton("new_set_cancel") { Text = "Cancel" };
        _make.Pressed += Confirm;
        _newName.Submitted += Confirm;
        cancelNew.Pressed += CancelPrompt;
        _newName.Cancelled += CancelPrompt;
        _newRow = new AddonButtonRow("new_set_row", _make, cancelNew);
        newSet.Pressed += () => Ask(Prompt.New, "");
        copy.Pressed += () => Ask(Prompt.Copy, "");
        rename.Pressed += () => Ask(Prompt.Rename, EditedSetName() ?? "");
        remove.Pressed += () => { _confirmRemove = EditedSet() ?? Guid.Empty; Layout(); };
        _setRow = new AddonButtonRow("set_row", newSet, copy, rename, remove);
        _drop = new AddonDropZone("drop") { Text = "Drop animations or a folder from your inventory here to add them to this set." };
        _drop.Dropped += Dropped;
        _show = new AddonChoice("show") { Text = "Animations of:" };
        _show.Chosen += index => { _shown = index >= 0 && index < _show.Items.Count ? StateByName(_show.Items[index]) : null; Layout(); };
        _editorStatus = new AddonLabel("editor_status");
        var edit = new AddonButton("edit") { Text = "Edit sets and animations…" };
        edit.Pressed += _editor.Open;
        _window!.Insert(_window.Controls.Count - 1, edit);
        _controlsButton = new AddonButton("switch_window") { Text = "On/off switch and stand controls…" };
        _controlsButton.Pressed += _window.Open;

        // the dock button (interface 0.5): a click turns the AO on or off; its menu lists the sets, the one in use checked, then
        // "Edit sets...", which opens this editor
        _dockItem = context.AddDockItem("ao", "AO", AddonDockKind.ToggleWithMenu, "person-standing", "Animation Overrider", DockMenu());
        if (_dockItem is not null)
        {
            _dockItem.SetOn(context.GetSetting(UseKey) == "true");
            _dockItem.Clicked += () => Use(!_dockItem.On);
            _dockItem.MenuChosen += index =>
            {
                if (index < _names.Length)
                    Select(_names[index]);
                else
                    _editor.Open();
            };
        }
        context.Tick += LookForAwaited;
        Layout();
    }

    private AddonButton? _controlsButton;

    /// <summary>Opens the name prompt for one of the three changes, with the keyboard in its field.</summary>
    private void Ask(Prompt prompt, string start)
    {
        if (_newName is null || _make is null)
            return;
        _prompt = prompt;
        (_newName.Text, _make.Text) = prompt switch
        {
            Prompt.Copy => ("Name of the copy:", "Make copy"),
            Prompt.Rename => ("New name of the set:", "Rename"),
            _ => ("Name of the new set:", "Make set"),
        };
        _newName.Value = start;
        Status("Type the name, then press Enter or " + _make.Text + ". Esc cancels.");
        Layout();
        _newName.Focus();
    }

    private void CancelPrompt()
    {
        if (_prompt == Prompt.None)
            return;
        _prompt = Prompt.None;
        Status("Nothing was changed.");
        Layout();
    }

    /// <summary>Enter or the prompt's first button: makes, copies or renames the set, and the editor then works on that set.</summary>
    private void Confirm()
    {
        string name = _newName?.Value.Trim() ?? "";
        if (_prompt == Prompt.None)
            return;
        if (name.Length == 0)
        {
            Status("Type a name first.");
            return;
        }
        var prompt = _prompt;
        _prompt = Prompt.None;
        const string Refused = "Choose a name no other set has, without a colon.";
        switch (prompt)
        {
            case Prompt.New:
                Write(async lib => Picked(await lib.NewSetAsync(name)) ? $"Set \"{name}\" made." : $"No set was made: \"{name}\" is taken or has a colon. " + Refused);
                break;
            case Prompt.Copy:
                Write(async lib => EditedSet() is { } set && Picked(await lib.DuplicateSetAsync(set, name)) ? $"Set copied as \"{name}\"." : "No copy was made. " + Refused);
                break;
            case Prompt.Rename:
                Rename(name);
                break;
        }
    }

    /// <summary>Makes a set just made the one being edited; true when there is one. The inventory may show it only after the grid has
    /// answered, so the editor looks for it again for a while (<see cref="LookForAwaited"/>).</summary>
    private bool Picked(Guid made)
    {
        if (made == Guid.Empty)
            return false;
        _editing = made;
        _awaiting = made;
        double now = _context?.Now ?? 0;
        _awaitUntil = now + 30;
        _nextLook = now + 1;
        return true;
    }

    /// <summary>Each frame while a new set is awaited: once the inventory shows it, the sets are read again and it is listed and picked.</summary>
    private void LookForAwaited()
    {
        if (_awaiting == Guid.Empty || _context is not { } context || context.Now < _nextLook)
            return;
        _nextLook = context.Now + 1;
        if ((Library()?.SetFolders() ?? []).Any(s => s.Id == _awaiting))
        {
            _awaiting = Guid.Empty;
            Reload();
            Layout();
        }
        else if (context.Now > _awaitUntil)
        {
            _awaiting = Guid.Empty;
            Status("The new set has not shown in your inventory yet. Press \"Read my sets again\" in the switch window to look again.");
        }
    }

    private string? EditedSetName() =>
        EditedSet() is { } e ? (Library()?.SetFolders() ?? []).Where(s => s.Id == e).Select(s => s.Name.Name).FirstOrDefault() : null;

    private AoLibrary? Library() =>
        _context?.Inventory is { } inventory && _context.InventoryWriter is { } writer ? new AoLibrary(new Inventory(inventory), new Writer(writer)) : null;

    /// <summary>The set the editor works on: the set chosen in the overrider's window, else the first.</summary>
    private Guid? EditedSet()
    {
        var sets = Library()?.SetFolders() ?? [];
        if (_editing != Guid.Empty && sets.Any(s => s.Id == _editing))
            return _editing;   // the set picked last, kept by its folder (a rename keeps it)
        if (_engine?.CurrentSet is { } current)
            return current.InventoryId;
        return sets is [var first, ..] ? first.Id : null;
    }

    private void Rename(string name)
    {
        Write(async lib =>
        {
            if (EditedSet() is not { } set || !await lib.RenameSetAsync(set, name))
                return "Choose a name no other set has, without a colon.";
            _context?.SetSetting(SetKey, name);
            if (_engine is { } engine)
                engine.SavedSetName = name;
            return "Set renamed.";
        });
    }

    /// <summary>Runs a change to the inventory, then reads the sets again and shows the result. The change finishes where it
    /// started (the viewer's main thread), so the window is drawn again from there.</summary>
    private void Write(Func<AoLibrary, Task<string>> change)
    {
        if (Library() is not { } lib)
        {
            Status("Log in to change your animation sets.");
            return;
        }
        _ = Run();

        async Task Run()
        {
            string said;
            try { said = await change(lib); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { said = "That change did not go through: " + ex.Message; }
            Status(said);
            Reload();
            Layout();
        }
    }

    private void Status(string text)
    {
        if (_editorStatus is not null)
            _editorStatus.Text = text;
    }

    // ---------------- dropping ----------------

    private void Dropped(IReadOnlyList<AddonDropped> dropped)
    {
        if (_context?.Inventory is not { } inventory)
            return;
        // a folder holding a notecard may be a ZHAO-II set: its card is read and, when it is one, the set is imported in one step
        var withCards = new List<(AddonDropped Folder, Guid Card)>();
        foreach (var d in dropped.Where(d => d.IsFolder))
        {
            if (!inventory.IsComplete(d.Id))
                inventory.Fetch(d.Id);
            if (inventory.Items(d.Id).FirstOrDefault(i => i.IsNotecard) is { Id: var card } && card != Guid.Empty)
                withCards.Add((d, card));
        }
        if (withCards.Count == 0)
        {
            Review(inventory, dropped);
            return;
        }
        _ = ImportCards(inventory, dropped, withCards);
    }

    /// <summary>Imports each dropped folder whose card reads as a ZHAO-II card as a new set named after the folder (AO-2's import: the
    /// card's animations found in the folder are linked, those missing are listed, never guessed); what is left goes to the review list.</summary>
    private async Task ImportCards(IAddonInventory inventory, IReadOnlyList<AddonDropped> dropped, List<(AddonDropped Folder, Guid Card)> withCards)
    {
        var imported = new HashSet<Guid>();
        var said = new List<string>();
        foreach (var (folder, card) in withCards)
        {
            string? text;
            try { text = await inventory.ReadNotecardTextAsync(card); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { text = null; }
            if (text is null || ZhaoNotecard.Parse(text).Lines.Count == 0 || Library() is not { } lib)
                continue;
            AoImportResult result;
            try { result = await lib.ImportAsync(folder.Id, folder.Name, text); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { said.Add("That import did not go through: " + ex.Message); continue; }
            if (result.SetFolder == Guid.Empty)
                continue;
            imported.Add(folder.Id);
            string line = $"Set \"{folder.Name}\" made from its notecard: {result.Linked} animations linked.";
            if (result.Missing.Count > 0)
                line += " Named in the card but not in the folder: " + string.Join(", ", result.Missing) + ".";
            said.Add(line);
        }
        var rest = dropped.Where(d => !imported.Contains(d.Id)).ToArray();
        if (rest.Length > 0)
            Review(inventory, rest);
        if (said.Count > 0)
            Status(string.Join(" ", said) + (_review is null ? "" : " " + (_editorStatus?.Text ?? "")));
        Reload();
        Layout();
    }

    /// <summary>Dropped animations and folders without a card go to the review list.</summary>
    private void Review(IAddonInventory inventory, IReadOnlyList<AddonDropped> dropped)
    {
        var animations = new List<(Guid Id, string Name)>();
        foreach (var d in dropped)
        {
            if (d.IsFolder)
                Collect(inventory, d.Id, animations, depth: 0);
            else
                animations.Add((d.Id, d.Name));
        }
        if (animations.Count == 0)
        {
            Status("No animations in what was dropped.");
            return;
        }
        _review = AoLibrary.Review(animations);
        Status("Check where each animation goes, then press Add. Nothing is changed before that.");
        Layout();
    }

    /// <summary>The animations in a folder and the folders inside it (a link counts as the animation it points to).</summary>
    private static void Collect(IAddonInventory inventory, Guid folder, List<(Guid, string)> into, int depth)
    {
        if (!inventory.IsComplete(folder))
            inventory.Fetch(folder);
        foreach (var item in inventory.Items(folder).Where(i => i.IsAnimation))
            into.Add((item.IsLink ? item.LinkedId : item.Id, item.Name));
        if (depth < 4)
            foreach (var sub in inventory.Folders(folder))
                Collect(inventory, sub.Id, into, depth + 1);
    }

    private void DroppedOnState(AoStateId state, IReadOnlyList<AddonDropped> dropped)
    {
        var items = new List<Guid>();
        if (_context?.Inventory is { } inventory)
        {
            foreach (var d in dropped)
            {
                if (!d.IsFolder)
                    items.Add(d.Id);
                else
                {
                    var found = new List<(Guid Id, string Name)>();
                    Collect(inventory, d.Id, found, depth: 0);
                    items.AddRange(found.Select(f => f.Id));
                }
            }
        }
        Write(async lib => EditedSet() is { } set ? $"{await lib.AddToStateAsync(set, state, items)} added to {StateName(state)}." : "Make a set first.");
    }

    private void AddReviewed()
    {
        var rows = _review;
        _review = null;
        if (rows is null)
            return;
        Write(async lib => EditedSet() is { } set ? $"{await lib.ApplyReviewAsync(set, rows)} animations added." : "Make a set first.");
        Layout();
    }

    // ---------------- the list of a state's animations ----------------

    private static AoStateId? StateByName(string name) => StateNames.GetStateByName(name)?.Num;

    /// <summary>The state folders of the edited set, by state, with the links each holds directly.</summary>
    private Dictionary<AoStateId, List<AddonItem>> StateLinks()
    {
        var found = new Dictionary<AoStateId, List<AddonItem>>();
        if (_context?.Inventory is not { } inventory || EditedSet() is not { } set)
            return found;
        foreach (var folder in inventory.Folders(set))
        {
            if (StateByName(AoFolderName.Parse(folder.Name).Name) is not { } state)
                continue;
            if (!found.TryGetValue(state, out var list))
                found[state] = list = [];
            list.AddRange(inventory.Items(folder.Id).Where(i => i.IsLink));
            // a group or a track counts as one entry each, listed by its folder's first animation
            foreach (var sub in inventory.Folders(folder.Id))
                list.AddRange(inventory.Items(sub.Id).Where(i => i.IsLink).Take(1));
        }
        return found;
    }

    private void Preview(Guid asset)
    {
        if (_context is not { } context || asset == Guid.Empty)
            return;
        if (_previewing == asset)
        {
            _previewing = Guid.Empty;
            context.StopLocally();
            return;
        }
        _previewing = asset;
        context.PlayLocally(asset);
    }

    // ---------------- drawing ----------------

    /// <summary>Puts the editor's controls in order for what it shows now.</summary>
    private void Layout()
    {
        if (_editor is null || _setLabel is null || _setRow is null || _drop is null || _show is null || _editorStatus is null)
            return;
        Guid? edited = EditedSet();
        var setFolders = Library()?.SetFolders() ?? [];
        string? setName = edited is { } e ? setFolders.Where(s => s.Id == e).Select(s => s.Name.Name).FirstOrDefault() : null;
        _setLabel.Text = setName is null ? "Make a set first: press New set." : $"Editing the set \"{setName}\".";
        var controls = new List<AddonControl>();
        if (_askingName && _newName is not null && _newRow is not null)
            controls.AddRange([_newName, _newRow]);
        if (_editSet is not null)
        {
            string[] names = setFolders.Select(s => s.Name.Name).ToArray();
            if (!_editSet.Items.SequenceEqual(names))
                _editSet.Items = names;
            int at = edited is { } id ? setFolders.ToList().FindIndex(s => s.Id == id) : -1;
            if (_editSet.Selected != at)
                _editSet.Selected = at;
            controls.Add(_editSet);
        }
        controls.AddRange([_setLabel, _setRow, _editorStatus]);   // the result of a change is said here, where it is seen
        if (_controlsButton is not null)
            controls.Add(_controlsButton);

        if (_confirmRemove != Guid.Empty)
        {
            var yes = new AddonButton("confirm_yes") { Text = "Move it to the Trash" };
            var no = new AddonButton("confirm_no") { Text = "Keep it" };
            Guid target = _confirmRemove;
            yes.Pressed += () => { _confirmRemove = Guid.Empty; Write(async lib => await lib.RemoveSetAsync(target, confirmed: true) ? "Set moved to the Trash." : "That set could not be removed."); };
            no.Pressed += () => { _confirmRemove = Guid.Empty; Layout(); };
            controls.Add(new AddonLabel("confirm_text") { Text = $"Remove \"{setName}\"? Its folder goes to the Trash; nothing is purged." });
            controls.Add(new AddonButtonRow("confirm_row", yes, no));
        }

        controls.Add(_drop);

        if (_review is { } review)
        {
            string[] choices = [.. States.Select(StateName), Unsorted, Skip];
            controls.Add(new AddonLabel("review_note") { Text = "Where each animation goes:" });
            for (int i = 0; i < review.Count; i++)
            {
                var row = review[i];
                var choice = new AddonChoice($"review_{i}") { Text = row.Name, Items = choices, Selected = row.Choice.Skip ? choices.Length - 1 : row.Choice.State is { } s ? Array.IndexOf(choices, StateName(s)) : choices.Length - 2 };
                choice.Chosen += index => row.Choice = index == choices.Length - 1 ? AoTarget.Skipped : index == choices.Length - 2 ? AoTarget.Unsorted : AoTarget.To(States[index]);
                controls.Add(choice);
            }
            var add = new AddonButton("review_add") { Text = "Add" };
            var cancel = new AddonButton("review_cancel") { Text = "Cancel" };
            add.Pressed += AddReviewed;
            cancel.Pressed += () => { _review = null; Status("Nothing was added."); Layout(); };
            controls.Add(new AddonButtonRow("review_row", add, cancel));
        }

        var links = StateLinks();
        foreach (var state in States)
        {
            int count = links.TryGetValue(state, out var l) ? l.Count : 0;
            var zone = new AddonDropZone($"state_{state}") { Text = $"{StateName(state)}: {count} — drop animations here" };
            zone.Dropped += d => DroppedOnState(state, d);
            controls.Add(zone);
        }

        string[] withAnimations = States.Where(links.ContainsKey).Select(StateName).ToArray();
        _show.Items = withAnimations;
        _show.Selected = _shown is { } shown ? Array.IndexOf(withAnimations, StateName(shown)) : -1;
        controls.Add(_show);
        if (_shown is { } showing && links.TryGetValue(showing, out var anims))
        {
            for (int i = 0; i < anims.Count; i++)
            {
                var item = anims[i];
                var play = new AddonButton($"anim_{i}_play") { Text = "▶ " + item.Name };
                var drop = new AddonButton($"anim_{i}_remove") { Text = "Remove" };
                play.Pressed += () => Preview(item.AssetId != Guid.Empty ? item.AssetId : _context?.Inventory?.AssetOf(item.Id) ?? Guid.Empty);
                drop.Pressed += () => Write(async lib => await lib.RemoveFromStateAsync(item.Id) ? "Moved to the Trash." : "That could not be removed.");
                controls.Add(new AddonButtonRow($"anim_{i}", play, drop));
            }
        }
        // redrawn only when what it shows has changed: a redraw replaces every control, so a click or a name being typed would be lost
        string laidOut = string.Join("\n", controls.Select(Describe));
        if (laidOut == _laidOut)
            return;
        _laidOut = laidOut;
        _editor.SetControls(controls);
    }

    /// <summary>What a control shows, for telling whether the editor's content changed (a label's or a button's own text changes in place).</summary>
    private static string Describe(AddonControl c) => c switch
    {
        AddonTextField => c.Id,
        AddonChoice choice => $"{c.Id}={c.Text}[{string.Join(",", choice.Items)}]@{choice.Selected}",
        AddonButtonRow row => c.Id + "(" + string.Join(",", row.Buttons.Select(b => b.Id + "=" + b.Text)) + ")",
        _ => c.Id + "=" + c.Text,
    };

    /// <summary>The add-on's inventory writer as the set editor needs it.</summary>
    private sealed class Writer(IAddonInventoryWriter writer) : IAoInventoryWriter
    {
        public Task<Guid> CreateFolderAsync(Guid parent, string name) => writer.CreateFolderAsync(parent, name);
        public Task<bool> LinkAsync(Guid folder, Guid target, string description) => writer.LinkAsync(folder, target, description);
        public Task<bool> RenameFolderAsync(Guid folder, string name) => writer.RenameFolderAsync(folder, name);
        public Task<bool> MoveToTrashAsync(Guid id, bool isFolder) => writer.MoveToTrashAsync(id, isFolder);
    }
}
