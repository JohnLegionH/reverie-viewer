// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;

namespace Viewer.Core.World;

/// <summary>
/// How the region textures avatars (ADR-009 in the gateway): Server when its RegionHandshake sets RegionProtocols bit 0
/// (the viewer's own test, llviewerregion.cpp: mCentralBakeVersion = region_protocols &amp; 1), else Client. The core is
/// appearance-passive in BOTH: it records what the simulator sends and never bakes or sends. In a
/// client-bake region nobody textures this session's own avatar; that is a later, explicit decision, not a default.
/// </summary>
public enum AppearanceMode { Unknown, Server, Client }

/// <summary>
/// One avatar's visual parameters exactly as received: the raw bytes in wire order plus the wire order itself (the
/// parameter id at each position). LibreMetaverse 3.1.5+ (we pin 3.1.6) puts them on the wire in ASCENDING
/// parameter id over its group-0 set; the adapter supplies that order from the library, so no index is copied from
/// the gateway's 3.1.4-era LAD lookup. Immutable; nothing here aliases the packet's buffer.
/// </summary>
public sealed class VisualParamSet
{
    /// <summary>The shape's "male" parameter; the body is chosen by it (byte &gt; 127 is male).</summary>
    public const int MaleParamId = 80;

    public VisualParamSet(IEnumerable<byte> raw, ImmutableArray<int> wireOrder)
    {
        Raw = raw.ToImmutableArray();
        WireOrder = wireOrder.IsDefault ? ImmutableArray<int>.Empty : wireOrder;
    }

    public static VisualParamSet Empty { get; } = new([], ImmutableArray<int>.Empty);

    public ImmutableArray<byte> Raw { get; }

    /// <summary>The parameter id at each wire position.</summary>
    public ImmutableArray<int> WireOrder { get; }

    public int Count => Raw.Length;

    /// <summary>The received byte for parameter <paramref name="paramId"/>; false if the blob does not carry it.</summary>
    public bool TryGet(int paramId, out byte value)
    {
        int i = WireOrder.IndexOf(paramId);
        if (i >= 0 && i < Raw.Length)
        {
            value = Raw[i];
            return true;
        }
        value = 0;
        return false;
    }

    public string Sex => TryGet(MaleParamId, out var b) ? (b > 127 ? "male" : "female") : "unknown";

    public bool SameAs(VisualParamSet other) => Raw.SequenceEqual(other.Raw) && WireOrder.SequenceEqual(other.WireOrder);
}

/// <summary>An AvatarAppearance as the protocol adapter reports it (no LibreMetaverse type).</summary>
/// <param name="FaceTextures">Texture id per TextureEntry face index as sent; Guid.Empty where the face is unset or the library default.</param>
/// <param name="AppearanceVersion">0 when the packet carried no AppearanceData block (the library leaves it 0).</param>
public sealed record AvatarAppearanceData(
    Guid AgentId,
    ImmutableArray<Guid> FaceTextures,
    VisualParamSet VisualParams,
    int AppearanceVersion,
    int CofVersion);

/// <summary>An avatar's appearance in the world state. <see cref="Bakes"/> is by slot name (the viewer's eleven).</summary>
public sealed record AvatarAppearance(
    Guid AgentId,
    bool IsSelf,
    IReadOnlyDictionary<string, Guid> Bakes,
    ImmutableArray<Guid> FaceTextures,
    VisualParamSet VisualParams,
    int AppearanceVersion,
    int CofVersion,
    float? Hover = null)
{
    public string Sex => VisualParams.Sex;

    internal bool SameAs(AvatarAppearance o) =>
        AgentId == o.AgentId && IsSelf == o.IsSelf && AppearanceVersion == o.AppearanceVersion && CofVersion == o.CofVersion
        && FaceTextures.SequenceEqual(o.FaceTextures) && VisualParams.SameAs(o.VisualParams)
        && Hover == o.Hover && Bakes.Count == o.Bakes.Count && Bakes.All(kv => o.Bakes.TryGetValue(kv.Key, out var v) && v == kv.Value);
}

/// <summary>
/// The bake slots (from the gateway's AppearanceMapping / AppearanceSlots): the viewer's shared bake-type numbering, the
/// TextureEntry face each bake arrives on, and its name (llavatarappearancedefines.cpp:81-91). Pure functions, no state.
/// </summary>
public static class AppearanceSlots
{
    public const int Count = 11;

    /// <summary>The slot name for a bake type, or null for one the viewer does not draw.</summary>
    public static string? SlotOf(int bakeType) => bakeType switch
    {
        0 => "head",
        1 => "upper",
        2 => "lower",
        3 => "eyes",
        4 => "skirt",
        5 => "hair",
        6 => "leftarm",
        7 => "leftleg",
        8 => "aux1",
        9 => "aux2",
        10 => "aux3",
        _ => null,
    };

