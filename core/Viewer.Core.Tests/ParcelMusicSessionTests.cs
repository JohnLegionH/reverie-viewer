// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The music address of the agent's parcel: it comes with the parcel the agent stands in and changes when the region sends a new address for
/// that parcel, whatever sequence number the message carries (About Land's update after a save carries the number of its own request, not the one
/// that announces the agent's parcel). A message about another parcel or region says nothing about what plays here.
/// </summary>
public class ParcelMusicSessionTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly SimRef There = new(300000UL * 256 << 32 | 300000UL * 256, "Far Region");

    private static (ViewerSession Session, FakeTransport Fake) InWorld()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake);
    }

    [Fact]
    public void The_music_address_comes_with_the_parcel_the_agent_stands_in()
    {
        var (session, fake) = InWorld();
        Assert.Equal("", session.ParcelMusicUrl);
        fake.Raise(new AgentParcelChanged(Here, "Example Parcel", 7, MusicUrl: "http://radio.example.org:8040"));
        Assert.Equal("http://radio.example.org:8040", session.ParcelMusicUrl);
        fake.Raise(new AgentParcelChanged(Here, "Next Parcel", 8));   // a parcel with no music
        Assert.Equal("", session.ParcelMusicUrl);
    }

    [Fact]
    public void A_new_address_for_the_agents_parcel_is_taken_at_once_whatever_the_sequence_number()
    {
        var (session, fake) = InWorld();
        fake.Raise(new AgentParcelChanged(Here, "Example Parcel", 7, MusicUrl: "http://radio.example.org/one"));
        fake.Raise(new ParcelMusicReceived(Here, 7, "http://radio.example.org/two"));
        Assert.Equal("http://radio.example.org/two", session.ParcelMusicUrl);
        fake.Raise(new ParcelMusicReceived(Here, 7, ""));   // the owner cleared it
        Assert.Equal("", session.ParcelMusicUrl);
    }

    [Fact]
    public void An_address_for_another_parcel_or_another_region_changes_nothing()
    {
        var (session, fake) = InWorld();
        fake.Raise(new AgentParcelChanged(Here, "Example Parcel", 7, MusicUrl: "http://radio.example.org/one"));
        fake.Raise(new ParcelMusicReceived(Here, 8, "http://radio.example.org/other-parcel"));
        fake.Raise(new ParcelMusicReceived(There, 7, "http://radio.example.org/other-region"));
        Assert.Equal("http://radio.example.org/one", session.ParcelMusicUrl);
    }

    [Fact]
    public void Arriving_in_another_region_clears_the_address_until_the_region_names_the_parcel()
    {
        var (session, fake) = InWorld();
        fake.Raise(new AgentParcelChanged(Here, "Example Parcel", 7, MusicUrl: "http://radio.example.org/one"));
        fake.Raise(new SimConnected(There, 256, 256));
        fake.CurrentSim = There;
        Assert.Equal("", session.ParcelMusicUrl);
        fake.Raise(new ParcelMusicReceived(There, 9, "http://radio.example.org/late"));   // a message before the region named the parcel is not taken for it
        Assert.Equal("", session.ParcelMusicUrl);
        fake.Raise(new AgentParcelChanged(There, "Far Parcel", 7, MusicUrl: "http://radio.example.org/far"));
        Assert.Equal("http://radio.example.org/far", session.ParcelMusicUrl);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10000)]   // About Land's own request
    [InlineData(4242)]
    [InlineData(25)]
    public void Every_sequence_number_but_the_collision_ones_gives_the_parcels_address(int sequence)
    {
        var music = LmvTransport.ToParcelMusic(sequence, 7, "http://radio.example.org/one", Here);
        Assert.Equal(new ParcelMusicReceived(Here, 7, "http://radio.example.org/one"), music);
    }

    [Fact]
    public void A_message_that_keeps_the_agent_out_or_has_no_parcel_gives_no_address()
    {
        int collisions = 0;
        for (int sequence = -20000; sequence <= 20000; sequence++)   // the numbers the region uses for a parcel that keeps the agent out
            if (LmvTransport.ToParcelCollision(sequence) is not null)
            {
                collisions++;
                Assert.Null(LmvTransport.ToParcelMusic(sequence, 7, "http://radio.example.org/one", Here));
            }
        Assert.True(collisions >= 1, "no sequence number was found that the transport treats as a collision");
        Assert.Null(LmvTransport.ToParcelMusic(0, 0, "http://radio.example.org/one", Here));
        Assert.Null(LmvTransport.ToParcelMusic(0, 7, "http://radio.example.org/one", null));
        Assert.Equal("", LmvTransport.ToParcelMusic(0, 7, null, Here)!.Url);
    }

    [Fact]
    public void An_agent_parcel_update_carries_the_address_and_it_is_never_written_to_a_capture()
    {
        Assert.Equal("http://radio.example.org/one", LmvTransport.ToAgentParcel(3, "P", 7, Here, musicUrl: "http://radio.example.org/one")!.MusicUrl);
        Assert.False(CaptureFile.IsRecorded(new ParcelMusicReceived(Here, 7, "http://radio.example.org/one")));
    }
}
