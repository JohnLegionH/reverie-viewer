// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Grids;
using Viewer.Shell.Notifications;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The pages a grid offers: the Help menu's grid entries, the login screen's welcome link, and the question that shows the host
/// before a page opens in the person's browser. Pure values; the class runs in parallel.</summary>
public class GridLinksTests
{
    private static GridInfo Info(string xml) => GridInfo.Parse("<gridinfo><gridname>Example Grid</gridname>" + xml + "</gridinfo>")!;

    [Fact]
    public void A_grid_that_sets_help_about_and_a_viewer_page_gets_three_entries_in_that_order()
    {
        var info = Info("<help>https://grid.example.org/help</help><about>https://grid.example.org/about</about><viewerurl>https://dl.example.org/reverie</viewerurl>");
        var list = GridLinks.HelpMenu(info, "Example Grid", false);
        Assert.Equal([GridLinks.HelpId, GridLinks.AboutId, GridLinks.ViewerId], list.Select(l => l.Id).ToArray());
        Assert.Equal(["menu.help.grid_help", "menu.help.grid_about", "menu.help.grid_viewer"], list.Select(l => l.LabelKey).ToArray());
        Assert.All(list, l => Assert.Equal("Example Grid", l.LabelArg));
        Assert.Equal("https://dl.example.org/reverie", list[2].Page.ToString());
    }

    [Fact]
    public void An_item_is_left_out_when_its_key_is_not_set()
    {
        Assert.Empty(GridLinks.HelpMenu(Info(""), "Example Grid", false));
        Assert.Empty(GridLinks.HelpMenu(null, "Example Grid", false));
        var only = GridLinks.HelpMenu(Info("<about>https://grid.example.org/about</about>"), "Example Grid", false);
        Assert.Equal([GridLinks.AboutId], only.Select(l => l.Id).ToArray());
    }

    [Fact]
    public void The_welcome_link_is_the_grids_welcome_page_and_absent_when_unset()
    {
        var link = GridLinks.Welcome(Info("<welcome>https://grid.example.org/hello</welcome>"), "Example Grid", false);
        Assert.NotNull(link);
        Assert.Equal("login.visit_grid", link!.LabelKey);
        Assert.Equal("Example Grid", link.LabelArg);
        Assert.Equal("https://grid.example.org/hello", link.Page.ToString());
        Assert.Null(GridLinks.Welcome(Info(""), "Example Grid", false));
        Assert.Null(GridLinks.Welcome(null, "Example Grid", false));
    }

    [Fact]
    public void On_second_life_no_grid_link_applies()
    {
        var info = Info("<help>https://grid.example.org/help</help><welcome>https://grid.example.org/hello</welcome>");
        Assert.Empty(GridLinks.HelpMenu(info, "Second Life", true));
        Assert.Null(GridLinks.Welcome(info, "Second Life", true));
    }

    [Theory]
    [InlineData("https://grid.example.org/help", "grid.example.org")]
    [InlineData("http://grid.example.org:8002/help", "grid.example.org:8002")]
    [InlineData("https://GRID.example.org:443/x", "grid.example.org")]
    public void The_host_shown_is_the_pages_own_with_a_port_only_when_it_is_not_the_schemes(string page, string host) =>
        Assert.Equal(host, GridLinks.HostText(new Uri(page)));

    [Fact]
    public void The_question_names_the_host_and_opens_only_on_the_first_button()
    {
        var s = StringTable.English();
        int opened = 0;
        var n = Confirmations.OpenLink(new Uri("https://grid.example.org/help"), () => opened++);
        Assert.True(n.Confirmation);
        Assert.Contains("grid.example.org", s.Get(n.Body.Key!, n.Body.Arg));
        n.Responder!(new NotificationAnswer(Confirmations.No, ""));
        Assert.Equal(0, opened);
        n.Responder!(new NotificationAnswer(Confirmations.Yes, ""));
        Assert.Equal(1, opened);
    }

    [Fact]
    public void The_login_screens_question_names_the_host_and_has_an_open_and_a_cancel_button()
    {
        var s = StringTable.English();
        var d = LoginDialogs.OpenLink(s, new Uri("http://grid.example.org:8002/welcome"));
        Assert.Contains("grid.example.org:8002", d.Body);
        Assert.Equal(LoginDialogKind.OpenLink, d.Kind);
        Assert.False(string.IsNullOrEmpty(d.ConfirmLabel));
        Assert.False(string.IsNullOrEmpty(d.CancelLabel));
    }
}
