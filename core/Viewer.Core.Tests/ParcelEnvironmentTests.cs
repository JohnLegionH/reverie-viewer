// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A parcel can carry an environment of its own, asked from the same capability as the region's with the parcel's local id, and it wins over the region's
/// while the agent stands in the parcel. The answer counts only if it is for the parcel the agent is in now; a failed answer changes nothing.
/// </summary>
public class ParcelEnvironmentTests
{
    private static readonly SimRef Sim = new(1099511628032000UL, "Alpha");
    private static string Synthetic => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "fixtures", "environment", "synthetic-environment.llsd.xml"));

    private static (ViewerSession, FakeTransport, ListSink) InWorld()
    {
        var t = new FakeTransport();
        var sink = new ListSink();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, t, new CoreLog(sink, "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("A", "B", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        t.DriveToInWorld(Sim);
        return (s, t, sink);
    }

    [Fact]
    public void A_parcels_environment_is_asked_from_the_capability_with_the_parcels_local_id()
    {
        var cap = new Uri("http://env.example.test/cap/env");
        Assert.Equal("http://env.example.test/cap/env?parcelid=7", EnvironmentFetcher<object>.ParcelUri(cap, 7).ToString());
        Assert.Equal("http://env.example.test/cap/env?parcelid=12", EnvironmentFetcher<object>.ParcelUri(cap, 12).ToString());
    }

    [Fact]
    public void The_answer_for_the_parcel_the_agent_is_in_is_kept()
    {
        var (s, t, _) = InWorld();
        t.Raise(new AgentParcelChanged(Sim, "Shore", 7));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, Synthetic, null));
        Assert.Equal(Synthetic, s.Region!.ParcelEnvironmentLlsd);
        Assert.Equal(7, s.Region.ParcelEnvironmentId);
        s.LogoutAsync(CancellationToken.None).Wait();
    }

    [Fact]
    public void An_answer_for_a_parcel_the_agent_has_left_is_ignored()
    {
        var (s, t, _) = InWorld();
        t.Raise(new AgentParcelChanged(Sim, "Shore", 7));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, Synthetic, null));
        t.Raise(new AgentParcelChanged(Sim, "Hill", 9));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, "<llsd><map /></llsd>", null));   // the late answer for the first parcel
        Assert.Equal(Synthetic, s.Region!.ParcelEnvironmentLlsd);   // the first answer is still the one held: the late one did not replace it
        Assert.Equal(7, s.Region.ParcelEnvironmentId);
        s.LogoutAsync(CancellationToken.None).Wait();
    }

    [Fact]
    public void A_newer_answer_for_the_same_parcel_replaces_the_older_one()
    {
        var (s, t, _) = InWorld();
        t.Raise(new AgentParcelChanged(Sim, "Shore", 7));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, Synthetic, null));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, "<llsd><map /></llsd>", null));   // the parcel's environment was removed: no day cycle
        Assert.Equal("<llsd><map /></llsd>", s.Region!.ParcelEnvironmentLlsd);
        s.LogoutAsync(CancellationToken.None).Wait();
    }

    [Fact]
    public void A_failed_answer_changes_nothing()
    {
        var (s, t, sink) = InWorld();
        t.Raise(new AgentParcelChanged(Sim, "Shore", 7));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, Synthetic, null));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, null, "ExtEnvironment timed out"));
        Assert.Equal(Synthetic, s.Region!.ParcelEnvironmentLlsd);
        var e = Assert.Single(sink.Entries, x => x.Event == "environment.parcel-missing");
        Assert.Contains(e.Fields, f => f.Key == "why" && f.Value == "ExtEnvironment timed out");
        s.LogoutAsync(CancellationToken.None).Wait();
    }

    [Fact]
    public void The_answer_is_logged_with_the_parcel_and_what_it_holds()
    {
        var (s, t, sink) = InWorld();
        t.Raise(new AgentParcelChanged(Sim, "Shore", 7));
        t.Raise(new ParcelEnvironmentReceived(Sim, 7, Synthetic, null));
        var e = Assert.Single(sink.Entries, x => x.Event == "environment.parcel");
        Assert.Contains(e.Fields, f => f.Key == "parcel" && f.Value == "7");
        Assert.Contains(e.Fields, f => f.Key == "own-day-cycle" && f.Value == "True");
        Assert.Contains(e.Fields, f => f.Key == "sky-frames" && int.Parse(f.Value) > 0);
        s.LogoutAsync(CancellationToken.None).Wait();
    }
}
