// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
// Acknowledgement: when to bake and send follows the OpenSim-aware Firestorm viewer (llagent.cpp sendAgentSetAppearance sends nothing
// while the region reports a central bake version, and sends the look again on arrival in a region that does not bake;
// llviewertexlayer.cpp uploads each bake through the UploadBakedTexture capability); no viewer code is used here.

using LibreMetaverse;
using LibreMetaverse.Packets;
using OpenSimNGC.Appearance.Baking;
using Viewer.Core.Appearance;
using Viewer.Core.Diagnostics;
using BakeKind = OpenSimNGC.Appearance.Baking.WearableKind;
using TextureSlot = OpenSimNGC.Appearance.Baking.TextureSlot;

namespace Viewer.Core.Protocol;

/// <summary>
/// The protocol side of client-side baking. In a region that does not announce the agent appearance service (and only there) this
/// viewer composites its own look from the worn wearables, uploads each bake and sends one AgentSetAppearance, so that other
/// viewers in the region see the avatar dressed. In a region that bakes on the server nothing here sends or uploads anything.
/// </summary>
internal sealed partial class LmvTransport
{
    private ClientBakeController? _bakeController;
    private IBakeCompositor? _bakeCompositor;
    private string? _characterFolder;
    private PreparedLook? _lastLook;
    private uint _appearanceSerial;
    private readonly object _bakeGate = new();
    private TaskCompletionSource<IReadOnlyList<WornItem>>? _wearablesAnswer;
    private List<WornItem>? _wornOverride;
    private IReadOnlyList<WornItem>? _wornFromRegion;

    /// <summary>The longest the upload capability is waited for after arriving in a region, and how often it is looked for.</summary>
    private static readonly TimeSpan BakeCapabilityWait = TimeSpan.FromSeconds(45), BakeCapabilityEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WearablesWait = TimeSpan.FromSeconds(30), WearablesAsk = TimeSpan.FromSeconds(4);

    /// <summary>Where the character data (avatar_lad.xml and its images) is read from. Without it no look can be composited, so none is baked.</summary>
    internal void UseCharacterFolder(string? folder) => _characterFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;

    private void InitClientBake()
    {
        _bakeController = new ClientBakeController(RunBakeAsync, ResendLook, _log);
        _client.Network.RegisterCallback(PacketType.AgentWearablesUpdate, OnAgentWearablesUpdate);
        _client.Appearance.RebakeAvatarRequested += OnRebakeRequested;
    }

    private void StopClientBake()
    {
        _client.Network.UnregisterCallback(PacketType.AgentWearablesUpdate, OnAgentWearablesUpdate);
        _client.Appearance.RebakeAvatarRequested -= OnRebakeRequested;
        _bakeController?.OnRegion("", serverBaked: true);   // stops a run that has not sent
    }

    /// <summary>The agent is in <paramref name="sim"/>'s region (login, teleport or crossing): choose between the region's bakes and our own.</summary>
    private void ClientBakeRegion(Simulator sim)
    {
        var serverBaked = sim.Protocols.HasFlag(RegionProtocols.AgentAppearanceService);
        _bakeController?.OnRegion(sim.Name, serverBaked);
        if (Environment.GetEnvironmentVariable(BakeDryRun.FolderVariable) is { Length: > 0 } folder && Interlocked.Exchange(ref _dryRunStarted, 1) == 0)
            _ = Task.Run(() => DryBakeToFolderAsync(folder));
    }

    private int _dryRunStarted;

