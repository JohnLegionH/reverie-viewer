// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Overrider;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Building animation sets without notecards or name rules: the state guesser's table, the ZHAO-II notecard reader, and
/// the set editor, which writes the inventory layout the Firestorm viewer reads (aoengine.cpp: getSetFolderName,
/// getStateFolderName, addAnimation's sort order in the link description) and reads what Firestorm wrote, keeping what it
/// does not know. A fake inventory that both answers reads and takes writes. Pure, runs in parallel.
/// </summary>
public class AoSetEditingTests
{
    /// <summary>An inventory the overrider reads and the editor writes. Moves to the Trash are recorded, nothing is purged.</summary>
    private sealed class Inv : IAoInventory, IAoInventoryWriter
    {
        public sealed class Folder(Guid id, Guid parent, string name)
        {
            public Guid Id = id, Parent = parent;
            public string Name = name;
        }

        public readonly Dictionary<Guid, Folder> FolderById = [];
        public readonly Dictionary<Guid, (Guid Folder, AoItem Item)> ItemById = [];
        public readonly List<Guid> Trashed = [];
        public readonly List<string> Writes = [];
        public Guid RootId { get; } = Guid.NewGuid();

        public bool IsComplete(Guid folder) => true;
        public void Fetch(Guid folder) { }
        public IReadOnlyList<AoFolder> Folders(Guid folder) =>
            FolderById.Values.Where(f => f.Parent == folder && !Trashed.Contains(f.Id)).Select(f => new AoFolder(f.Id, f.Name)).ToArray();
        public IReadOnlyList<AoItem> Items(Guid folder) =>
            ItemById.Values.Where(i => i.Folder == folder && !Trashed.Contains(i.Item.Id)).Select(i => i.Item).ToArray();
        public Guid AssetOf(Guid linkItemId) => ItemById.TryGetValue(linkItemId, out var i) ? i.Item.AssetId : Guid.Empty;

        public Guid MakeFolder(Guid parent, string name)
        {
            var id = Guid.NewGuid();
            FolderById[id] = new Folder(id, parent, name);
            return id;
        }

        /// <summary>An animation item (not a link) in a folder.</summary>
        public Guid Animation(Guid folder, string name)
        {
            var id = Guid.NewGuid();
            ItemById[id] = (folder, new AoItem(id, name, "", false, true, Guid.Empty, Guid.NewGuid()));
            return id;
        }

        public Guid Notecard(Guid folder, string name)
        {
            var id = Guid.NewGuid();
            ItemById[id] = (folder, new AoItem(id, name, "", false, false, Guid.Empty, Guid.Empty));
            return id;
        }

        /// <summary>A link to an animation item, as Firestorm makes it.</summary>
        public Guid LinkTo(Guid folder, Guid target, string description)
        {
            var id = Guid.NewGuid();
            var t = ItemById[target].Item;
            ItemById[id] = (folder, new AoItem(id, t.Name, description, true, true, target, t.AssetId));
            return id;
        }

        public Guid Path(params string[] names)
        {
            Guid at = RootId;
            foreach (string n in names)
                at = Folders(at).First(f => f.Name == n).Id;
            return at;
        }

        public string NameOf(Guid folder) => FolderById[folder].Name;
        public IReadOnlyList<AoItem> LinksIn(Guid folder) => Items(folder).Where(i => i.IsLink).ToArray();

        Task<Guid> IAoInventoryWriter.CreateFolderAsync(Guid parent, string name)
        {
            Writes.Add($"folder {name}");
            return Task.FromResult(MakeFolder(parent, name));
        }

        Task<bool> IAoInventoryWriter.LinkAsync(Guid folder, Guid target, string description)
        {
            Writes.Add($"link {description}");
            LinkTo(folder, target, description);
            return Task.FromResult(true);
        }

        Task<bool> IAoInventoryWriter.RenameFolderAsync(Guid folder, string name)
        {
            Writes.Add($"rename {name}");
            FolderById[folder].Name = name;
            return Task.FromResult(true);
        }

        Task<bool> IAoInventoryWriter.MoveToTrashAsync(Guid id, bool isFolder)
        {
            Writes.Add($"trash {(isFolder ? "folder" : "item")}");
            Trashed.Add(id);
            return Task.FromResult(true);
        }
    }

