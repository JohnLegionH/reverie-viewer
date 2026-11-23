// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>An unticked check box is only an outline, so its outline holds 3:1 against the surface behind it in every look (WCAG 1.4.11). Plain .NET; parallel.</summary>
public class CheckOutlineTests
{
    public static TheoryData<string> Looks() => new(ThemePresets.CardIds);

    [Theory]
    [MemberData(nameof(Looks))]
    public void The_outline_of_an_unticked_box_holds_3_to_1_against_every_surface_it_stands_on(string look)
    {
        var tokens = ReverieTheme.LookFor(ThemePresets.Get(look)).Tokens;
        var outline = CheckColours.Outline(tokens);
        foreach (var surface in CheckColours.SurfaceNames)
            Assert.True(UiColour.Contrast(outline, tokens[surface]) >= CheckColours.MinContrast,
                $"{look}: the outline against {surface} is {UiColour.Contrast(outline, tokens[surface]):F2}:1");
    }
}
