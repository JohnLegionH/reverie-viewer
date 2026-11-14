// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Interest management (map section 6): draw distance 64-1024 m sent to the sim; in-range by linkset root or wearer.</summary>
public class InterestTests
{
    private static readonly SimRef Sim = new(256000UL << 32 | 256000UL, "Here");

    [Theory]
    [InlineData(10f, 64f)]
    [InlineData(64f, 64f)]
    [InlineData(300f, 300f)]
    [InlineData(5000f, 1024f)]
    [InlineData(float.NaN, 128f)]
    public void Draw_distance_is_clamped_to_64_1024(float asked, float expected) =>
        Assert.Equal(expected, Interest.ClampDrawDistance(asked));

    [Fact]
    public async Task The_session_default_is_128_and_the_far_distance_is_sent_only_in_the_world_stepped_by_the_ramp()
    {
        var t = new FakeTransport();
        using var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/" }, t, new CoreLog(new ListSink(), "t"));
        Assert.Equal(128f, s.DrawDistance);
        s.SetDrawDistance(20f);
        s.SetDrawDistance(2000f);
        Assert.Equal(1024f, s.DrawDistance);
        Assert.Equal([50f, 512f], t.DrawDistances);   // arriving: half of the distance asked for (at least 50 m); the transport sends nothing until it is connected
        var t2 = new FakeTransport();
        using var s2 = new ViewerSession(new SessionOptions { LoginUri = "http://grid.example/", DrawDistance = 512 }, t2, new CoreLog(new ListSink(), "t"));
        Assert.Equal(512f, s2.DrawDistance);
        await s.LoginAsync(new LoginCredentials("Test", "Resident", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None);
        t.DriveToInWorld(new SimRef(1000UL * 256 << 32 | 1000UL * 256, "Home"));
        s.AdvanceFar();
        // in the world, the far distance grows from half the draw distance toward it, a tenth a second, never past it
        Assert.True(t.DrawDistances.Count >= 2);
        Assert.All(t.DrawDistances.Skip(2), d => Assert.InRange(d, 512f, 1024f));
        Assert.Equal(t.DrawDistances.OrderBy(d => d), t.DrawDistances.OrderBy(d => d));
    }

    [Fact]
    public void In_range_is_judged_by_linkset_root_and_by_wearer()
    {
        var w = new WorldState(new CoreLog(new ListSink(), "t"));
        w.ApplyObject(Sim, new PrimData(1, Guid.NewGuid(), 0, 0, new Vector3(10, 10, 20), Quaternion.Identity, Vector3.One));        // near
        w.ApplyObject(Sim, new PrimData(2, Guid.NewGuid(), 1, 0, new Vector3(500, 0, 0), Quaternion.Identity, Vector3.One));         // child of near root: local offset
        w.ApplyObject(Sim, new PrimData(3, Guid.NewGuid(), 0, 0, new Vector3(250, 250, 20), Quaternion.Identity, Vector3.One));      // far
        w.ApplyAvatar(Sim, new AvatarData(7, Guid.NewGuid(), "Far Wearer", new Vector3(240, 240, 25), Quaternion.Identity, Vector3.One));
        w.ApplyObject(Sim, new PrimData(4, Guid.NewGuid(), 7, 2, Vector3.Zero, Quaternion.Identity, Vector3.One), eventIsAttachment: true);  // worn far away
        w.ApplyObject(Sim, new PrimData(5, Guid.NewGuid(), 99, 0, Vector3.Zero, Quaternion.Identity, Vector3.One));                  // parent not known yet

        var near = Interest.InRange(w, new Vector3(0, 0, 20), 64f).Select(o => o.Id.LocalId).Order();
        Assert.Equal([1u, 2u], near);
        var all = Interest.InRange(w, new Vector3(0, 0, 20), 1024f).Select(o => o.Id.LocalId).Order();
        Assert.Equal([1u, 2u, 3u, 4u], all);
    }
}
