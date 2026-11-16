// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The per-grid switch: Second Life is told by its login host, every feature beyond the official viewer has a declared place,
/// an undeclared one is off on Second Life and unchanged elsewhere, and saving objects to a file on Second Life needs every part
/// to be the person's own creation (Third-Party Viewer Policy 2.b).
/// </summary>
public class GridPolicyTests
{
    [Theory]
    [InlineData("https://login.agni.lindenlab.com/cgi-bin/login.cgi")]
    [InlineData("https://LOGIN.AGNI.LINDENLAB.COM/cgi-bin/login.cgi")]
    [InlineData("https://login.aditi.lindenlab.com/cgi-bin/login.cgi")]
    [InlineData("https://login.secondlife.com/")]
    public void A_login_host_of_Linden_Lab_is_Second_Life(string uri) =>
        Assert.Equal(GridKind.SecondLife, GridPolicy.KindOf(uri));

    [Theory]
    [InlineData("http://grid.example.org:8002/")]
    [InlineData("https://lindenlab.com.example.org/login")]
    [InlineData("https://example.org/lindenlab.com/login.cgi")]
    [InlineData("https://notlindenlab.com/")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not an address")]
    public void Any_other_login_host_is_not_Second_Life_whatever_its_path_or_name(string? uri) =>
        Assert.Equal(GridKind.OpenSim, GridPolicy.KindOf(uri));

    [Fact]
    public void Every_feature_has_a_declared_place()
    {
        foreach (var f in Enum.GetValues<ViewerFeature>())
            Assert.True(GridPolicy.ReachOf(f) is not null, $"{f} has no declared place");
    }

    [Fact]
    public void An_undeclared_feature_is_off_on_Second_Life_and_on_elsewhere()
    {
        var unknown = (ViewerFeature)999;
        Assert.Null(GridPolicy.ReachOf(unknown));
        Assert.False(GridPolicy.Allows(unknown, GridKind.SecondLife));
        Assert.True(GridPolicy.Allows(unknown, GridKind.OpenSim));
    }

    [Theory]
    [InlineData(ViewerFeature.Backup)]
    [InlineData(ViewerFeature.Restore)]
    public void Backup_and_restore_are_own_creations_only_on_Second_Life(ViewerFeature f)
    {
        Assert.Equal(FeatureReach.OwnCreationsOnSecondLife, GridPolicy.ReachOf(f));
        Assert.True(GridPolicy.Allows(f, GridKind.SecondLife));
        Assert.True(GridPolicy.Allows(f, GridKind.OpenSim));
    }

    private static readonly Guid Self = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void On_Second_Life_a_backup_needs_every_part_to_be_the_persons_own_creation()
    {
        Assert.True(GridPolicy.MayBackUp(GridKind.SecondLife, Self, [Self, Self]));
        Assert.False(GridPolicy.MayBackUp(GridKind.SecondLife, Self, [Self, Other]));
        Assert.False(GridPolicy.MayBackUp(GridKind.SecondLife, Self, [Other]));
        Assert.False(GridPolicy.MayBackUp(GridKind.SecondLife, Self, []));
        Assert.False(GridPolicy.MayBackUp(GridKind.SecondLife, Guid.Empty, [Guid.Empty]));
    }

    [Fact]
    public void Elsewhere_a_backup_is_not_limited_by_who_made_the_parts()
    {
        Assert.True(GridPolicy.MayBackUp(GridKind.OpenSim, Self, [Self, Other]));
        Assert.True(GridPolicy.MayBackUp(GridKind.OpenSim, Self, [Other]));
    }

    [Fact]
    public void The_policy_has_no_way_to_be_changed_at_run_time()
    {
        // nothing public that writes: no settable property, no public field, no method other than the reads
        var t = typeof(GridPolicy);
        Assert.Empty(t.GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.CanWrite));
        Assert.Empty(t.GetFields(BindingFlags.Public | BindingFlags.Static));
        Assert.Equal(["Allows", "KindOf", "MayBackUp", "ReachOf"],
            t.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).Distinct().Order().ToArray());
    }

    private static ViewerSession SessionAt(string uri) =>
        new(new SessionOptions { LoginUri = uri }, new FakeTransport(), new CoreLog(new ListSink(), "test"));

    [Fact]
    public void The_session_knows_its_grid_from_its_login_address_and_applies_the_policy()
    {
        using var sl = SessionAt("https://login.agni.lindenlab.com/cgi-bin/login.cgi");
        using var os = SessionAt("http://grid.example.org:8002/");
        Assert.Equal(GridKind.SecondLife, sl.Grid);
        Assert.Equal(GridKind.OpenSim, os.Grid);
        Assert.False(sl.Allows((ViewerFeature)999));
        Assert.True(os.Allows((ViewerFeature)999));
        Assert.False(sl.MayBackUp(Self, [Other]));
        Assert.True(os.MayBackUp(Self, [Other]));
    }
}
