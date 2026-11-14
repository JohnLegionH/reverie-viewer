// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A local look for the own avatar's shape while an edit is open: it replaces what is drawn, never what was received, and sends nothing.</summary>
public class SelfPreviewTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid Me = ServerAppearanceTests.Me;
    private static readonly Guid Other = Guid.Parse("0c4f0c4f-1111-4222-8333-944444444444");

    private static VisualParamSet Set(byte v) => new([v, (byte)(v + 1)], [33, 34]);

    private static AvatarAppearanceData Look(Guid agent, VisualParamSet vp, int version) =>
        new(agent, new Guid[45].ToImmutableArray(), vp, version, 3);

    private sealed class Clock { public double Now; }

    private static (ViewerSession S, FakeTransport T, AvatarScene Scene, List<WorldChange> Changes) Rig()
    {
        var (s, t, _) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, Here, serverBaked: true);
        var clock = new Clock();
        var scene = new AvatarScene(s.World, Me, () => clock.Now);
        var changes = new List<WorldChange>();
        s.World.Changed += changes.Add;
        t.Raise(new AvatarUpdated(Here, new AvatarData(900, Me, "Test User", new Vector3(128, 128, 25), Quaternion.Identity, new Vector3(0.45f, 0.6f, 1.9f), default, 0)));
        t.Raise(new AvatarAppearanceReceived(Here, Look(Me, Set(100), 1)));
        Pump(scene, changes);
        return (s, t, scene, changes);
    }

    private static void Pump(AvatarScene scene, List<WorldChange> changes)
    {
        foreach (var c in changes) scene.Apply(c);
        changes.Clear();
    }

    private static AvatarEntry Self(AvatarScene scene) => Assert.Single(scene.Entries, e => e.AgentId == Me);

    [Fact]
    public void A_preview_is_what_is_drawn_while_the_received_look_stays_as_it_was()
    {
        var (s, _, _, _) = Rig();
        var preview = Set(200);
        s.World.SetSelfPreview(Me, preview);
        Assert.Same(preview, s.World.DrawnVisualParams(Me));
        Assert.True(s.World.Appearance(Me)!.VisualParams.SameAs(Set(100)));
    }

    [Fact]
    public void Setting_and_ending_a_preview_raises_an_appearance_change_for_the_avatar()
    {
        var (s, _, _, changes) = Rig();
        s.World.SetSelfPreview(Me, Set(200));
        var set = Assert.Single(changes, c => c.Kind == WorldChangeKind.AppearanceChanged && c.AgentId == Me);
        Assert.Equal(900u, set.Id.LocalId);
        changes.Clear();
        s.World.SetSelfPreview(Me, null);
        Assert.Single(changes, c => c.Kind == WorldChangeKind.AppearanceChanged && c.AgentId == Me);
        Assert.True(s.World.DrawnVisualParams(Me)!.SameAs(Set(100)));
    }

    [Fact]
    public void Ending_a_preview_that_was_never_set_raises_nothing()
    {
        var (s, _, _, changes) = Rig();
        s.World.SetSelfPreview(Me, null);
        Assert.Empty(changes);
        Assert.True(s.World.DrawnVisualParams(Me)!.SameAs(Set(100)));
    }

    [Fact]
    public void The_scene_draws_the_preview_and_goes_back_to_the_received_look_when_it_ends()
    {
        var (s, _, scene, changes) = Rig();
        int before = Self(scene).ShapeVersion;
        Assert.True(Self(scene).VisualParams!.SameAs(Set(100)));
        s.World.SetSelfPreview(Me, Set(200));
        Pump(scene, changes);
        Assert.True(Self(scene).VisualParams!.SameAs(Set(200)));
        Assert.True(Self(scene).ShapeVersion > before);
        s.World.SetSelfPreview(Me, null);
        Pump(scene, changes);
        Assert.True(Self(scene).VisualParams!.SameAs(Set(100)));
    }

    [Fact]
    public void A_new_look_from_the_simulator_leaves_a_preview_in_place_unless_it_was_set_to_end_with_it()
    {
        var (s, t, _, _) = Rig();
        s.World.SetSelfPreview(Me, Set(200));
        t.Raise(new AvatarAppearanceReceived(Here, Look(Me, Set(110), 2)));
        Assert.True(s.World.DrawnVisualParams(Me)!.SameAs(Set(200)));
        s.World.SetSelfPreview(Me, Set(210), releaseOnNextAppearance: true);
        t.Raise(new AvatarAppearanceReceived(Here, Look(Me, Set(120), 3)));
        Assert.True(s.World.DrawnVisualParams(Me)!.SameAs(Set(120)));
    }

    [Fact]
    public void A_preview_is_for_one_avatar_only()
    {
        var (s, t, _, _) = Rig();
        t.Raise(new AvatarAppearanceReceived(Here, Look(Other, Set(50), 1)));
        s.World.SetSelfPreview(Me, Set(200));
        Assert.True(s.World.DrawnVisualParams(Me)!.SameAs(Set(200)));
        Assert.True(s.World.DrawnVisualParams(Other)!.SameAs(Set(50)));
        Assert.Null(s.World.DrawnVisualParams(Guid.NewGuid()));
    }

    [Fact]
    public void A_region_change_drops_the_preview_with_the_look_it_was_made_for()
    {
        var (s, t, _, _) = Rig();
        s.World.SetSelfPreview(Me, Set(200));
        Assert.True(s.World.DrawnVisualParams(Me)!.SameAs(Set(200)));
        var there = new SimRef(256256UL << 32 | 256000UL, "There");
        t.CurrentSim = there;
        t.Raise(new SimConnected(there, 256, 256, true));
        Assert.Null(s.World.DrawnVisualParams(Me));
    }

    [Fact]
    public void A_preview_sends_nothing()
    {
        var (s, t, _, _) = Rig();
        int logins = t.Logins.Count;
        s.World.SetSelfPreview(Me, Set(200));
        Assert.True(s.World.DrawnVisualParams(Me)!.SameAs(Set(200)));
        Assert.Equal(logins, t.Logins.Count);
        Assert.Empty(t.Commands);
    }
}