    /// <summary>The development bake (<see cref="BakeDryRun"/>): the worn outfit baked here, each bake written to the folder as a picture; nothing is uploaded or sent.</summary>
    private async Task DryBakeToFolderAsync(string folder)
    {
        try
        {
            if (Compositor() is not { } compositor)
            {
                _log.Info("bake.dryrun", CoreLog.F("state", "no character data"));
                return;
            }
            var bakes = await BakeDryRun.RunAsync(new LmvBakeEnvironment(this), compositor, _log, CancellationToken.None).ConfigureAwait(false);
            if (bakes is null)
            {
                _log.Info("bake.dryrun", CoreLog.F("state", "the outfit could not be baked"));
                return;
            }
            Directory.CreateDirectory(folder);
            foreach (var (channel, j2k) in bakes)
            {
                File.WriteAllBytes(Path.Combine(folder, $"local-{channel}.png"), BakeDryRun.Png(J2kCodec.Decode(j2k)));
                File.WriteAllBytes(Path.Combine(folder, $"local-{channel}.j2c"), j2k);
            }
            _log.Info("bake.dryrun", CoreLog.F("state", "written"), CoreLog.F("bakes", bakes.Count));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Warn("bake.dryrun", CoreLog.F("state", "failed"), CoreLog.F("error", ex.GetType().Name));
        }
    }

    /// <summary>The region asks for a bake it holds for us to be made again.</summary>
    private void OnRebakeRequested(object? sender, RebakeAvatarTexturesEventArgs e) => ClientBakeWornChanged("region request");

    /// <summary>The worn set changed: bake again, but only in a region that wants our bakes.</summary>
    internal void ClientBakeWornChanged(string why) => _bakeController?.RequestBake(why);

    /// <summary>The wearables as the worn set (from the current outfit folder) after an outfit change, once the folder holds the change. A region that
    /// does not bake keeps its own list from the legacy message, so it is told too (the official viewers do the same for such regions), and the look is
    /// baked again from the new set.</summary>
    internal void ClientBakeOutfitChanged(UUID cof)
    {
        if (_bakeController is not { Mode: Viewer.Core.World.AppearanceMode.Client } controller)
            return;
        var worn = WornFromOutfitFolder(cof);
        if (worn.Count == 0)
        {
            _log.Info("bake.outfit-unchanged", CoreLog.F("why", "the current outfit folder names no wearables"));
            return;
        }
        lock (_bakeGate) _wornOverride = worn;
        var now = new AgentIsNowWearingPacket();
        now.AgentData.AgentID = _client.Self.AgentID;
        now.AgentData.SessionID = _client.Self.SessionID;
        now.WearableData = worn.Select(w => new AgentIsNowWearingPacket.WearableDataBlock { ItemID = new UUID(w.ItemId), WearableType = (byte)w.WearableType }).ToArray();
        _client.Network.SendPacket(now);
        controller.RequestBake("outfit");
    }

    /// <summary>The wearable items the current outfit folder links to, with the asset and type of each.</summary>
    private List<WornItem> WornFromOutfitFolder(UUID cof)
    {
        var worn = new List<WornItem>();
        if (_client.Inventory.Store is not { } store)
            return worn;
        foreach (var link in store.GetContents(cof).OfType<InventoryItem>())
            if (link.AssetType == AssetType.Link && Stored(link.AssetUUID.Guid) is InventoryItem item && item.InventoryType == InventoryType.Wearable && item.AssetUUID != UUID.Zero)
                worn.Add(new WornItem(item.UUID.Guid, item.AssetUUID.Guid, (int)(item.Flags & 0xFF)));
        return worn;
    }

    private IBakeCompositor? Compositor()
    {
        if (_bakeCompositor is not null)
            return _bakeCompositor;
        if (_characterFolder is not { } folder)
            return null;
        try
        {
            _bakeCompositor = LibraryCompositor.FromFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException)
        {
            _log.Warn("bake.character-data", CoreLog.F("state", "unreadable"), CoreLog.F("error", ex.GetType().Name));
        }
        return _bakeCompositor;
    }

