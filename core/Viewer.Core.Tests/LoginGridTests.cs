// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Shell;
using Viewer.Shell.Grids;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The grid-info document as the grid manager reads it (the fields Firestorm's grid manager reads).</summary>
public class GridInfoParseTests
{
    private const string Full =
        "<gridinfo>" +
        "<login>http://grid.example.org:8002/</login>" +
        "<gridname>Example Grid</gridname>" +
        "<gridnick>example</gridnick>" +
        "<welcome>http://grid.example.org/welcome</welcome>" +
        "<register>http://grid.example.org/register</register>" +
        "<password>http://grid.example.org/forgot</password>" +
        "<gatekeeper>http://grid.example.org:8002/</gatekeeper>" +
        "<economy>http://grid.example.org:8008/</economy>" +
        "<help>http://grid.example.org/help</help>" +
        "<about>http://grid.example.org/about</about>" +
        "<search>http://grid.example.org/search</search>" +
        "<profileuri>http://grid.example.org/profile</profileuri>" +
        "<platform>OpenSim</platform>" +
        "<message>Welcome to the example grid</message>" +
        "</gridinfo>";

    [Fact]
    public void Every_field_the_grid_manager_reads_is_read()
    {
        var info = GridInfo.Parse(Full);
        Assert.NotNull(info);
        Assert.Equal("Example Grid", info!.Name);
        Assert.Equal("example", info.Nick);
        Assert.Equal("http://grid.example.org:8002/", info.Login?.ToString());
        Assert.Equal("http://grid.example.org/welcome", info.Welcome?.ToString());
        Assert.Equal("http://grid.example.org/register", info.Register?.ToString());
        Assert.Equal("http://grid.example.org/forgot", info.Password?.ToString());
        Assert.Equal("grid.example.org:8002", info.Gatekeeper);
        Assert.Equal("http://grid.example.org:8008/", info.Economy?.ToString());
        Assert.Equal("http://grid.example.org/help", info.Help?.ToString());
        Assert.Equal("http://grid.example.org/about", info.About?.ToString());
        Assert.Equal("http://grid.example.org/search", info.Search?.ToString());
        Assert.Equal("http://grid.example.org/profile", info.Profile?.ToString());
        Assert.Equal("OpenSim", info.Platform);
        Assert.Equal("Welcome to the example grid", info.Message);
        Assert.True(info.Hypergrid);
        Assert.True(info.Currency);
    }

    [Fact]
    public void Helperuri_stands_for_economy_and_web_profile_url_for_profileuri()
    {
        var info = GridInfo.Parse("<gridinfo><gridname>G</gridname><helperuri>http://grid.example.org:8008/</helperuri><web_profile_url>http://grid.example.org/p</web_profile_url></gridinfo>");
        Assert.NotNull(info);
        Assert.Equal("http://grid.example.org:8008/", info!.Economy?.ToString());
        Assert.Equal("http://grid.example.org/p", info.Profile?.ToString());
        Assert.True(info.Currency);
    }

    [Fact]
    public void A_grid_without_a_gatekeeper_or_economy_has_no_Hypergrid_and_no_currency()
    {
        var info = GridInfo.Parse("<gridinfo><gridname>Plain Grid</gridname><login>https://grid.example.org/</login></gridinfo>");
        Assert.NotNull(info);
        Assert.Equal("Plain Grid", info!.Name);
        Assert.False(info.Hypergrid);
        Assert.False(info.Currency);
        Assert.Null(info.Register);
        Assert.Null(info.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Not found</body></html>")]
    [InlineData("not xml at all")]
    public void Text_that_is_not_a_grid_info_document_is_not_read(string body) => Assert.Null(GridInfo.Parse(body));

    [Fact]
    public void Page_links_that_are_not_web_addresses_are_dropped()
    {
        var info = GridInfo.Parse("<gridinfo><gridname>G</gridname><register>javascript:alert(1)</register><password>file:///c:/x</password><help>http://u:p@grid.example.org/</help></gridinfo>");
        Assert.NotNull(info);
        Assert.Equal("G", info!.Name);
        Assert.Null(info.Register);
        Assert.Null(info.Password);
        Assert.Null(info.Help);
    }
}

/// <summary>The grid list in the viewer's own settings: empty at first, add, choose, remove, --grid, and the last address of an older version.</summary>
public class GridListTests
{
    private static GridEntry Entry(string address, string name) => new(address, name, null);

    [Fact]
    public void No_grid_ships_with_the_viewer()
    {
        var list = new GridList(TestTemp.NewFolder("grids-empty"));
        Assert.Empty(list.Entries);
        Assert.Null(list.Selected);
    }

    [Fact]
    public void An_added_grid_is_chosen_and_kept_in_the_settings_folder()
    {
        string dir = TestTemp.NewFolder("grids-add");
        var list = new GridList(dir);
        list.Add(Entry("https://grid.example.org:8002/", "Example Grid"));
        Assert.Equal("Example Grid", list.Selected?.Name);
        Assert.True(File.Exists(Path.Combine(dir, GridList.FileName)));

        var again = new GridList(dir);
        var only = Assert.Single(again.Entries);
        Assert.Equal("Example Grid", only.Name);
        Assert.Equal("https://grid.example.org:8002/", only.Address);
        Assert.Equal("Example Grid", again.Selected?.Name);
    }

    [Fact]
    public void Adding_the_same_grid_again_replaces_it_and_does_not_list_it_twice()
    {
        var list = new GridList(TestTemp.NewFolder("grids-same"));
        list.Add(Entry("https://grid.example.org:8002/", "Old Name"));
        list.Add(Entry("https://GRID.example.org:8002/get_grid_info", "New Name"));
        var only = Assert.Single(list.Entries);
        Assert.Equal("New Name", only.Name);
    }

    [Fact]
    public void The_key_ignores_case_get_grid_info_and_a_closing_slash()
    {
        Assert.Equal("grid.example.org:8002", GridList.KeyOf("https://GRID.example.org:8002/"));
        Assert.Equal("grid.example.org:8002", GridList.KeyOf("https://grid.example.org:8002/get_grid_info"));
        Assert.Equal("grid.example.org:443/login", GridList.KeyOf("https://grid.example.org/login/"));
        Assert.Equal("", GridList.KeyOf("not an address"));
    }

    [Fact]
    public void Remove_takes_the_grid_off_the_list_only()
    {
        string dir = TestTemp.NewFolder("grids-remove");
        var list = new GridList(dir);
        var names = new SavedNames(dir);
        list.Add(Entry("https://a.example.org/", "A"));
        var b = list.Add(Entry("https://b.example.org/", "B"));
        names.Remember("https://b.example.org/", "Test User");
        Assert.True(list.Remove(b.Key));
        Assert.Equal(["A"], list.Entries.Select(e => e.Name));
        Assert.Equal("A", list.Selected?.Name);   // the chosen grid went, so the first left is chosen
        Assert.Equal(["Test User"], names.Names("https://b.example.org/"));   // its saved names stay
        Assert.Single(new GridList(dir).Entries);
    }

    [Fact]
    public void Select_chooses_a_listed_grid_and_is_kept()
    {
        string dir = TestTemp.NewFolder("grids-select");
        var list = new GridList(dir);
        var a = list.Add(Entry("https://a.example.org/", "A"));
        list.Add(Entry("https://b.example.org/", "B"));
        Assert.True(list.Select(a.Key));
        Assert.False(list.Select("nothing.example.org:443"));
        Assert.Equal("A", new GridList(dir).Selected?.Name);
    }

    [Fact]
    public void Grid_on_the_command_line_adds_a_new_grid_and_chooses_it()
    {
        var list = new GridList(TestTemp.NewFolder("grids-cmd"));
        list.Add(Entry("https://a.example.org/", "A"));
        var e = list.ApplyCommandLine("https://grid.example.org:8002/");
        Assert.NotNull(e);
        Assert.Equal(2, list.Entries.Count);
        Assert.Equal("https://grid.example.org:8002/", list.Selected?.Address);
        Assert.Equal("grid.example.org:8002", list.Selected?.Name);   // the host until its grid info is read
    }

    [Fact]
    public void Grid_on_the_command_line_chooses_a_listed_grid_without_adding_it_again()
    {
        var list = new GridList(TestTemp.NewFolder("grids-cmd2"));
        list.Add(Entry("https://grid.example.org:8002/", "Example Grid"));
        list.Add(Entry("https://a.example.org/", "A"));
        Assert.NotNull(list.ApplyCommandLine("https://grid.example.org:8002"));
        Assert.Equal(2, list.Entries.Count);
        Assert.Equal("Example Grid", list.Selected?.Name);
        Assert.Null(list.ApplyCommandLine("grid.example.org"));   // not an address: ignored
        Assert.Equal("Example Grid", list.Selected?.Name);
    }

    [Fact]
    public void The_last_login_address_of_an_earlier_version_becomes_the_first_grid()
    {
        string dir = TestTemp.NewFolder("grids-migrate");
        LoginAddressStore.Save(dir, "https://grid.example.org:8002/");
        var list = new GridList(dir);
        var only = Assert.Single(list.Entries);
        Assert.Equal("https://grid.example.org:8002/", only.Address);
        Assert.Equal(only.Key, list.Selected?.Key);
    }

    [Fact]
    public void A_damaged_list_file_reads_as_an_empty_list()
    {
        string dir = TestTemp.NewFolder("grids-damaged");
        File.WriteAllText(Path.Combine(dir, GridList.FileName), "{ not json");
        var list = new GridList(dir);
        Assert.Empty(list.Entries);
        list.Add(Entry("https://a.example.org/", "A"));
        Assert.Single(new GridList(dir).Entries);
    }
}

/// <summary>Add a grid's Look up and Refresh info, and the http warning.</summary>
public class GridLookupTests
{
    private const string Info = "<gridinfo><login>{0}</login><gridname>Example Grid</gridname><gridnick>example</gridnick>" +
        "<register>https://grid.example.org/join</register><password>https://grid.example.org/reset</password><gatekeeper>https://grid.example.org:8002/</gatekeeper></gridinfo>";

    private static GridFetcher Answer(Func<Uri, GridFetch> answer, List<Uri>? asked = null) => (uri, _) =>
    {
        asked?.Add(uri);
        return Task.FromResult(answer(uri));
    };

    [Fact]
    public async Task Look_up_reads_the_grid_info_document_at_the_address_typed()
    {
        var asked = new List<Uri>();
        var r = await GridLookup.LookUpAsync(" https://grid.example.org:8002/ ", Answer(u => new GridFetch(200, string.Format(Info, "https://grid.example.org:8002/"), null), asked), default);
        Assert.Null(r.ProblemKey);
        Assert.NotNull(r.Entry);
        Assert.Equal("https://grid.example.org:8002/get_grid_info", Assert.Single(asked).ToString());
        Assert.Equal("Example Grid", r.Entry!.Name);
        Assert.Equal("https://grid.example.org:8002/", r.Entry.Address);
        Assert.Equal("https://grid.example.org/join", r.Entry.Info?.Register?.ToString());
        Assert.Equal("https://grid.example.org/reset", r.Entry.Info?.Password?.ToString());
        Assert.True(r.Entry.Info?.Hypergrid);
        Assert.False(r.Entry.Info?.Currency);
        Assert.False(r.PlainHttp);
    }

    [Fact]
    public async Task An_address_that_does_not_answer_gets_a_plain_message()
    {
        var r = await GridLookup.LookUpAsync("https://grid.example.org:8002/", Answer(_ => new GridFetch(null, null, "HttpRequestException")), default);
        Assert.Null(r.Entry);
        Assert.Equal("grid.add.no_answer", r.ProblemKey);
        Assert.False(string.IsNullOrWhiteSpace(StringTable.English().Get(r.ProblemKey!)));
    }

    [Theory]
    [InlineData(404, "<html>Not found</html>")]
    [InlineData(200, "<html>A web page</html>")]
    public async Task An_address_that_answers_but_is_not_a_grid_says_so(int status, string body)
    {
        var r = await GridLookup.LookUpAsync("https://grid.example.org:8002/", Answer(_ => new GridFetch(status, body, null)), default);
        Assert.Null(r.Entry);
        Assert.Equal("grid.add.not_grid", r.ProblemKey);
        Assert.False(string.IsNullOrWhiteSpace(StringTable.English().Get(r.ProblemKey!)));
    }

    [Fact]
    public async Task An_address_that_is_not_an_address_is_not_looked_up()
    {
        var asked = new List<Uri>();
        var r = await GridLookup.LookUpAsync("grid.example.org", Answer(_ => new GridFetch(200, "", null), asked), default);
        Assert.Null(r.Entry);
        Assert.Equal("login.status.no_scheme", r.ProblemKey);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task A_plain_http_address_is_warned_about_and_can_still_be_added()
    {
        var r = await GridLookup.LookUpAsync("http://grid.example.org:8002/", Answer(_ => new GridFetch(200, string.Format(Info, "http://grid.example.org:8002/"), null)), default);
        Assert.True(r.PlainHttp);
        Assert.NotNull(r.Entry);
        Assert.Equal(GridLookup.HttpWarningKey, GridLookup.HttpWarning(new Uri("http://grid.example.org:8002/")));
        Assert.Null(GridLookup.HttpWarning(new Uri("https://grid.example.org:8002/")));
        Assert.Equal("This address uses http, so the connection isn't encrypted", StringTable.English().Get(GridLookup.HttpWarningKey));
    }

    [Fact]
    public async Task Refresh_info_takes_what_the_grid_says_now_and_keeps_a_name_the_person_gave()
    {
        var old = new GridEntry("https://grid.example.org:8002/", "My Name", null, NameEdited: true);
        var r = await GridLookup.RefreshAsync(old, Answer(_ => new GridFetch(200, string.Format(Info, "https://grid.example.org:8002/"), null)), default);
        Assert.NotNull(r.Entry);
        Assert.Equal("My Name", r.Entry!.Name);
        Assert.Equal("https://grid.example.org/join", r.Entry.Info?.Register?.ToString());
        Assert.NotNull(r.Entry.Refreshed);

        var plain = old with { Name = "grid.example.org:8002", NameEdited = false };
        var r2 = await GridLookup.RefreshAsync(plain, Answer(_ => new GridFetch(200, string.Format(Info, "https://grid.example.org:8002/"), null)), default);
        Assert.Equal("Example Grid", r2.Entry?.Name);
    }
}

/// <summary>Saved names per grid, and forgetting one with its saved password.</summary>
public class SavedNamesTests
{
    private sealed class MemoryVault : ILoginVault
    {
        public readonly Dictionary<string, string> Kept = new();
        public bool Available => true;
        public string? UnavailableKey => null;
        public void Save(string grid, string name, Secret hash) => Kept[RememberPassword.EntryKey(grid, name)] = hash.Reveal();
        public Secret? Load(string grid, string name) =>
            Kept.TryGetValue(RememberPassword.EntryKey(grid, name), out var h) ? new Secret(h, SensitiveKind.PasswordDigest) : null;
        public bool Forget(string grid, string name) => Kept.Remove(RememberPassword.EntryKey(grid, name));
    }

    private const string A = "https://a.example.org:8002/", B = "https://b.example.org/";

    [Fact]
    public void Each_grid_has_its_own_saved_names_the_latest_first()
    {
        var names = new SavedNames(TestTemp.NewFolder("names-grid"));
        names.Remember(A, "Test User");
        names.Remember(A, "Second Tester");
        names.Remember(B, "Other Person");
        Assert.Equal(["Second Tester", "Test User"], names.Names(A));
        Assert.Equal(["Other Person"], names.Names(B));
        Assert.Equal(["Second Tester", "Test User"], names.Names("https://A.example.org:8002"));   // the same grid however it is written
    }

    [Fact]
    public void A_name_saved_again_moves_to_the_front_and_is_not_listed_twice()
    {
        var names = new SavedNames(TestTemp.NewFolder("names-again"));
        names.Remember(A, "Test User");
        names.Remember(A, "Second Tester");
        names.Remember(A, "test  user");
        Assert.Equal(["test user", "Second Tester"], names.Names(A));
    }

    [Fact]
    public void Saved_names_are_kept_in_the_settings_folder()
    {
        string dir = TestTemp.NewFolder("names-keep");
        new SavedNames(dir).Remember(A, "Test User");
        Assert.True(File.Exists(Path.Combine(dir, SavedNames.FileName)));
        Assert.Equal(["Test User"], new SavedNames(dir).Names(A));
    }

    [Fact]
    public void Forgetting_a_name_forgets_its_saved_password_on_that_grid_only()
    {
        var vault = new MemoryVault();
        var names = new SavedNames(TestTemp.NewFolder("names-forget"));
        names.Remember(A, "Test User");
        names.Remember(A, "Second Tester");
        names.Remember(B, "Test User");
        vault.Save(A, "Test User", new Secret(LoginHash.Of("one"), SensitiveKind.PasswordDigest));
        vault.Save(B, "Test User", new Secret(LoginHash.Of("two"), SensitiveKind.PasswordDigest));
        Assert.True(names.Forget(A, "test user", vault));
        Assert.Equal(["Second Tester"], names.Names(A));
        Assert.Null(vault.Load(A, "Test User"));
        Assert.NotNull(vault.Load(B, "Test User"));
        Assert.Equal(["Test User"], names.Names(B));
        Assert.False(names.Forget(A, "Nobody Here", vault));
    }

    [Fact]
    public void Clear_all_forgets_every_saved_name()
    {
        string dir = TestTemp.NewFolder("names-clear");
        var names = new SavedNames(dir);
        names.Remember(A, "Test User");
        Assert.True(names.ClearAll());
        Assert.Empty(new SavedNames(dir).Names(A));
        Assert.False(names.ClearAll());
    }
}

/// <summary>The login screen's fields: Enter logs in from every text field, Start at's three choices, and the remember ticks.</summary>
public class LoginScreenFieldTests
{
    [Fact]
    public void The_login_card_holds_the_controls_of_the_approved_layout_in_order()
    {
        Assert.Equal(["grid", "username", "password", "remember", "remember_password", "start", "start_region", "login", "cancel", "status"],
            LoginScreenSpec.Controls.Select(c => c.Id));
        Assert.Equal(LoginControlKind.Choice, LoginScreenSpec.Controls.Single(c => c.Id == "grid").Kind);
        Assert.Equal(LoginControlKind.Check, LoginScreenSpec.Controls.Single(c => c.Id == "remember").Kind);
        Assert.Equal(LoginControlKind.Check, LoginScreenSpec.Controls.Single(c => c.Id == "remember_password").Kind);
        Assert.Equal(LoginControlKind.Choice, LoginScreenSpec.Controls.Single(c => c.Id == "start").Kind);
        Assert.Equal("First Last", StringTable.English().Get(LoginScreenSpec.Controls.Single(c => c.Id == "username").HintKey!));
    }

    [Fact]
    public void Enter_in_any_text_field_logs_in()
    {
        var texts = LoginScreenSpec.Controls.Where(c => c.Kind is LoginControlKind.Text or LoginControlKind.Secret).Select(c => c.Id).ToList();
        Assert.Equal(["username", "password", "start_region"], texts);
        Assert.All(LoginScreenSpec.Controls.Where(c => c.Kind is LoginControlKind.Text or LoginControlKind.Secret), c => Assert.True(c.EnterLogsIn, c.Id));
        Assert.All(LoginScreenSpec.Controls.Where(c => c.Kind is not (LoginControlKind.Text or LoginControlKind.Secret)), c => Assert.False(c.EnterLogsIn, c.Id));
    }

    [Theory]
    [InlineData("last", StartKind.Last, "")]
    [InlineData("home", StartKind.Home, "")]
    [InlineData("HOME", StartKind.Home, "")]
    [InlineData("", StartKind.Last, "")]
    [InlineData(" Some Region ", StartKind.Region, "Some Region")]
    public void Start_at_reads_last_home_or_a_region(string start, StartKind kind, string region)
    {
        var (k, r) = StartChoice.Parse(start);
        Assert.Equal(kind, k);
        Assert.Equal(region, r);
    }

    [Theory]
    [InlineData(StartKind.Last, "ignored", "last")]
    [InlineData(StartKind.Home, "", "home")]
    [InlineData(StartKind.Region, "  Some Region ", "Some Region")]
    [InlineData(StartKind.Region, "   ", "last")]
    public void Start_at_sends_last_home_or_the_region_name(StartKind kind, string region, string sent) =>
        Assert.Equal(sent, StartChoice.ToStart(kind, region));

    [Fact]
    public void Remember_password_can_be_ticked_only_while_Remember_name_is()
    {
        Assert.True(StartChoice.PasswordTickEnabled(true));
        Assert.False(StartChoice.PasswordTickEnabled(false));
    }
}
