// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Grids;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The built-in Second Life entry: on every grid list, with the official viewer's main grid login address
/// (llviewernetwork.cpp, MAIN_GRID_LOGIN_URI), never saved to or removed from the person's list, and never asked for a grid-info
/// document (Second Life serves none): not by the login, the branding, Look up or Refresh info.
/// </summary>
public class SecondLifeGridTests
{
    private const string MainGrid = "https://login.agni.lindenlab.com/cgi-bin/login.cgi";

    private sealed class CountingFetcher
    {
        public readonly List<Uri> Asked = [];
        public Task<GridFetch> Fetch(Uri uri, CancellationToken ct)
        {
            lock (Asked) Asked.Add(uri);
            return Task.FromResult(new GridFetch(404, "", null));
        }
    }

    [Fact]
    public void The_list_has_a_built_in_Second_Life_entry_with_the_official_main_grid_login_address()
    {
        var list = new GridList(TestTemp.NewFolder("sl-builtin"));
        var sl = Assert.Single(list.BuiltIn);
        Assert.Equal("Second Life", sl.Name);
        Assert.Equal(MainGrid, sl.Address);
        Assert.True(sl.BuiltIn);
        Assert.Equal(GridKind.SecondLife, GridPolicy.KindOf(sl.Address));
        Assert.Contains(list.All, e => e.Key == sl.Key);
        Assert.Empty(list.Entries);   // the person's own list is still empty
        Assert.Null(list.Selected);   // nothing is chosen for the person
    }

    [Fact]
    public void Choosing_Second_Life_is_kept_but_the_entry_is_never_written_to_the_list_file()
    {
        string dir = TestTemp.NewFolder("sl-choose");
        var list = new GridList(dir);
        list.Add(new GridEntry("https://grid.example.org:8002/", "Example Grid", null));
        Assert.True(list.Select(Assert.Single(list.BuiltIn).Key));
        Assert.Equal("Second Life", list.Selected?.Name);
        Assert.DoesNotContain("https://login.agni", File.ReadAllText(Path.Combine(dir, GridList.FileName)), StringComparison.OrdinalIgnoreCase);

        var again = new GridList(dir);
        Assert.Equal("Second Life", again.Selected?.Name);
        Assert.Equal(["Example Grid"], again.Entries.Select(e => e.Name));
    }

    [Fact]
    public void The_built_in_entry_cannot_be_removed_or_replaced()
    {
        var list = new GridList(TestTemp.NewFolder("sl-keep"));
        var sl = Assert.Single(list.BuiltIn);
        Assert.False(list.Remove(sl.Key));
        list.Add(new GridEntry(MainGrid, "My Grid", null));
        Assert.Equal("Second Life", list.Find(MainGrid)?.Name);
        Assert.Empty(list.Entries);
        Assert.Single(list.BuiltIn);
    }

    [Theory]
    [InlineData("https://login.agni.lindenlab.com/")]
    [InlineData("https://login.aditi.lindenlab.com/cgi-bin/login.cgi")]
    [InlineData("http://login.agni.lindenlab.com/cgi-bin/login.cgi")]
    public void No_other_Linden_Lab_address_reaches_the_persons_list_by_command_line_add_or_the_file(string address)
    {
        string dir = TestTemp.NewFolder("sl-other");
        var list = new GridList(dir);
        Assert.Null(list.ApplyCommandLine(address));
        Assert.Equal("Second Life", list.Add(new GridEntry(address, "Mine", null)).Name);   // adding a Linden Lab address chooses the built-in one
        Assert.Empty(list.Entries);
        Assert.Equal("Second Life", list.Selected?.Name);

        // a list file that already holds one (written by hand, or by an earlier version) drops it on reading
        File.WriteAllText(Path.Combine(dir, GridList.FileName),
            "{\"Selected\":null,\"Grids\":[{\"Address\":\"" + address + "\",\"Name\":\"Mine\"},{\"Address\":\"https://grid.example.org:8002/\",\"Name\":\"Example Grid\"}]}");
        Assert.Equal(["Example Grid"], new GridList(dir).Entries.Select(e => e.Name));
    }

    [Fact]
    public void The_main_grid_address_on_the_command_line_chooses_the_built_in_entry()
    {
        var list = new GridList(TestTemp.NewFolder("sl-cmd"));
        Assert.Equal("Second Life", list.ApplyCommandLine(MainGrid)?.Name);
        Assert.Empty(list.Entries);
        Assert.Equal("Second Life", list.Selected?.Name);
    }