    private async Task RunBakeAsync(string why, CancellationToken ct)
    {
        var controller = _bakeController;
        if (controller is null || _disposed)
            return;
        var compositor = Compositor();
        if (compositor is null)
        {
            _log.Info("bake.skipped", CoreLog.F("why", "no character data (the character folder is not set or not readable)"));
            return;
        }
        _log.Info("bake.start", CoreLog.F("why", why));
        var pipeline = new ClientBakePipeline(new LmvBakeEnvironment(this), compositor, _log);
        var result = await pipeline.RunAsync(ct).ConfigureAwait(false);
        if (!result.Ok || result.Look is not { } look)
        {
            _log.Warn("bake.not-sent", CoreLog.F("detail", result.Detail), CoreLog.F("missing", string.Join(",", result.Missing)));
            return;
        }
        controller.TrySend(ct, () => SendLook(look));
    }

    private bool ResendLook()
    {
        var look = _lastLook;
        return look is not null && _bakeController!.TrySend(CancellationToken.None, () => SendLook(look));
    }

    /// <summary>Puts one AgentSetAppearance on the wire for <paramref name="look"/>. Only called through the controller's TrySend.</summary>
    private bool SendLook(PreparedLook look)
    {
        if (_disposed || !_client.Network.Connected)
            return false;
        _lastLook = look;
        var m = SetAppearanceBuilder.Build(look, ++_appearanceSerial, SelfReportedHeight());
        var set = new AgentSetAppearancePacket();
        set.AgentData.AgentID = _client.Self.AgentID;
        set.AgentData.SessionID = _client.Self.SessionID;
        set.AgentData.SerialNum = m.Serial;
        set.AgentData.Size = new LibreMetaverse.Vector3(m.SizeX, m.SizeY, m.SizeZ);
        set.VisualParam = m.VisualParams.Select(b => new AgentSetAppearancePacket.VisualParamBlock { ParamValue = b }).ToArray();
        var te = new Primitive.TextureEntry(AppearanceManager.DEFAULT_AVATAR_TEXTURE);
        foreach (var (face, id) in m.Faces)
            te.CreateFace((uint)face).TextureID = new UUID(id);
        set.ObjectData.TextureEntry = te.GetBytes();
        set.WearableData = m.Cache.Select(c => new AgentSetAppearancePacket.WearableDataBlock { TextureIndex = c.TextureIndex, CacheID = new UUID(c.CacheId) }).ToArray();
        _client.Network.SendPacket(set);
        _log.Info("bake.sent", CoreLog.F("serial", m.Serial), CoreLog.F("bakes", m.Faces.Count), CoreLog.F("params", m.VisualParams.Length));
        return true;
    }

