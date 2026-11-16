// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse.StructuredData;
using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Chat;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The grid's own settings from the region's simulator features (OpenSim extras): chat ranges, the map server, the search,
/// destination-guide and avatar-picker addresses, the currency, the export flag and the grid's status addresses, each read as
/// Firestorm reads it (lfsimfeaturehandler.cpp setSupportedFeatures), and Second Life's values where a grid sends none.
/// Pure values; the class runs in parallel.
/// </summary>
public class GridFeaturesTests
{
    private static Dictionary<string, object?> One(string key, object? value) => new() { [key] = value };

    [Fact]
    public void A_grid_that_sends_no_extras_gets_second_life_ranges_and_no_currency()
    {
        var f = GridFeatures.From(null, null);
        Assert.Equal(20, f.SayRange);
        Assert.Equal(100, f.ShoutRange);
        Assert.Equal(10, f.WhisperRange);
        Assert.False(f.HasCurrency);
        Assert.Null(f.BalanceText(250));
        Assert.False(f.ExportSupported);
        Assert.False(f.FromGrid);
        Assert.Null(f.SearchUrl);
        Assert.Null(f.DestinationGuideUrl);
    }

    [Fact]
    public void Without_extras_the_login_replys_addresses_stand()
    {
        var login = new GridLoginValues("http://maps.example.org/", "http://search.example.org/", "http://guide.example.org/", "http://pick.example.org/");
        var f = GridFeatures.From(null, login);
        Assert.Equal("http://maps.example.org/", f.MapServerUrl);
        Assert.Equal("http://search.example.org/", f.SearchUrl);
        Assert.Equal("http://guide.example.org/", f.DestinationGuideUrl);
        Assert.Equal("http://pick.example.org/", f.AvatarPickerUrl);
        Assert.Equal(20, f.SayRange);
    }

    [Fact]
    public void Each_chat_range_is_the_grids_own()
    {
        Assert.Equal(35, GridFeatures.From(One("say-range", 35L), null).SayRange);
        Assert.Equal(250, GridFeatures.From(One("shout-range", 250L), null).ShoutRange);
        Assert.Equal(4, GridFeatures.From(One("whisper-range", 4L), null).WhisperRange);
        // a key the grid leaves out keeps Second Life's value while the others are the grid's
        var f = GridFeatures.From(One("say-range", 35L), null);
        Assert.Equal(100, f.ShoutRange);
        Assert.Equal(10, f.WhisperRange);
        Assert.True(f.FromGrid);
    }

    [Fact]
    public void A_range_sent_as_a_real_number_is_read_as_whole_metres()
    {
        Assert.Equal(30, GridFeatures.From(One("say-range", 30.7), null).SayRange);
    }

    [Fact]
    public void The_grids_map_server_wins_over_the_login_replys()
    {
        var f = GridFeatures.From(One("map-server-url", "http://tiles.example.org/"), new GridLoginValues("http://maps.example.org/"));
        Assert.Equal("http://tiles.example.org/", f.MapServerUrl);
    }

    [Fact]
    public void Extras_without_a_map_server_keep_the_login_replys()
    {
        var f = GridFeatures.From(One("say-range", 20L), new GridLoginValues("http://maps.example.org/"));
        Assert.Equal("http://maps.example.org/", f.MapServerUrl);
    }

    [Fact]
    public void Search_destination_guide_and_avatar_picker_come_from_the_extras_first()
    {
        var extras = new Dictionary<string, object?>
        {
            ["search-server-url"] = "http://s.example.org/",
            ["destination-guide-url"] = "http://d.example.org/",
            ["avatar-picker-url"] = "http://a.example.org/",
        };
        var f = GridFeatures.From(extras, new GridLoginValues(null, "http://login-s.example.org/", "http://login-d.example.org/", "http://login-a.example.org/"));
        Assert.Equal("http://s.example.org/", f.SearchUrl);
        Assert.Equal("http://d.example.org/", f.DestinationGuideUrl);
        Assert.Equal("http://a.example.org/", f.AvatarPickerUrl);
    }