    [Fact]
    public async Task Look_up_and_refresh_of_Second_Life_ask_for_no_grid_info()
    {
        var f = new CountingFetcher();
        var found = await GridLookup.LookUpAsync(MainGrid, f.Fetch, CancellationToken.None);
        Assert.Equal("Second Life", found.Entry?.Name);
        Assert.True(found.Entry?.BuiltIn);
        var refreshed = await GridLookup.RefreshAsync(Assert.Single(new GridList(null).BuiltIn), f.Fetch, CancellationToken.None);
        Assert.Equal("Second Life", refreshed.Entry?.Name);
        Assert.Empty(f.Asked);
    }

    [Fact]
    public async Task The_login_goes_to_the_main_grid_address_as_given_and_asks_for_no_grid_info()
    {
        var f = new CountingFetcher();
        var flow = LoginFlow.At(f.Fetch, MainGrid, LoginFlow.Fail("key", "Sorry! We couldn't log you in."));
        var model = flow.Model;

        Assert.False(await model.LoginAsync());

        Assert.Empty(f.Asked);
        Assert.Equal(MainGrid, Assert.Single(flow.Requests).LoginUri);
        Assert.Equal(MainGrid, model.ResolvedLoginUri);
        Assert.Contains("Sorry! We couldn't log you in.", model.Status);
    }

    [Fact]
    public async Task The_branding_of_Second_Life_asks_for_no_grid_info()
    {
        var f = new CountingFetcher();
        var model = new LoginModel(StringTable.English(), _ => throw new InvalidOperationException(), f.Fetch);
        await model.RefreshBrandingAsync(MainGrid);
        Assert.Empty(f.Asked);
        Assert.Equal(GridBranding.None, model.Branding);
    }

    // The login service's replies below have the fields the official viewer reads for each reason (lllogininstance.cpp,
    // handleLoginFailure): reason, message, message_id and error_code. The two-factor flow on Second Life: a challenge with
    // message_id LoginFailedAuthenticationMFARequired, then a retry carrying the token (llpanellogin/lllogininstance).

    [Fact]
    public async Task On_Second_Life_the_terms_are_shown_and_agreeing_logs_in_again_at_the_same_address()
    {
        var f = new CountingFetcher();
        var flow = LoginFlow.At(f.Fetch, MainGrid, LoginFlow.Fail("tos", "Please read and agree to the Second Life Terms of Service."), LoginFlow.Ok())
            .Answer(new LoginDialogResult(true, Agreed: true));

        Assert.True(await flow.Model.LoginAsync());

        Assert.Equal(LoginDialogKind.Terms, Assert.Single(flow.Shown).Kind);
        Assert.Equal([MainGrid, MainGrid], flow.Requests.Select(r => r.LoginUri));
        Assert.True(flow.Requests[1].Extras!.AgreeToTos);
        Assert.Empty(f.Asked);
    }

    [Fact]
    public async Task On_Second_Life_the_critical_message_is_shown_and_reading_it_logs_in_again()
    {
        var f = new CountingFetcher();
        var flow = LoginFlow.At(f.Fetch, MainGrid, LoginFlow.Fail("critical", "Linden Lab has updated its Privacy Policy.", errorCode: "privacy"), LoginFlow.Ok())
            .Answer(new LoginDialogResult(true));

        Assert.True(await flow.Model.LoginAsync());

        var d = Assert.Single(flow.Shown);
        Assert.Equal(LoginDialogKind.Critical, d.Kind);
        Assert.Contains("Linden Lab has updated its Privacy Policy.", d.ReadOnlyText);
        Assert.True(flow.Requests[1].Extras!.ReadCritical);
        Assert.Empty(f.Asked);
    }

    [Fact]
    public async Task On_Second_Life_a_two_factor_challenge_asks_for_the_token_and_the_retry_carries_it()
    {
        var f = new CountingFetcher();
        var flow = LoginFlow.At(f.Fetch, MainGrid,
            LoginFlow.Fail("mfa_challenge", "Multifactor authentication required", messageId: "LoginFailedAuthenticationMFARequired"),
            LoginFlow.Ok(mfaHash: "hash-from-second-life"))
            .Answer(new LoginDialogResult(true, "654321"));

        Assert.True(await flow.Model.LoginAsync());

        Assert.Equal(LoginDialogKind.Token, Assert.Single(flow.Shown).Kind);
        Assert.Equal("654321", flow.Requests[1].Extras!.Token!.Reveal());
        Assert.Equal(MainGrid, flow.Requests[1].LoginUri);
        Assert.Empty(f.Asked);
    }

    [Fact]
    public async Task Another_grid_is_still_asked_for_its_grid_info()
    {
        var f = new CountingFetcher();
        await GridLookup.LookUpAsync("https://grid.example.org:8002/", f.Fetch, CancellationToken.None);
        Assert.Single(f.Asked);
    }
}
