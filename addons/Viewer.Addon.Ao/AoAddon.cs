// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addons;
using Viewer.Overrider;

namespace Viewer.Addon.Ao;

/// <summary>
/// The animation overrider as an add-on: the engine (Viewer.Overrider) reads the user's animation sets from the inventory
/// folders the Firestorm viewer keeps them in and plays them in place of the default animations. This class only connects
/// it to add-on interface 0.1: the region's list of the avatar's animations goes in, animation requests come out, and its
/// window offers the switch, the set list, the previous and next animation, reading the sets again and the set editor
/// (AoEditor.cs); the switch and the set list are also in the viewer's quick bar. The switch and the set chosen are kept in
/// the add-on's own settings.
/// </summary>
public sealed partial class AoAddon : IAddon, IAddonDiagnostics
{
    private const string UseKey = "use", SetKey = "set";

    private IAddonContext? _context;
    private AoEngine? _engine;
    private AddonWindow? _window;
    private AddonToggle? _use;
    private AddonChoice? _sets;
    private AddonButton? _previous, _next, _reload;
    private AddonLabel? _status;
    private string[] _names = [];
    private Guid _editing;

    public void Start(IAddonContext context)
    {
        _context = context;
        _window = context.AddWindow("overrider", "Animation Overrider");
        _use = _window.Add(new AddonToggle("use") { Text = "Use my animation set", On = context.GetSetting(UseKey) == "true" });
        _use.Toggled += Use;
        _sets = _window.Add(new AddonChoice("sets") { Text = "Set:" });
        _sets.Chosen += index => { if (index >= 0 && index < _names.Length) Select(_names[index]); };
        _previous = new AddonButton("previous") { Text = "Previous" };
        _next = new AddonButton("next") { Text = "Next" };
        _previous.Pressed += () => Stand(false);
        _next.Pressed += () => Stand(true);
        _window.Add(new AddonButtonRow("stand_row", _previous, _next));
        _reload = _window.Add(new AddonButton("reload") { Text = "Read my sets again" });
        _reload.Pressed += Reload;
        _status = _window.Add(new AddonLabel("status"));
        context.AddMenuItem("Animation Overrider", () => _editor?.Open());   // the set editor; its button opens the switch and stand controls
        StartEditor(context);

        context.LoggedIn += Attach;
        context.LoggedOut += Detach;
        context.RegionChanged += _ => _engine?.RegionChanged();   // the region entered is asked again for what the avatar plays
        context.MouselookChanged += on => _engine?.InMouselook(on);
        context.PlayingChanged += Signalled;
        context.Tick += () => _engine?.Tick();
        if (context.LoggedInNow)
            Attach();
        Show();
    }

    /// <summary>Switched off: the engine stops what it plays (as turning the switch off does), and is dropped. The switch itself is kept.</summary>
    public void Stop()
    {
        _engine?.SetUseAo(false);
        _engine = null;
        _context = null;
        _review = null;
        _previewing = Guid.Empty;
    }

    /// <summary>The user's own avatar as the engine needs it.</summary>
    private sealed class Agent(IAddonContext context) : IAoAgent
    {
        public void SendAnimation(Guid animation, bool start)
        {
            if (start) context.PlayAnimation(animation);
            else context.StopAnimation(animation);
        }

        public bool SittingOnObject => context.Motion == MotionState.SittingOnObject;

        // which object started an animation on the avatar is not known, so a seat's own animation is not told apart
        public bool ForeignAnimationOnSeat => false;

        public bool IsSignalled(Guid animation) => context.Playing.Any(p => p.Animation == animation);
    }

    /// <summary>The inventory as the engine reads it.</summary>
    private sealed class Inventory(IAddonInventory inventory) : IAoInventory
    {
        public Guid RootId => inventory.RootId;
        public bool IsComplete(Guid folder) => inventory.IsComplete(folder);
        public void Fetch(Guid folder) => inventory.Fetch(folder);
        public IReadOnlyList<AoFolder> Folders(Guid folder) => inventory.Folders(folder).Select(f => new AoFolder(f.Id, f.Name)).ToArray();
        public IReadOnlyList<AoItem> Items(Guid folder) =>
            inventory.Items(folder).Select(i => new AoItem(i.Id, i.Name, i.Description, i.IsLink, i.IsAnimation, i.LinkedId, i.AssetId)).ToArray();
        public Guid AssetOf(Guid linkItemId) => inventory.AssetOf(linkItemId);
    }

    private void Attach()
    {
        if (_context is not { } context || _engine is not null || context.Inventory is not { } inventory)
            return;
        var engine = new AoEngine(new Inventory(inventory), new Agent(context), () => context.Now)
        {
            SavedSetName = context.GetSetting(SetKey),
        };
        engine.SetUseAo(context.GetSetting(UseKey) == "true");
        engine.Updated += Show;
        _engine = engine;
        engine.LoginComplete();
        Signalled(context.Playing);
        Show();
    }

    private void Detach()
    {
        _engine = null;
        Show();
    }

    private void Signalled(IReadOnlyList<PlayingAnimation> playing)
    {
        // an avatar always plays something: an empty list is one that has not arrived yet (a region just entered)
        if (playing.Count > 0)
            _engine?.SignalledChanged(playing.Select(p => (p.Animation, p.Sequence)).ToArray());
    }

    private void Use(bool use)
    {
        _context?.SetSetting(UseKey, use ? "true" : "false");
        _engine?.SetUseAo(use);
        Show();
    }

    private bool Select(string name)
    {
        if (_engine?.SelectSetByName(name) is null)
            return false;
        _engine.SavedSetName = name;
        _editing = _engine.CurrentSet?.InventoryId ?? Guid.Empty;
        _context?.SetSetting(SetKey, name);
        Show();
        return true;
    }

