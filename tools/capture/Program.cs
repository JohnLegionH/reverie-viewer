// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using System.Text.Json;
using Viewer.Core.Diagnostics;
using Viewer.Core.Net;
using Viewer.Core.Protocol;
using Viewer.Core.Replay;
using Viewer.Core.Session;

// capture tool. Read-mostly: log in, let the scene settle, log out. Nothing else is ever sent.
//   capture --accounts <file> --account tester1 --tag alpha --start "uri:Alpha&128&128&25" --out <fixtures-local\x.jsonl.gz>
//           [--settle 5] [--cap 120] [--assets <fixtures-local\assets>]
//   with --assets, after recording stops and before logout, every mesh asset and sculpt-map texture the scene
//   references is fetched through the session's own asset access and kept in the local-only cache (meshes only if they
//   pass the bounded decoder; sculpt maps raw). Also every PBR material asset the faces name (kept if it decodes as a
//   material), and the textures those materials, the region's legacy materials and the glTF overrides name.
//   Still read-only: asset GETs and RenderMaterials lookups only.
//   with --avatar, also the grid's built-in stand and walk animations (kept if they read as animations) and this
//   avatar's own server bakes (kept if they pass the decode guard), with self-bakes.json naming each bake slot's texture.
//   f55     --accounts <file> --a tester1 --b viewer1 --start "uri:Alpha&128&128&25" [--settle 5] [--observe 20]
// The login URI is read from the environment variable NV_LOGIN_URI; it is never printed or written.

var opts = Args.Parse(args);
if (opts.Command == "replay")
    return Replay();
if (opts.Command == "lmv-log-probe")
    return LmvLogProbe();
if (opts.Command == "account-check")
    return AccountCheck();
string loginUri = await ResolveLoginUriAsync();
var accounts = opts.Has("accounts") ? Accounts.Load(opts.Get("accounts")) : Accounts.None;
var sink = new ConsoleSink();

return opts.Command switch
{
    "capture" => await CaptureAsync(),
    "f55" => await F55Async(),
    _ => Usage(),
};

// Offline: replays a capture clean and under seeded faults and prints the summaries (no grid, no credentials).
int Replay()
{
    var c = CaptureFile.Read(opts.Get("in"));
    var clean = ReplayRunner.Run(c);
    var runs = Enumerable.Range(1, opts.Int("seeds", 5)).Select(seed => new
    {
        seed,
        reorder = Stat(ReplayRunner.Run(c, new FaultPlan(seed, ReorderWindow: 32))),
        duplicate = Stat(ReplayRunner.Run(c, new FaultPlan(seed, DuplicateRate: 0.2))),
        dropFivePercent = Stat(ReplayRunner.Run(c, new FaultPlan(seed, DropRate: 0.05))),
    }).ToList();
    var again = ReplayRunner.Run(c);
    Report(new { records = c.Records.Count, footerMatches = c.Footer?.Live == clean.Summary, clean = clean.Summary, deterministic = again == clean, runs });
    return 0;
    object Stat(ReplayResult r) => new { r.Reordered, r.Duplicated, r.Dropped, sameStructure = r.Summary.StructuralHash == clean.Summary.StructuralHash, sameFull = r.Summary.FullHash == clean.Summary.FullHash, r.Summary.Objects, r.Summary.PendingAttachmentsUnbound };
}

