// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse;
using Viewer.Core.Actions;
using Viewer.Core.Avatars;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Viewer.Core.Tests;

/// <summary>
/// Undo and Redo on the edit selection, as the official viewer sends them (llselectmgr.cpp undo and redo: the selected roots by id, or every
/// selected part with Edit linked on; Undo enabled when an object can be moved or modified, Redo when one can be modified), their place in the
/// Build menu on Ctrl+Z and Ctrl+Y, a text field keeping Ctrl+Z and Ctrl+Y for its own text, and build mode owning the left button so a
/// drag of a handle never steers the avatar. In-memory world only; each test builds its own, so the class runs in parallel.
/// </summary>
public class EditUndoTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = Guid.Parse("7d2e4f60-1a3b-4c5d-8e9f-0a1b2c3d4e51");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Own = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            World.ApplyAvatar(Sim, new AvatarData(500, Me, "Me", new Vector3(100, 100, 21), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f)));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); }, () => Viewer.Core.Building.EditLimits.Official,
                regions: () => [new Viewer.Core.Building.RegionArea(0, 0, 256, 256)]);
        }

        public void Prim(uint local, Vector3 pos, uint flags, uint parent = 0) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), parent, 0, pos, Quaternion.Identity, new Vector3(2, 2, 2), Interaction: new PrimInteraction(0, flags)));
    }

    [Fact]
    public void Build_mode_owns_the_left_button_so_dragging_a_handle_never_steers_the_avatar()
    {
        var r = new Rig();
        Assert.False(r.C.OwnsLeftButton);
        r.C.ToggleBuild();
        Assert.True(r.C.OwnsLeftButton);
        r.C.SelectTool(BuildTool.Move);
        Assert.True(r.C.OwnsLeftButton);
        r.C.CloseBuild();
        Assert.False(r.C.OwnsLeftButton);
    }

    [Fact]
    public void Undo_and_Redo_send_the_selected_roots_by_id()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(1, 0, 0), Own, parent: 1);
        r.Prim(3, new Vector3(20, 10, 30), Own);
        r.C.ToggleBuild();
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1, 3], shift: false);
        Assert.True(r.C.CanUndo);
        Assert.True(r.C.CanRedo);
        Assert.True(r.C.Undo());
        Assert.Equal([G(1), G(3)], Assert.IsType<UndoObjectsIntent>(r.Sent[^1]).ObjectIds.Order().ToArray());
        Assert.True(r.C.Redo());
        Assert.Equal([G(1), G(3)], Assert.IsType<RedoObjectsIntent>(r.Sent[^1]).ObjectIds.Order().ToArray());
    }

    [Fact]
    public void With_Edit_linked_on_Undo_sends_the_selected_part()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.Prim(2, new Vector3(1, 0, 0), Own, parent: 1);
        r.C.ToggleBuild();
        r.C.SelectTool(BuildTool.Edit);
        r.C.EditLinked = true;
        r.C.SelectMany([2], shift: false);
        Assert.True(r.C.Undo());
        Assert.Equal([G(2)], Assert.IsType<UndoObjectsIntent>(r.Sent[^1]).ObjectIds.ToArray());
    }

    [Fact]
    public void With_nothing_selected_Undo_and_Redo_are_off_and_send_nothing()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        r.C.ToggleBuild();
        int before = r.Sent.Count;
        Assert.False(r.C.CanUndo);
        Assert.False(r.C.CanRedo);
        Assert.False(r.C.Undo());
        Assert.False(r.C.Redo());
        Assert.Equal(before, r.Sent.Count);
    }

    [Fact]
    public void Undo_needs_move_or_modify_and_Redo_needs_modify()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), ObjectFlagBits.Move);   // someone else's, movable only
        r.Prim(2, new Vector3(20, 10, 30), 0);                      // neither
        r.C.ToggleBuild();
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([1], shift: false);
        Assert.True(r.C.CanUndo);
        Assert.False(r.C.CanRedo);
        r.C.SelectMany([2], shift: false);
        Assert.False(r.C.CanUndo);
        Assert.False(r.C.CanRedo);
        int before = r.Sent.Count;
        Assert.False(r.C.Undo());
        Assert.Equal(before, r.Sent.Count);
    }

    [Fact]
    public void The_messages_carry_the_agent_the_group_and_each_object_id()
    {
        UUID agent = new("7d2e4f60-1a3b-4c5d-8e9f-0a1b2c3d4e52"), session = new("7d2e4f60-1a3b-4c5d-8e9f-0a1b2c3d4e53"), group = new("7d2e4f60-1a3b-4c5d-8e9f-0a1b2c3d4e54");
        var u = LmvEdit.Undo(agent, session, group, [G(5), G(6)]);
        Assert.Equal(agent, u.AgentData.AgentID);
        Assert.Equal(session, u.AgentData.SessionID);
        Assert.Equal(group, u.AgentData.GroupID);
        Assert.Equal([new UUID(G(5)), new UUID(G(6))], u.ObjectData.Select(o => o.ObjectID).ToArray());
        var d = LmvEdit.Redo(agent, session, group, [G(7)]);
        Assert.Equal(agent, d.AgentData.AgentID);
        Assert.Equal(group, d.AgentData.GroupID);
        Assert.Equal(new UUID(G(7)), Assert.Single(d.ObjectData).ObjectID);
    }

    [Fact]
    public void Ctrl_Z_and_Ctrl_Y_reach_Undo_and_Redo_in_the_Build_menu_and_a_text_field_keeps_them()
    {
        var r = new Rig();
        r.Prim(1, new Vector3(10, 10, 30), Own);
        var shell = new ShellController(StringTable.English(), () => { });
        BuildMenu.Install(shell.Menus, () => true);
        BuildMenu.InstallEditing(shell.Menus, () => true, () => r.C, () => r.C.Selection.Count > 0);
        shell.Commands.Register(BuildMenu.UndoCommand, () => r.C.Undo());
        shell.Commands.Register(BuildMenu.RedoCommand, () => r.C.Redo());
        r.C.ToggleBuild();
        r.C.SelectTool(BuildTool.Edit);
        var view = TopBarMenuMap.Build(shell.Menus, shell.Commands);
        Assert.Null(MenuShortcuts.Route(view, new KeyChord("Z", Control: true), textEntry: false));   // nothing selected: off
        r.C.SelectMany([1], shift: false);
        view = TopBarMenuMap.Build(shell.Menus, shell.Commands);
        Assert.Equal(BuildMenu.UndoId, MenuShortcuts.Route(view, new KeyChord("Z", Control: true), textEntry: false)?.Id);
        Assert.Equal(BuildMenu.RedoId, MenuShortcuts.Route(view, new KeyChord("Y", Control: true), textEntry: false)?.Id);
        // a text field holding the keyboard undoes its own typing (the official line editor is the edit menu's handler while it has focus)
        Assert.True(MenuShortcuts.TextFieldFirst(new KeyChord("Z", Control: true)));
        Assert.True(MenuShortcuts.TextFieldFirst(new KeyChord("Y", Control: true)));
        Assert.False(MenuShortcuts.TextFieldFirst(new KeyChord("I", Control: true)));
        Assert.Null(MenuShortcuts.Route(view, new KeyChord("Z", Control: true), textEntry: true));
        Assert.Null(MenuShortcuts.Route(view, new KeyChord("Y", Control: true), textEntry: true));
        Assert.DoesNotContain(ViewerShortcuts.NotBuilt, n => n.Shortcut is "control|Z" or "control|Y");
    }
}
