// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
// Acknowledgement: the order of the steps (list the worn wearables, fetch them and their textures, composite each bake, upload
// it, then send one AgentSetAppearance, all or nothing) follows the OpenSim-aware Firestorm viewer's legacy bake path
// (llagent.cpp sendAgentSetAppearance, llvoavatarself.cpp, llviewertexlayer.cpp); no viewer code is used here.

using OpenSimNGC.Appearance.Baking;
using Viewer.Core.Diagnostics;
using BakeKind = OpenSimNGC.Appearance.Baking.WearableKind;

namespace Viewer.Core.Protocol;

/// <summary>One worn wearable as the region lists it: the inventory item, the wearable asset and its type number.</summary>
internal sealed record WornItem(Guid ItemId, Guid AssetId, int WearableType);

/// <summary>What a bake run needs from the session. The protocol adapter implements it over the grid client.</summary>
internal interface IBakeEnvironment
{
    /// <summary>The worn wearables (the region's answer to AgentWearablesRequest); null when it did not answer.</summary>
    Task<IReadOnlyList<WornItem>?> ListWornAsync(CancellationToken ct);

    /// <summary>A wearable asset's text; null when it could not be fetched.</summary>
    Task<byte[]?> FetchWearableAsync(Guid assetId, bool bodyPart, CancellationToken ct);

    /// <summary>A texture's JPEG 2000 bytes; null when it could not be fetched.</summary>
    Task<byte[]?> FetchTextureAsync(Guid textureId, CancellationToken ct);

    /// <summary>Uploads one finished bake through the region's UploadBakedTexture capability; the empty id when it failed.</summary>
    Task<Guid> UploadBakeAsync(byte[] j2k, CancellationToken ct);
}

/// <summary>The compositing side of a bake run: the library over the character data, or a stand-in in tests.</summary>
internal interface IBakeCompositor
{
    /// <summary>Why the outfit cannot be reproduced faithfully; empty when it can.</summary>
    IReadOnlyList<string> Refusals(IReadOnlyList<ParsedWearable> worn, IReadOnlyList<BakeChannel> channels);

    /// <summary>The texture slots the given channels draw or mask with.</summary>
    IReadOnlyCollection<TextureSlot> SlotsOf(IReadOnlyList<BakeChannel> channels);

    /// <summary>Whether the worn shape is the male one.</summary>
    bool IsMale(IReadOnlyList<ParsedWearable> worn);

    /// <summary>The finished bakes, one per channel the outfit needs.</summary>
    Task<IReadOnlyList<BakeResult>> BakeAsync(BakeRequest request, CancellationToken ct);

    /// <summary>The visual parameter bytes of the wire message, from the worn wearables' stored values.</summary>
    byte[] EncodeVisualParams(IEnumerable<(BakeKind Kind, IReadOnlyDictionary<int, float> Params)> worn);
}

/// <summary>A complete look: every bake uploaded, ready to send.</summary>
internal sealed record PreparedLook(IReadOnlyList<WornItem> Worn, IReadOnlyList<ParsedWearable> Parsed, IReadOnlyDictionary<BakeChannel, Guid> Bakes, byte[] VisualParams, bool Male);

/// <summary>The outcome of one run. Either every bake is made and uploaded (<see cref="Look"/>), or nothing is to be sent and
/// <see cref="Missing"/> says what stopped it.</summary>
internal sealed record BakeRunResult(bool Ok, PreparedLook? Look, string Detail, IReadOnlyList<string> Missing);

/// <summary>
/// The bake run, all or nothing: a look is only ever produced when the four required wearables are present and decoded, the
/// outfit passes the library's fidelity check, every texture a drawn slot needs was fetched and decoded, and every bake was
/// composited and uploaded. Anything less returns the reasons and no look, so a partial look is never sent.
/// </summary>
internal sealed class ClientBakePipeline
{

    private readonly IBakeEnvironment _env;
    private readonly IBakeCompositor _compositor;
    private readonly CoreLog _log;
    private readonly int _bakeSize;
    private readonly int _uploadAttempts;

    public ClientBakePipeline(IBakeEnvironment env, IBakeCompositor compositor, CoreLog log, int bakeSize = 1024, int uploadAttempts = 3)
    {
        _env = env;
        _compositor = compositor;
        _log = log;
        _bakeSize = bakeSize;
        _uploadAttempts = Math.Max(1, uploadAttempts);
    }

    private static BakeRunResult Fail(string detail, params string[] missing) => new(false, null, detail, missing);