    private sealed class Agent : IAoAgent
    {
        public void SendAnimation(Guid animation, bool start) { }
        public bool SittingOnObject => false;
        public bool ForeignAnimationOnSeat => false;
        public bool IsSignalled(Guid animation) => false;
    }

    /// <summary>The overrider's engine reading the inventory, as at login.</summary>
    private static AoEngine Read(Inv inv)
    {
        double now = 0;
        var engine = new AoEngine(inv, new Agent(), () => now, () => 0);
        engine.LoginComplete();
        engine.Tick();
        now += 5;
        engine.Tick();
        return engine;
    }

    // ---------------- the guesser ----------------

    public static TheoryData<string, AoStateId?> Names => new()
    {
        { "Stand.1", AoStateId.Standing },
        { "Walk.N", AoStateId.Walking },
        { "Sit.G", AoStateId.SittingOnGround },
        { "Jump.P", AoStateId.PreJumping },
        { "Hover.U", AoStateId.FlyingUp },
        { "AO Stand 03", AoStateId.Standing },
        { "MyStand_v2", AoStateId.Standing },
        { "stands - relaxed", AoStateId.Standing },
        { "Female walk 2", AoStateId.Walking },
        { "walking slow", AoStateId.Walking },
        { "Run fast", AoStateId.Running },
        { "sit chair 1", AoStateId.Sitting },
        { "gsit cross legs", AoStateId.SittingOnGround },
        { "ground sit 2", AoStateId.SittingOnGround },
        { "SitGround", AoStateId.SittingOnGround },
        { "Fly", AoStateId.Flying },
        { "flying slow", AoStateId.FlyingSlow },
        { "Hover", AoStateId.Hovering },
        { "fly up", AoStateId.FlyingUp },
        { "Up", AoStateId.FlyingUp },
        { "down", AoStateId.FlyingDown },
        { "prejump", AoStateId.PreJumping },
        { "pre-jump", AoStateId.PreJumping },
        { "Jump", AoStateId.Jumping },
        { "Land", AoStateId.Landing },
        { "soft land", AoStateId.SoftLanding },
        { "Fall", AoStateId.Falling },
        { "stand up", AoStateId.StandingUp },
        { "StandUp", AoStateId.StandingUp },
        { "turn left", AoStateId.TurningLeft },
        { "TurnRight", AoStateId.TurningRight },
        { "typing hands", AoStateId.Typing },
        { "Type", AoStateId.Typing },
        { "swim", AoStateId.SwimmingForward },
        { "swim up", AoStateId.SwimmingUp },
        { "Float", AoStateId.Floating },
        { "Crouch", AoStateId.Crouching },
        { "crouch walk", AoStateId.CrouchWalking },
        // nothing to go on, or a word that cannot say which state: Unsorted, never a guess
        { "Dance 1", null },
        { "Hug", null },
        { "turn", null },
        { "Landmark", null },
        { "Outstanding", null },
        { "", null },
        { "12345", null },
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void The_guesser_reads_the_state_from_common_names_and_sends_the_rest_to_Unsorted(string name, AoStateId? expected)
    {
        Assert.Equal(expected, AoStateGuess.Guess(name));
    }

    [Fact]
    public void The_guess_table_names_every_state_the_order_lists()
    {
        var states = AoStateGuess.Table.Select(r => r.State).ToHashSet();
        foreach (var s in new[] { AoStateId.Standing, AoStateId.Walking, AoStateId.Running, AoStateId.Sitting, AoStateId.SittingOnGround,
                     AoStateId.Flying, AoStateId.Hovering, AoStateId.FlyingUp, AoStateId.FlyingDown, AoStateId.Jumping, AoStateId.PreJumping,
                     AoStateId.Landing, AoStateId.Falling, AoStateId.TurningLeft, AoStateId.TurningRight, AoStateId.Typing,
                     AoStateId.SwimmingForward, AoStateId.Floating, AoStateId.Crouching })
            Assert.Contains(s, states);
    }

    // ---------------- the ZHAO notecard ----------------

    [Fact]
    public void A_ZHAO_notecard_gives_each_states_steps_with_groups_and_lists_what_it_cannot_read()
    {
        const string card = """
            # a comment
            [ Stand.1 ]stand one|stand two
            [ Walk.N ] walk a , walk b |walk c
            [ Sit.G ]gsit

            [ Dancing ]dance
            no bracket here
            [ Typing ]
            [ Standing ]stand three
            """;
        var parsed = ZhaoNotecard.Parse(card);

        Assert.Equal([AoStateId.Standing, AoStateId.Walking, AoStateId.SittingOnGround, AoStateId.Standing], parsed.Lines.Select(l => l.State));
        Assert.Equal([["stand one"], ["stand two"]], parsed.Lines[0].Steps.Select(s => s.ToArray()));
        Assert.Equal([["walk a", "walk b"], ["walk c"]], parsed.Lines[1].Steps.Select(s => s.ToArray()));
        Assert.Equal([["gsit"]], parsed.Lines[2].Steps.Select(s => s.ToArray()));
        Assert.Equal(["Dancing"], parsed.UnknownStates);
        Assert.Equal([7], parsed.BadLines);   // the line number, counted from 1
    }

    // ---------------- the review list ----------------

    [Fact]
    public async Task Dropped_animations_are_reviewed_first_and_nothing_is_written_until_Add()
    {
        var inv = new Inv();
        var lib = new AoLibrary(inv, inv);
        var src = inv.MakeFolder(inv.RootId, "Bought AO");
        var a = inv.Animation(src, "Stand 1");
        var b = inv.Animation(src, "walk f");
        var c = inv.Animation(src, "Dance 3");
        var d = inv.Animation(src, "Stand 2");
        var set = await lib.NewSetAsync("Mine");
        int writesBefore = inv.Writes.Count;

        var rows = AoLibrary.Review(new[] { a, b, c, d }.Select(id => (id, inv.ItemById[id].Item.Name)));

        Assert.Equal([AoStateId.Standing, AoStateId.Walking, null, AoStateId.Standing], rows.Select(r => r.Guess));
        Assert.Equal(AoTarget.Unsorted, rows[2].Choice);
        Assert.Equal(writesBefore, inv.Writes.Count);           // reviewing writes nothing

        rows[1].Choice = AoTarget.To(AoStateId.Running);       // the user changes a guess
        rows[3].Choice = AoTarget.Skipped;                      // and skips one
        int made = await lib.ApplyReviewAsync(set, rows);

        Assert.Equal(3, made);
        Assert.Equal([a], inv.LinksIn(inv.Path("#Firestorm", "#AO", "Mine", "Standing")).Select(l => l.LinkedId));
        Assert.Equal([b], inv.LinksIn(inv.Path("#Firestorm", "#AO", "Mine", "Running")).Select(l => l.LinkedId));
        Assert.Equal([c], inv.LinksIn(inv.Path("#Firestorm", "#AO", "Mine", "Unsorted")).Select(l => l.LinkedId));
        Assert.DoesNotContain(inv.ItemById.Values, i => i.Item.IsLink && i.Item.LinkedId == d);
        Assert.Empty(inv.Trashed);
    }

    [Fact]
    public async Task Animations_dragged_onto_a_state_are_linked_after_the_ones_there_and_never_copied()
    {
        var inv = new Inv();
        var lib = new AoLibrary(inv, inv);
        var src = inv.MakeFolder(inv.RootId, "Anims");
        var x = inv.Animation(src, "x");
        var y = inv.Animation(src, "y");
        var z = inv.Animation(src, "z");
        var set = await lib.NewSetAsync("S");

        Assert.Equal(2, await lib.AddToStateAsync(set, AoStateId.Standing, [x, y]));
        Assert.Equal(1, await lib.AddToStateAsync(set, AoStateId.Standing, [z]));

        var links = inv.LinksIn(inv.Path("#Firestorm", "#AO", "S", "Standing"));
        Assert.Equal([x, y, z], links.Select(l => l.LinkedId));
        Assert.Equal(["0", "1", "2"], links.Select(l => l.Description));
        Assert.Equal(3, inv.Items(src).Count);                 // the animations stay where they were
        Assert.Equal(3, Read(inv).Sets.Single().GetState(AoStateId.Standing).Steps.Count);
    }

    // ---------------- sets ----------------

    [Fact]
    public async Task Sets_are_made_duplicated_and_renamed_and_removing_moves_to_the_Trash_after_a_confirm()
    {
        var inv = new Inv();
        var lib = new AoLibrary(inv, inv);
        var src = inv.MakeFolder(inv.RootId, "Anims");
        var x = inv.Animation(src, "x");
        var y = inv.Animation(src, "y");

        var set = await lib.NewSetAsync("Beach");
        Assert.NotEqual(Guid.Empty, set);
        Assert.Equal(Guid.Empty, await lib.NewSetAsync("Beach"));          // taken
        Assert.Equal(Guid.Empty, await lib.NewSetAsync("a:b"));            // a colon starts the options
        Assert.Equal(Guid.Empty, await lib.NewSetAsync("  "));
        await lib.AddToStateAsync(set, AoStateId.Standing, [x, y]);
        await lib.SetSetOptionsAsync(set, sitOverride: true, smart: false, mouselookStandDisable: false);
        await lib.SetStateOptionsAsync(set, AoStateId.Standing, cycle: true, random: false, cycleTime: 15f);

        var copy = await lib.DuplicateSetAsync(set, "Beach 2");
        Assert.NotEqual(Guid.Empty, copy);
        Assert.Equal("Beach 2:SO", inv.NameOf(copy));
        var copied = inv.LinksIn(inv.Path("#Firestorm", "#AO", "Beach 2:SO", "Standing:CT15.00:CY"));
        Assert.Equal([x, y], copied.Select(l => l.LinkedId));
        Assert.Equal(["0", "1"], copied.Select(l => l.Description));

        Assert.True(await lib.RenameSetAsync(copy, "Club"));
        Assert.Equal("Club:SO", inv.NameOf(copy));
        Assert.False(await lib.RenameSetAsync(copy, "Beach"));             // taken

        var link = inv.LinksIn(inv.Path("#Firestorm", "#AO", "Club:SO", "Standing:CT15.00:CY"))[0].Id;
        Assert.True(await lib.RemoveFromStateAsync(link));
        Assert.Equal([link], inv.Trashed);

        Assert.False(await lib.RemoveSetAsync(copy, confirmed: false));
        Assert.Equal([link], inv.Trashed);
        Assert.True(await lib.RemoveSetAsync(copy, confirmed: true));
        Assert.Equal([link, copy], inv.Trashed);
        Assert.DoesNotContain(inv.Writes, w => w.StartsWith("purge"));
        Assert.Equal(["Beach"], Read(inv).Sets.Select(s => s.Name));
    }

    // ---------------- Firestorm's layout, both ways ----------------

    [Fact]
    public async Task A_set_made_here_has_the_names_Firestorm_writes_and_reads_back_with_its_options()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");   // a comma for decimals must not reach a folder name
        try
        {
            var inv = new Inv();
            var lib = new AoLibrary(inv, inv);
            var src = inv.MakeFolder(inv.RootId, "Anims");
            var x = inv.Animation(src, "x");

            var set = await lib.NewSetAsync("Beach");
            Assert.NotEqual(Guid.Empty, set);
            await lib.AddToStateAsync(set, AoStateId.SittingOnGround, [x]);
            await lib.SetSetOptionsAsync(set, sitOverride: true, smart: true, mouselookStandDisable: true);
            await lib.SetStateOptionsAsync(set, AoStateId.SittingOnGround, cycle: true, random: true, cycleTime: 20.5f);

            Assert.Equal("#AO", inv.NameOf(inv.Path("#Firestorm", "#AO")));
            Assert.Equal("Beach:SO:SM:DM", inv.NameOf(set));
            Assert.Equal(["Sitting On Ground:CT20.50:CY:RN"], inv.Folders(set).Select(f => f.Name));
            Assert.Equal(["0"], inv.Items(inv.Folders(set)[0].Id).Select(i => i.Description));

            var read = Read(inv).Sets.Single();
            Assert.Equal("Beach", read.Name);
            Assert.True(read.SitOverride && read.Smart && read.MouselookStandDisable);
            var gsit = read.GetState(AoStateId.SittingOnGround);
            Assert.True(gsit.Cycle && gsit.Random);
            Assert.Equal(20.5f, gsit.CycleTime);
            Assert.Single(gsit.Steps);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public async Task A_set_Firestorm_made_is_edited_here_with_nothing_of_it_lost()
    {
        var inv = new Inv();
        var lib = new AoLibrary(inv, inv);
        var src = inv.MakeFolder(inv.RootId, "Anims");
        var s1 = inv.Animation(src, "s1");
        var g1 = inv.Animation(src, "g1");
        var g2 = inv.Animation(src, "g2");
        var t1 = inv.Animation(src, "t1");
        var d1 = inv.Animation(src, "d1");
        var add = inv.Animation(src, "new");

        // the layout as Firestorm writes it: the default set, a group step, a track, and a folder it does not know
        var ao = inv.MakeFolder(inv.MakeFolder(inv.RootId, "#Firestorm"), "#AO");
        var set = inv.MakeFolder(ao, "Club:SO:**");
        var standing = inv.MakeFolder(set, "Standing:CT12.50:CY:RN");
        inv.LinkTo(standing, s1, "0");
        var group = inv.MakeFolder(standing, "Group 1:OR1");
        inv.LinkTo(group, g1, "0");
        inv.LinkTo(group, g2, "1");
        var track = inv.MakeFolder(standing, "Breath:TR:CT5.00:CY");
        inv.LinkTo(track, t1, "0");
        var dancing = inv.MakeFolder(set, "Dancing");
        inv.LinkTo(dancing, d1, "0");
        var before = inv.FolderById.Values.Where(f => f.Id != set).ToDictionary(f => f.Id, f => f.Name);

        Assert.Equal(1, await lib.AddToStateAsync(set, AoStateId.Standing, [add]));
        Assert.True(await lib.RenameSetAsync(set, "Lounge"));

        Assert.Equal("Lounge:SO:**", inv.NameOf(set));
        foreach (var (id, name) in before)
            Assert.Equal(name, inv.NameOf(id));                           // every other folder name kept, options and all
        Assert.Equal(["0", "2"], inv.LinksIn(standing).Select(l => l.Description));   // after the step and the group
        Assert.Single(inv.LinksIn(dancing));
        Assert.Empty(inv.Trashed);

        var read = Read(inv).Sets.Single();
        Assert.Equal("Lounge", read.Name);
        Assert.True(read.SitOverride);
        var st = read.GetState(AoStateId.Standing);
        Assert.True(st.Cycle && st.Random);
        Assert.Equal(12.5f, st.CycleTime);
        Assert.Equal(3, st.Steps.Count);
        Assert.Single(st.Tracks);
    }

    [Fact]
    public void Folder_names_keep_options_they_do_not_know_in_their_place()
    {
        var name = AoFolderName.Parse("Club:SO:XX:**");
        Assert.Equal("Club", name.Name);
        Assert.Equal(["SO", "XX", "**"], name.Options);
        Assert.Equal("Club:SO:XX:**", name.Format());
        Assert.Equal("Lounge:SO:XX:**", (name with { Name = "Lounge" }).Format());
    }

    // ---------------- import ----------------

    [Fact]
    public async Task A_folder_with_a_ZHAO_notecard_imports_as_a_new_set_and_lists_missing_animations()
    {
        var inv = new Inv();
        var lib = new AoLibrary(inv, inv);
        var bought = inv.MakeFolder(inv.RootId, "Bought AO");
        inv.Notecard(bought, "*Default Anims");
        var st1 = inv.Animation(bought, "stand one");
        var st2 = inv.Animation(bought, "stand two");
        var wa = inv.Animation(bought, "walk a");
        var wb = inv.Animation(bought, "walk b");
        const string card = """
            [ Stand.1 ]stand one|stand two
            [ Walk.N ]walk a,walk b|walk gone
            [ Sit.N ]sit missing
            [ Dancing ]dance
            """;

        var result = await lib.ImportAsync(bought, "Bought AO", card);
        Assert.NotEqual(Guid.Empty, result.SetFolder);

        Assert.Equal("Bought AO", inv.NameOf(result.SetFolder));
        Assert.Equal(4, result.Linked);
        Assert.Equal(["walk gone", "sit missing"], result.Missing);
        Assert.Equal(["Dancing"], result.UnknownStates);
        var standing = inv.LinksIn(inv.Path("#Firestorm", "#AO", "Bought AO", "Standing"));
        Assert.Equal([st1, st2], standing.Select(l => l.LinkedId));
        var walking = inv.Path("#Firestorm", "#AO", "Bought AO", "Walking");
        var walkGroup = Assert.Single(inv.Folders(walking));
        Assert.Equal("Group 1:OR0", walkGroup.Name);
        Assert.Equal([wa, wb], inv.LinksIn(walkGroup.Id).Select(l => l.LinkedId));
        Assert.DoesNotContain(inv.Folders(result.SetFolder), f => f.Name.StartsWith("Sitting"));   // nothing guessed for the missing sit

        var read = Read(inv).Sets.Single();
        Assert.Equal(2, read.GetState(AoStateId.Standing).Steps.Count);
        Assert.True(read.GetState(AoStateId.Walking).Steps.Single().IsGroup);
    }
}