    private void Stand(bool next) => _engine?.Cycle(next ? AoCycleMode.Next : AoCycleMode.Previous, true);

    /// <summary>Reads the sets from the inventory again (the inventory's top folder and the set folders are asked for again first).</summary>
    private void Reload()
    {
        if (_engine is not { } engine || _context?.Inventory is not { } inventory)
            return;
        inventory.Refresh(inventory.RootId);   // a top folder made since login is seen
        if (inventory.Folders(inventory.RootId).FirstOrDefault(f => f.Name == "#Firestorm") is { Id: var top } && top != Guid.Empty)
        {
            var folders = new List<Guid> { top };
            for (int i = 0; i < folders.Count; i++)
                folders.AddRange(inventory.Folders(folders[i]).Select(f => f.Id));
            foreach (Guid id in folders)
                inventory.Refresh(id);
        }
        engine.Reload();
        Show();
    }

    private void Show()
    {
        if (_use is null || _sets is null || _previous is null || _next is null || _reload is null || _status is null)
            return;
        var engine = _engine;
        bool attached = engine is not null;
        bool use = engine?.UseAo ?? _context?.GetSetting(UseKey) == "true";
        bool loaded = engine?.Loaded ?? false;
        _names = engine?.Sets.Select(s => s.Name).ToArray() ?? [];
        _use.On = use;
        _sets.Items = _names;
        _sets.Selected = Array.IndexOf(_names, engine?.CurrentSet?.Name);
        bool usable = attached && loaded && _names.Length > 0;
        _sets.Enabled = usable;
        _previous.Enabled = _next.Enabled = usable && use;
        _reload.Enabled = attached;
        if (_quickUse is not null && _quickSet is not null)
        {
            _quickUse.On = use;
            _quickSet.Items = _names;
            _quickSet.Selected = _sets.Selected;
            _quickSet.Enabled = usable;
        }
        Layout();
        _status.Text = !attached ? "Log in to use your animation sets."
            : !loaded ? "Reading your animation sets from your inventory…"
            : _names.Length == 0 ? "No animation sets found. Sets are read from the #AO folder inside the #Firestorm folder of your inventory."
            : "Previous and Next change the animation of what your avatar is doing now, such as the stand.";
    }

    /// <summary>The overrider as development tools see and drive it: ids, counts and state names, never a set's or an animation's name.</summary>
    public object? Call(string action, string? text)
    {
        if (_engine is not { } engine)
            return null;
        switch (action)
        {
            case "on": Use(true); break;
            case "off": Use(false); break;
            case "next": Stand(true); break;
            case "previous": Stand(false); break;
            case "reload": Reload(); break;
            case "review_add": AddReviewed(); break;
            case "review_cancel": _review = null; Layout(); break;
            case "newset":       // New set, the name typed and Enter pressed in the prompt
                if (_newName is not null && _setRow is not null)
                {
                    _setRow.Buttons.Single(b => b.Id == "set_new").Press();
                    _newName.Type(text ?? "");
                    _newName.Submit();
                }
                break;
            case "newset_ask":   // New set pressed: the editor asks for a name
                _setRow?.Buttons.Single(b => b.Id == "set_new").Press();
                break;
            case "prompt_cancel":   // Esc in the name prompt
                _newName?.Cancel();
                break;
            case "newset_make":   // the asked-for name typed and Make set pressed
                if (_newName is not null && _newRow is not null)
                {
                    _newName.Type(text ?? "");
                    _newRow.Buttons.Single(b => b.Id == "new_set_make").Press();
                }
                break;
            case "set":
                if (!int.TryParse(text, out int index) || index < 0 || index >= engine.Sets.Count || !Select(engine.Sets[index].Name))
                    return new { error = "set" };
                break;
        }
        var current = engine.CurrentSet;
        var review = _review;
        return new
        {
            review = review?.Select(r => new
            {
                guess = r.Guess?.ToString(),
                choice = r.Choice.Skip ? "skip" : r.Choice.State?.ToString() ?? "unsorted",
            }).ToArray(),
            editor_status = _editorStatus?.Text,
            asking_name = _askingName,
            name_focus_requests = _newName?.FocusRequests ?? 0,
            edited_set = _editSet is { Selected: >= 0 } picked && picked.Selected < picked.Items.Count ? picked.Selected : -1,
            edit_sets = _editSet?.Items.Count ?? 0,
            edited_id = EditedSet()?.ToString(),
            set_ids = (Library()?.SetFolders() ?? []).Select(s => s.Id.ToString()).ToArray(),
            editor_controls = _editor?.Controls.Select(c => c.Id).ToArray() ?? [],
            quick = _quickUse is not null && _quickSet is not null,
            use = engine.UseAo,
            enabled = engine.Enabled,
            loaded = engine.Loaded,
            sets = engine.Sets.Count,
            current = current is null ? -1 : engine.Sets.ToList().IndexOf(current),
            sit_override = current?.SitOverride ?? false,
            state = engine.CurrentState?.Name,
            signalled = _context?.Playing.Select(p => p.Animation).ToArray() ?? [],
            states = current is null ? [] : Enum.GetValues<AoStateId>().Select(current.GetState).Where(s => s.Steps.Count > 0 || s.Tracks.Count > 0).Select(s => new
            {
                state = s.Name,
                cycle = s.Cycle,
                random = s.Random,
                cycle_time = s.CycleTime,
                step = s.CurrentAnimation,
                steps = s.Steps.Select(step => step.Members.Select(m => m.AssetId).ToArray()).ToArray(),
                playing = s.CurrentAnimationIds.ToArray(),
            }).ToArray(),
        };
    }
}
