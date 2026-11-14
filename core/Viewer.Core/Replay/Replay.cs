// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.World;

namespace Viewer.Core.Replay;

/// <summary>
/// A world state reduced to what replay compares. <see cref="StructuralHash"/> covers what must not depend on delivery
/// order (which objects exist, their ids, parents, attachment points and wearers; avatars; terrain; appearances);
/// <see cref="FullHash"/> adds motion (positions, rotations, velocities, the last self sample), which legitimately
/// depends on order. Agent ids are hashed through a mapper, so a live world (real ids) and its replay (aliases) compare.
/// </summary>
public sealed record StateSummary(
    int Objects, int Avatars, int Attachments, int AttachmentsBound, int PendingAttachmentsUnbound,
    int TerrainPatches, int TerrainExpected, bool TerrainComplete, int RegionSizeX, int RegionSizeY,
    int Appearances, string AppearanceMode, string StructuralHash, string FullHash)
{
    public static StateSummary Of(WorldState w, Func<Guid, Guid>? agentAlias = null)
    {
        var id = agentAlias ?? (g => g);
        var snap = w.Snapshot();
        var s = new StringBuilder();
        var f = new StringBuilder();
        foreach (var o in snap.Objects.Values.OrderBy(o => o.Id.LocalId))
        {
            s.Append($"o{o.Id.LocalId}|{o.FullId}|{o.ParentLocalId}|{o.AttachmentPoint}|{(o.WearerAgentId is { } g ? id(g) : "-")}\n");
            f.Append($"o{o.Id.LocalId}|{o.Position}|{o.Rotation}|{o.Velocity}|{o.AngularVelocity}|{o.Acceleration}|{o.Scale}|{o.TextureAnim}\n");
        }
        foreach (var a in snap.Avatars.Values.OrderBy(a => a.Id.LocalId))
        {
            s.Append($"a{a.Id.LocalId}|{id(a.AgentId)}\n");
            f.Append($"a{a.Id.LocalId}|{a.Position}|{a.Rotation}|{a.Velocity}|{a.AngularVelocity}|{a.Scale}\n");
        }
        var region = snap.Region;
        if (region is not null)
        {
            s.Append($"r{region.SizeX}x{region.SizeY}\n");
            for (int y = 0; y * 16 < region.SizeY; y++)
                for (int x = 0; x * 16 < region.SizeX; x++)
                    if (w.TryGetPatch(x, y, out var h))
                        s.Append($"p{x},{y}|{Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(h.AsSpan())))}\n");
        }
        var looks = w.Appearances().OrderBy(l => id(l.AgentId)).ToList();
        foreach (var l in looks)
            s.Append($"v{id(l.AgentId)}|{l.AppearanceVersion}|{l.CofVersion}|{string.Join(",", l.Bakes.OrderBy(b => b.Key).Select(b => b.Key + "=" + b.Value))}|{Convert.ToHexString(l.VisualParams.Raw.AsSpan())}\n");
        if (w.LastSelfMotion is { } m)
            f.Append($"self|{m.Motion.Position}|{m.Motion.Rotation}|{m.Motion.Velocity}\n");

        string structural = Hash(s.ToString());
        int attachments = snap.Objects.Values.Count(o => o.AttachmentPoint is not null);
        return new(snap.Objects.Count, snap.Avatars.Count, attachments,
            snap.Objects.Values.Count(o => o.AttachmentPoint is not null && o.WearerAgentId is not null && snap.Avatars.Values.Any(a => a.AgentId == o.WearerAgentId)),
            w.PendingAttachmentRoots, w.TerrainPatchCount, w.TerrainExpected, w.TerrainComplete, region?.SizeX ?? 0, region?.SizeY ?? 0,
            looks.Count, w.AppearanceMode.ToString(), structural, Hash(structural + "\n" + f));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}

/// <summary>
/// Scans a written capture for what must never be in one (ADR-019). Pattern checks catch the shapes (capability URLs
/// and any URL at all, OpenSim password digests, seed-capability keys); literal checks catch the values the capture tool
/// holds in memory and never writes (session id, password, the login host, real avatar names and agent ids).
/// </summary>
public static class ScrubScan
{
    private static readonly Regex Shapes = new(@"https?://|/CAPS/|seed_capability|\$1\$[0-9a-fA-F]{32}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>What was found, by label only (the matching value is never echoed).</summary>
    public static IReadOnlyList<string> Find(string text, IEnumerable<(string Label, string Value)> literals)
    {
        var found = new List<string>();
        foreach (Match m in Shapes.Matches(text))
            found.Add("pattern:" + (m.Value.StartsWith("$1$") ? "md5-digest" : m.Value.ToLowerInvariant()));
        foreach (var (label, value) in literals)
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 4 && text.Contains(value, StringComparison.OrdinalIgnoreCase))
                found.Add("literal:" + label);
        return found.Distinct().ToList();
    }
}

/// <summary>
/// A transport decorator that records every event the adapter raises, scrubbed, with its arrival time and the adapter's
/// current-simulator view at that instant, then forwards it. Recording and forwarding happen under one lock, so the
/// file's order is the order the session applied, and <see cref="Stop"/> can take the live summary at exactly the point
/// the file ends.
/// </summary>
public sealed class RecordingTransport : ISessionTransport
{
    private readonly ISessionTransport _inner;
    private readonly CaptureFile.Writer _writer;
    private readonly CaptureFile.Aliases _aliases;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly object _gate = new();
    private bool _recording = true;

    public event Action<TransportEvent>? Event;

    public int Recorded { get; private set; }

    /// <summary>The session id of the login, held for the scrub scan only (never written).</summary>
    public Secret? SessionId { get; private set; }

    public RecordingTransport(ISessionTransport inner, CaptureFile.Writer writer, CaptureFile.Aliases aliases)
    {
        _inner = inner;
        _writer = writer;
        _aliases = aliases;
        _inner.Event += OnInner;
    }

    private void OnInner(TransportEvent e)
    {
        lock (_gate)
        {
            if (_recording && CaptureFile.IsRecorded(e))
            {
                _writer.Write(new CaptureRecord(_clock.Elapsed.TotalMilliseconds, _inner.CurrentSim?.Handle, _inner.ConnectedSimulatorCount, CaptureFile.Scrub(e, _aliases)));
                Recorded++;
            }
            Event?.Invoke(e);
        }
    }

    /// <summary>Stops recording and returns <paramref name="snapshot"/> taken under the same lock (the file's last state).</summary>
    public T Stop<T>(Func<T> snapshot)
    {
        lock (_gate)
        {
            _recording = false;
            return snapshot();
        }
    }

    public async Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct)
    {
        var r = await _inner.LoginAsync(request, ct).ConfigureAwait(false);
        SessionId = r.SessionId;
        if (r.Ok)
            _writer.WriteLoginReply(_aliases.Id(r.AgentId), r.LoginRegionSizeX, r.LoginRegionSizeY);
        return r;
    }

    public Task LogoutAsync(CancellationToken ct) => _inner.LogoutAsync(ct);
    public SimRef? CurrentSim => _inner.CurrentSim;
    public int ConnectedSimulatorCount => _inner.ConnectedSimulatorCount;
    public void SetDrawDistance(float metres) => _inner.SetDrawDistance(metres);

    public void Dispose()
    {
        _inner.Event -= OnInner;
        Event = null;
        _inner.Dispose();
    }
}