    /// <summary>The height the region reports for our own avatar (its object's Z scale), when it has told us.</summary>
    private float? SelfReportedHeight()
    {
        try
        {
            var self = _client.Network.CurrentSim?.ObjectsAvatars.TryGetValue(_client.Self.LocalID, out var a) == true ? a : null;
            var z = self?.Scale.Z;
            return z is > 0.5f and < 3f ? z : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private void OnAgentWearablesUpdate(object? sender, PacketReceivedEventArgs e)
    {
        if (e.Packet is not AgentWearablesUpdatePacket p)
            return;
        var list = new List<WornItem>();
        foreach (var w in p.WearableData)
            if (w.AssetID != UUID.Zero && w.ItemID != UUID.Zero)
                list.Add(new WornItem(w.ItemID.Guid, w.AssetID.Guid, w.WearableType));
        TaskCompletionSource<IReadOnlyList<WornItem>>? tcs;
        lock (_bakeGate)
        {
            _wornFromRegion = list;   // kept: the region may send the list on its own before the first bake asks for it
            tcs = _wearablesAnswer;
        }
        tcs?.TrySetResult(list);
    }

    /// <summary>What a bake run asks of the session, over the grid client.</summary>
    private sealed class LmvBakeEnvironment(LmvTransport t) : IBakeEnvironment
    {
        public async Task<IReadOnlyList<WornItem>?> ListWornAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<WornItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (t._bakeGate)
            {
                if (t._wornOverride is { } known)
                    return known;
                if (t._wornFromRegion is { } have)
                    return have;
                t._wearablesAnswer = tcs;
            }
            try
            {
                // a request sent before the region has made the avatar present can go unanswered: ask again every few seconds
                var end = DateTime.UtcNow + WearablesWait;
                while (DateTime.UtcNow < end)
                {
                    var req = new AgentWearablesRequestPacket();
                    req.AgentData.AgentID = t._client.Self.AgentID;
                    req.AgentData.SessionID = t._client.Self.SessionID;
                    t._client.Network.SendPacket(req);
                    try { return await tcs.Task.WaitAsync(WearablesAsk, ct).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                return null;
            }
            finally
            {
                lock (t._bakeGate) t._wearablesAnswer = null;
            }
        }

        public async Task<byte[]?> FetchWearableAsync(Guid assetId, bool bodyPart, CancellationToken ct)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(UploadWait);
                var asset = await t._client.Assets.RequestAssetAsync(new UUID(assetId), bodyPart ? AssetType.Bodypart : AssetType.Clothing, true, limit.Token).ConfigureAwait(false);
                return asset?.AssetData is { Length: > 0 } b ? b : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                t._log.Warn("bake.wearable-fetch-failed", CoreLog.F("error", ex.GetType().Name));
                return null;
            }
        }

        public async Task<byte[]?> FetchTextureAsync(Guid textureId, CancellationToken ct)
        {
            var r = await t.FetchTextureAsync(textureId, ct).ConfigureAwait(false);
            return r.Status == Viewer.Core.Assets.AssetFetchStatus.Ok && !r.Bytes.IsEmpty ? r.Bytes.ToArray() : null;
        }

        public async Task<Guid> UploadBakeAsync(byte[] j2k, CancellationToken ct)
        {
            // the region's capabilities come up a little after the arrival; wait for the upload address
            var waited = TimeSpan.Zero;
            while (t._client.Network.CurrentSim?.Caps?.CapabilityURI("UploadBakedTexture") is null)
            {
                if (waited >= BakeCapabilityWait || t._disposed)
                    return Guid.Empty;
                await Task.Delay(BakeCapabilityEvery, ct).ConfigureAwait(false);
                waited += BakeCapabilityEvery;
            }
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(UploadWait);
                return (await t._client.Assets.RequestUploadBakedTextureAsync(j2k, limit.Token).ConfigureAwait(false)).Guid;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                t._log.Warn("bake.upload-failed", CoreLog.F("error", ex.GetType().Name));
                return Guid.Empty;
            }
        }
    }
}

/// <summary>The compositor over the character folder: the library's backend with its character data read from disk.</summary>
internal sealed class LibraryCompositor : IBakeCompositor
{
    private readonly AvatarLad _lad;
    private readonly AvatarBakeCompositor _compositor;
    private readonly BakeBackend _backend;

    private LibraryCompositor(AvatarLad lad, AvatarBakeCompositor compositor)
    {
        _lad = lad;
        _compositor = compositor;
        _backend = new BakeBackend(compositor);
    }

    /// <summary>Reads avatar_lad.xml and the character images from the folder (the official viewer's character folder, read in place).</summary>
    public static LibraryCompositor FromFolder(string folder)
    {
        var lad = AvatarLad.Load(Path.Combine(folder, "avatar_lad.xml"));
        return new LibraryCompositor(lad, new AvatarBakeCompositor(lad, BakeResourceFiles.FromDirectory(folder)));
    }

    public IReadOnlyList<string> Refusals(IReadOnlyList<ParsedWearable> worn, IReadOnlyList<BakeChannel> channels) =>
        FidelityCheck.Check(worn.Select((w, i) => new FidelityCheck.WornSummary(w.Kind, $"{w.Kind} {i}", w.Textures)).ToList(), _compositor, channels);

