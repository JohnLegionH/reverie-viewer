// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addon.Ao;
using Viewer.Addons;
using Viewer.Core.Diagnostics;
using Viewer.Shell.Addons;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The overrider's set editor, driven as the user drives it: a folder dropped on the editor opens a review list and nothing
/// is written until Add; animations dragged onto a state are linked there; an animation clicked plays on the user's own
/// avatar only; sets are made, copied, renamed and removed (to the Trash, after a confirm); the switch and the set picker
/// sit in the quick bar. Through the real host with a fake inventory that takes writes. Pure, runs in parallel.
/// </summary>
public class AoEditorTests
{
    private static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");

    private sealed class Inv : IAddonInventory, IAddonInventoryWriter
    {
        public readonly Dictionary<Guid, (Guid Parent, string Name)> FolderById = [];
        public readonly Dictionary<Guid, (Guid Parent, AddonItem Item)> ItemById = [];
        public readonly List<Guid> Trashed = [];
        public int Writes;
        public Guid RootId { get; } = Guid.NewGuid();
        public bool IsComplete(Guid folder) => true;
        public void Fetch(Guid folder) { }
        /// <summary>When on, folders made through the writer stay out of sight until <see cref="Arrive"/> (the grid's answer comes later).</summary>
        public bool Lag;
        private readonly HashSet<Guid> _unseen = [];
        public void Arrive() => _unseen.Clear();
        public IReadOnlyList<AddonFolder> Folders(Guid folder) =>
            FolderById.Where(f => f.Value.Parent == folder && !Trashed.Contains(f.Key) && !_unseen.Contains(f.Key)).Select(f => new AddonFolder(f.Key, f.Value.Name)).ToArray();
        public IReadOnlyList<AddonItem> Items(Guid folder) =>
            ItemById.Values.Where(i => i.Parent == folder && !Trashed.Contains(i.Item.Id)).Select(i => i.Item).ToArray();
        public Guid AssetOf(Guid linkItemId) => ItemById.TryGetValue(linkItemId, out var i) ? i.Item.AssetId : Guid.Empty;
        public void Refresh(Guid folder) { }

        public readonly Dictionary<Guid, string> NotecardText = [];
        public readonly List<Guid> NotecardsRead = [];

        public Task<string?> ReadNotecardTextAsync(Guid itemId)
        {
            NotecardsRead.Add(itemId);
            return Task.FromResult(NotecardText.TryGetValue(itemId, out var text) ? text : null);
        }

        public Guid Notecard(Guid folder, string name, string text)
        {
            var id = Guid.NewGuid();
            ItemById[id] = (folder, new AddonItem(id, name, "", false, false, Guid.Empty, Guid.Empty, IsNotecard: true));
            NotecardText[id] = text;
            return id;
        }

        public Guid Folder(Guid parent, string name) { var id = Guid.NewGuid(); FolderById[id] = (parent, name); return id; }

        public Guid Animation(Guid folder, string name, Guid? asset = null)
        {
            var id = Guid.NewGuid();
            ItemById[id] = (folder, new AddonItem(id, name, "", false, true, Guid.Empty, asset ?? Guid.NewGuid()));
            return id;
        }

        public Guid Link(Guid folder, Guid target, string description)
        {
            var id = Guid.NewGuid();
            var t = ItemById[target].Item;
            ItemById[id] = (folder, new AddonItem(id, t.Name, description, true, true, target, t.AssetId));
            return id;
        }

        public Guid Path(params string[] names)
        {
            Guid at = RootId;
            foreach (string n in names)
                at = Folders(at).First(f => f.Name == n).Id;
            return at;
        }

        public IReadOnlyList<Guid> LinkedIn(Guid folder) => Items(folder).Where(i => i.IsLink).Select(i => i.LinkedId).ToArray();

