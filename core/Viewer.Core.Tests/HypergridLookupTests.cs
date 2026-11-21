// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.Map;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A travel address is found through the region's map lookup, as the official viewer asks it (flags 2), so OpenSim answers a Hypergrid
/// name with a block carrying the name as asked; a Hypergrid link is looked up afresh each time; and when the lookup fails the grid's own
/// words (its alert during the lookup) are what the user is shown.
/// </summary>
public class HypergridLookupTests
{
    [Fact]
    public void A_named_lookup_is_sent_with_the_official_viewers_flags_and_the_name_as_given()
    {
        var agent = UUID.Random();
        var p = LmvTransport.ToMapNameRequest(agent, UUID.Random(), "grid.example.org:8002:Visit Landing");
        Assert.Equal(2u, p.AgentData.Flags);
        Assert.Equal(agent, p.AgentData.AgentID);
        Assert.Equal("grid.example.org:8002:Visit Landing", Utils.BytesToString(p.NameData.Name));
    }

    [Theory]
    [InlineData("grid.example.org:8002:Visit Landing", true)]
    [InlineData("http://grid.example.org:8002/ Visit Landing", true)]
    [InlineData("Alder", false)]
    [InlineData("Mr.Smith Island", false)]
    public void Hypergrid_lookup_names_are_told_apart(string name, bool hypergrid) => Assert.Equal(hypergrid, LmvTransport.IsHypergridLookup(name));

    [Fact]
    public async Task A_name_answered_before_is_asked_again_when_fresh_is_asked_for()
    {
        var waits = new RegionNameWaits();
        waits.Offer([new MapBlockSize(30000, 0, 256, 256, "grid.example.org:8002:Visit Landing")]);
        Assert.Equal(MapMath.HandleOf(30000, 0), await waits.WaitAsync("grid.example.org:8002:Visit Landing", TimeSpan.FromSeconds(5), CancellationToken.None));
        var again = waits.WaitAsync("grid.example.org:8002:Visit Landing", TimeSpan.FromSeconds(5), CancellationToken.None, fresh: true);
        Assert.False(again.IsCompleted);   // waits for the new answer: the grid links the region again, with a new handle
        waits.Offer([new MapBlockSize(31000, 0, 256, 256, "grid.example.org:8002:Visit Landing")]);
        Assert.Equal(MapMath.HandleOf(31000, 0), await again);
    }

    [Fact]
    public void The_grids_words_heard_during_a_lookup_are_its_refusal()
    {
        var r = new LookupRefusals();
        r.Heard("before the lookup");   // not during one: ignored
        long ticket = r.Begin();
        r.Heard("No regions found with that name.");
        Assert.Equal("No regions found with that name.", r.End(ticket));
    }

    [Fact]
    public void A_lookup_during_which_the_grid_said_nothing_has_no_refusal_text()
    {
        var r = new LookupRefusals();
        long first = r.Begin();
        r.Heard("Use a search string with at least 3 characters");
        r.End(first);
        long second = r.Begin();
        r.Heard("   ");
        Assert.Null(r.End(second));
    }
}