/// <summary>Seedable delivery faults for replay (the test strategy 4.2).</summary>
/// <param name="ReorderWindow">World events are shuffled within windows of this many (0: none). Structural events (login
/// progress, connect, event queue, movement complete, map block, disconnect) are barriers and never move.</param>
/// <param name="DuplicateRate">Probability a world event is delivered twice in a row.</param>
/// <param name="DropRate">Probability a world event is not delivered.</param>
public sealed record FaultPlan(int Seed = 0, int ReorderWindow = 0, double DuplicateRate = 0, double DropRate = 0)
{
    public static FaultPlan None { get; } = new();
}

public sealed record ReplayResult(StateSummary Summary, int Delivered, int Duplicated, int Dropped, int Reordered, SessionStage Stage);

/// <summary>
/// Feeds a capture into a fresh <see cref="ViewerSession"/> through a replay transport on a virtual clock (the capture's
/// own timestamps; nothing waits on real time), single-threaded and therefore deterministic for a given capture and
/// <see cref="FaultPlan"/>.
/// </summary>
public static class ReplayRunner
{
    public static ReplayResult Run(Capture capture, FaultPlan? faults = null, ILogSink? sink = null)
    {
        var (session, delivered, dup, drop, moved) = Replay(capture, faults, sink);
        using (session)
            return new(StateSummary.Of(session.World), delivered, dup, drop, moved, session.Stage);
    }

    /// <summary>
    /// Replays <paramref name="capture"/> (no faults) and hands back the session, still alive, so a consumer can read its
    /// world as a live one (the region B terrain rendered from the core's TerrainStore). The caller disposes it.
    /// </summary>
    public static ViewerSession Load(Capture capture, ILogSink? sink = null) => Replay(capture, null, sink).Session;

