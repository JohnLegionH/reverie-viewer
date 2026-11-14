// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The login screen asks the typed grid for its grid-info document once, to show the grid's own name, tagline, accent and links.
/// A grid that does not answer, answers with something else, or an address that is not an address leaves the placeholder look,
/// and a slow earlier answer never replaces a later one. Fake fetcher, example addresses. Every member builds its own model.
/// </summary>
public class LoginBrandingTests
{
    private const string Info = "<gridinfo><login>https://grid.example.org:8002/</login><gridname>Example Grid</gridname><accent>#2dd4bf</accent></gridinfo>";

    private static LoginModel Model(GridFetcher? fetcher) =>
        new(StringTable.English(), _ => throw new InvalidOperationException("no login in this test"), fetcher);

    [Fact]
    public async Task A_grid_that_answers_gives_its_branding_once_and_the_screen_is_told()
    {
        var asked = new List<Uri>();
        var m = Model((uri, _) => { asked.Add(uri); return Task.FromResult(new GridFetch(200, Info, null)); });
        var told = new List<GridBranding>();
        m.BrandingChanged += told.Add;
        await m.RefreshBrandingAsync("https://grid.example.org:8002/");
        Assert.Equal(new Uri("https://grid.example.org:8002/get_grid_info"), Assert.Single(asked));
        Assert.Equal("Example Grid", m.Branding.Name);
        Assert.Equal("Example Grid", Assert.Single(told).Name);
    }

    [Fact]
    public async Task A_grid_that_does_not_answer_or_answers_with_something_else_leaves_the_placeholder_look()
    {
        var down = Model((_, _) => Task.FromResult(new GridFetch(null, null, "HttpRequestException")));
        await down.RefreshBrandingAsync("https://grid.example.org:8002/");
        Assert.Equal(GridBranding.None, down.Branding);
        var html = Model((_, _) => Task.FromResult(new GridFetch(200, "<html>Welcome</html>", null)));
        await html.RefreshBrandingAsync("https://grid.example.org:8002/");
        Assert.Equal(GridBranding.None, html.Branding);
        var missing = Model((_, _) => Task.FromResult(new GridFetch(404, Info, null)));   // a body from an error page is not trusted
        await missing.RefreshBrandingAsync("https://grid.example.org:8002/");
        Assert.Equal(GridBranding.None, missing.Branding);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("ftp://grid.example.org/")]
    public async Task An_address_that_is_not_an_address_asks_nothing_and_clears_the_look(string typed)
    {
        int asked = 0;
        var m = Model((_, _) => { asked++; return Task.FromResult(new GridFetch(200, Info, null)); });
        await m.RefreshBrandingAsync("https://grid.example.org:8002/");
        Assert.Equal("Example Grid", m.Branding.Name);
        await m.RefreshBrandingAsync(typed);
        Assert.Equal(1, asked);
        Assert.Equal(GridBranding.None, m.Branding);
    }

    [Fact]
    public async Task A_slow_earlier_answer_never_replaces_a_later_one()
    {
        var first = new TaskCompletionSource<GridFetch>();
        var m = Model((uri, _) => uri.Host == "slow.example.org" ? first.Task : Task.FromResult(new GridFetch(200, Info, null)));
        var slow = m.RefreshBrandingAsync("https://slow.example.org/");
        await m.RefreshBrandingAsync("https://grid.example.org:8002/");
        Assert.Equal("Example Grid", m.Branding.Name);
        first.SetResult(new GridFetch(200, "<gridinfo><gridname>Slow Grid</gridname></gridinfo>", null));
        await slow;
        Assert.Equal("Example Grid", m.Branding.Name);
    }

    [Fact]
    public async Task With_no_way_to_ask_the_look_stays_the_placeholder()
    {
        var m = Model(null);
        await m.RefreshBrandingAsync("https://grid.example.org:8002/");
        Assert.Equal(GridBranding.None, m.Branding);
    }

    [Fact]
    public void Remembering_the_username_is_off_until_the_person_asks_and_stores_nothing_else()
    {
        var s = new SettingsService(null);
        Assert.False(s.Get(Settings.LoginRememberName));
        Assert.Null(s.GetText(Settings.LoginFirstNameKey));
    }
}