    public async Task<BakeRunResult> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BakeKind[] RequiredWearables = [BakeKind.Shape, BakeKind.Skin, BakeKind.Hair, BakeKind.Eyes];
        BakeChannel[] RequiredBakes = [BakeChannel.Head, BakeChannel.Upper, BakeChannel.Lower, BakeChannel.Eyes];

        // 1. the worn wearables, and the four every look needs
        var worn = await _env.ListWornAsync(ct).ConfigureAwait(false);
        if (worn is null || worn.Count == 0)
            return Fail("the region listed no wearables", "wearables");
        var absent = RequiredWearables.Where(k => !worn.Any(w => w.WearableType == (int)k)).Select(k => $"wearable:{k}").ToArray();
        if (absent.Length > 0)
            return Fail("required wearables absent from the list", absent);

        // 2. their assets
        // A wearable that cannot be fetched or read is left out and the look is baked from the rest, as the official viewer does with a wearable
        // it cannot load; each such item is logged once. One of the four every look needs cannot be left out: without it nothing faithful can be drawn.
        var texts = new List<(WornItem Item, string Text, ParsedWearable Parsed)>();
        var missing = new List<string>();
        foreach (var w in worn)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await _env.FetchWearableAsync(w.AssetId, w.WearableType <= (int)BakeKind.Eyes, ct).ConfigureAwait(false);
            string? text = null;
            ParsedWearable? parsedWearable = null;
            if (bytes is { Length: > 0 })
            {
                try
                {
                    text = System.Text.Encoding.UTF8.GetString(bytes);
                    parsedWearable = WearableParser.Parse(text);
                }
                catch (FormatException) { text = null; }
            }
            if (text is not null && parsedWearable is not null)
            {
                texts.Add((w, text, parsedWearable));
                continue;
            }
            if (RequiredWearables.Contains((BakeKind)w.WearableType)) missing.Add($"wearable-asset:{w.WearableType}");
            else _log.Warn("bake.wearable-missing", CoreLog.F("type", w.WearableType), CoreLog.F("asset", w.AssetId));
        }
        if (missing.Count > 0)
            return Fail("one or more wearable assets failed to download or decode", [.. missing]);
        var parsed = texts.Select(t => t.Parsed).ToList();

        // 3. the fidelity check, before any texture is fetched
        var channels = BakeBackend.ChannelsFor(parsed);
        var refusals = _compositor.Refusals(parsed, channels);
        if (refusals.Count > 0)
        {
            _log.Warn("bake.unsupported", CoreLog.F("reasons", string.Join("; ", refusals)));
            return Fail("the outfit cannot be reproduced faithfully", [.. refusals.Select(r => "unsupported:" + r)]);
        }

        // 4. the textures a drawn slot needs
        var inScope = _compositor.SlotsOf(channels).ToHashSet();
        var textures = new Dictionary<LibreMetaverse.UUID, TextureInput>();
        var failed = new HashSet<Guid>();
        foreach (var t in texts)
            foreach (var (slot, id) in t.Parsed.Textures)
            {
                if (id == LibreMetaverse.UUID.Zero || id == BakeConstants.DefaultAvatarTexture || !inScope.Contains(slot) || textures.ContainsKey(id) || failed.Contains(id.Guid))
                    continue;
                ct.ThrowIfCancellationRequested();
                var bytes = await _env.FetchTextureAsync(id.Guid, ct).ConfigureAwait(false);
                if (bytes is not { Length: > 0 })
                {
                    failed.Add(id.Guid);
                    missing.Add($"texture:{slot}");
                    continue;
                }
                textures[id] = new TextureInput(id, bytes);
            }
        if (missing.Count > 0)
            return Fail("one or more wearable textures failed to download", [.. missing]);

        // 5. composite
        var request = new BakeRequest(texts.Select(t => new WearableInput(new LibreMetaverse.UUID(t.Item.AssetId), t.Item.WearableType, t.Text)).ToList(),
            new Dictionary<int, float>(), textures, _bakeSize);
        IReadOnlyList<BakeResult> results;
        try
        {
            results = await _compositor.BakeAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("bake.compose-failed", CoreLog.F("error", ex.GetType().Name));
            return Fail($"compositor: {ex.GetType().Name}", [.. channels.Select(c => $"bake:{c}:compose")]);
        }

        // 6. upload each bake; a channel that drew nothing is not stored (the library says such a bake must not be applied)
        var ids = new Dictionary<BakeChannel, Guid>();
        foreach (var r in results)
        {
            ct.ThrowIfCancellationRequested();
            if (r.Fidelity.Refusals.Count > 0) { missing.Add($"bake:{r.Channel}:unsupported"); continue; }
            if (r.Fidelity.MissingTextures.Count > 0) { missing.Add($"bake:{r.Channel}:textures"); continue; }
            if (r.NothingDrawn) { if (RequiredBakes.Contains(r.Channel)) missing.Add($"bake:{r.Channel}:nothing-drawn"); continue; }
            if (r.J2kBytes is not { Length: > 0 }) { missing.Add($"bake:{r.Channel}:encode"); continue; }
            var id = Guid.Empty;
            for (var attempt = 1; attempt <= _uploadAttempts && id == Guid.Empty; attempt++)
                id = await _env.UploadBakeAsync(r.J2kBytes, ct).ConfigureAwait(false);
            if (id == Guid.Empty) { missing.Add($"bake:{r.Channel}:upload"); continue; }
            ids[r.Channel] = id;
        }
        foreach (var c in RequiredBakes)
            if (!ids.ContainsKey(c) && !missing.Any(m => m.StartsWith($"bake:{c}:", StringComparison.Ordinal)))
                missing.Add($"bake:{c}");
        if (missing.Count > 0)
            return Fail("one or more bakes failed to compose or upload", [.. missing]);

        var visual = _compositor.EncodeVisualParams(parsed.Select(p => (p.Kind, p.Params)));
        _log.Info("bake.made", CoreLog.F("bakes", ids.Count), CoreLog.F("wearables", worn.Count), CoreLog.F("size", _bakeSize));
        return new BakeRunResult(true, new PreparedLook(texts.Select(t => t.Item).ToList(), parsed, ids, visual, _compositor.IsMale(parsed)), "", []);
    }
}

