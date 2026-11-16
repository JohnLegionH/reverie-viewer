// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Diagnostics;
using Viewer.Core.Interaction;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Create hands back to Edit: after the create tool places a box the tool becomes Edit with the Move handles, unless Keep Tool selected is
/// ticked (the official viewer's CreateToolKeepSelected, off by default; lltoolplacer.cpp), and the box the region sends back made selected
/// becomes the selection. No engine and no grid; each test builds its own world, so the class runs in parallel.
/// </summary>
public class CreateHandBackTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");
    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);
    private const uint Full = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;

    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private sealed class Rig
    {
        public WorldState World = new(new CoreLog(new ListSink(), "test"));
        public List<ActionIntent> Sent = new();
        public EditController C;

        public Rig()
        {
            World.BeginRegion(Sim, new RegionInfo("Here", 1000, 1000, 256, 256, false));
            C = new EditController(World, i => { Sent.Add(i); return new ActionResult(ActionStatus.Sent, Sent.Count); });
        }

        public void Prim(uint local, uint flags) =>
            World.ApplyObject(Sim, new PrimData(local, G(local), 0, 0, new Vector3(10, 10, 30), Quaternion.Identity, new Vector3(0.5f, 0.5f, 0.5f),
                Interaction: new PrimInteraction(0, flags)));
    }

    [Fact]
    public void Keep_Tool_selected_is_off_by_default_and_remembered()
    {
        Assert.False(new Rig().C.KeepToolSelected);
        var settings = new SettingsService(new MemoryStore());
        Assert.False(settings.Get(BuildSettings.KeepToolSelected));
        settings.Set(BuildSettings.KeepToolSelected, true);
        Assert.True(settings.Get(BuildSettings.KeepToolSelected));
    }

    [Fact]
    public void A_placed_box_hands_the_tool_to_Edit_with_the_Move_handles()
    {
        var r = new Rig();
        r.C.SelectTool(BuildTool.Create);
        r.C.Manip = ManipMode.Rotate;
        bool keepCreating = r.C.Placed();
        Assert.False(keepCreating);
        Assert.True(r.C.BuildMode);
        Assert.Equal(BuildTool.Edit, r.C.Tool);
        Assert.Equal(ManipMode.Move, r.C.Manip);
    }

    [Fact]
    public void With_Keep_Tool_selected_the_create_tool_stays_for_the_next_click()
    {
        var r = new Rig();
        r.C.SelectTool(BuildTool.Create);
        r.C.KeepToolSelected = true;
        Assert.True(r.C.Placed());
        Assert.Equal(BuildTool.Create, r.C.Tool);
    }

    [Fact]
    public void The_new_box_becomes_the_selection_when_it_arrives()
    {
        var r = new Rig();
        r.Prim(4, Full);
        r.C.SelectTool(BuildTool.Edit);
        r.C.SelectMany([4u], shift: false);
        r.C.SelectTool(BuildTool.Create);
        r.C.Placed();
        r.Prim(9, Full | ObjectFlagBits.CreateSelected);
        r.C.Created(9);
        Assert.Equal([9u], r.C.Selection);
        Assert.Equal(9u, r.C.Primary);
        Assert.Equal(BuildTool.Edit, r.C.Tool);
    }

    [Fact]
    public void A_box_that_arrives_after_build_mode_closed_is_not_selected()
    {
        var r = new Rig();
        r.C.SelectTool(BuildTool.Create);
        r.C.Placed();
        r.C.CloseBuild();
        r.Prim(9, Full | ObjectFlagBits.CreateSelected);
        r.C.Created(9);
        Assert.Empty(r.C.Selection);
        Assert.False(r.C.BuildMode);
    }
}
