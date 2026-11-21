// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse.StructuredData;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A grid's own name, currency symbol and currency name, and how they follow the grid the person is in. Precedence for the name:
/// the region's GridName, then the grid-info document's gridname. For the symbol: the region's currency, then the login reply's, then
/// the grid-info document's, then OS$ where the grid has a currency but names no symbol. The word "none" means the grid has no money.
/// On a Hypergrid visit the name and symbol are the visited grid's, and the balance keeps the home grid's symbol. The region's values
/// are fixtures in the shape the simulator features answer has (an OSDMap "OpenSimExtras"). Pure values; the class runs in parallel.
/// </summary>
public class GridPersonalFeaturesTests
{
    private static readonly string[] Home = ["home.example.org"];

    private static IReadOnlyDictionary<string, object?> Extras(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    /// <summary>The OpenSimExtras of a region on the home grid and of one on another grid, as the simulator features answer carries them.</summary>
    private static IReadOnlyDictionary<string, object?>? Fixture(string name)
    {
        var map = new OSDMap();
        switch (name)
        {
            case "home-region":
                map["GridName"] = "Home Grid"; map["GridURL"] = "http://home.example.org:8002/"; map["currency"] = "H$";
                map["currency-base-uri"] = "http://home.example.org:8008/"; map["say-range"] = 20;
                break;
            case "visited-region":
                map["GridName"] = "Visited Grid"; map["GridURL"] = "http://visited.example.net:9000/"; map["currency"] = "V$";
                map["currency-base-uri"] = "http://visited.example.net:8008/"; map["say-range"] = 20;
                break;
            case "visited-region-no-currency":
                map["GridName"] = "Visited Grid"; map["GridURL"] = "http://visited.example.net:9000/";
                break;
        }
        return LmvGridFeatures.Extras(map);
    }

    private static GridLoginValues HomeValues(string? currency = "H$") =>
        new(GridName: "Home Grid", InfoCurrency: currency, CurrencyHelper: "http://home.example.org:8008/", HomeHosts: Home);

    [Fact]
    public void The_regions_grid_name_wins_over_the_grid_info_name()
    {
        var f = GridFeatures.From(Extras(("GridName", "Region Name")), new GridLoginValues(GridName: "Info Name"));
        Assert.Equal("Region Name", f.GridName);
        Assert.Equal("Info Name", GridFeatures.From(null, new GridLoginValues(GridName: "Info Name")).GridName);
        Assert.Null(GridFeatures.From(null, null).GridName);
    }

    [Fact]
    public void The_symbol_is_the_regions_then_the_login_replys_then_the_grid_infos_then_OS_when_the_grid_has_a_currency_helper()
    {
        var all = new GridLoginValues(Currency: "L1", InfoCurrency: "I1");
        Assert.Equal("R1", GridFeatures.From(Extras(("currency", "R1")), all).CurrencySymbol);
        Assert.Equal("L1", GridFeatures.From(Extras(("say-range", 20L)), all).CurrencySymbol);
        Assert.Equal("I1", GridFeatures.From(null, new GridLoginValues(InfoCurrency: "I1")).CurrencySymbol);
        Assert.Equal("OS$", GridFeatures.From(null, new GridLoginValues(CurrencyHelper: "http://money.example.org/")).CurrencySymbol);
        Assert.Null(GridFeatures.From(null, null).CurrencySymbol);
    }

    [Fact]
    public void The_currency_name_is_the_grid_infos_and_the_symbol_stands_in_for_a_missing_one()
    {
        Assert.Equal("Example Dollars", GridFeatures.From(null, new GridLoginValues(InfoCurrency: "E$", CurrencyName: "Example Dollars")).CurrencyName);
        Assert.Equal("E$", GridFeatures.From(null, new GridLoginValues(InfoCurrency: "E$")).CurrencyName);
        Assert.Null(GridFeatures.From(null, null).CurrencyName);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("NONE")]
    public void A_currency_of_none_means_the_grid_has_no_money(string word)
    {
        var f = GridFeatures.From(null, new GridLoginValues(InfoCurrency: word, CurrencyHelper: "http://money.example.org/"));
        Assert.True(f.NoCurrency);
        Assert.False(f.HasCurrency);
        Assert.Null(f.CurrencySymbol);
        Assert.Null(f.CurrencyName);
        Assert.Null(f.BalanceText(250));
        Assert.Equal("", f.PriceSymbol(false));
    }

    [Fact]
    public void A_region_that_says_none_wins_over_a_currency_in_the_grid_info()
    {
        var f = GridFeatures.From(Extras(("currency", "none")), new GridLoginValues(InfoCurrency: "E$"));
        Assert.True(f.NoCurrency);
        Assert.False(f.HasCurrency);
    }

    [Fact]
    public void A_grid_that_sets_nothing_keeps_the_earlier_behaviour()
    {
        var f = GridFeatures.From(null, null);
        Assert.False(f.NoCurrency);
        Assert.False(f.HasCurrency);
        Assert.False(f.Visiting);
        Assert.Equal("OS$", f.PriceSymbol(false));
    }

    [Fact]
    public void Prices_carry_the_grids_symbol_and_second_life_keeps_its_own()
    {
        var f = GridFeatures.From(null, new GridLoginValues(InfoCurrency: "E$"));
        Assert.Equal("E$", f.PriceSymbol(false));
        Assert.Equal("L$", f.PriceSymbol(true));
        Assert.Equal("L$", GridFeatures.From(null, new GridLoginValues(InfoCurrency: "none")).PriceSymbol(true));
    }

    [Fact]
    public void A_region_on_the_home_grid_is_not_a_visit()
    {
        var f = GridFeatures.From(Fixture("home-region"), HomeValues());
        Assert.False(f.Visiting);
        Assert.Equal("Home Grid", f.GridName);
        Assert.Equal("H$", f.CurrencySymbol);
        Assert.Equal("H$127", f.BalanceText(127));
    }

    [Fact]
    public void After_a_hypergrid_teleport_the_name_and_the_prices_follow_the_region_and_the_balance_keeps_the_home_symbol()
    {
        var f = GridFeatures.From(Fixture("visited-region"), HomeValues());
        Assert.True(f.Visiting);
        Assert.Equal("Visited Grid", f.GridName);
        Assert.Equal("visited.example.net:9000", f.VisitedHost);
        Assert.Equal("V$", f.CurrencySymbol);
        Assert.Equal("V$", f.PriceSymbol(false));
        Assert.Equal("H$127", f.BalanceText(127));   // the balance is the home grid's money
    }

    [Fact]
    public void A_visited_region_with_no_currency_does_not_borrow_the_home_grids_symbol_for_its_prices()
    {
        var f = GridFeatures.From(Fixture("visited-region-no-currency"), HomeValues());
        Assert.True(f.Visiting);
        Assert.Equal("Visited Grid", f.GridName);
        Assert.Null(f.CurrencySymbol);
        Assert.Equal("OS$", f.PriceSymbol(false));
        Assert.Equal("H$5", f.BalanceText(5));
    }

    [Fact]
    public void A_home_grid_with_no_money_shows_no_balance_while_visiting_a_grid_with_money()
    {
        var f = GridFeatures.From(Fixture("visited-region"), HomeValues("none"));
        Assert.True(f.Visiting);
        Assert.Equal("V$", f.PriceSymbol(false));
        Assert.Null(f.BalanceText(5));
    }

    [Fact]
    public void Without_the_regions_grid_address_a_different_grid_name_means_a_visit()
    {
        var elsewhere = Extras(("GridName", "Other Grid"));
        Assert.True(GridFeatures.From(elsewhere, HomeValues()).Visiting);
        Assert.False(GridFeatures.From(Extras(("GridName", "Home Grid")), HomeValues()).Visiting);
        Assert.False(GridFeatures.From(Extras(("say-range", 20L)), HomeValues()).Visiting);   // a region that names no grid is taken as home
    }

    [Fact]
    public void The_home_grids_host_is_compared_without_its_port_or_case()
    {
        var f = GridFeatures.From(Extras(("GridURL", "http://HOME.example.org:9999/"), ("GridName", "Home Grid")), HomeValues());
        Assert.False(f.Visiting);
    }

    [Fact]
    public void Home_values_are_not_applied_to_a_visited_regions_helper_address()
    {
        var f = GridFeatures.From(Fixture("visited-region-no-currency"), HomeValues());
        Assert.Null(f.CurrencyBaseUri);
    }

    [Fact]
    public async Task On_second_life_nothing_the_grid_supplies_applies_to_the_name_or_the_money()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "https://login.agni.lindenlab.com/cgi-bin/login.cgi" }, t,
            new Viewer.Core.Diagnostics.CoreLog(new ListSink(), "test"));
        await Task.CompletedTask;
        s.GridInfoValues = new GridLoginValues(GridName: "Fake Grid", InfoCurrency: "F$", CurrencyName: "Fakes", CurrencyHelper: "http://money.example.org/");
        var f = s.GridFeatures;
        Assert.Null(f.GridName);
        Assert.Null(f.CurrencySymbol);
        Assert.Null(f.CurrencyName);
        Assert.Null(f.BalanceText(10));
        Assert.Equal("L$", f.PriceSymbol(s.Grid == GridKind.SecondLife));
    }
}
