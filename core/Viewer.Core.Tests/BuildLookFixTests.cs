// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The minimized window's restore button, the Snap to objects guide that stands clear of both boxes, Ctrl+W closing a window, and the
/// grid declaration of Snap to objects. Pure values and a manager made by each test, so the class runs in parallel.
/// </summary>
public class BuildLookFixTests
{
    // ---------------- the title-bar button ----------------

    [Fact]
    public void A_minimized_window_shows_a_restore_button_where_minimize_was()
    {
        var face = FloaterButtons.Minimize(minimized: true);
        Assert.Equal(("restore", "floater.restore", "floater.restore.named"), (face.Icon, face.TipKey, face.NameKey));
    }

    [Fact]
    public void A_whole_window_shows_the_minimize_button()
    {
        var face = FloaterButtons.Minimize(minimized: false);
        Assert.Equal((UiIcons.Minimize, "floater.minimize", "floater.minimize.named"), (face.Icon, face.TipKey, face.NameKey));
    }

    [Fact]
    public void The_restore_button_has_a_drawing_and_its_words()
    {
        var face = FloaterButtons.Minimize(minimized: true);
        Assert.NotEqual(UiIcons.Minimize, face.Icon);
        Assert.NotNull(UiAssets.IconSvg(face.Icon, new UiColour(1, 1, 1)));
        var s = StringTable.English();
        Assert.Equal("Restore", s.Get(face.TipKey));
        Assert.Equal("Restore Build", s.Get(face.NameKey, "Build"));
    }

    // ---------------- the snap guide ----------------

    private static SnapBox Box(Vector3 middle, Vector3 size) => new(middle - size / 2f, middle + size / 2f);

    private static float[] Parts(Vector3 v) => [v.X, v.Y, v.Z];

    [Theory]
    [InlineData(0, 3f, 1f)]    // the dragged box has the larger face: the frame goes round the target
    [InlineData(1, 3f, 1f)]
    [InlineData(2, 3f, 1f)]
    [InlineData(0, 0.5f, 20f)] // a small box set against a floor or a long wall: the frame stays at the small box
    [InlineData(1, 0.5f, 20f)]
    [InlineData(2, 0.5f, 20f)]
    [InlineData(2, 1f, 1f)]    // alike: round the dragged box
    public void The_guide_stands_out_by_the_margin_round_whichever_box_has_the_smaller_face(int axis, float movedAcross, float targetAcross)
    {
        var along = axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
        var across = Vector3.One - along;
        var target = Box(along * 2f, along + across * targetAcross);
        var moved = Box(across * 0.1f, along + across * movedAcross);
        var lines = ObjectSnap.Outline(new SnapGuide(axis, 1.5f, SnapKind.Face, target), Quaternion.Identity, moved, 0.2f);
        Assert.Equal(4, lines.Count);
        var round = movedAcross <= targetAcross ? moved : target;
        var ends = lines.SelectMany(l => new[] { l.From, l.To }).ToArray();
        for (int a = 0; a < 3; a++)
        {
            float low = ends.Min(p => Parts(p)[a]), high = ends.Max(p => Parts(p)[a]);
            if (a == axis)
            {
                Assert.Equal(1.5f, low, 3);
                Assert.Equal(1.5f, high, 3);
                continue;
            }
            Assert.Equal(Parts(round.Min)[a] - 0.2f, low, 3);
            Assert.Equal(Parts(round.Max)[a] + 0.2f, high, 3);
        }
    }

