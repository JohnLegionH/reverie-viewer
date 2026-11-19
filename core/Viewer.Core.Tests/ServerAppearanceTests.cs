// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Reflection;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Parity oracle: the gateway's ServerAppearanceTests (web-viewer bb91ba5), the subset the extraction map lists for the
/// passive core (the client-bake ones stay later). Adapted: the gateway's server mode "has no bake"; the core has no bake
/// in EITHER mode, so the structural guarantee is asserted over the whole receipt surface, and the mode choice, the
/// version-0 refusal and the appearance-service route over the world state driven through a session on the fake transport.
/// </summary>
public class ServerAppearanceTests
{
    internal static readonly SimRef Alpha = new(256000UL << 32 | 256000UL, "Alpha");
    private static readonly SimRef Beta = new(256256UL << 32 | 256000UL, "Beta");
    private static readonly SimRef Gamma = new(256512UL << 32 | 256000UL, "Gamma");
    internal static readonly Guid Me = Guid.Parse("11111111-2222-3333-4444-555555555555");   // FakeTransport's default login agent

    internal static (ViewerSession Session, FakeTransport Transport, ListSink Log) LoggedIn()
    {
        var t = new FakeTransport();
        var sink = new ListSink();
        var s = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid:8002/" }, t, new CoreLog(sink, "test"));
        Assert.True(s.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw", SensitiveKind.PasswordDigest)), CancellationToken.None).GetAwaiter().GetResult().Ok);
        return (s, t, sink);
    }

    internal static void Arrive(FakeTransport t, SimRef sim, bool serverBaked)
    {
        t.Raise(new LoginProgressed(LoginPhase.ConnectingToSim, null));
        t.ConnectedSimulatorCount = 1;
        t.Raise(new SimConnected(sim, 256, 256, serverBaked));
        t.CurrentSim = sim;
        t.Raise(new EventQueueRunning(sim));
        t.Raise(new MovementComplete(sim));
    }

    internal static AvatarAppearanceData Look(Guid agent, int version, params (int Face, Guid Texture)[] faces)
    {
        var arr = new Guid[45];
        foreach (var (face, tex) in faces)
            arr[face] = tex;
        return new AvatarAppearanceData(agent, arr.ToImmutableArray(), VisualParamSet.Empty, version, 3);
    }

    private static AvatarAppearance Held(Guid agent, params (string Slot, Guid Texture)[] bakes) =>
        new(agent, false, bakes.ToDictionary(b => b.Slot, b => b.Texture), [], VisualParamSet.Empty, 1, 0);

    // ---------------- the structural guarantee ----------------

    [Fact]
    public void NothingInServerAppearanceCanSendAnAppearance()
    {
        // The whole declared surface of every type on the receipt path, by name: nothing mentions what a bake or a send
        // would need. (The IL scan in SessionInvariantTests proves the same of the method bodies.)
        Type[] surface = [typeof(ISessionTransport), typeof(ViewerSession), typeof(WorldState), typeof(SelfAppearanceState),
            typeof(AppearanceSlots), typeof(AvatarAppearance), typeof(AvatarAppearanceData), typeof(VisualParamSet)];
        string[] forbidden = ["SetAppearance", "SendAppearance", "Wearable", "CachedTexture", "Upload", "Composite", "Rebake"];
        foreach (var type in surface)
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name).ToList();
            foreach (var word in forbidden)
                Assert.DoesNotContain(members, n => n.Contains(word, StringComparison.OrdinalIgnoreCase) && !IsReceiptName(n));
        }
    }

    // Names on the receipt path that contain "bake" because they READ the simulator's bakes.
    private static bool IsReceiptName(string n) =>
        n is "Bakes" or "get_Bakes" or "BakesOf" or "ServerBakes" or "get_ServerBakes" or "RecordServerBakes" or "_serverBakes"
            or "BakedTextureUrl" or "<Bakes>k__BackingField";

    // ---------------- the mode choice ----------------

    [Fact]
    public void Bit0ChoosesServerModeAndItsAbsenceChoosesClientMode()
    {
        var (s, t, _) = LoggedIn();
        Arrive(t, Alpha, serverBaked: true);
        Assert.Equal(AppearanceMode.Server, s.World.AppearanceMode);
        t.Raise(new SimConnected(Gamma, 256, 256, ServerBakedAppearance: false));
        Assert.Equal(AppearanceMode.Client, s.World.AppearanceMode);
    }

    [Fact]
    public void ModeIsReChosenInBothDirectionsAcrossRegionChanges()
    {
        var (s, t, log) = LoggedIn();
        Arrive(t, Alpha, serverBaked: true);
        var onServer = s.World.AppearanceMode;
        t.Raise(new SimConnected(Gamma, 256, 256, false));
        var onClient = s.World.AppearanceMode;
        t.Raise(new SimConnected(Alpha, 256, 256, true));

        Assert.Equal(AppearanceMode.Server, onServer);
        Assert.Equal(AppearanceMode.Client, onClient);
        Assert.Equal(AppearanceMode.Server, s.World.AppearanceMode);

        // Every choice is on the record, with the region name and bit 0.
        var chosen = log.Entries.Where(e => e.Event == "appearance.mode").Select(e => e.Format()).ToList();
        Assert.Equal(3, chosen.Count);
        Assert.Contains("Alpha", chosen[0]);
        Assert.Contains("bit0=set", chosen[0]);
        Assert.Contains("Gamma", chosen[1]);
        Assert.Contains("bit0=clear", chosen[1]);
    }

    [Fact]
    public void TheSameKindOfRegionTwiceKeepsTheModeAndSaysSo()
    {
        var (s, t, log) = LoggedIn();
        Arrive(t, Alpha, serverBaked: true);
        t.Raise(new SimConnected(Beta, 256, 256, true));
        Assert.Equal(AppearanceMode.Server, s.World.AppearanceMode);
        Assert.Contains(log.Entries, e => e.Event == "appearance.mode" && e.Format().Contains("staying=True"));
    }

    // ---------------- zero bakes, zero sends ----------------

    [Fact]
    public void AServerModeSessionStartsNoBakeAcrossLoginAndARegionChange()
    {
        var (s, t, log) = LoggedIn();
        Arrive(t, Alpha, serverBaked: true);
        t.Raise(new AvatarAppearanceReceived(Alpha, Look(Me, 1, (8, Guid.NewGuid()))));
        t.CurrentSim = Beta;
        t.Raise(new SimConnected(Beta, 256, 256, true));
        t.Raise(new AvatarAppearanceReceived(Beta, Look(Me, 1, (8, Guid.NewGuid()))));

        Assert.Equal(AppearanceMode.Server, s.World.AppearanceMode);
        // The only call the session ever made on its transport was the login; the interface has no send to make.
        Assert.Single(t.Logins);
        Assert.Equal(0, t.Logouts);
        Assert.DoesNotContain(log.Entries, e => e.Format().Contains("bake", StringComparison.OrdinalIgnoreCase) && !e.Format().Contains("appearance"));
        Assert.DoesNotContain(log.Entries, e => e.Format().Contains("AgentSetAppearance"));
    }

    [Fact]
    public void ServerModeRecordsTheSimsBakesAsOurLook()
    {
        var (s, t, _) = LoggedIn();
        Arrive(t, Alpha, serverBaked: true);
        var head = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var upper = Guid.Parse("22222222-2222-2222-2222-222222222222");
        t.Raise(new AvatarAppearanceReceived(Alpha, Look(Me, 1, (8, head), (9, upper))));
        Assert.Equal(new Dictionary<string, Guid> { ["head"] = head, ["upper"] = upper }, s.World.Self.ServerBakes);
        Assert.True(s.World.Appearance(Me)!.IsSelf);
    }

    // ---------------- the version-0 refusal ----------------

    [Theory]
    [InlineData(AppearanceMode.Server, 0, true)]    // no AppearanceData block: not a server bake, refuse it
    [InlineData(AppearanceMode.Server, 1, false)]   // the block says version 1: this is the region's bake
    [InlineData(AppearanceMode.Client, 0, false)]   // the ordinary shape of an OpenSim appearance
    [InlineData(AppearanceMode.Client, 1, false)]
    public void SelfAppearanceIsRefusedOnlyWhenServerModeSeesNoAppearanceDataBlock(AppearanceMode mode, int version, bool refused)
    {
        Assert.Equal(refused, AppearanceSlots.RejectsSelfAppearance(mode, version));

        // And the world state applies it: a refused self appearance is counted, not stored; another avatar's never is.
        var (s, t, _) = LoggedIn();
        Arrive(t, Alpha, serverBaked: mode == AppearanceMode.Server);
        t.Raise(new AvatarAppearanceReceived(Alpha, Look(Me, version, (8, Guid.NewGuid()))));
        t.Raise(new AvatarAppearanceReceived(Alpha, Look(Guid.NewGuid(), version, (8, Guid.NewGuid()))));
        Assert.Equal(refused ? 1 : 0, s.World.Dropped(WorldDrop.RejectedSelfAppearance));
        Assert.Equal(refused, s.World.Appearance(Me) is null);
        Assert.Equal(refused ? 1 : 2, s.World.Appearances().Count);
    }

    /// <summary>The version-0 self appearance is the grid's ordinary first message in server mode (every live
    /// login had one, then the region's bake was accepted): it is logged as information, not a warning.</summary>
    [Fact]
    public void TheVersionZeroSelfAppearanceIsLoggedAsInformationNotAWarning()
    {
        var (s, t, log) = LoggedIn();
        Arrive(t, Alpha, serverBaked: true);
        t.Raise(new AvatarAppearanceReceived(Alpha, Look(Me, 0, (8, Guid.NewGuid()))));
        var e = Assert.Single(log.Entries, x => x.Event == "appearance.self-rejected");
        Assert.Equal(LogLevel.Info, e.Level);
        Assert.Equal(1, s.World.Dropped(WorldDrop.RejectedSelfAppearance));
    }

    // ---------------- the appearance-service route ----------------

    [Fact]
    public void ABakeWeHoldResolvesToTheUrlTheViewerWouldBuild()
    {
        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var agent = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var url = AppearanceSlots.BakedTextureUrl("http://grid:8003/CAPS/agentappearance/", [Held(agent, ("head", id))], id);
        // llvoavatar.cpp getImageURL: service + "texture/" + agent + "/" + channel NAME + "/" + uuid
        Assert.Equal($"http://grid:8003/CAPS/agentappearance/texture/{agent}/head/{id}", url);
    }

    [Fact]
    public void TheChannelInTheUrlIsItsNameNotItsNumber()
    {
        var id = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var url = AppearanceSlots.BakedTextureUrl("http://s/", [Held(Guid.NewGuid(), ("leftarm", id))], id);
        Assert.EndsWith("/leftarm/" + id, url);
        Assert.Equal("leftarm", AppearanceSlots.SlotOf((int)LibreMetaverse.BakeType.BakedLeftArm));
    }

    [Fact]
    public void NoServiceOrNoSuchBakeMeansNoSecondRoute()
    {
        var id = Guid.Parse("66666666-6666-6666-6666-666666666666");
        AvatarAppearance[] looks = [Held(Guid.NewGuid(), ("head", id))];
        Assert.Null(AppearanceSlots.BakedTextureUrl(null, looks, id));
        Assert.Null(AppearanceSlots.BakedTextureUrl("", looks, id));
        Assert.Null(AppearanceSlots.BakedTextureUrl("http://s/", looks, Guid.Parse("77777777-7777-7777-7777-777777777777")));
        Assert.Null(AppearanceSlots.BakedTextureUrl("http://s/", [], id));
    }

    [Fact]
    public void AnyAvatarsBakeResolvesNotOnlyOurOwn()
    {
        var mine = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var theirs = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var me = Guid.NewGuid();
        var them = Guid.NewGuid();
        AvatarAppearance[] looks = [Held(me, ("head", mine)), Held(them, ("upper", theirs))];
        Assert.Equal($"http://s/texture/{them}/upper/{theirs}", AppearanceSlots.BakedTextureUrl("http://s/", looks, theirs));
    }
}