// proof, run as a child process by LmvLogRouteTests: LibreMetaverse's logger with the route installed the
// normal way (the adapter factory), then one library log line carrying a LAN address, a capability URL and a
// session-id-shaped value. --default-console (NEGATIVE CONTROL) uses LibreMetaverse's own console factory instead.
int LmvLogProbe()
{
    var log = new CoreLog(new PrintAllSink(), "probe");   // every entry, Info included, on stdout (already redacted by CoreLog)
    if (opts.Has("default-console"))
        LibreMetaverse.Logger.SetLoggerFactory(LibreMetaverse.Logger.CreateDefaultConsoleLoggerFactory(), "LibreMetaverse");
    else
        using (ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), log)) { }
    var client = new LibreMetaverse.GridClient();   // created after the route, as in the app
    LibreMetaverse.Logger.Info("Connecting to (203.0.113.13:9000) seed https://grid.invalid:9000/CAPS/0f1e2d3c-secret-path/ session 11111111-2222-3333-4444-555555555555", client);
    LibreMetaverse.Logger.Warn("resend from 198.51.100.40", new InvalidOperationException("sim 192.0.2.9 said https://grid.invalid/CAPS/abc/"), client);
    // CoreJ2K's own logger: encoding a 64x64 image makes it warn ("Tile dimensions... exceed image dimensions").
    if (!opts.Has("default-console"))
        LmvTexture.Encode(64, 64, Enumerable.Range(0, 64 * 64 * 4).Select(i => (byte)(i * 7)).ToArray(), withAlpha: false);   // 64x64: LibreMetaverse's encode settings throw "target bitrate too low" for 16x16
    LibreMetaverse.Logger.Shutdown();
    Console.Out.Flush();
    return 0;
}

// The login address: NV_LOGIN_URI if set, else the <login> value of the grid's get_grid_info (--grid <base address>).
// Never printed.
async Task<string> ResolveLoginUriAsync()
{
    var env = Environment.GetEnvironmentVariable("NV_LOGIN_URI");
    if (!string.IsNullOrEmpty(env))
        return env;
    if (!opts.Has("grid"))
        return opts.Command is "replay" or "lmv-log-probe" or "account-check" ? "" : throw new InvalidOperationException("NV_LOGIN_URI is not set (or pass --grid <base address>)");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var text = await http.GetStringAsync(opts.Get("grid").TrimEnd('/') + "/get_grid_info");
    var login = System.Xml.Linq.XDocument.Parse(text).Descendants("login").FirstOrDefault()?.Value.Trim();
    return string.IsNullOrEmpty(login) ? throw new InvalidOperationException("the grid's get_grid_info names no login address") : login;
}

// The typed account: name on one line, password read without echo. Held in memory only, for this run.
Account PromptAccount()
{
    Console.Write("First name: ");
    string first = (Console.ReadLine() ?? "").Trim();
    Console.Write("Last name: ");
    string last = (Console.ReadLine() ?? "").Trim();
    Console.Write("Password (not shown): ");
    var pw = new System.Text.StringBuilder();
    for (var k = Console.ReadKey(intercept: true); k.Key != ConsoleKey.Enter; k = Console.ReadKey(intercept: true))
    {
        if (k.Key == ConsoleKey.Backspace) { if (pw.Length > 0) pw.Length--; }
        else if (k.KeyChar != '\0') pw.Append(k.KeyChar);
    }
    Console.WriteLine();
    if (first.Length == 0 || pw.Length == 0)
        throw new InvalidOperationException("a first name and a password are needed");
    return new Account("typed", first, last.Length == 0 ? "Resident" : last, new Secret(pw.ToString(), SensitiveKind.PasswordDigest));
}

// Offline: says whether the account may be used for a capture login (the test-account pool and protected-accounts files decide);
// logs in to nothing and prints no password.
int AccountCheck()
{
    try
    {
        var a = Accounts.Load(opts.Get("accounts")).Get(opts.Get("account"));
        Console.WriteLine("account allowed: " + a.Alias);
        return 0;
    }
    catch (InvalidOperationException e)
    {
        Console.WriteLine("account refused: " + e.Message);
        return 1;
    }
}

int Usage()
{
    Console.Error.WriteLine("usage: capture ... | f55 ...  (see Program.cs)");
    return 2;
}