    [Fact]
    public void The_guide_is_drawn_wide_over_a_dark_edge()
    {
        (Vector3 From, Vector3 To)[] lines = [(Vector3.Zero, Vector3.UnitX), (Vector3.UnitX, Vector3.One)];
        var drawn = Manipulator.SnapGuideDrawing(lines);
        Assert.Equal(4, drawn.Count);
        // every dark edge is drawn before any coloured line, so no edge covers a line
        Assert.All(drawn.Take(2), l => Assert.True(l.Colour.X + l.Colour.Y + l.Colour.Z < 0.2f, $"not dark: {l.Colour}"));
        Assert.All(drawn.Skip(2), l => Assert.Equal(Manipulator.SnapGuideColour, l.Colour));
        Assert.All(drawn.Skip(2), l => Assert.True(l.Pixels >= 4f, $"only {l.Pixels} px wide"));
        Assert.True(drawn[0].Pixels >= drawn[2].Pixels + 2f);
    }

    // ---------------- Ctrl+W ----------------

    private static (FloaterManager M, InputFocus F) Windows()
    {
        var m = new FloaterManager();
        m.SetBounds(new UiRect(0, 0, 1280, 720));
        m.Register(new FloaterSpec("inventory", "floater.inventory", new UiSize(300, 400), new UiSize(200, 200)));
        m.Register(new FloaterSpec("build", "edit.title", new UiSize(460, 600), new UiSize(400, 520)));
        m.Open("inventory");
        m.Open("build");
        return (m, new InputFocus(m));
    }

    private static readonly KeyChord CtrlW = new("W", Control: true);

    [Fact]
    public void Ctrl_W_closes_the_Build_window_when_it_is_in_front_and_only_that_one()
    {
        var (m, f) = Windows();
        Assert.True(f.OnWindowShortcut(CtrlW));
        Assert.False(m.IsOpen("build"));
        Assert.True(m.IsOpen("inventory"));
    }

    [Fact]
    public void Ctrl_W_closes_the_window_while_a_field_in_it_is_being_typed_in()
    {
        var (m, f) = Windows();
        f.SetTextEntryActive(true);
        Assert.True(f.OnWindowShortcut(CtrlW));
        Assert.False(m.IsOpen("build"));
    }

    [Fact]
    public void Ctrl_W_with_no_window_open_is_left_for_the_world()
    {
        var m = new FloaterManager();
        Assert.False(new InputFocus(m).OnWindowShortcut(CtrlW));
    }

    [Fact]
    public void Ctrl_W_asks_a_windows_close_guard_and_keeps_the_key_when_the_guard_says_no()
    {
        var (m, f) = Windows();
        int asked = 0;
        m.SetCloseGuard("build", () => { asked++; return false; });
        Assert.True(f.OnWindowShortcut(CtrlW));
        Assert.Equal(1, asked);
        Assert.True(m.IsOpen("build"));
    }

    [Theory]
    [InlineData("W", false, false, false)]
    [InlineData("W", true, true, false)]
    [InlineData("W", true, false, true)]
    [InlineData("Q", true, false, false)]
    public void Only_Ctrl_W_closes_a_window(string key, bool control, bool shift, bool alt)
    {
        var (m, f) = Windows();
        Assert.False(f.OnWindowShortcut(new KeyChord(key, control, shift, alt)));
        Assert.True(m.IsOpen("build"));
    }

    [Fact]
    public void Ctrl_W_waits_while_an_alert_is_up()
    {
        var (m, f) = Windows();
        Assert.False(f.OnWindowShortcut(CtrlW, modalUp: true));
        Assert.True(m.IsOpen("build"));
    }

    [Fact]
    public void Ctrl_W_is_no_longer_listed_as_not_built()
    {
        Assert.DoesNotContain(ViewerShortcuts.NotBuilt, n => n.Shortcut == "control|W");
    }

    // ---------------- the grid declaration ----------------

    [Fact]
    public void Snap_to_objects_is_declared_as_allowed_on_every_grid()
    {
        Assert.Equal(FeatureReach.Everywhere, GridPolicy.ReachOf(ViewerFeature.SnapToObjects));
        Assert.True(GridPolicy.Allows(ViewerFeature.SnapToObjects, GridKind.SecondLife));
        Assert.True(GridPolicy.Allows(ViewerFeature.SnapToObjects, GridKind.OpenSim));
    }
}
