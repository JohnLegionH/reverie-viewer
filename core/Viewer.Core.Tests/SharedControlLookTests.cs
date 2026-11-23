// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The look every window's tick boxes, number boxes, chips and swatches share, held in the theme: an unticked box, a disabled box and
/// an enabled one tell apart; a number box's arrows are a target a pointer hits at each interface size; an outline that makes a control
/// findable holds 3:1 (WCAG 1.4.11). Plain .NET, no engine; parallel (no process-wide state).
/// </summary>
public class SharedControlLookTests
{
    public static TheoryData<string> Looks() => new(ThemePresets.CardIds);
    public static TheoryData<int> Sizes() => new(100, 150, 200);

    private static ReverieTokens TokensOf(string look) => ReverieTheme.LookFor(ThemePresets.Get(look)).Tokens;

    [Theory]
    [MemberData(nameof(Looks))]
    public void The_outline_of_an_unticked_box_holds_3_to_1_on_every_surface_including_a_selected_row(string look)
    {
        var t = TokensOf(look);
        var outline = CheckColours.Outline(t);
        foreach (var surface in new[] { "surface.selected", "surface.selected.strong" })
            Assert.True(UiColour.Contrast(outline, t[surface]) >= CheckColours.MinContrast,
                $"{look}: against {surface} {UiColour.Contrast(outline, t[surface]):F2}:1");
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void A_disabled_box_keeps_an_outline_that_can_be_seen(string look)
    {
        var t = TokensOf(look);
        var d = CheckColours.Disabled(t);
        foreach (var surface in CheckColours.SurfaceNames)
        {
            var seen = CheckColours.Over(d.Outline, d.Strength, t[surface]);
            Assert.True(UiColour.Contrast(seen, t[surface]) >= CheckColours.DisabledMinContrast,
                $"{look}: the disabled outline against {surface} is {UiColour.Contrast(seen, t[surface]):F2}:1");
        }
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void A_disabled_box_is_dashed_and_dimmer_than_an_unticked_one(string look)
    {
        var t = TokensOf(look);
        var d = CheckColours.Disabled(t);

        double enabled = UiColour.Contrast(CheckColours.Outline(t), t["surface.base"]);
        double disabled = UiColour.Contrast(CheckColours.Over(d.Outline, d.Strength, t["surface.base"]), t["surface.base"]);
        Assert.True(d.Dashed, $"{look}: the disabled box is not dashed");
        Assert.True(disabled <= enabled, $"{look}: unticked {enabled:F2}:1, disabled {disabled:F2}:1");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_number_boxs_arrows_are_a_target_of_at_least_24_pixels_across_and_12_high_at_each_interface_size(int percent)
    {
        Assert.True(StepperMetrics.HitWidth(percent) >= StepperMetrics.MinTarget, $"{percent} %: the arrow column is {StepperMetrics.HitWidth(percent):F1} px wide");
        Assert.True(StepperMetrics.HitHeight(percent) >= StepperMetrics.MinTarget / 2, $"{percent} %: one arrow is {StepperMetrics.HitHeight(percent):F1} px high");
    }

    [Theory]
    [MemberData(nameof(Looks))]
    public void The_outline_of_a_secondary_button_a_chip_and_a_swatch_holds_3_to_1_on_the_surfaces_they_stand_on(string look)
    {
        var t = TokensOf(look);
        var outline = CheckColours.ButtonOutline(t);
        foreach (var surface in CheckColours.SurfaceNames)
            Assert.True(UiColour.Contrast(outline, t[surface]) >= CheckColours.MinContrast,
                $"{look}: against {surface} {UiColour.Contrast(outline, t[surface]):F2}:1");
    }
}

/// <summary>The label of a disabled filled (primary) button stays readable on its dimmed face. Plain .NET; parallel.</summary>
public class DisabledFilledButtonTests
{
    public static TheoryData<string> Looks() => new(ThemePresets.CardIds);

    [Theory]
    [MemberData(nameof(Looks))]
    public void The_label_of_a_disabled_primary_button_keeps_3_to_1_on_its_dimmed_face_on_every_surface(string look)
    {
        var t = ReverieTheme.LookFor(ThemePresets.Get(look)).Tokens;
        var label = ControlStates.FilledDisabledText(t);
        foreach (var surface in CheckColours.SurfaceNames)
        {
            var face = CheckColours.Over(t.Accent, ControlStates.DisabledStrength, t[surface]);
            var seen = CheckColours.Over(label, 1f, face);
            Assert.True(UiColour.Contrast(seen, face) >= ControlStates.DisabledFilledMinContrast,
                $"{look}: the label on its face over {surface} is {UiColour.Contrast(seen, face):F2}:1");
        }
    }
}
