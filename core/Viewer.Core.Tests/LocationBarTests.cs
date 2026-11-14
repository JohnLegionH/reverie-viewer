// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Identity;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The location bar says where the agent is as the official viewer's does (its location string, indra/newview/llagentui.cpp
/// buildLocationString): the parcel's name, then the region and the agent's position, "Parcel, Region (x, y, z)"; a parcel with
/// no name leaves it out. The position is rounded to the half metre and, while the agent moves, to every second or fourth metre.
/// The parcel comes from the region's agent-parcel updates (llviewerparcelmgr.cpp processParcelProperties).
/// </summary>
public class LocationBarTests
{
    [Fact]
    public void A_named_parcel_comes_first_then_the_region_and_the_rounded_position()
    {
        Assert.Equal("Example Parcel, Example Region (128, 65, 25)", LocationText.Build("Example Region", "Example Parcel", new Vector3(128.4f, 64.5f, 25.2f), 0));
    }

    [Fact]
    public void A_parcel_without_a_name_leaves_it_out()
    {
        Assert.Equal("Example Region (128, 65, 25)", LocationText.Build("Example Region", "", new Vector3(128.4f, 64.5f, 25.2f), 0));
        Assert.Equal("Example Region (128, 65, 25)", LocationText.Build("Example Region", null, new Vector3(128.4f, 64.5f, 25.2f), 0));
        Assert.Equal("Example Region (128, 65, 25)", LocationText.Build("Example Region", "   ", new Vector3(128.4f, 64.5f, 25.2f), 0));
    }

    [Theory]
    [InlineData(0f, 131, 133)]    // standing: the half metre rounding only
    [InlineData(1.4f, 131, 133)]  // slower than a walk
    [InlineData(2f, 130, 132)]    // walking: every second metre
    [InlineData(5.9f, 130, 132)]
    [InlineData(7f, 128, 132)]    // running or flying: every fourth metre
    public void While_the_agent_moves_the_position_is_rounded_to_every_second_or_fourth_metre(float speed, int expectedX, int expectedY)
    {
        Assert.Equal($"Example Region ({expectedX}, {expectedY}, 20)", LocationText.Build("Example Region", null, new Vector3(130.6f, 132.6f, 19.7f), speed));
    }

    [Fact]
    public void Without_a_region_there_is_no_text()
    {
        Assert.Equal("", LocationText.Build(null, "Example Parcel", new Vector3(1, 2, 3), 0));
    }

    [Theory]
    [InlineData(-10000)]   // a selected parcel
    [InlineData(-20000)]   // a parcel that denies entry
    [InlineData(-30000)]
    [InlineData(-40000)]
    [InlineData(-50000)]   // the parcel under the pointer
    [InlineData(LmvTransport.ParcelInfoSequence)]   // About Land's own request
    public void Parcel_properties_for_anything_but_the_agents_own_parcel_are_not_an_agent_parcel_update(int sequence)
    {
        Assert.Null(LmvTransport.ToAgentParcel(sequence, "Example Parcel", 7, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(120)]
    public void Parcel_properties_with_an_ordinary_sequence_are_the_agents_parcel(int sequence)
    {
        var sim = new SimRef(1099511628032256UL, "Example Region");
        var update = LmvTransport.ToAgentParcel(sequence, "Example Parcel", 7, sim);
        Assert.NotNull(update);
        Assert.Equal(("Example Parcel", 7), (update!.Name, update.LocalId));
        Assert.Same(sim, update.Sim);
        Assert.Equal("", LmvTransport.ToAgentParcel(sequence, null, 7, sim)!.Name);
    }

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
    public void The_session_keeps_the_agents_parcel_name_from_the_current_region_only_and_drops_it_on_arrival_elsewhere()
    {
        var (session, fake) = InWorld();
        Assert.Null(session.AgentParcelName);
        fake.Raise(new AgentParcelChanged(Here, "Example Parcel", 7));
        Assert.Equal("Example Parcel", session.AgentParcelName);
        fake.Raise(new AgentParcelChanged(Here, "Next Parcel", 8));
        Assert.Equal("Next Parcel", session.AgentParcelName);
        fake.Raise(new AgentParcelChanged(There, "Somewhere Else", 1));   // another simulator's parcel is not ours
        Assert.Equal("Next Parcel", session.AgentParcelName);

        fake.Raise(new SimConnected(There, 256, 256));   // the agent arrives in another region
        fake.CurrentSim = There;
        Assert.Null(session.AgentParcelName);
        fake.Raise(new AgentParcelChanged(There, "Far Parcel", 2));
        Assert.Equal("Far Parcel", session.AgentParcelName);
    }

    [Fact]
    public void A_parcels_name_is_never_written_to_a_capture()
    {
        Assert.False(CaptureFile.IsRecorded(new AgentParcelChanged(Here, "Example Parcel", 7)));
    }
}