/// <summary>One cache entry of the message: a bake layer's texture index and the hash the region keeps it under.</summary>
internal readonly record struct CacheEntry(byte TextureIndex, Guid CacheId);

/// <summary>The fields of one AgentSetAppearance message, before they are put on the wire.</summary>
internal sealed record SetAppearanceData(uint Serial, byte[] VisualParams, IReadOnlyDictionary<int, Guid> Faces, IReadOnlyList<CacheEntry> Cache, float SizeX, float SizeY, float SizeZ);

internal static class SetAppearanceBuilder
{
    /// <summary>The worn wearables' stored value for a parameter, else the default (the figure the height estimate needs).</summary>
    private static float Param(PreparedLook look, int id, float fallback)
    {
        foreach (var p in look.Parsed)
            if (p.Params.TryGetValue(id, out var v))
                return v;
        return fallback;
    }

    public static SetAppearanceData Build(PreparedLook look, uint serial, float? reportedHeight)
    {
        var faces = new Dictionary<int, Guid>();
        foreach (var (channel, id) in look.Bakes)
            faces[(int)BakeConstants.BakedSlotOf(channel)] = id;

        // the cache hash of each baked layer: every worn wearable of every type that feeds it, combined, then the layer's constant
        var cache = new List<CacheEntry>(LibreMetaverse.AppearanceManager.BAKED_TEXTURE_COUNT);
        for (var layer = 0; layer < LibreMetaverse.AppearanceManager.BAKED_TEXTURE_COUNT; layer++)
        {
            var hash = LibreMetaverse.UUID.Zero;
            for (var k = 0; k < LibreMetaverse.AppearanceManager.WEARABLES_PER_LAYER; k++)
            {
                var type = LibreMetaverse.AppearanceManager.WEARABLE_BAKE_MAP[layer][k];
                if (type == LibreMetaverse.WearableType.Invalid)
                    continue;
                foreach (var w in look.Worn)
                    if (w.WearableType == (int)type)
                        hash ^= new LibreMetaverse.UUID(w.AssetId);
            }
            if (hash != LibreMetaverse.UUID.Zero)
                hash ^= LibreMetaverse.AppearanceManager.BAKED_TEXTURE_HASH[layer];
            cache.Add(new CacheEntry(LibreMetaverse.AppearanceManager.BakeIndexToTextureIndex[layer], hash.Guid));
        }

        // the agent size: a viewer measures its skeleton; when the region has not said what it holds for us, estimate it from the shape
        float height;
        if (reportedHeight is { } z && z > 0.5f && z < 3f)
            height = z;
        else
            height = (float)(1.706 + Param(look, 692, 0) * .1918 + Param(look, 842, 0) * .0375 + Param(look, 33, 0) * .12022 + Param(look, 682, 0.5f) * .01117
                + Param(look, 756, 0) * .038 + Param(look, 198, 0) * .08 + Param(look, 503, 0) * .07);
        return new SetAppearanceData(serial, look.VisualParams, faces, cache, 0.45f, 0.6f, height);
    }
}