public class SelfShapeReceiptLogTests
{
    /// <summary>The own avatar's accepted appearance leaves one information line with the size and a short hash of the visual-parameter set it carried, the sex it
    /// selects and the byte of the Height parameter, so a log shows which set was drawn without holding the values themselves.</summary>
    [Fact]
    public void An_accepted_self_appearance_logs_the_size_and_hash_of_its_visual_parameter_set()
    {
        var (s, t, log) = ServerAppearanceTests.LoggedIn();
        ServerAppearanceTests.Arrive(t, ServerAppearanceTests.Alpha, serverBaked: true);
        var order = ImmutableArray.Create(33, 80);
        var set = new VisualParamSet([200, 255], order);
        var data = new AvatarAppearanceData(ServerAppearanceTests.Me, new Guid[45].ToImmutableArray(), set, 1, 3);
        t.Raise(new AvatarAppearanceReceived(ServerAppearanceTests.Alpha, data));
        var e = log.Entries.FirstOrDefault(x => x.Event == "appearance.self-received");
        Assert.NotNull(e);
        Assert.Equal(LogLevel.Info, e!.Level);
        string Field(string name) => e.Fields.First(f => f.Key == name).Value;
        Assert.Equal("2", Field("params"));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 200, 255 }))[..12], Field("params_hash"));
        Assert.Equal("male", Field("sex"));
        Assert.Equal("200", Field("height_byte"));
        // another avatar's appearance is not logged this way
        var before = log.Entries.Count(x => x.Event == "appearance.self-received");
        t.Raise(new AvatarAppearanceReceived(ServerAppearanceTests.Alpha, new AvatarAppearanceData(Guid.NewGuid(), new Guid[45].ToImmutableArray(), set, 1, 3)));
        Assert.Equal(before, log.Entries.Count(x => x.Event == "appearance.self-received"));
    }
}