async Task<int> CaptureAsync()
{
    // --account typed: the person at the console types their own name and password (nothing is read from a file).
    var acct = opts.Get("account") == "typed" ? PromptAccount() : accounts.Get(opts.Get("account"));
    string tag = opts.Get("tag");
    string outPath = Path.GetFullPath(opts.Get("out"));
    if (!outPath.Contains($"{Path.DirectorySeparatorChar}fixtures-local{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("captures are written only under fixtures-local\\ (ADR-011)");
    int settle = opts.Int("settle", 5), cap = opts.Int("cap", 120);

    var log = new CoreLog(sink, "capture");
    var lmv = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), log);
    // The self alias and login region size follow in a loginReply line once login returns (RecordingTransport).
    var aliases = new CaptureFile.Aliases(Guid.Empty);
    var writer = new CaptureFile.Writer(outPath, new CaptureHeader(CaptureFile.FormatVersion, tag, Guid.Empty, 0, 0, "LibreMetaverse 3.1.6"));
    var recording = new RecordingTransport(lmv, writer, aliases);
    using var session = new ViewerSession(new SessionOptions { LoginUri = loginUri, StartLocation = opts.Get("start") }, recording, log);

    var result = await RunReadMostlyAsync(session, acct, settle, cap);
    if (result.Login is null)
    {
        writer.Dispose();
        return 1;
    }
    if (opts.Has("hold"))
    {
        Console.WriteLine($"IN WORLD: standing still for {opts.Int("hold", 0)} s");
        await Task.Delay(TimeSpan.FromSeconds(opts.Int("hold", 0)));
    }
    var live = recording.Stop(() => StateSummary.Of(session.World, aliases.Id));
    object? assetReport = opts.Has("assets") ? await CacheAssetsAsync(session, lmv, opts.Get("assets")) : null;
    object? avatarReport = opts.Has("avatar") ? await CacheAvatarAsync(session, lmv, opts.Get("avatar")) : null;
    var logout = await LogoutAsync(session, lmv);
    writer.WriteFooter(new CaptureFooter(live));
    writer.Dispose();

    var text = CaptureFile.ReadText(outPath);
    var literals = new List<(string, string)>
    {
        ("session-id", recording.SessionId?.Reveal() ?? ""),
        ("password", acct.Password.Reveal()),
        // The full name and the last name: a bare first name can be a common word ("Agent" matches the JSON key "agent").
        ("account-name", acct.First + " " + acct.Last), ("account-last", acct.Last),
        ("login-host", new Uri(loginUri).Host),
    };
    literals.AddRange(aliases.RealIds.Select((g, i) => ($"real-agent-id-{i + 1}", g.ToString())));
    literals.AddRange(aliases.RealNames.Select((n, i) => ($"real-avatar-name-{i + 1}", n)));
    var found = ScrubScan.Find(text, literals);

    var summary = new
    {
        tag, file = Path.GetFileName(outPath), bytes = new FileInfo(outPath).Length, events = recording.Recorded,
        settledAfterSeconds = result.SettledAfter, stableSeconds = settle, capped = result.Capped,
        live, logout, assets = assetReport, avatar = avatarReport, scrubScan = new { literalsChecked = literals.Count, findings = found },
    };
    Report(summary);
    if (opts.Has("report"))
        File.WriteAllText(opts.Get("report"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    return found.Count == 0 && logout.Clean ? 0 : 1;
}

// the referenced meshes and sculpt maps, through this session's SessionAssets, into the local-only cache.
async Task<object> CacheAssetsAsync(ViewerSession session, ISessionTransport transport, string dir)
{
    string full = Path.GetFullPath(dir);
    if (!full.Contains($"{Path.DirectorySeparatorChar}fixtures-local{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("the asset cache lives only under fixtures-local (ADR-011)");
    var cache = new LocalAssetCache(full);
    var objects = session.World.Snapshot().Objects.Values.Where(o => o.Shape is { SculptType: > 0 } sh && sh.SculptId != Guid.Empty).ToList();
    var meshIds = objects.Where(o => o.Shape!.SculptType == Viewer.Core.Meshing.PrimShape.SculptTypeMesh).Select(o => o.Shape!.SculptId).Distinct().ToList();
    var sculptIds = objects.Where(o => o.Shape!.SculptType != Viewer.Core.Meshing.PrimShape.SculptTypeMesh).Select(o => o.Shape!.SculptId).Distinct().ToList();
    var mt = (IMeshTransport)transport;
    var tt = (ITextureTransport)transport;
    // Per-session access: authorise (OpenSim does not gate mesh/texture GETs, so this tool allows them), per-session failures.
    var store = new SharedAssetStore();
    using var meshes = new SessionAssets(store, _ => true, mt.FetchMeshAsync);
    using var textures = new SessionAssets(store, _ => true, tt.FetchTextureAsync);
    int meshOk = 0, meshRefused = 0, meshMissing = 0, texOk = 0, texMissing = 0;
    var refusals = new List<string>();
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(opts.Int("asset-budget", 300)));
    foreach (var id in meshIds)
    {
        using var one = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        one.CancelAfter(TimeSpan.FromSeconds(20));
        var r = await meshes.GetAsync(id, one.Token);
        if (r.Status != AssetFetchStatus.Ok) { meshMissing++; continue; }
        var d = LmvMesh.Decode(id, r.Bytes.ToArray());
        if (!d.Ok) { meshRefused++; refusals.Add(d.Refusal!.Reason); continue; }
        cache.PutMesh(id, r.Bytes.Span);
        meshOk++;
    }
    // every texture the objects' texture entries reference, kept raw only if it passes the decode guard.
    var textureIds = session.World.Snapshot().Objects.Values.Where(o => o.Textures is not null)
        .SelectMany(o => new[] { (Viewer.Core.World.FaceTexture?)o.Textures!.Default }.Concat(o.Textures!.Overrides))
        .Where(t => t is { } x && x.TextureId != Guid.Empty).Select(t => t!.Value.TextureId).Distinct().Except(sculptIds).ToList();
    // the PBR material assets the faces name, kept raw if they decode as materials; then every texture the materials
    // (PBR, legacy and the glTF overrides) name joins the texture list, through the same guarded path
    var materialReport = await CacheMaterialsAsync(session, (IMaterialTransport)transport, cache, budget.Token);
    textureIds.AddRange(materialReport.TextureIds.Except(textureIds).Except(sculptIds));
    // the region's four terrain detail textures, through the same guarded path
    var terrain = session.World.Region?.Terrain;
    var terrainIds = terrain is null ? [] : terrain.Details.Where(d => d != Guid.Empty).Distinct().Except(textureIds).ToList();
    textureIds.AddRange(terrainIds);
    int texCached = 0, texRefused = 0, texAbsent = 0, texAlready = 0;
    long texBytes = 0;
    var texRefusals = new List<string>();
    foreach (var id in textureIds)
    {
        if (cache.HasTexture(id)) { texAlready++; texCached++; texBytes += new FileInfo(cache.TexturePath(id)).Length; continue; }
        using var one = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        one.CancelAfter(TimeSpan.FromSeconds(20));
        var r = await textures.GetAsync(id, one.Token);
        if (r.Status != AssetFetchStatus.Ok) { texAbsent++; continue; }
        var d = LmvTexture.Decode(r.Bytes.ToArray());
        if (!d.Ok) { texRefused++; texRefusals.Add(d.Refusal!); continue; }
        cache.PutTexture(id, r.Bytes.Span);
        texCached++;
        texBytes += r.Bytes.Length;
    }
    foreach (var id in sculptIds)
    {
        using var one = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        one.CancelAfter(TimeSpan.FromSeconds(20));
        var r = await textures.GetAsync(id, one.Token);
        if (r.Status != AssetFetchStatus.Ok) { texMissing++; continue; }
        cache.PutTexture(id, r.Bytes.Span);
        texOk++;
    }
    return new
    {
        meshObjects = objects.Count(o => o.Shape!.SculptType == Viewer.Core.Meshing.PrimShape.SculptTypeMesh),
        sculptObjects = objects.Count(o => o.Shape!.SculptType != Viewer.Core.Meshing.PrimShape.SculptTypeMesh),
        meshAssets = meshIds.Count, meshCached = meshOk, meshRefused, meshMissing, refusals = refusals.Distinct(),
        sculptMaps = sculptIds.Count, sculptCached = texOk, sculptMissing = texMissing,
        terrainSettings = terrain is null ? null : new { details = terrain.Details.Length, start = terrain.StartHeights, range = terrain.HeightRanges, detailsCached = terrain.Details.Count(d => d != Guid.Empty && cache.HasTexture(d)) },
        sculptFlagsSet = objects.Count(o => o.Shape!.SculptFlags != 0),
        objectsWithTextureEntries = session.World.Snapshot().Objects.Values.Count(o => o.Textures is not null),
        textures = textureIds.Count, texturesCached = texCached, texturesAlreadyCached = texAlready, texturesRefused = texRefused, texturesMissing = texAbsent,
        textureBytes = texBytes, textureRefusals = texRefusals.Distinct(), budgetExpired = budget.IsCancellationRequested,
        materials = materialReport.Report,
    };
}

// the built-in stand and walk animations and this avatar's own bakes, into the local-only cache.
async Task<object> CacheAvatarAsync(ViewerSession session, ISessionTransport transport, string dir)
{
    string full = Path.GetFullPath(dir);
    if (!full.Contains($"{Path.DirectorySeparatorChar}fixtures-local{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("the asset cache lives only under fixtures-local (ADR-011)");
    var cache = new LocalAssetCache(full);
    var at = (IAnimationTransport)transport;
    var animations = new List<object>();
    foreach (var (name, id) in new[] { ("stand", Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f")), ("walk", Guid.Parse("6ed24bd8-91aa-4b12-ccc7-c97c857ab4e0")) })
    {
        using var one = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var r = await at.FetchAnimationAsync(id, one.Token);
        string? why = null;
        Viewer.Core.Avatars.AnimationClip? clip = null;
        if (r.Status != AssetFetchStatus.Ok) why = r.Status.ToString();
        else
        {
            try { clip = Viewer.Core.Avatars.AnimationClip.Parse(r.Bytes.Span); cache.PutAnimation(id, r.Bytes.Span); }
            catch (InvalidDataException ex) { why = ex.Message; }
        }
        animations.Add(new { name, bytes = r.Bytes.Length, kept = why is null, why, joints = clip?.Tracks.Count, duration = clip?.Duration, loop = clip?.Loop });
    }
    var self = session.World.Appearances().FirstOrDefault(a => a.IsSelf);
    var tt = (ITextureTransport)transport;
    var bakes = new List<object>();
    var kept = new Dictionary<string, string>();
    foreach (var (slot, id) in self?.Bakes ?? new Dictionary<string, Guid>())
    {
        if (id == Guid.Empty) continue;
        using var one = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var r = cache.HasTexture(id) ? await cache.FetchTextureAsync(id, one.Token) : await tt.FetchTextureAsync(id, one.Token);
        string? why = r.Status == AssetFetchStatus.Ok ? null : r.Status.ToString();
        if (why is null)
        {
            var d = LmvTexture.Decode(r.Bytes.ToArray());
            if (!d.Ok) why = d.Refusal;
            else { cache.PutTexture(id, r.Bytes.Span); kept[slot] = id.ToString("D"); }
        }
        bakes.Add(new { slot, bytes = r.Bytes.Length, kept = why is null, why });
    }
    File.WriteAllText(Path.Combine(full, "self-bakes.json"), JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = true }));
    return new { animations, appearance_seen = self is not null, appearance_version = self?.AppearanceVersion, bakes };
}

// What the scene carries in materials (counts only, no ids), and the PBR material assets cached.
async Task<(object Report, List<Guid> TextureIds)> CacheMaterialsAsync(ViewerSession session, IMaterialTransport mt, LocalAssetCache cache, CancellationToken budget)
{
    var objects = session.World.Snapshot().Objects.Values.ToList();
    var faces = objects.Where(o => o.Materials is not null).SelectMany(o => o.Materials!.All).Where(f => f is { IsNone: false }).Select(f => f!.Value).ToList();
    var pbrIds = faces.Select(f => f.RenderMaterialId).Where(g => g != Guid.Empty).Distinct().ToList();
    var legacyIds = faces.Select(f => f.MaterialId).Where(g => g != Guid.Empty).Distinct().ToList();
    var legacy = session.World.LegacyMaterials();
    var textures = new List<Guid>();
    var allOnes = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
    foreach (var m in legacy.Values)
        textures.AddRange(new[] { m.Normal.TextureId, m.Specular.TextureId }.Where(g => g != Guid.Empty));
    // the textures the glTF overrides set ("tex"; the all-ones id means "set to none")
    var overridden = objects.Where(o => o.MaterialOverrides is not null).ToList();
    var overrideKeys = new Dictionary<string, int>();
    foreach (var f in overridden.SelectMany(o => o.MaterialOverrides!.Faces))
    {
        using var doc = JsonDocument.Parse(f.Json);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            overrideKeys[p.Name] = overrideKeys.GetValueOrDefault(p.Name) + 1;
            if (p.Name == "tex" && p.Value.ValueKind == JsonValueKind.Array)
                foreach (var t in p.Value.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String && Guid.TryParse(t.GetString(), out var g) && g != Guid.Empty && g != allOnes)
                        textures.Add(g);
        }
    }
    int cached = 0, already = 0, missing = 0, refused = 0;
    var refusedShapes = new HashSet<string>();   // how a refused asset starts (its first characters, letters and marks only)
    foreach (var id in pbrIds)
    {
        if (cache.HasMaterial(id))
        {
            already++;
            textures.AddRange(LmvMaterials.TexturesOfMaterialAsset(id, File.ReadAllBytes(cache.MaterialPath(id))) ?? []);
            continue;
        }
        using var one = CancellationTokenSource.CreateLinkedTokenSource(budget);
        one.CancelAfter(TimeSpan.FromSeconds(20));
        AssetFetchResult r;
        try { r = await mt.FetchMaterialAsync(id, one.Token); }
        catch (OperationCanceledException) { missing++; continue; }
        if (r.Status != AssetFetchStatus.Ok) { missing++; continue; }
        var bytes = r.Bytes.ToArray();
        if (LmvMaterials.TexturesOfMaterialAsset(id, bytes) is not { } named)
        {
            refused++;
            refusedShapes.Add(new string(System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(12, bytes.Length)).Where(c => char.IsLetter(c) || "<>?{[/ ".Contains(c)).ToArray()));
            continue;
        }
        cache.PutMaterial(id, bytes);
        cached++;
        textures.AddRange(named);
    }
    var report = new
    {
        objectsWithMaterialReferences = objects.Count(o => o.Materials is not null),
        facesWithBump = faces.Count(f => f.Bump != 0), facesWithShiny = faces.Count(f => f.Shiny != 0),
        facesWithLegacyMaterial = faces.Count(f => f.MaterialId != Guid.Empty), facesWithPbrMaterial = faces.Count(f => f.RenderMaterialId != Guid.Empty),
        legacyMaterialIds = legacyIds.Count, legacyMaterialsServed = legacy.Count, legacyMaterialsMissing = legacyIds.Count(g => !legacy.ContainsKey(g)),
        legacyAlphaModes = legacy.Values.GroupBy(m => m.DiffuseAlphaMode.ToString()).ToDictionary(g => g.Key, g => g.Count()),
        legacyWithNormalMap = legacy.Values.Count(m => m.Normal.TextureId != Guid.Empty), legacyWithSpecularMap = legacy.Values.Count(m => m.Specular.TextureId != Guid.Empty),
        pbrMaterialIds = pbrIds.Count, pbrMaterialsCached = cached, pbrMaterialsAlreadyCached = already, pbrMaterialsMissing = missing, pbrMaterialsRefused = refused, pbrRefusedShapes = refusedShapes,
        objectsWithOverrides = overridden.Count, overriddenFaces = overridden.Sum(o => o.MaterialOverrides!.Faces.Count), overrideFieldCounts = overrideKeys,
        materialTextures = textures.Distinct().Count(),
    };
    return (report, textures.Distinct().ToList());
}

async Task<int> F55Async()
{
    var a = accounts.Get(opts.Get("a"));
    var b = accounts.Get(opts.Get("b"));
    int settle = opts.Int("settle", 5), observe = opts.Int("observe", 20);
    var start = opts.Get("start");

    var logA = new CoreLog(sink, "f55.a");
    var logB = new CoreLog(sink, "f55.b");
    var ta = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), logA);
    var tb = ProtocolAdapters.CreateLibreMetaverse(new OutboundPolicy(new OutboundOptions()), logB);
    using var sa = new ViewerSession(new SessionOptions { LoginUri = loginUri, StartLocation = start }, ta, logA);
    using var sb = new ViewerSession(new SessionOptions { LoginUri = loginUri, StartLocation = start }, tb, logB);

    var ra = await RunReadMostlyAsync(sa, a, settle, 90);
    if (ra.Login is null)
        return 1;
    var rb = await RunReadMostlyAsync(sb, b, settle, 90);
    if (rb.Login is null)
    {
        await LogoutAsync(sa, ta);
        return 1;
    }
    var both = ProtocolDiagnostics.DataPools(ta, tb);

    // Log B out; watch A for `observe` seconds: does A's world keep updating, and is A's session untouched?
    int changesA = 0, eventsA = 0;
    sa.World.Changed += _ => Interlocked.Increment(ref changesA);
    ta.Event += _ => Interlocked.Increment(ref eventsA);
    int objectsBefore = sa.World.Snapshot().Objects.Count;
    var logoutB = await LogoutAsync(sb, tb);
    await Task.Delay(TimeSpan.FromSeconds(observe));
    var afterB = ProtocolDiagnostics.DataPools(ta, tb);
    var survivor = new
    {
        stage = sa.Stage.ToString(), ended = sa.IsEnded, eventsDuringObserve = eventsA, worldChangesDuringObserve = changesA,
        objectsBefore, objectsAfter = sa.World.Snapshot().Objects.Count, avatarsAfter = sa.World.Snapshot().Avatars.Count,
    };
    var logoutA = await LogoutAsync(sa, ta);

    Report(new { bothConnected = both, afterBLoggedOut = afterB, survivor, logoutB, logoutA, observeSeconds = observe });
    return logoutA.Clean && logoutB.Clean ? 0 : 1;
}

async Task<Run> RunReadMostlyAsync(ViewerSession session, Account acct, int settle, int cap)
{
    var outcome = await session.LoginAsync(new LoginCredentials(acct.First, acct.Last, acct.Password), CancellationToken.None);
    if (!outcome.Ok)
    {
        Console.WriteLine($"LOGIN FAILED ({acct.Alias}): code={outcome.Code} message={outcome.Message}");
        return new(null, 0, false);
    }
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (session.Stage != SessionStage.InWorld && clock.Elapsed.TotalSeconds < 60 && !session.IsEnded)
        await Task.Delay(250);
    if (session.Stage != SessionStage.InWorld)
    {
        Console.WriteLine($"NOT IN WORLD ({acct.Alias}): stage={session.Stage} ended={session.IsEnded}");
        await session.LogoutAsync(CancellationToken.None);
        return new(null, 0, false);
    }
    // Settled: object, avatar, terrain and legacy material counts unchanged for `settle` consecutive seconds (capped).
    (int, int, int, int) last = (-1, -1, -1, -1);
    int stable = 0;
    while (clock.Elapsed.TotalSeconds < cap)
    {
        await Task.Delay(1000);
        var s = session.World.Snapshot();
        var now = (s.Objects.Count, s.Avatars.Count, session.World.TerrainPatchCount, session.World.LegacyMaterials().Count);
        stable = now == last ? stable + 1 : 0;
        last = now;
        if (stable >= settle)
            break;
    }
    var region = session.Region;
    return new((region?.SizeX ?? 0, region?.SizeY ?? 0), Math.Round(clock.Elapsed.TotalSeconds, 1), stable < settle);
}

async Task<LogoutResult> LogoutAsync(ViewerSession session, ISessionTransport transport)
{
    var entries = sink.Count;
    await session.LogoutAsync(CancellationToken.None);
    await Task.Delay(1500);
    bool complete = sink.Since(entries).Any(e => e.Event == "logout.complete");
    bool unclean = sink.Since(entries).Any(e => e.Event == "logout.unclean");
    return new(session.EndReason.ToString(), complete, unclean, transport.ConnectedSimulatorCount, complete && !unclean && transport.ConnectedSimulatorCount == 0);
}

static void Report(object o) => Console.WriteLine(JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

record struct Run((int X, int Y)? Login, double SettledAfter, bool Capped);

record LogoutResult(string EndReason, bool LogoutReplyReceived, bool Unclean, int ConnectedSimulatorsAfter, bool Clean);

sealed record Account(string Alias, string First, string Last, Secret Password);

sealed class Accounts(string? path)
{
    /// <summary>No accounts file: only a typed account (--account typed) can be used.</summary>
    public static Accounts None { get; } = new(null);

    /// <summary>The test client's accounts file. It is read, one account at a time (the password as a Secret), only when an account
    /// is asked for; nothing is copied anywhere.</summary>
    public static Accounts Load(string path) => new(path);

    /// <summary>An account for a capture login: the viewer automation's rule (AccountsFile): the alias must be in the test-account pool
    /// file and neither it nor the account's name on the protected-accounts file. No account name is built into this tool.</summary>
    public Account Get(string alias)
    {
        if (path is null)
            throw new InvalidOperationException("no accounts file was given (--accounts)");
        var (first, last, password) = Viewer.Automation.AccountsFile.Load(path, alias);
        return new Account(alias, first, last, password);
    }
}

sealed class PrintAllSink : ILogSink
{
    public void Write(LogEntry e) => Console.WriteLine(e.Format());
}

sealed class ConsoleSink : ILogSink
{
    private readonly List<LogEntry> _all = new();

    public int Count { get { lock (_all) return _all.Count; } }

    public IReadOnlyList<LogEntry> Since(int index) { lock (_all) return _all.Skip(index).ToList(); }

    public void Write(LogEntry e)
    {
        lock (_all) _all.Add(e);
        // Only stage, region, logout and problems reach the console; the fields are already redacted by CoreLog, and
        // login.start (which carries the login URI) is not printed.
        if (e.Level >= LogLevel.Warn || e.Event is "session.stage" or "region.connect" or "region.size" or "logout.complete" or "session.ended" or "appearance.mode")
            Console.WriteLine(e.Format());
    }
}

sealed class Args
{
    private readonly Dictionary<string, string> _v = new(StringComparer.OrdinalIgnoreCase);
    public string Command { get; private init; } = "";

    public static Args Parse(string[] a)
    {
        var r = new Args { Command = a.Length > 0 ? a[0] : "" };
        for (int i = 1; i + 1 < a.Length; i += 2)
            r._v[a[i].TrimStart('-')] = a[i + 1];
        return r;
    }

    public string Get(string k) => _v.TryGetValue(k, out var v) ? v : throw new ArgumentException("missing --" + k);
    public int Int(string k, int d) => _v.TryGetValue(k, out var v) ? int.Parse(v) : d;
    public bool Has(string k) => _v.ContainsKey(k);
}
