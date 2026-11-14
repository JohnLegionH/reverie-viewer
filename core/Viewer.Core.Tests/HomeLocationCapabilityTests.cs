// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using LibreMetaverse.StructuredData;
using Viewer.Core.Protocol;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The set-home post to the region's capability: when it is used, what the body holds, and how the reply is read.
/// Pure functions, no grid; every member is independent, so the class runs in parallel.
/// </summary>
public class HomeLocationCapabilityTests
{
    [Fact]
    public void A_region_that_offers_the_capability_gets_the_post() =>
        Assert.Equal(HomeRoute.Capability, HomeLocationCapability.Choose(new Uri("https://example.org/cap/abc")));

    [Fact]
    public void A_region_without_the_capability_gets_the_older_message() =>
        Assert.Equal(HomeRoute.Legacy, HomeLocationCapability.Choose(null));

    [Fact]
    public void The_body_is_a_HomeLocation_map_with_the_place_and_the_look_direction()
    {
        string xml = HomeLocationCapability.Body(new Vector3(12.5f, 200.25f, 31f), new Vector3(0f, 1f, 0f));
        var root = Assert.IsType<OSDMap>(OSDParser.DeserializeLLSDXml(xml));
        var home = Assert.IsType<OSDMap>(root["HomeLocation"]);
        Assert.Equal(1, home["LocationId"].AsInteger());
        var pos = Assert.IsType<OSDMap>(home["LocationPos"]);
        Assert.Equal(12.5, pos["X"].AsReal());
        Assert.Equal(200.25, pos["Y"].AsReal());
        Assert.Equal(31.0, pos["Z"].AsReal());
        var look = Assert.IsType<OSDMap>(home["LocationLookAt"]);
        Assert.Equal(0.0, look["X"].AsReal());
        Assert.Equal(1.0, look["Y"].AsReal());
        Assert.Equal(0.0, look["Z"].AsReal());
    }

    [Fact]
    public void A_success_reply_gives_the_stored_place()
    {
        const string xml = "<llsd><map><key>success</key><string>true</string><key>HomeLocation</key><map><key>LocationPos</key>" +
            "<map><key>X</key><real>10</real><key>Y</key><real>20</real><key>Z</key><real>30.5</real></map></map></map></llsd>";
        var r = HomeLocationCapability.ReadReply(xml);
        Assert.True(r.Success);
        Assert.Equal(new Vector3(10f, 20f, 30.5f), r.Place);
    }

    [Fact]
    public void A_failure_reply_is_not_a_success()
    {
        // even with a whole stored place in it, "false" is not a success
        var r = HomeLocationCapability.ReadReply("<llsd><map><key>success</key><string>false</string><key>HomeLocation</key><map><key>LocationPos</key>" +
            "<map><key>X</key><real>1</real><key>Y</key><real>2</real><key>Z</key><real>3</real></map></map></map></llsd>");
        Assert.False(r.Success);
        Assert.Null(r.Place);
    }

    [Theory]
    [InlineData("<llsd><map><key>success</key><string>true</string></map></llsd>")] // no stored place: the official viewer calls it an error
    [InlineData("<llsd><map><key>success</key><string>true</string><key>HomeLocation</key><map><key>LocationPos</key><map><key>X</key><real>1</real></map></map></map></llsd>")]
    [InlineData("<llsd><map><key>success</key><string>true</string><key>HomeLocation</key><map><key>LocationPos</key><map><key>X</key><real>1</real><key>Y</key><real>2</real></map></map></map></llsd>")] // no height
    [InlineData("not llsd at all")]
    [InlineData("")]
    public void A_reply_without_a_whole_stored_place_or_not_llsd_is_not_a_success(string xml)
    {
        var r = HomeLocationCapability.ReadReply(xml);
        Assert.False(r.Success);
    }
}