    public IReadOnlyCollection<TextureSlot> SlotsOf(IReadOnlyList<BakeChannel> channels) => channels.SelectMany(_compositor.SlotsOf).ToHashSet();

    public bool IsMale(IReadOnlyList<ParsedWearable> worn) => _backend.IsMale(worn);

    public Task<IReadOnlyList<BakeResult>> BakeAsync(BakeRequest request, CancellationToken ct) => _backend.BakeAsync(request, ct);

    public byte[] EncodeVisualParams(IEnumerable<(BakeKind Kind, IReadOnlyDictionary<int, float> Params)> worn) =>
        VisualParamEncoder.Encode(_lad, worn, null).Bytes;
}

/// <summary>
/// A development aid: bakes the worn outfit here and keeps the finished bakes, uploading nothing and sending no appearance. The bakes a region's server made
/// for the same outfit can then be compared with these, texel for texel. It runs only when a folder is named in <see cref="FolderVariable"/>.
/// </summary>
internal static class BakeDryRun
{
    /// <summary>The environment variable that names the folder the finished bakes are written to (one picture per channel). Unset, nothing is baked.</summary>
    public const string FolderVariable = "NV_BAKE_DRYRUN_DIR";

    /// <summary>The pipeline's environment with the upload put aside: the bake's bytes are kept under the id handed back, and nothing leaves the machine.</summary>
    private sealed class Keeping(IBakeEnvironment inner) : IBakeEnvironment
    {
        public readonly Dictionary<Guid, byte[]> Kept = [];
        public Task<IReadOnlyList<WornItem>?> ListWornAsync(CancellationToken ct) => inner.ListWornAsync(ct);
        public Task<byte[]?> FetchWearableAsync(Guid assetId, bool bodyPart, CancellationToken ct) => inner.FetchWearableAsync(assetId, bodyPart, ct);
        public Task<byte[]?> FetchTextureAsync(Guid textureId, CancellationToken ct) => inner.FetchTextureAsync(textureId, ct);
        public Task<Guid> UploadBakeAsync(byte[] j2k, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            Kept[id] = j2k;
            return Task.FromResult(id);
        }
    }

    /// <summary>Runs the bake as a real one would and returns each channel's JPEG 2000 bytes; null when the outfit cannot be baked here.</summary>
    public static async Task<IReadOnlyDictionary<BakeChannel, byte[]>?> RunAsync(IBakeEnvironment env, IBakeCompositor compositor, CoreLog log, CancellationToken ct)
    {
        var keeping = new Keeping(env);
        var result = await new ClientBakePipeline(keeping, compositor, log).RunAsync(ct).ConfigureAwait(false);
        if (!result.Ok || result.Look is not { } look)
            return null;
        return look.Bakes.ToDictionary(b => b.Key, b => keeping.Kept[b.Value]);
    }

    /// <summary>The picture as a PNG file: 8-bit RGBA, one filter-free row after another.</summary>
    public static byte[] Png(RgbaPlanes planes)
    {
        var raw = new byte[planes.H * (planes.W * 4 + 1)];
        for (int y = 0; y < planes.H; y++)
        {
            int o = y * (planes.W * 4 + 1);
            for (int x = 0; x < planes.W; x++)
            {
                int i = y * planes.W + x;
                raw[o + 1 + x * 4] = planes.R[i]; raw[o + 2 + x * 4] = planes.G[i]; raw[o + 3 + x * 4] = planes.B[i]; raw[o + 4 + x * 4] = planes.A[i];
            }
        }
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteInt(header, 0, planes.W); WriteInt(header, 4, planes.H);
        header[8] = 8; header[9] = 6;   // 8 bits, RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteInt(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        WriteInt(head, 0, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(head, 4);
        s.Write(head);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in head.Skip(4).Concat(data))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        var tail = new byte[4];
        WriteInt(tail, 0, unchecked((int)~crc));
        s.Write(tail);
    }
}