        public Task<Guid> CreateFolderAsync(Guid parent, string name)
        {
            Writes++;
            var id = Folder(parent, name);
            if (Lag) _unseen.Add(id);
            return Task.FromResult(id);
        }
        public Task<bool> LinkAsync(Guid folder, Guid target, string description) { Writes++; Link(folder, target, description); return Task.FromResult(true); }
        public Task<bool> RenameFolderAsync(Guid folder, string name) { Writes++; FolderById[folder] = (FolderById[folder].Parent, name); return Task.FromResult(true); }
        public Task<bool> MoveToTrashAsync(Guid id, bool isFolder) { Writes++; Trashed.Add(id); return Task.FromResult(true); }
    }

    private sealed class Services : IAddonServices
    {
        public double Clock;
        public readonly Inv Inv = new();
        public readonly List<(Guid Animation, bool Start)> Sent = [];
        public readonly List<Guid?> Local = [];
        public double Now => Clock;
        public void SendAnimations(IReadOnlyList<(Guid Animation, bool Start)> requests) => Sent.AddRange(requests);
        public bool Say(string text) => false;
        public IAddonInventory? Inventory => Inv;
        public IAddonInventoryWriter? InventoryWriter => Inv;
        public void PlayLocally(Guid? animation) => Local.Add(animation);
    }

    private sealed class Rig
    {
        public readonly Services Services = new();
        public readonly AddonHost Host;
        public Inv Inv => Services.Inv;
        public readonly Guid SetStand = Guid.NewGuid();
        public readonly Guid Bought;
        public readonly Guid StandTwo, WalkF, Dance;

