// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Viewer.Shell;
using Viewer.Shell.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Blocked tab as data: the rows, their kinds, the note over the list, and the actions with the words they answer with. The
/// model is driven through a real session on a fake transport, so every action is the pipeline's. No grid. Every member builds its
/// own objects, so the class runs in parallel.
/// </summary>
public class BlockedModelTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Pest = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Thing = Guid.Parse("0b0b0b0b-0000-4000-8000-000000000001");

    private static (BlockedModel Model, ViewerSession Session, FakeTransport Fake) Rig()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        var model = new BlockedModel(session.Mutes, (k, id, n) => session.Block(k, id, n), (id, n) => session.Unblock(id, n));
        return (model, session, fake);
    }

    [Fact]
    public void The_rows_are_the_lists_entries_sorted_by_name_each_with_its_kinds_words_key()
    {
        var (model, session, _) = Rig();
        session.Mutes.Load([new MuteEntry(MuteKind.Resident, Pest, "zed Resident"), new MuteEntry(MuteKind.Object, Thing, "Alpha Box"),
            new MuteEntry(MuteKind.ByName, Guid.Empty, "beta bot"), new MuteEntry(MuteKind.Group, Guid.NewGuid(), "Gamma Group")]);
        Assert.Equal(["Alpha Box", "beta bot", "Gamma Group", "zed Resident"], model.Rows.Select(r => r.Name));
        Assert.Equal(["blocked.type.object", "blocked.type.byname", "blocked.type.group", "blocked.type.resident"], model.Rows.Select(r => r.TypeKey));
    }

    [Fact]
    public void Every_type_key_and_status_key_the_model_uses_is_in_the_string_table()
    {
        var strings = StringTable.English();
        foreach (var key in new[] { "blocked.type.byname", "blocked.type.resident", "blocked.type.object", "blocked.type.group", "blocked.type.external", "blocked.asking",
            "blocked.unavailable", "blocked.none", "status.blocked.added", "status.blocked.changed", "status.blocked.duplicate", "status.blocked.linden", "status.blocked.self",
            "status.blocked.limit", "status.blocked.invalid", "status.blocked.refused", "status.blocked.removed" })
            Assert.True(strings.Has(key), key);
    }

    [Fact]
    public void The_note_says_the_grid_is_being_asked_then_nothing_once_loaded_and_that_the_list_lasts_only_this_session_when_the_grid_has_none()
    {
        var (model, session, _) = Rig();
        Assert.Equal("blocked.asking", model.NoteKey);   // requested at the world
        session.Mutes.Load([]);
        Assert.Null(model.NoteKey);
        var (model2, session2, _) = Rig();
        session2.Mutes.SetState(MuteListState.Unavailable);
        Assert.Equal("blocked.unavailable", model2.NoteKey);
    }

    [Fact]
    public void Blocking_a_resident_adds_a_row_says_so_and_raises_a_change_and_the_command_went_through_the_pipeline()
    {
        var (model, _, fake) = Rig();
        int changes = 0;
        model.Changed += () => changes++;
        Assert.True(model.BlockResident(Pest, "Pest Resident"));
        Assert.Equal(("status.blocked.added", "Pest Resident"), model.Status);
        Assert.Equal(new BlockedRow(MuteKind.Resident, Pest, "Pest Resident", "blocked.type.resident", 0), Assert.Single(model.Rows));
        Assert.True(changes > 0);
        Assert.Equal(new MuteAddCommand(new MuteEntry(MuteKind.Resident, Pest, "Pest Resident", 0)), Assert.Single(fake.Commands));
    }

    [Fact]
    public void An_object_and_an_object_name_are_blocked_the_same_way()
    {
        var (model, _, fake) = Rig();
        Assert.True(model.BlockObject(Thing, "Noisy Box"));
        Assert.True(model.BlockObjectName("Spam Bot"));
        Assert.Equal([MuteKind.Object, MuteKind.ByName], fake.Commands.Cast<MuteAddCommand>().Select(c => c.Entry.Kind));
        Assert.Equal(2, model.Rows.Count);
    }

    [Theory]
    [InlineData("Test Linden", "status.blocked.linden")]
    public void A_refusal_is_told_in_words_and_sends_nothing(string name, string key)
    {
        var (model, _, fake) = Rig();
        Assert.False(model.BlockResident(Pest, name));
        Assert.Equal(key, model.Status?.Key);
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public void Blocking_yourself_twice_or_a_blank_name_says_why()
    {
        var (model, _, fake) = Rig();
        Assert.False(model.BlockResident(Self, "Test User"));
        Assert.Equal("status.blocked.self", model.Status?.Key);
        Assert.True(model.BlockResident(Pest, "Pest Resident"));
        Assert.False(model.BlockResident(Pest, "Pest Resident"));
        Assert.Equal("status.blocked.duplicate", model.Status?.Key);
        Assert.False(model.BlockObjectName("   "));
        Assert.Equal("status.blocked.invalid", model.Status?.Key);
        Assert.Single(fake.Commands);
    }

    [Fact]
    public void A_send_the_pipeline_refused_leaves_the_list_alone_and_says_so()
    {
        var (model, _, fake) = Rig();
        fake.OnCommand = _ => CommandDispatch.NotSent("not_connected");
        Assert.False(model.BlockObject(Thing, "Box"));
        Assert.Equal("status.blocked.refused", model.Status?.Key);
        Assert.Empty(model.Rows);
    }

    [Fact]
    public void Unblocking_removes_the_row_sends_the_remove_and_says_so_and_a_row_that_is_gone_does_nothing()
    {
        var (model, _, fake) = Rig();
        model.BlockResident(Pest, "Pest Resident");
        model.BlockObjectName("Spam Bot");
        fake.Commands.Clear();
        var row = model.Rows.FirstOrDefault(r => r.Kind == MuteKind.Resident) ?? new BlockedRow(MuteKind.Resident, Pest, "Pest Resident", "blocked.type.resident", 0);
        Assert.True(model.Unblock(row));
        Assert.Equal(("status.blocked.removed", "Pest Resident"), model.Status);
        Assert.Equal(new MuteRemoveCommand(Pest, "Pest Resident"), Assert.Single(fake.Commands));
        Assert.Equal("Spam Bot", Assert.Single(model.Rows).Name);
        Assert.False(model.Unblock(row));
        Assert.Single(fake.Commands);
    }
}