    [Fact]
    public void Extras_without_search_fall_back_to_the_login_reply()
    {
        var f = GridFeatures.From(One("say-range", 20L), new GridLoginValues(null, "http://login-s.example.org/", "http://login-d.example.org/"));
        Assert.Equal("http://login-s.example.org/", f.SearchUrl);
        Assert.Equal("http://login-d.example.org/", f.DestinationGuideUrl);
    }

    [Fact]
    public void The_grids_currency_symbol_shows_the_balance()
    {
        var f = GridFeatures.From(One("currency", "T$"), null);
        Assert.True(f.HasCurrency);
        Assert.Equal("T$", f.CurrencySymbol);
        Assert.Equal("T$1,250", f.BalanceText(1250));
    }

    [Fact]
    public void A_currency_named_by_the_login_reply_shows_the_balance_when_the_region_names_none()
    {
        var f = GridFeatures.From(One("say-range", 20L), new GridLoginValues(Currency: "G$"));
        Assert.Equal("G$", f.CurrencySymbol);
        Assert.Equal("G$7", f.BalanceText(7));
    }

    [Fact]
    public void A_currency_helper_address_alone_means_the_grid_has_a_currency()
    {
        var f = GridFeatures.From(One("currency-base-uri", "http://money.example.org/"), null);
        Assert.Equal("http://money.example.org/", f.CurrencyBaseUri);
        Assert.True(f.HasCurrency);
        Assert.Equal("OS$", f.CurrencySymbol);   // Firestorm's symbol for an OpenSim grid that names none (llstartup.cpp)
        Assert.Equal("OS$3", f.BalanceText(3));
    }

    [Fact]
    public void A_currency_helper_in_the_grids_own_document_means_the_grid_has_a_currency()
    {
        var f = GridFeatures.From(null, new GridLoginValues(CurrencyHelper: "http://money.example.org/"));
        Assert.True(f.HasCurrency);
        Assert.Equal("OS$", f.CurrencySymbol);
        Assert.Equal("http://money.example.org/", f.CurrencyBaseUri);
        // the region's own helper address wins
        Assert.Equal("http://region-money.example.org/", GridFeatures.From(One("currency-base-uri", "http://region-money.example.org/"),
            new GridLoginValues(CurrencyHelper: "http://money.example.org/")).CurrencyBaseUri);
    }

