// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A search of the world map by region name: the viewer sends the map-name request itself (the official viewer's name request,
/// indra/newview/llworldmapmessage.cpp, sendNamedRegionRequest) and handles the reply as it arrives, with no fixed wait. The model times
/// the answer from its clock, gives up after a time limit instead of waiting for ever, and says at once when the grid answers that
/// no region has the name. A fake request link and a fake clock; no grid. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class MapSearchTimingTests
{
    private sealed class Rig
    {
        public DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        public readonly List<string> Asked = new();
        public readonly WorldMapModel Map;

        public Rig()
        {
            var links = new MapLinks { RequestName = Asked.Add };
            Map = new WorldMapModel(links, WorldMapModel.DefaultScale, () => Now);
        }

        public void Reply(string name, int x, int y, int access = 13) => Map.Receive(new MapBlockSize(x, y, 256, 256, name, null, access));
    }

    [Fact]
    public void The_time_from_the_search_to_its_first_matching_answer_is_measured_and_the_search_is_no_longer_pending()
    {
        var r = new Rig();
        Assert.True(r.Map.Search("Exam"));
        Assert.Equal(["Exam"], r.Asked);
        Assert.True(r.Map.SearchPending);
        Assert.Null(r.Map.LastSearchTime);
        r.Now += TimeSpan.FromMilliseconds(120);
        r.Reply("Example North", 1000, 1001);
        Assert.False(r.Map.SearchPending);
        Assert.Equal(TimeSpan.FromMilliseconds(120), r.Map.LastSearchTime);
        r.Now += TimeSpan.FromSeconds(5);
        r.Reply("Example South", 1000, 1000);                    // a second answer does not change the first answer's time
        Assert.Equal(TimeSpan.FromMilliseconds(120), r.Map.LastSearchTime);
    }

    [Fact]
    public void An_answer_for_some_other_name_does_not_end_the_wait()
    {
        var r = new Rig();
        r.Map.Search("Exam");
        r.Now += TimeSpan.FromMilliseconds(50);
        r.Reply("Zebra Region", 1002, 1000);                     // an area reply for something else
        Assert.True(r.Map.SearchPending);
        Assert.Null(r.Map.LastSearchTime);
        r.Now += TimeSpan.FromMilliseconds(70);
        r.Reply("Example North", 1000, 1001);
        Assert.Equal(TimeSpan.FromMilliseconds(120), r.Map.LastSearchTime);
    }

    [Fact]
    public void No_answer_within_the_limit_ends_the_search_with_a_message_naming_what_was_typed()
    {
        var r = new Rig();
        r.Map.Search("Nowhere");
        r.Now += WorldMapModel.SearchTimeout - TimeSpan.FromSeconds(1);
        r.Map.Update(800, 600);
        Assert.True(r.Map.SearchPending);
        Assert.Null(r.Map.Status);
        r.Now += TimeSpan.FromSeconds(2);
        r.Map.Update(800, 600);
        Assert.False(r.Map.SearchPending);
        Assert.Equal(("map.search.no_answer", "Nowhere"), r.Map.Status);
        Assert.Null(r.Map.LastSearchTime);
    }

    [Fact]
    public void The_limit_is_ten_seconds_not_a_fixed_wait_before_every_answer()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), WorldMapModel.SearchTimeout);
        var r = new Rig();
        r.Map.Search("Exam");
        r.Now += TimeSpan.FromMilliseconds(20);
        r.Reply("Example North", 1000, 1001);                    // an answer after 20 ms is used at once; nothing waited
        Assert.Single(r.Map.Results);
    }

    [Fact]
    public void A_reply_that_says_there_is_no_such_region_ends_the_wait_at_once()
    {
        var r = new Rig();
        r.Map.Search("Nowhere");
        r.Now += TimeSpan.FromMilliseconds(30);
        r.Reply("Nowhere", 0, 0, access: 255);                   // the grid echoes the name with the no-region access level
        Assert.False(r.Map.SearchPending);
        Assert.Equal(("map.search.none", "Nowhere"), r.Map.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(30), r.Map.LastSearchTime);
        Assert.Empty(r.Map.Results);
    }

    [Fact]
    public void A_new_search_starts_the_clock_again_and_forgets_the_old_message()
    {
        var r = new Rig();
        r.Map.Search("Nowhere");
        r.Now += WorldMapModel.SearchTimeout + TimeSpan.FromSeconds(1);
        r.Map.Update(800, 600);
        Assert.Equal("map.search.no_answer", r.Map.Status?.Key);
        r.Map.Search("Exam");
        Assert.Null(r.Map.Status);
        Assert.True(r.Map.SearchPending);
        r.Now += TimeSpan.FromSeconds(3);
        r.Map.Update(800, 600);
        Assert.True(r.Map.SearchPending);                        // 3 s into the new search, not 14 s into the old one
        Assert.Null(r.Map.Status);
    }

    [Fact]
    public void A_destination_a_script_named_is_dropped_when_the_name_gets_no_answer()
    {
        var r = new Rig();
        Assert.True(r.Map.ShowDestination("Nowhere", new Vector3(10, 20, 30)));
        r.Now += WorldMapModel.SearchTimeout + TimeSpan.FromSeconds(1);
        r.Map.Update(800, 600);
        Assert.Equal("map.search.no_answer", r.Map.Status?.Key);
        r.Reply("Nowhere", 1000, 1000);                          // an answer that comes much later does not move the map by itself
        Assert.Null(r.Map.Selected);
    }

    [Fact]
    public void A_destination_that_is_answered_in_time_still_centres_the_map_and_stops_the_wait()
    {
        var r = new Rig();
        r.Map.ShowDestination("Nowhere", new Vector3(10, 20, 30));
        r.Now += TimeSpan.FromMilliseconds(40);
        r.Reply("Nowhere", 1000, 1000);
        Assert.NotNull(r.Map.Selected);
        Assert.False(r.Map.SearchPending);
        Assert.Equal(TimeSpan.FromMilliseconds(40), r.Map.LastSearchTime);
    }

    [Fact]
    public void The_two_messages_have_words()
    {
        var strings = StringTable.English();
        Assert.True(strings.Has("map.search.no_answer"));
        Assert.True(strings.Has("map.search.none"));
    }
}