    private static (ViewerSession Session, int Delivered, int Duplicated, int Dropped, int Reordered) Replay(Capture capture, FaultPlan? faults, ILogSink? sink)
    {
        faults ??= FaultPlan.None;
        var (plan, dup, drop, moved) = Plan(capture.Records, faults);
        var transport = new ReplayTransport(capture.Header);
        var never = new TaskCompletionSource();
        var session = new ViewerSession(
            new SessionOptions { LoginUri = "http://replay.invalid/", Delay = (_, ct) => never.Task.WaitAsync(ct) },
            transport, new CoreLog(sink ?? NullSink.Instance, "replay"));
        try
        {
            var outcome = session.LoginAsync(new LoginCredentials("Replay", "Session", new Secret("unused", SensitiveKind.PasswordDigest)), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!outcome.Ok)
                throw new InvalidOperationException("replay login failed");
            var sims = new Dictionary<ulong, SimRef>();
            foreach (var r in plan)
            {
                foreach (var s in SimsOf(r.Event))
                    sims.TryAdd(s.Handle, s);
                transport.CurrentSim = r.CurrentSim is { } h && sims.TryGetValue(h, out var cur) ? cur : null;
                transport.ConnectedSimulatorCount = r.ConnectedSims;
                transport.Deliver(r.Event);
            }
            return (session, plan.Count, dup, drop, moved);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static IEnumerable<SimRef> SimsOf(TransportEvent e) => e switch
    {
        SimConnected c => [c.Sim],
        EventQueueRunning q => [q.Sim],
        MovementComplete m => [m.Sim],
        ObjectUpdated { Sim: { } s } => [s],
        AvatarUpdated { Sim: { } s } => [s],
        ObjectMoved { Sim: { } s } => [s],
        RegionTimeDilation { Sim: { } s } => [s],
        SelfMoved { Sim: { } s } => [s],
        AvatarAppearanceReceived { Sim: { } s } => [s],
        AvatarAnimationsReceived { Sim: { } s } => [s],
        AvatarHoverReceived { Sim: { } s } => [s],
        CameraConstraintReceived { Sim: { } s } => [s],
        ObjectsKilled { Sim: { } s } => [s],
        LandPatch { Sim: { } s } => [s],
        MaterialOverridesReceived { Sim: { } s } => [s],
        LegacyMaterialsReceived { Sim: { } s } => [s],
        RenderMaterialsReceived { Sim: { } s } => [s],
        _ => [],
    };

    private static bool IsBarrier(TransportEvent e) =>
        e is LoginProgressed or SimConnected or EventQueueRunning or MovementComplete or MapBlockSize or Disconnected;

    /// <summary>The delivery order after faults. Exposed for tests.</summary>
    public static (List<CaptureRecord> Plan, int Duplicated, int Dropped, int Reordered) Plan(IReadOnlyList<CaptureRecord> records, FaultPlan f)
    {
        var rng = new Random(f.Seed);
        var output = new List<CaptureRecord>(records.Count);
        int dup = 0, drop = 0, moved = 0;
        var segment = new List<CaptureRecord>();

        void Flush()
        {
            for (int start = 0; start < segment.Count; start += Math.Max(1, f.ReorderWindow))
            {
                var window = segment.Skip(start).Take(Math.Max(1, f.ReorderWindow)).ToList();
                if (f.ReorderWindow > 1)
                {
                    var before = window.ToList();
                    for (int i = window.Count - 1; i > 0; i--)
                    {
                        int j = rng.Next(i + 1);
                        (window[i], window[j]) = (window[j], window[i]);
                    }
                    moved += window.Where((r, i) => !ReferenceEquals(r, before[i])).Count();
                }
                foreach (var r in window)
                {
                    if (f.DropRate > 0 && rng.NextDouble() < f.DropRate) { drop++; continue; }
                    output.Add(r);
                    if (f.DuplicateRate > 0 && rng.NextDouble() < f.DuplicateRate) { output.Add(r); dup++; }
                }
            }
            segment.Clear();
        }

        foreach (var r in records)
        {
            if (IsBarrier(r.Event))
            {
                Flush();
                output.Add(r);
            }
            else
            {
                segment.Add(r);
            }
        }
        Flush();
        return (output, dup, drop, moved);
    }

    private sealed class NullSink : ILogSink
    {
        public static readonly NullSink Instance = new();
        public void Write(LogEntry entry) { }
    }
}

/// <summary>The transport a replay session runs on: login answers from the capture header; events are delivered by the runner.</summary>
public sealed class ReplayTransport(CaptureHeader header) : ISessionTransport
{
    public event Action<TransportEvent>? Event;
    public SimRef? CurrentSim { get; set; }
    public int ConnectedSimulatorCount { get; set; }

    public Task<TransportLoginResult> LoginAsync(TransportLoginRequest request, CancellationToken ct) =>
        Task.FromResult(new TransportLoginResult(true, null, null, header.SelfAgent, null, header.LoginSizeX, header.LoginSizeY));

    public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
    public void SetDrawDistance(float metres) { }
    public void Deliver(TransportEvent e) => Event?.Invoke(e);
    public void Dispose() => Event = null;
}