    [Fact]
    public async Task The_session_uses_the_grids_own_document_behind_the_region()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example:8002/" }, t, new CoreLog(new ListSink(), "test"));
        await Task.CompletedTask;
        s.GridInfoValues = new GridLoginValues(SearchUrl: "http://search.example.org/", CurrencyHelper: "http://money.example.org/");
        Assert.Equal("http://search.example.org/", s.GridFeatures.SearchUrl);
        Assert.True(s.GridFeatures.HasCurrency);
    }

    [Fact]
    public void A_grid_with_extras_but_no_currency_shows_no_balance()
    {
        var f = GridFeatures.From(One("say-range", 20L), null);
        Assert.False(f.HasCurrency);
        Assert.Null(f.BalanceText(0));
    }

    [Fact]
    public void The_export_flag_is_kept()
    {
        Assert.True(GridFeatures.From(One("ExportSupported", true), null).ExportSupported);
        Assert.False(GridFeatures.From(One("ExportSupported", false), null).ExportSupported);
        Assert.False(GridFeatures.From(One("say-range", 20L), null).ExportSupported);
    }

    [Fact]
    public void The_grid_address_loses_its_scheme_and_the_status_addresses_are_kept()
    {
        var extras = new Dictionary<string, object?>
        {
            ["GridURL"] = "http://grid.example.org:8002/",
            ["GridStatus"] = "http://status.example.org/",
            ["GridStatusRSS"] = "http://status.example.org/rss",
        };
        var f = GridFeatures.From(extras, null);
        Assert.Equal("grid.example.org:8002/", f.GridUrl);
        Assert.Equal("http://status.example.org/", f.GridStatusUrl);
        Assert.Equal("http://status.example.org/rss", f.GridStatusRss);
    }

    [Fact]
    public void A_value_of_the_wrong_kind_is_ignored()
    {
        var f = GridFeatures.From(new Dictionary<string, object?> { ["say-range"] = "far", ["map-server-url"] = 5L, ["currency"] = "" }, null);
        Assert.Equal(20, f.SayRange);
        Assert.Null(f.MapServerUrl);
        Assert.False(f.HasCurrency);
    }

    [Fact]
    public void A_line_is_beyond_say_range_only_past_the_grids_range()
    {
        var sl = GridFeatures.From(null, null);
        Assert.False(sl.BeyondSayRange(19.9f));
        Assert.True(sl.BeyondSayRange(20.1f));
        var wide = GridFeatures.From(One("say-range", 50L), null);
        Assert.False(wide.BeyondSayRange(30f));
        Assert.True(wide.BeyondSayRange(50.5f));
    }

    // ---- the adapter: the region's LLSD as plain values

    [Fact]
    public void The_adapter_reads_each_kind_of_value()
    {
        var map = new OSDMap
        {
            ["say-range"] = OSD.FromInteger(30),
            ["MaxPrimScale"] = OSD.FromReal(128.5),
            ["map-server-url"] = OSD.FromString("http://tiles.example.org/"),
            ["ExportSupported"] = OSD.FromBoolean(true),
        };
        var extras = LmvGridFeatures.Extras(map);
        Assert.NotNull(extras);
        Assert.Equal(30L, extras!["say-range"]);
        Assert.Equal(128.5, extras["MaxPrimScale"]);
        Assert.Equal("http://tiles.example.org/", extras["map-server-url"]);
        Assert.Equal(true, extras["ExportSupported"]);
        var f = GridFeatures.From(extras, null);
        Assert.Equal(30, f.SayRange);
        Assert.True(f.ExportSupported);
    }

    [Fact]
    public void The_adapter_gives_null_when_the_region_sent_no_extras()
    {
        Assert.Null(LmvGridFeatures.Extras(null));
        Assert.Null(LmvGridFeatures.Extras(OSD.FromString("not a map")));
    }

    // ---- nearby chat: a line from beyond the say range is dimmed, as the official viewer dims far chat (llviewerchat.cpp)

    [Fact]
    public void Nearby_chat_marks_a_line_from_beyond_the_say_range()
    {
        var self = Guid.NewGuid();
        var chat = new NearbyChat(self, _ => new Viewer.Core.Actions.ActionResult(Viewer.Core.Actions.ActionStatus.Invalid, 0, null), () => 100, () => false, () => DateTimeOffset.UnixEpoch);
        var features = GridFeatures.From(One("say-range", 30L), null);
        var own = new Vector3(100, 100, 25);
        chat.IsBeyondSayRange = p => features.BeyondSayRange(Vector3.Distance(p, own));
        chat.Receive(Line(new Vector3(125, 100, 25), "near"));
        chat.Receive(Line(new Vector3(140, 100, 25), "far"));
        Assert.False(chat.Lines[0].Far);
        Assert.True(chat.Lines[1].Far);
    }

    [Fact]
    public void A_far_line_is_drawn_at_eight_tenths()
    {
        var theme = ThemeSpec.Default;
        var strings = StringTable.English();
        var near = new ChatLine(DateTimeOffset.UnixEpoch, ChatLineStyle.Agent, Guid.NewGuid(), "Test User", "hello");
        string plain = ChatText.Format(near, theme, strings, false);
        string dim = ChatText.Format(near with { Far = true }, theme, strings, false);
        Assert.DoesNotContain(theme.ChatAgent.Hex + "cc", plain);
        Assert.Contains("[color=" + theme.ChatAgent.Hex + "cc]", dim);
    }

    private static ChatReceived Line(Vector3 at, string text) =>
        new(null, Guid.NewGuid(), Guid.Empty, "Test User", ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, at, new MessageBody(text, MessageChannel.LocalChat));
}
