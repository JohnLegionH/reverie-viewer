// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;
using static Viewer.Core.Tests.ThemeTokenTests;

namespace Viewer.Core.Tests;

/// <summary>
/// A grid's branding from its grid-info document: name, tagline, accent, logo, splash, register and forgot-password links
/// and news. A grid that publishes none of it gets the placeholder look, and nothing it publishes can put an unsafe link or
/// a control character on the login screen. Example addresses only. Every member is independent, so the class runs in parallel.
/// </summary>
public class GridBrandingTests
{
    private const string Full = """
        <?xml version="1.0"?>
        <gridinfo>
          <login>https://grid.example.org:8002/</login>
          <gridname>Example Grid</gridname>
          <tagline>A grid for examples</tagline>
          <accent>#2dd4bf</accent>
          <logo>https://grid.example.org/logo.png</logo>
          <splash>https://grid.example.org/splash.jpg</splash>
          <register>https://grid.example.org/register</register>
          <password>https://grid.example.org/forgot</password>
          <news><item title="Opening night">Come to the plaza at noon.</item><item title="Maintenance">Short break on Sunday.</item></news>
        </gridinfo>
        """;

    [Fact]
    public void A_grid_that_publishes_everything_gives_everything()
    {
        var b = GridBranding.FromGridInfo(Full);
        Assert.Equal("Example Grid", b.Name);
        Assert.Equal("A grid for examples", b.Tagline);
        Assert.Equal(H("#2dd4bf"), b.Accent);
        Assert.Equal(new Uri("https://grid.example.org/logo.png"), b.Logo);
        Assert.Equal(new Uri("https://grid.example.org/splash.jpg"), b.Splash);
        Assert.Equal(new Uri("https://grid.example.org/register"), b.Register);
        Assert.Equal(new Uri("https://grid.example.org/forgot"), b.ForgotPassword);
        Assert.Equal([new NewsItem("Opening night", "Come to the plaza at noon."), new NewsItem("Maintenance", "Short break on Sunday.")], b.News);
    }

    [Fact]
    public void A_grid_that_publishes_only_a_name_and_a_login_has_that_name_and_nothing_else()
    {
        var b = GridBranding.FromGridInfo("<gridinfo><login>https://grid.example.org/</login><gridname>Plain Grid</gridname></gridinfo>");
        Assert.Equal("Plain Grid", b.Name);
        Assert.Equal(GridBranding.None with { Name = "Plain Grid" }, b with { News = [] });
        Assert.Empty(b.News);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<html><body>Welcome</body></html>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///x\">]><gridinfo><gridname>&e;</gridname></gridinfo>")]
    public void A_document_that_is_not_grid_info_gives_the_placeholder_look(string? body) => Assert.Equal(GridBranding.None, GridBranding.FromGridInfo(body));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://grid.example.org/register")]
    [InlineData("//grid.example.org/register")]
    [InlineData("register")]
    [InlineData("")]
    public void A_link_that_is_not_an_http_or_https_address_is_dropped(string link)
    {
        var b = GridBranding.FromGridInfo($"<gridinfo><register>{link}</register><password>{link}</password><logo>{link}</logo><splash>{link}</splash></gridinfo>");
        Assert.Null(b.Register);
        Assert.Null(b.ForgotPassword);
        Assert.Null(b.Logo);
        Assert.Null(b.Splash);
    }

    [Fact]
    public void A_link_with_a_user_name_and_password_in_it_is_dropped()
    {
        var b = GridBranding.FromGridInfo("<gridinfo><register>https://user:secret@grid.example.org/r</register></gridinfo>");
        Assert.Null(b.Register);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("#12345")]
    [InlineData("2dd4bf")]
    public void An_accent_that_is_not_a_colour_is_dropped(string accent) =>
        Assert.Null(GridBranding.FromGridInfo($"<gridinfo><accent>{accent}</accent></gridinfo>").Accent);

    [Fact]
    public void Control_characters_are_removed_and_long_texts_are_cut()
    {
        var b = GridBranding.FromGridInfo("<gridinfo><gridname>Ex\u0007ample\u001b[31m Grid</gridname><tagline>" + new string('x', 5000) + "</tagline></gridinfo>");
        Assert.Equal("Example[31m Grid", b.Name);
        Assert.Equal(GridBranding.MaxText, b.Tagline!.Length);
    }

    [Fact]
    public void A_name_of_only_blanks_is_no_name()
    {
        Assert.Null(GridBranding.FromGridInfo("<gridinfo><gridname>   </gridname></gridinfo>").Name);
    }

    [Fact]
    public void News_items_without_text_or_title_are_skipped_and_the_list_is_bounded()
    {
        string items = string.Concat(Enumerable.Range(0, 40).Select(i => $"<item title=\"T{i}\">Body {i}</item>"));
        var b = GridBranding.FromGridInfo($"<gridinfo><news><item/><item title=\"\"></item>{items}</news></gridinfo>");
        Assert.Equal(10, b.News.Count);
        Assert.Equal(new NewsItem("T0", "Body 0"), b.News[0]);
    }
}