        public Rig()
        {
            var anims = Inv.Folder(Inv.RootId, "Animations");
            var standOne = Inv.Animation(anims, "stand one", SetStand);
            var set = Inv.Folder(Inv.Folder(Inv.Folder(Inv.RootId, "#Firestorm"), "#AO"), "Main:**");
            Inv.Link(Inv.Folder(set, "Standing"), standOne, "0");
            Bought = Inv.Folder(Inv.RootId, "Bought AO");
            StandTwo = Inv.Animation(Bought, "Stand 2");
            WalkF = Inv.Animation(Bought, "walk f");
            Dance = Inv.Animation(Bought, "Dance");

            Host = new AddonHost(Services, new CoreLog(new ListSink(), "test"));
            string manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "addons-ao", AddonManifest.FileName));
            Host.Register(AddonManifest.Parse(manifest, out _)!, () => new AoAddon(), builtIn: true);
            Host.StartEnabled();
            Host.LoggedIn();
            Host.Motion(MotionState.Standing);
            Host.Playing([new PlayingAnimation(Stand, 1)]);
            Settle();
        }

        /// <summary>Two frames five seconds apart: the engine reads the sets again after a change.</summary>
        public void Settle()
        {
            Host.Frame();
            Services.Clock += 5;
            Host.Frame();
        }

        public AddonWindow Editor => Assert.Single(Host.Windows, w => w.AddonId == "ao" && w.Window.Id == "editor").Window;
        public T Control<T>(string id) where T : AddonControl => Assert.IsType<T>(Assert.Single(Editor.Controls, c => c.Id == id));
        public void Run(Action action) => Host.Run("ao", action);
        public void Press(string id) => Run(() => Control<AddonButton>(id).Press());
        public void PressInRow(string row, string id) => Run(() => Control<AddonButtonRow>(row).Buttons.Single(b => b.Id == id).Press());
        public Guid MainSet => Inv.Path("#Firestorm", "#AO", "Main:**");
    }

    [Fact]
    public void A_dropped_folder_is_reviewed_with_guessed_states_and_written_only_on_Add()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        int writes = r.Inv.Writes;
        r.Run(() => r.Control<AddonDropZone>("drop").Drop([new AddonDropped(r.Bought, true, "Bought AO")]));

        var rows = r.Editor.Controls.OfType<AddonChoice>().Where(c => c.Id.StartsWith("review_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["Stand 2", "walk f", "Dance"], rows.Select(c => c.Text));
        Assert.Equal(["Standing", "Walking", "Unsorted"], rows.Select(c => c.Items[c.Selected]));
        Assert.Contains("Skip", rows[0].Items);
        Assert.Equal(writes, r.Inv.Writes);                                        // nothing written yet

        r.Run(() => rows[2].Choose(rows[2].Items.ToList().IndexOf("Skip")));
        r.PressInRow("review_row", "review_add");

        Assert.Equal(2, r.Inv.LinkedIn(r.Inv.Path("#Firestorm", "#AO", "Main:**", "Standing")).Count);
        Assert.Equal([r.WalkF], r.Inv.LinkedIn(r.Inv.Path("#Firestorm", "#AO", "Main:**", "Walking")));
        Assert.DoesNotContain(r.Inv.ItemById.Values, i => i.Item.IsLink && i.Item.LinkedId == r.Dance);
        Assert.DoesNotContain(r.Editor.Controls, c => c.Id.StartsWith("review_", StringComparison.Ordinal));
    }

    [Fact]
    public void Animations_dragged_onto_a_state_row_are_linked_there_with_no_review()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        r.Run(() => r.Control<AddonDropZone>("state_Walking").Drop(
            [new AddonDropped(r.WalkF, false, "walk f"), new AddonDropped(r.Dance, false, "Dance")]));

        Assert.Equal([r.WalkF, r.Dance], r.Inv.LinkedIn(r.Inv.Path("#Firestorm", "#AO", "Main:**", "Walking")));
        Assert.DoesNotContain(r.Editor.Controls, c => c.Id.StartsWith("review_", StringComparison.Ordinal));
        Assert.Contains("2", r.Control<AddonDropZone>("state_Walking").Text);
    }

    [Fact]
    public void Clicking_an_animation_previews_it_on_the_own_avatar_only_and_a_second_click_stops_it()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        r.Run(() => r.Control<AddonDropZone>("state_Standing").Drop([new AddonDropped(r.StandTwo, false, "Stand 2")]));
        r.Settle();
        var show = r.Control<AddonChoice>("show");
        r.Run(() => show.Choose(show.Items.ToList().IndexOf("Standing")));
        var standTwoAsset = r.Inv.ItemById[r.StandTwo].Item.AssetId;
        int sent = r.Services.Sent.Count;

        r.PressInRow("anim_0", "anim_0_play");
        r.PressInRow("anim_1", "anim_1_play");
        r.PressInRow("anim_1", "anim_1_play");

        Assert.Equal([r.SetStand, standTwoAsset, null], r.Services.Local);
        Assert.Equal(sent, r.Services.Sent.Count);                               // nothing went to the region
    }

    [Fact]
    public void The_editor_is_not_drawn_again_while_nothing_it_shows_has_changed()
    {
        // every redraw frees the editor's controls, so a click or a typed name in flight is lost: the overrider's own updates
        // (a stand cycling, a walk starting) must not redraw an editor whose content is the same
        var r = new Rig();
        r.Run(() => Assert.Single(r.Host.Windows, w => w.Window.Id == "overrider").Window.Controls.OfType<AddonToggle>().Single(c => c.Id == "use").Turn(true));
        r.Settle();
        int redraws = 0;
        r.Editor.Changed += () => redraws++;
        for (int i = 0; i < 6; i++)
        {
            r.Host.Motion(i % 2 == 0 ? MotionState.Walking : MotionState.Standing);
            r.Host.Playing([new PlayingAnimation(Stand, 2 + i)]);
            r.Services.Clock += 20;
            r.Host.Frame();
        }
        Assert.Equal(0, redraws);
    }

    [Fact]
    public void New_set_asks_for_a_name_with_the_field_focused_and_OK_makes_the_set()
    {
        var r = new Rig();
        r.PressInRow("set_row", "set_new");
        Assert.Contains(r.Editor.Controls, c => c.Id == "new_set_name");
        var asked = r.Control<AddonTextField>("new_set_name");
        Assert.Contains("name", asked.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(asked.FocusRequests > 0);
        r.Run(() => asked.Type("Test"));
        r.PressInRow("new_set_row", "new_set_make");
        r.Settle();
        Assert.Contains("Test", r.Inv.Folders(r.Inv.Path("#Firestorm", "#AO")).Select(f => f.Name));
        Assert.DoesNotContain(r.Editor.Controls, c => c.Id == "new_set_name");
        Assert.Contains("Test", r.Control<AddonLabel>("editor_status").Text);
    }

    [Fact]
    public void Enter_in_the_name_prompt_makes_the_set_and_the_new_set_is_listed_and_selected()
    {
        var r = new Rig();
        r.PressInRow("set_row", "set_new");
        var asked = r.Control<AddonTextField>("new_set_name");
        r.Run(() => asked.Type("Test"));
        r.Run(asked.Submit);
        r.Settle();
        Assert.Contains("Test", r.Inv.Folders(r.Inv.Path("#Firestorm", "#AO")).Select(f => f.Name));
        Assert.DoesNotContain(r.Editor.Controls, c => c.Id == "new_set_name");
        var sets = r.Control<AddonChoice>("edit_set");
        Assert.Contains("Test", sets.Items);
        Assert.Contains("Main", sets.Items);
        Assert.Equal("Test", sets.Items[sets.Selected]);
        Assert.Contains("Test", r.Control<AddonLabel>("set_label").Text);
    }

    [Fact]
    public void A_set_the_inventory_shows_only_later_is_listed_and_selected_when_it_arrives()
    {
        var r = new Rig();
        r.Inv.Lag = true;                       // the grid answers after the editor first reads the sets again
        r.PressInRow("set_row", "set_new");
        var asked = r.Control<AddonTextField>("new_set_name");
        r.Run(() => asked.Type("Later"));
        r.Run(asked.Submit);
        r.Settle();
        Assert.DoesNotContain("Later", r.Control<AddonChoice>("edit_set").Items);
        r.Inv.Arrive();
        r.Settle();
        r.Settle();
        var sets = r.Control<AddonChoice>("edit_set");
        Assert.Contains("Later", sets.Items);
        Assert.Equal("Later", sets.Items[sets.Selected]);
    }

    [Fact]
    public void Esc_or_Cancel_closes_the_name_prompt_and_makes_nothing()
    {
        var r = new Rig();
        r.PressInRow("set_row", "set_new");
        var asked = r.Control<AddonTextField>("new_set_name");
        r.Run(() => asked.Type("Nope"));
        r.Run(asked.Cancel);
        Assert.DoesNotContain(r.Editor.Controls, c => c.Id == "new_set_name");
        r.PressInRow("set_row", "set_new");
        r.PressInRow("new_set_row", "new_set_cancel");
        Assert.DoesNotContain(r.Editor.Controls, c => c.Id == "new_set_name");
        r.Settle();
        Assert.Equal(0, r.Inv.Writes);
    }

    [Fact]
    public void The_top_bar_holds_the_set_list_and_four_buttons_and_no_name_field()
    {
        var r = new Rig();
        var ids = r.Editor.Controls.Select(c => c.Id).ToList();
        Assert.DoesNotContain("name", ids);
        Assert.Contains("edit_set", ids);
        Assert.Equal(["set_new", "set_copy", "set_rename", "set_remove"], r.Control<AddonButtonRow>("set_row").Buttons.Select(b => b.Id));
    }

    [Fact]
    public void The_editors_result_line_sits_at_the_top_where_it_is_seen()
    {
        var r = new Rig();
        var ids = r.Editor.Controls.Select(c => c.Id).ToList();
        Assert.True(ids.IndexOf("editor_status") >= 0 && ids.IndexOf("editor_status") < ids.IndexOf("drop"), string.Join(",", ids));
    }

    [Fact]
    public void The_menu_item_and_the_dock_buttons_edit_sets_entry_open_the_set_editor_and_there_is_no_separate_AO_strip()
    {
        var r = new Rig();
        int opened = 0;
        r.Editor.OpenRequested += open => { if (open) opened++; };
        var item = Assert.Single(r.Host.MenuItems, m => m.AddonId == "ao");
        r.Run(item.Chosen);
        Assert.Equal(1, opened);
        Assert.Contains(r.Host.DockItems, d => d.AddonId == "ao");
        var dock = r.Host.DockItems.Single(d => d.AddonId == "ao").Item;
        Assert.Equal(("AO", AddonDockKind.ToggleWithMenu, "person-standing"), (dock.Label, dock.Kind, dock.Icon));
        Assert.NotEmpty(dock.Menu);
        Assert.Equal("Edit sets...", dock.Menu[^1].Label);
        r.Run(() => dock.ChooseMenu(dock.Menu.Count - 1));
        Assert.Equal(2, opened);
        Assert.DoesNotContain(r.Host.QuickControls, q => q.AddonId == "ao");   // the button is the AO's only place on the main screen
        Assert.Contains("animation", r.Editor.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("set", r.Editor.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sets_are_made_copied_renamed_and_removed_to_the_Trash_after_a_confirm()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        void Prompt(string button, string name)
        {
            r.PressInRow("set_row", button);
            var asked = r.Control<AddonTextField>("new_set_name");
            r.Run(() => asked.Type(name));
            r.Run(asked.Submit);
            r.Settle();
        }
        string Selected() { var c = r.Control<AddonChoice>("edit_set"); return c.Items[c.Selected]; }

        Prompt("set_new", "Beach");
        Assert.Contains("Beach", r.Inv.Folders(r.Inv.Path("#Firestorm", "#AO")).Select(f => f.Name));
        Assert.Equal("Beach", Selected());

        var editSet = r.Control<AddonChoice>("edit_set");
        r.Run(() => editSet.Choose(editSet.Items.ToList().IndexOf("Main")));
        Prompt("set_copy", "Main copy");          // copies the set being edited: Main
        var copy = r.Inv.Path("#Firestorm", "#AO", "Main copy");
        Assert.Single(r.Inv.LinkedIn(r.Inv.Path("#Firestorm", "#AO", "Main copy", "Standing")));
        Assert.Equal("Main copy", Selected());

        r.PressInRow("set_row", "set_rename");
        Assert.Equal("Main copy", r.Control<AddonTextField>("new_set_name").Value);   // a rename starts from the current name
        r.Run(() => r.Control<AddonTextField>("new_set_name").Type("Club"));
        r.Run(r.Control<AddonTextField>("new_set_name").Submit);
        r.Settle();
        Assert.Equal("Club", r.Inv.FolderById[copy].Name);
        Assert.Equal("Club", Selected());

        r.PressInRow("set_row", "set_remove");
        Assert.Empty(r.Inv.Trashed);                                              // not before the confirm
        r.PressInRow("confirm_row", "confirm_yes");
        Assert.Equal([copy], r.Inv.Trashed);
    }

    [Fact]
    public void Removing_an_animation_from_a_state_moves_its_link_to_the_Trash()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        var show = r.Control<AddonChoice>("show");
        r.Run(() => show.Choose(show.Items.ToList().IndexOf("Standing")));
        var link = r.Inv.Items(r.Inv.Path("#Firestorm", "#AO", "Main:**", "Standing")).Single().Id;
        r.PressInRow("anim_0", "anim_0_remove");
        Assert.Equal([link], r.Inv.Trashed);
    }

    [Fact]
    public void The_dock_button_turns_the_AO_on_and_off_and_its_menu_lists_the_sets_with_the_one_in_use_marked()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        Assert.Contains(r.Host.DockItems, d => d.AddonId == "ao");
        var dock = r.Host.DockItems.Single(d => d.AddonId == "ao").Item;
        Assert.Equal(["Main", "", "Edit sets..."], dock.Menu.Select(m => m.Label));
        Assert.True(dock.Menu[1].Separator);
        Assert.True(dock.Menu[0].Checked);
        Assert.True(dock.On);                                                     // on from the first run

        r.Run(dock.Click);                                                        // the button turns it off
        r.Host.Frame();
        Assert.False(dock.On);
        var main = (AddonToggle)Assert.Single(r.Host.Windows, w => w.Window.Id == "overrider").Window.Controls.Single(c => c.Id == "use");
        Assert.False(main.On);                                                    // the window's switch follows
        r.Run(dock.Click);
        r.Host.Frame();
        Assert.Contains((r.SetStand, true), r.Services.Sent);
        Assert.True(dock.On);
    }

    [Fact]
    public void A_dropped_folder_with_a_ZHAO_card_is_imported_as_a_new_set_in_one_step()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        var shop = r.Inv.Folder(r.Inv.RootId, "Shop AO");
        var stand1 = r.Inv.Animation(shop, "Stand 1");
        var stand2 = r.Inv.Animation(shop, "Stand 2");
        var walk = r.Inv.Animation(shop, "Walk 1");
        var card = r.Inv.Notecard(shop, "~ZHAO-II config", "[ Standing ]Stand 1|Stand 2\n[ Walking ]Walk 1\n[ Flying ]Fly gone|Stand 1\n");

        r.Run(() => r.Control<AddonDropZone>("drop").Drop([new AddonDropped(shop, true, "Shop AO")]));
        r.Settle();

        Assert.Equal([card], r.Inv.NotecardsRead);
        var set = r.Inv.Path("#Firestorm", "#AO", "Shop AO");
        Assert.Equal([stand1, stand2], r.Inv.LinkedIn(r.Inv.Path("#Firestorm", "#AO", "Shop AO", "Standing")));
        Assert.Equal([walk], r.Inv.LinkedIn(r.Inv.Path("#Firestorm", "#AO", "Shop AO", "Walking")));
        Assert.DoesNotContain(r.Editor.Controls, c => c.Id.StartsWith("review_", StringComparison.Ordinal));   // no review list
        string status = r.Control<AddonLabel>("editor_status").Text;
        Assert.Contains("Shop AO", status);
        Assert.Contains("Fly gone", status);                                                                  // listed, not guessed
        Assert.NotEqual(Guid.Empty, set);
    }

    [Fact]
    public void A_dropped_folder_whose_card_is_not_a_ZHAO_card_still_goes_through_the_review_list()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        r.Inv.Notecard(r.Bought, "Read me", "Thank you for your purchase!");
        r.Run(() => r.Control<AddonDropZone>("drop").Drop([new AddonDropped(r.Bought, true, "Bought AO")]));
        r.Settle();
        var rows = r.Editor.Controls.OfType<AddonChoice>().Where(c => c.Id.StartsWith("review_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["Stand 2", "walk f", "Dance"], rows.Select(c => c.Text));
        Assert.DoesNotContain(r.Inv.Folders(r.Inv.Path("#Firestorm", "#AO")), f => f.Name.StartsWith("Bought AO", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dropped_folder_whose_card_cannot_be_read_still_goes_through_the_review_list()
    {
        var r = new Rig();
        Assert.Equal("editor", r.Editor.Id);
        var card = r.Inv.Notecard(r.Bought, "~ZHAO-II config", "[ Standing ]Stand 2");
        r.Inv.NotecardText.Remove(card);                                                                       // the viewer gives no text
        r.Run(() => r.Control<AddonDropZone>("drop").Drop([new AddonDropped(r.Bought, true, "Bought AO")]));
        r.Settle();
        Assert.Equal([card], r.Inv.NotecardsRead);
        Assert.Equal(3, r.Editor.Controls.Count(c => c.Id.StartsWith("review_", StringComparison.Ordinal) && c is AddonChoice));
    }
}
