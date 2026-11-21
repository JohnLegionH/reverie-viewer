// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Links;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// One reader for every travel address a user meets: Hypergrid addresses (hop://, host:port:Region, host:port Region, a grid's web
/// address with a region), the official viewer's secondlife:// location and a plain region name on the grid the user is on.
/// </summary>
public class TravelAddressTests
{
    private static TravelAddress Read(string text)
    {
        var a = TravelAddressParser.Parse(text, out var error);
        Assert.True(a is not null, $"'{text}' was refused: {error}");
        return a!;
    }

    private static TravelAddressError Refused(string? text)
    {
        var a = TravelAddressParser.Parse(text, out var error);
        Assert.Null(a);
        Assert.NotEqual(TravelAddressError.None, error);
        return error;
    }

    // ---------------- hop:// ----------------

    [Fact]
    public void A_full_hop_link_gives_host_port_region_and_place()
    {
        var a = Read("hop://grid.example.org:8002/Visit%20Landing/40/50/22");
        Assert.Equal("grid.example.org", a.Host);
        Assert.Equal(8002, a.Port);
        Assert.Equal("Visit Landing", a.Region);
        Assert.Equal(new Vector3(40, 50, 22), a.Position);
        Assert.Equal(3, a.Coordinates);
        Assert.True(a.IsHypergrid);
        Assert.Equal("grid.example.org:8002", a.Grid);
        Assert.Equal("grid.example.org:8002:Visit Landing", a.LookupName);
        Assert.Equal("Visit Landing (grid.example.org:8002)", a.Display);
    }

    [Fact]
    public void A_hop_link_without_coordinates_goes_to_the_middle_and_without_a_port_to_port_80()
    {
        var a = Read("hop://Grid.Example.org/Region%2BName");
        Assert.Equal("grid.example.org", a.Host);
        Assert.Null(a.Port);
        Assert.Equal(80, a.EffectivePort);
        Assert.Equal("Region+Name", a.Region);
        Assert.Equal(new Vector3(128, 128, 0), a.Position);
        Assert.Equal(0, a.Coordinates);
        Assert.Equal("grid.example.org:80:Region+Name", a.LookupName);
    }

    [Fact]
    public void A_hop_link_with_some_coordinates_keeps_those_and_defaults_the_rest()
    {
        var a = Read("hop://grid.example.org:8002/Region/10");
        Assert.Equal(new Vector3(10, 128, 0), a.Position);
        Assert.Equal(1, a.Coordinates);
    }

    [Fact]
    public void A_hop_link_with_coordinates_out_of_range_is_clamped()
    {
        var a = Read("hop://grid.example.org:8002/Region/-5/99999/99999");
        Assert.Equal(new Vector3(0, ViewerLinkParser.MaxRegionWidth, ViewerLinkParser.MaxHeight), a.Position);
    }

    [Theory]
    [InlineData("hop://grid.example.org:8002/")]
    [InlineData("hop://grid.example.org:8002")]
    [InlineData("hop://")]
    public void A_hop_link_with_no_region_is_refused(string text) => Assert.Equal(TravelAddressError.NoRegion, Refused(text));

    [Fact]
    public void A_hop_link_with_a_bad_port_is_refused() => Assert.Equal(TravelAddressError.BadPort, Refused("hop://grid.example.org:99999/Region"));

    // ---------------- host:port:Region, host:port Region ----------------

    [Fact]
    public void Host_port_colon_region_reads_as_a_hypergrid_address()
    {
        var a = Read("grid.example.org:8002:Visit Landing");
        Assert.Equal(("grid.example.org", 8002, "Visit Landing"), (a.Host, a.Port, a.Region));
        Assert.Equal(new Vector3(128, 128, 0), a.Position);
    }

    [Fact]
    public void Host_port_space_region_reads_as_a_hypergrid_address()
    {
        var a = Read("192.0.2.10:9000 Visit Landing");
        Assert.Equal(("192.0.2.10", 9000, "Visit Landing"), (a.Host, a.Port, a.Region));
    }

    [Fact]
    public void Host_port_region_with_coordinates_reads_them()
    {
        var a = Read("grid.example.org:8002:Visit Landing/12/34/56");
        Assert.Equal("Visit Landing", a.Region);
        Assert.Equal(new Vector3(12, 34, 56), a.Position);
        Assert.Equal(3, a.Coordinates);
    }

    [Fact]
    public void Host_colon_region_without_a_port_means_port_80()
    {
        var a = Read("grid.example.org:Welcome");
        Assert.Equal(("grid.example.org", (int?)null, "Welcome"), (a.Host, a.Port, a.Region));
        Assert.Equal("grid.example.org:80:Welcome", a.LookupName);
    }

    [Fact]
    public void Host_port_with_no_region_is_refused() => Assert.Equal(TravelAddressError.NoRegion, Refused("grid.example.org:8002"));

    [Fact]
    public void Host_with_a_port_above_65535_is_refused() => Assert.Equal(TravelAddressError.BadPort, Refused("grid.example.org:70000:Region"));

    [Fact]
    public void An_encoded_region_name_in_host_form_is_unescaped() => Assert.Equal("Visit Landing", Read("grid.example.org:8002:Visit%20Landing").Region);

    // ---------------- http(s):// with a region ----------------