    /// <summary>The TextureEntry face a bake type arrives on, or -1.</summary>
    public static int FaceOf(int bakeType) => bakeType switch
    {
        0 => 8,
        1 => 9,
        2 => 10,
        3 => 11,
        4 => 19,
        5 => 20,
        6 => 40,
        7 => 41,
        8 => 42,
        9 => 43,
        10 => 44,
        _ => -1,
    };

    /// <summary>The viewer's default texture and the default avatar texture (IMG_DEFAULT and IMG_DEFAULT_AVATAR, indra_constants.cpp:43, 51): ids
    /// the simulator may send on a bake face before the avatar is baked; they name no bake.</summary>
    public static readonly Guid ImgDefault = Guid.Parse("d2114404-dd59-4a4d-8e6c-49359e91bbf0"), ImgDefaultAvatar = Guid.Parse("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");

    /// <summary>Whether a bake face's texture id is a baked texture (the official viewer's isTextureDefined, llvoavatar.cpp:11727: not the two default ids; and not no id).</summary>
    public static bool IsDefinedBake(Guid id) => id != Guid.Empty && id != ImgDefault && id != ImgDefaultAvatar;

    /// <summary>The body slots whose last valid bake an appearance that leaves them undefined does not overwrite (llvoavatar.cpp:9804-9806: every slot but the
    /// skirt, left arm, left leg and the three extra slots).</summary>
    public static readonly IReadOnlySet<string> KeptSlots = new HashSet<string> { "head", "upper", "lower", "eyes", "hair" };

    /// <summary>The baked texture per slot for an appearance's faces; unset faces and the default texture ids are absent.</summary>
    public static IReadOnlyDictionary<string, Guid> BakesOf(ImmutableArray<Guid> faces)
    {
        var bakes = new Dictionary<string, Guid>();
        for (int bt = 0; bt < Count; bt++)
        {
            int face = FaceOf(bt);
            if (face < faces.Length && IsDefinedBake(faces[face]))
                bakes[SlotOf(bt)!] = faces[face];
        }
        return bakes;
    }

    /// <summary>
    /// Whether an AvatarAppearance for our own avatar is refused: in server mode, version 0 means the packet carried
    /// no AppearanceData block, so the region never claimed it as its bake. In client mode it is the ordinary shape.
    /// </summary>
    public static bool RejectsSelfAppearance(AppearanceMode mode, int appearanceVersion) =>
        mode == AppearanceMode.Server && appearanceVersion == 0;

    /// <summary>
    /// The appearance-service URL for a baked texture id, or null when there is no advertised service or no look held
    /// names that id. The viewer's own form (llvoavatar.cpp getImageURL): service + "texture/" + agent + "/" + slot NAME
    /// + "/" + id. Resolves any avatar's bake, not only our own.
    /// </summary>
    public static string? BakedTextureUrl(string? service, IEnumerable<AvatarAppearance> looks, Guid id)
    {
        if (string.IsNullOrEmpty(service))
            return null;
        foreach (var look in looks)
            foreach (var (slot, texture) in look.Bakes)
                if (texture == id)
                    return BakeAddress(service, look.AgentId, slot, id);
        return null;
    }

    /// <summary>The appearance service's address for one bake (llvoavatar.cpp:5889-5908): service + "texture/" + avatar id + "/" + slot name + "/" + bake id.</summary>
    public static string BakeAddress(string service, Guid agent, string slot, Guid id) => $"{service}texture/{agent:D}/{slot}/{id:D}";
}

/// <summary>
/// This session's own appearance as the simulator stores it (from the gateway's SelfAppearanceState, receive half
/// only: the bake echo belongs to a bake pipeline the core does not have). Per session; survives region changes.
/// </summary>
public sealed class SelfAppearanceState
{
    private readonly object _gate = new();
    private byte[]? _simParams;
    private IReadOnlyDictionary<string, Guid> _serverBakes = new Dictionary<string, Guid>();

    /// <summary>The sim's stored visual-parameter blob for us (sent at login). An empty blob is not a blob. Copied, never aliased.</summary>
    public void RecordSimParams(IReadOnlyList<byte> blob)
    {
        if (blob is not { Count: > 0 })
            return;
        lock (_gate) _simParams = blob.ToArray();
    }

    /// <summary>A copy of the most recent stored blob, or null before the sim sent one.</summary>
    public byte[]? SimParams { get { lock (_gate) return _simParams?.ToArray(); } }

    /// <summary>The simulator's bakes for our own avatar, by slot (server mode: these ARE our textures).</summary>
    public IReadOnlyDictionary<string, Guid> ServerBakes { get { lock (_gate) return _serverBakes; } }

    public void RecordServerBakes(IReadOnlyDictionary<string, Guid> bakes)
    {
        lock (_gate) _serverBakes = new Dictionary<string, Guid>(bakes);
    }
}
