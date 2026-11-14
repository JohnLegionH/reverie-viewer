// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Replay;
using Viewer.Core.Session;
using Viewer.Core.World;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Capture and replay on a synthetic session (no grid): the file round-trips, is scrubbed at write time (with a
/// negative control), replay reproduces the live state, and fault injection is seedable, deterministic and
/// order-independent where it must be.
/// </summary>
public class CaptureReplayTests
{
    private static readonly SimRef Here = new(256000UL << 32 | 256000UL, "Here");
    private static readonly Guid RealSelf = Guid.Parse("5e1f5e1f-0000-4000-8000-00000000abcd");
    private static readonly Guid RealOther = Guid.Parse("12345678-0000-4000-8000-00000000abcd");
    private const string RealOtherName = "Realname Person";

    internal static string OutDir
    {
        get
        {
            var d = Path.Combine(TestPaths.RepoRoot, "tools", "test-results", "capture-replay");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static Guid G(uint n) => new((int)n, 0, 0, new byte[8]);

    /// <summary>A small but complete session: login, connect, objects (a linkset, an attachment before its wearer), avatars,
    /// moves, a kill, a full 256 m terrain, appearances and self samples.</summary>
    private static IEnumerable<TransportEvent> Session()
    {
        yield return new LoginProgressed(LoginPhase.ConnectingToSim, "Welcome to the grid, secret motd");
        yield return new SimConnected(Here, 256, 256, true);
        yield return new MapBlockSize(1000, 1000, 256, 256);
        yield return new ObjectUpdated(Here, new PrimData(10, G(10), 0, 0, new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One), false, null);
        yield return new ObjectUpdated(Here, new PrimData(11, G(11), 10, 0, new Vector3(0, 0, 1), Quaternion.Identity, Vector3.One), false, null);
        yield return new ObjectUpdated(Here, new PrimData(20, G(20), 900, 5, Vector3.Zero, Quaternion.Identity, Vector3.One), true, null);   // before its wearer
        yield return new AvatarUpdated(Here, new AvatarData(900, RealOther, RealOtherName, new Vector3(50, 50, 25), Quaternion.Identity, Vector3.One));
        yield return new AvatarUpdated(Here, new AvatarData(901, RealSelf, "Test User", new Vector3(128, 128, 25), Quaternion.Identity, Vector3.One));
        for (int i = 0; i < 12; i++)
            yield return new ObjectMoved(Here, 10, false, new Vector3(1 + i, 2, 3), Quaternion.Identity, new Vector3(1, 0, 0), Vector3.Zero, new Vector3(0, 0, 0.5f));
        yield return new ObjectUpdated(Here, new PrimData(30, G(30), 0, 0, Vector3.One, Quaternion.Identity, Vector3.One), false, null);
        yield return new ObjectsKilled(Here, [30]);
        yield return new EventQueueRunning(Here);
        yield return new MovementComplete(Here);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                yield return new LandPatch(Here, x, y, 16, Enumerable.Range(0, 256).Select(k => (float)(x * 1000 + y * 10 + k % 7)).ToArray());
        var faces = new Guid[45];
        faces[8] = G(8008);
        yield return new AvatarAppearanceReceived(Here, new AvatarAppearanceData(RealSelf, faces.ToImmutableArray(), new VisualParamSet([1, 2, 3], [1, 2, 4]), 1, 3));
        yield return new AvatarAppearanceReceived(Here, new AvatarAppearanceData(RealOther, faces.ToImmutableArray(), VisualParamSet.Empty, 1, 7));
        for (int i = 0; i < 5; i++)
            yield return new SelfMoved(Here, new SelfMotion(new Vector3(128 + i, 128, 25), Quaternion.Identity, new Vector3(1, 0, 0), Vector3.Zero, Vector3.Zero, 1000 + i, default));
    }

    /// <summary>Records the synthetic session through a real ViewerSession + RecordingTransport, as the capture tool does.</summary>
    internal static (string Path, StateSummary Live, CaptureFile.Aliases Aliases) Record(string name)
    {
        var path = System.IO.Path.Combine(OutDir, name);
        var fake = new FakeTransport { OnLogin = (_, _) => Task.FromResult(FakeTransport.Ok(RealSelf, 256, 256)) };
        var aliases = new CaptureFile.Aliases(Guid.Empty);
        StateSummary live;
        using (var writer = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "synthetic", Guid.Empty, 0, 0, "test")))
        {
            var rec = new RecordingTransport(fake, writer, aliases);
            using var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, rec, new CoreLog(new ListSink(), "test"));
            Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
            foreach (var e in Session())
            {
                if (e is SimConnected)
                    fake.ConnectedSimulatorCount = 1;
                if (e is EventQueueRunning)
                    fake.CurrentSim = Here;
                fake.Raise(e);
            }
            live = rec.Stop(() => StateSummary.Of(session.World, aliases.Id));
            writer.WriteFooter(new CaptureFooter(live));
        }
        return (path, live, aliases);
    }

    [Fact]
    public void A_capture_round_trips_and_its_replay_reproduces_the_live_state()
    {
        var (path, live, _) = Record("roundtrip.jsonl.gz");
        var c = CaptureFile.Read(path);
        Assert.Equal(Session().Count(), c.Records.Count);
        Assert.Equal(CaptureFile.Aliases.AliasGuid(1), c.Header.SelfAgent);
        Assert.Equal(256, c.Header.LoginSizeX);
        Assert.Equal(live, c.Footer!.Live);

        var r = ReplayRunner.Run(c);
        Assert.Equal(SessionStage.InWorld, r.Stage);
        Assert.Equal(live.StructuralHash, r.Summary.StructuralHash);
        Assert.Equal(live.FullHash, r.Summary.FullHash);
        Assert.Equal(live, r.Summary);
        Assert.Equal(3, r.Summary.Objects);   // 10, 11 and the attachment; 30 was killed
        Assert.Equal(1, r.Summary.AttachmentsBound);
        Assert.Equal(0, r.Summary.PendingAttachmentsUnbound);
        Assert.True(r.Summary.TerrainComplete);
    }

    [Fact]
    public void The_written_capture_is_scrubbed()
    {
        var (path, _, aliases) = Record("scrub.jsonl.gz");
        var text = CaptureFile.ReadText(path);
        var literals = new List<(string, string)> { ("password", "pw-not-real"), ("session-id", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            ("account-first", "Test User"), ("login-host", "grid.invalid"), ("motd", "secret motd") };
        literals.AddRange(aliases.RealIds.Select((g, i) => ($"real-agent-id-{i}", g.ToString())));
        literals.AddRange(aliases.RealNames.Select((n, i) => ($"real-avatar-name-{i}", n)));
        Assert.Contains(RealOtherName, aliases.RealNames);
        Assert.Empty(ScrubScan.Find(text, literals));
        Assert.Contains("Avatar 2", text);   // names became aliases, not blanks
    }

    [Fact]
    public void Negative_control_an_unscrubbed_write_is_caught()
    {
        var path = Path.Combine(OutDir, "unscrubbed.jsonl.gz");
        using (var w = new CaptureFile.Writer(path, new CaptureHeader(CaptureFile.FormatVersion, "negative", Guid.Empty, 0, 0, "test")))
        {
            // Written WITHOUT CaptureFile.Scrub: a real agent id, and a name field carrying a real name, a cap URL, a
            // session id and a digest (what a careless writer could leak through any free-text field).
            w.Write(new CaptureRecord(1, null, 0, new AvatarUpdated(Here, new AvatarData(900, RealOther,
                RealOtherName + " seed http://grid.invalid:9000/CAPS/0f1e2d3c/ sid aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee $1$0123456789abcdef0123456789abcdef",
                Vector3.Zero, Quaternion.Identity, Vector3.One))));
        }
        var text = CaptureFile.ReadText(path);
        Assert.Contains("seed http", text);   // the format itself does not scrub: CaptureFile.Scrub does
        var found = ScrubScan.Find(text, [("session-id", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), ("real-avatar-name", RealOtherName), ("real-agent-id", RealOther.ToString())]);
        Assert.Contains("pattern:http://", found);
        Assert.Contains("pattern:/caps/", found);
        Assert.Contains("pattern:md5-digest", found);
        Assert.Contains("literal:session-id", found);
        Assert.Contains("literal:real-avatar-name", found);
        Assert.Contains("literal:real-agent-id", found);
        // ... and the same event through Scrub is clean.
        Assert.Empty(ScrubScan.Find(CaptureFile.Scrub(new Disconnected("http://x/CAPS/y"), new CaptureFile.Aliases(Guid.Empty)).ToString(), []));
    }

    [Fact]
    public void Reordered_and_duplicated_delivery_reaches_the_same_state()
    {
        var c = CaptureFile.Read(Record("faults.jsonl.gz").Path);
        var clean = ReplayRunner.Run(c).Summary;
        for (int seed = 1; seed <= 20; seed++)
        {
            var reordered = ReplayRunner.Run(c, new FaultPlan(seed, ReorderWindow: 8));
            Assert.True(reordered.Reordered > 0, $"seed {seed} reordered nothing");
            Assert.Equal(clean.StructuralHash, reordered.Summary.StructuralHash);
            Assert.Equal(0, reordered.Summary.PendingAttachmentsUnbound);

            var duplicated = ReplayRunner.Run(c, new FaultPlan(seed, DuplicateRate: 0.3));
            Assert.True(duplicated.Duplicated > 0);
            Assert.Equal(clean.FullHash, duplicated.Summary.FullHash);   // an immediate duplicate changes nothing at all
        }
    }

    [Fact]
    public void Replay_is_deterministic_for_a_capture_and_a_seed()
    {
        var c = CaptureFile.Read(Record("determinism.jsonl.gz").Path);
        var plan = new FaultPlan(Seed: 7, ReorderWindow: 16, DuplicateRate: 0.2, DropRate: 0.1);
        var a = ReplayRunner.Run(c, plan);
        var b = ReplayRunner.Run(c, plan);
        Assert.Equal(a, b);
        Assert.True(a.Dropped > 0);
        Assert.NotEqual(ReplayRunner.Run(c).Summary.FullHash, a.Summary.FullHash);   // the faults really changed something
    }

    [Fact]
    public void Structural_events_are_barriers_that_never_move()
    {
        var c = CaptureFile.Read(Record("barriers.jsonl.gz").Path);
        var (plan, _, _, _) = ReplayRunner.Plan(c.Records, new FaultPlan(3, ReorderWindow: 50));
        int Pos(Type t) => plan.FindIndex(r => r.Event.GetType() == t);
        int Orig(Type t) => c.Records.ToList().FindIndex(r => r.Event.GetType() == t);
        foreach (var t in new[] { typeof(LoginProgressed), typeof(SimConnected), typeof(MapBlockSize), typeof(EventQueueRunning), typeof(MovementComplete) })
            Assert.Equal(Orig(t), Pos(t));
    }
}

/// <summary>A [Fact] that is a real SKIP, with its reason, when a local-only capture is absent (a fresh clone stays green).</summary>
public sealed class CaptureFactAttribute : FactAttribute
{
    public CaptureFactAttribute(string file)
    {
        if (!File.Exists(LiveCaptures.PathOf(file)))
            Skip = $"local-only capture fixtures-local\\{file} is absent (captures are grid content and never committed, ADR-011)";
    }
}

internal static class LiveCaptures
{
    public static string PathOf(string file) => Path.Combine(TestPaths.FixturesLocal, file);   // NV_FIXTURES_LOCAL wins
}