    [Fact]
    public void A_web_address_with_the_region_after_a_space()
    {
        var a = Read("http://grid.example.org:8002/ Visit Landing");
        Assert.Equal(("grid.example.org", 8002, "Visit Landing"), (a.Host, a.Port, a.Region));
    }

    [Fact]
    public void A_web_address_with_the_region_and_coordinates_in_its_path()
    {
        var a = Read("https://grid.example.org/Visit%20Landing/1/2/3");
        Assert.Equal(("grid.example.org", 443, "Visit Landing"), (a.Host, a.Port, a.Region));
        Assert.Equal(new Vector3(1, 2, 3), a.Position);
    }

    [Fact]
    public void A_web_address_without_a_region_is_refused() => Assert.Equal(TravelAddressError.NoRegion, Refused("http://grid.example.org:8002/"));

    [Fact]
    public void A_maps_secondlife_address_is_a_place_on_the_current_grid()
    {
        var a = Read("http://maps.secondlife.com/secondlife/Ahern/10/20/30");
        Assert.Null(a.Host);
        Assert.Equal("Ahern", a.Region);
        Assert.Equal(new Vector3(10, 20, 30), a.Position);
    }

    // ---------------- secondlife:// and plain names ----------------

    [Fact]
    public void A_secondlife_location_is_a_region_on_the_current_grid()
    {
        var a = Read("secondlife://Visit%20Landing/1/2/3");
        Assert.Null(a.Host);
        Assert.False(a.IsHypergrid);
        Assert.Equal("Visit Landing", a.Region);
        Assert.Equal(new Vector3(1, 2, 3), a.Position);
        Assert.Equal("Visit Landing", a.LookupName);
    }

    [Fact]
    public void A_secondlife_teleport_app_link_is_a_place()
    {
        var a = Read("secondlife:///app/teleport/Alder/5/6/7");
        Assert.Equal("Alder", a.Region);
        Assert.Equal(new Vector3(5, 6, 7), a.Position);
    }

    [Fact]
    public void An_app_link_that_is_not_a_place_is_refused() => Assert.Equal(TravelAddressError.NotAPlace, Refused("secondlife:///app/agent/00000000-0000-0000-0000-000000000001/about"));

    [Fact]
    public void A_plain_region_name_is_on_the_current_grid()
    {
        var a = Read("  Alder  ");
        Assert.Null(a.Host);
        Assert.Equal("Alder", a.Region);
        Assert.Equal(new Vector3(128, 128, 0), a.Position);
        Assert.Equal("Alder", a.Display);
    }

    [Fact]
    public void A_plain_region_name_with_coordinates_reads_them()
    {
        var a = Read("Alder/10/20/30");
        Assert.Equal("Alder", a.Region);
        Assert.Equal(new Vector3(10, 20, 30), a.Position);
    }

    [Fact]
    public void A_plain_name_with_a_dot_and_a_space_stays_a_region_name()
    {
        var a = Read("Mr.Smith Island");
        Assert.Null(a.Host);
        Assert.Equal("Mr.Smith Island", a.Region);
    }

    // ---------------- bad input ----------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_refused_as_empty(string? text) => Assert.Equal(TravelAddressError.Empty, Refused(text));

    [Fact]
    public void An_address_too_long_is_refused() => Assert.Equal(TravelAddressError.TooLong, Refused(new string('a', TravelAddress.MaxLength + 1)));

    [Fact]
    public void Control_characters_are_refused() => Refused("Alder\u0007");

    [Fact]
    public void A_region_name_with_a_semicolon_is_refused() => Assert.Equal(TravelAddressError.BadRegion, Refused("grid.example.org:8002:Bad;Name"));

    [Fact]
    public void Another_scheme_is_not_a_place() => Assert.Equal(TravelAddressError.NotAPlace, Refused("ftp://grid.example.org/Region"));

    // ---------------- the grid the user is on, history ----------------

    [Fact]
    public void An_address_naming_the_current_grid_becomes_a_plain_region()
    {
        var a = Read("hop://grid.example.org:8002/Alder/1/2/3").OnGrid("grid.example.org:8002");
        Assert.Null(a.Host);
        Assert.Equal("Alder", a.LookupName);
        Assert.Same(a, a.OnGrid("other.example.org:80"));
    }

    [Fact]
    public void History_keeps_the_full_address_as_a_hop_link_that_reads_back_the_same()
    {
        var a = Read("grid.example.org:8002:Visit Landing/12/34/56");
        string hop = a.ToHop();
        Assert.Equal("hop://grid.example.org:8002/Visit%20Landing/12/34/56", hop);
        Assert.Equal(a, Read(hop));
        Assert.Equal("hop://home.example.org:9000/Alder/128/128/0", Read("Alder").ToHop("home.example.org:9000"));
        Assert.Equal("secondlife://Alder/128/128/0", Read("Alder").ToHop());
    }

    [Theory]
    [InlineData("http://Grid.Example.org:8002/", "grid.example.org:8002")]
    [InlineData("https://grid.example.org/", "grid.example.org:443")]
    [InlineData("grid.example.org:8002", "grid.example.org:8002")]
    [InlineData("", null)]
    [InlineData("not a uri at all", null)]
    public void Grid_keys_from_web_addresses(string uri, string? key) => Assert.Equal(key, GridKeys.OfUri(uri));
}
