// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Immutable;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Viewer.Core.Session;
using Viewer.Core.World;

namespace Viewer.Core.Replay;

/// <summary>The first line of a capture.</summary>
/// <param name="SelfAgent">The session's own agent, as its fixture alias.</param>
public sealed record CaptureHeader(int Version, string Tag, Guid SelfAgent, int LoginSizeX, int LoginSizeY, string Library);

/// <summary>One recorded transport event: arrival time (ms since capture start) and the adapter's current-simulator view at that instant.</summary>
/// <param name="CurrentSim">Handle of the simulator the library considered current when the event arrived (null: none yet, the W-21 race).</param>
public sealed record CaptureRecord(double T, ulong? CurrentSim, int ConnectedSims, TransportEvent Event);

/// <summary>The live session's final state, recorded at the instant recording stopped (the replay oracle).</summary>
public sealed record CaptureFooter(StateSummary Live);

public sealed record Capture(CaptureHeader Header, IReadOnlyList<CaptureRecord> Records, CaptureFooter? Footer);

/// <summary>
/// The capture file (the test strategy section 4.1): gzipped JSON lines, header / records / footer. It holds the core's
/// post-adapter <see cref="TransportEvent"/> stream, never raw packets, and is SCRUBBED AT WRITE TIME (ADR-019):
///   - the event types carry no session id, capability URL, token, digest or password (the login result is not recorded);
///   - login progress messages and disconnect texts are dropped (message bodies);
///   - agent ids become fixture aliases (00000000-0000-0000-0000-00000000000N, self = 1) and avatar names "Avatar N".
/// Object, texture and terrain data remain: a capture is grid content, so it lives only under fixtures-local\ and is never
/// committed (ADR-011). <see cref="ScrubScan"/> checks a written file.
/// </summary>
public static class CaptureFile
{
    public const int FormatVersion = 1;

    // ---------------- aliasing ----------------

    /// <summary>Agent-id and name aliases for one capture. Self is alias 1.</summary>
    public sealed class Aliases
    {
        private readonly Dictionary<Guid, Guid> _ids = new();
        private readonly HashSet<string> _names = new();
        private readonly object _gate = new();

        public Aliases(Guid self) => Id(self);

        public Guid Id(Guid real)
        {
            if (real == Guid.Empty)
                return real;
            lock (_gate)
            {
                if (!_ids.TryGetValue(real, out var alias))
                    _ids[real] = alias = AliasGuid(_ids.Count + 1);
                return alias;
            }
        }

        public string Name(Guid real) => "Avatar " + Number(Id(real));

        /// <summary>Remember a real avatar name so the scrub scan can prove it was not written.</summary>
        public void Saw(string realName)
        {
            if (!string.IsNullOrWhiteSpace(realName))
                lock (_gate) _names.Add(realName.Trim());
        }

        public IReadOnlyList<string> RealNames { get { lock (_gate) return _names.ToList(); } }

        /// <summary>The real agent ids seen (for the scrub scan; never written).</summary>
        public IReadOnlyList<Guid> RealIds { get { lock (_gate) return _ids.Keys.ToList(); } }

        public static Guid AliasGuid(int n) => new($"00000000-0000-0000-0000-{n:D12}");

        private static int Number(Guid alias) => int.Parse(alias.ToString()[24..]);
    }

    /// <summary>The event with every agent id and avatar name replaced by its alias, and message texts dropped.</summary>
    /// <summary>Whether an event is written to a capture at all. Communication (chat, instant messages, names) never is:
    /// it is what people wrote and who they are, and nothing in a replay needs it.</summary>
    public static bool IsRecorded(TransportEvent e) => e is not (ChatReceived or InstantMessageReceived or AgentNamesReceived
        or FriendsListed or FriendPresenceChanged or FriendRightsChanged or FriendshipOffered or FriendshipAnswered or FriendshipEnded
        or GroupsListed or ActiveGroupChanged or GroupTitlesListed or GroupChatJoined or GroupNoticeReceived
        or TeleportProgressed or TeleportOffered or TeleportRequested or ServerAlert or ParcelCollision or CoarseLocationsUpdated or FriendLocated
        or Viewer.Core.Inventory.InventorySkeletonReceived or Viewer.Core.Inventory.InventoryFolderFetched or Viewer.Core.Inventory.InventoryChanged or Viewer.Core.Inventory.OutfitCommandFinished or Viewer.Core.Inventory.LookRebuildFinished
        or SitResponded or ObjectPropertiesReceived or ParcelDetailsReceived or AgentParcelChanged or ParcelMusicReceived
        or ParcelMediaUpdated or ParcelMediaCommanded or ObjectMediaReceived
        or TaskInventoryReceived or ContentsCopied or PayPriceReceived
        or ScriptDialogReceived or ScriptPermissionRequested or LoadUrlRequested or MapDestinationRequested or ObjectInstantMessage or InventoryOffered);

    public static TransportEvent Scrub(TransportEvent e, Aliases a) => e switch
    {
        LoginProgressed p => p with { Message = null },
        Disconnected => new Disconnected("scrubbed"),
        ObjectUpdated o => o with { SimParentAvatar = o.SimParentAvatar is null ? null : ScrubAvatar(o.SimParentAvatar, a),
            Prim = (o.Prim with { MediaVersion = "", MediaFaces = 0 }) is var plain && o.Prim.Interaction is { HoverText: not null } hi ? plain with { Interaction = hi with { HoverText = null } } : plain },   // floating text is what people wrote: dropped
        AvatarUpdated u => u with { Avatar = ScrubAvatar(u.Avatar, a) },
        AvatarAppearanceReceived r => r with { Appearance = r.Appearance with { AgentId = a.Id(r.Appearance.AgentId) } },
        AvatarAnimationsReceived an => an with { AgentId = a.Id(an.AgentId) },
        AvatarHoverReceived hv => hv with { AgentId = a.Id(hv.AgentId) },
        _ => e,
    };

    private static AvatarData ScrubAvatar(AvatarData d, Aliases a)
    {
        a.Saw(d.Name);
        return d with { AgentId = a.Id(d.AgentId), Name = a.Name(d.AgentId) };
    }

    // ---------------- writing ----------------

    public sealed class Writer : IDisposable
    {
        private readonly Stream _file;
        private readonly GZipStream _gz;
        private readonly object _gate = new();
        private bool _closed;

        public Writer(string path, CaptureHeader header)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _file = File.Create(path);
            _gz = new GZipStream(_file, CompressionLevel.Optimal);
            Line(HeaderJson(header));
        }

        public void Write(CaptureRecord r) => Line(RecordJson(r));

        /// <summary>The login reply's part a replay needs (self alias, login region size), written when login returns; events
        /// raised during login may precede it, which is why it is a line of its own rather than part of the header.</summary>
        public void WriteLoginReply(Guid selfAlias, int loginSizeX, int loginSizeY) =>
            Line(new JsonObject { ["kind"] = "loginReply", ["self"] = selfAlias.ToString(), ["loginSizeX"] = loginSizeX, ["loginSizeY"] = loginSizeY });

        public void WriteFooter(CaptureFooter f) => Line(new JsonObject { ["kind"] = "footer", ["live"] = SummaryJson(f.Live) });

        private void Line(JsonObject o)
        {
            var bytes = Encoding.UTF8.GetBytes(o.ToJsonString() + "\n");
            lock (_gate)
            {
                if (!_closed)
                    _gz.Write(bytes);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed)
                    return;
                _closed = true;
                _gz.Dispose();
                _file.Dispose();
            }
        }
    }

    public static string ReadText(string path)
    {
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static Capture Read(string path) => Parse(ReadText(path));

    public static Capture Parse(string text)
    {
        CaptureHeader? header = null;
        CaptureFooter? footer = null;
        (Guid Self, int X, int Y)? loginReply = null;
        var records = new List<CaptureRecord>();
        var sims = new Dictionary<ulong, SimRef>();   // one SimRef instance per simulator, as the adapter gives
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var o = JsonNode.Parse(line)!.AsObject();
            switch ((string?)o["kind"])
            {
                case "header":
                    header = new((int)o["version"]!, (string)o["tag"]!, Guid.Parse((string)o["self"]!), (int)o["loginSizeX"]!, (int)o["loginSizeY"]!, (string)o["library"]!);
                    break;
                case "loginReply":
                    loginReply = (Guid.Parse((string)o["self"]!), (int)o["loginSizeX"]!, (int)o["loginSizeY"]!);
                    break;
                case "footer":
                    footer = new(SummaryFrom(o["live"]!.AsObject()));
                    break;
                default:
                    records.Add(new((double)o["t"]!, o["cur"] is null ? null : (ulong)o["cur"]!, (int)o["n"]!, EventFrom(o, sims)));
                    break;
            }
        }
        if (header is null || header.Version != FormatVersion)
            throw new InvalidDataException("not a version " + FormatVersion + " capture");
        if (loginReply is { } lr)
            header = header with { SelfAgent = lr.Self, LoginSizeX = lr.X, LoginSizeY = lr.Y };
        return new(header, records, footer);
    }

    // ---------------- JSON ----------------

    private static JsonObject HeaderJson(CaptureHeader h) => new()
    {
        ["kind"] = "header", ["version"] = h.Version, ["tag"] = h.Tag, ["self"] = h.SelfAgent.ToString(),
        ["loginSizeX"] = h.LoginSizeX, ["loginSizeY"] = h.LoginSizeY, ["library"] = h.Library,
    };

    private static JsonArray V(Vector3 v) => new(v.X, v.Y, v.Z);
    private static JsonArray Q(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    private static Vector3 V(JsonNode? n) => n is JsonArray a ? new((float)a[0]!, (float)a[1]!, (float)a[2]!) : default;
    private static Quaternion Q(JsonNode? n) => n is JsonArray a ? new((float)a[0]!, (float)a[1]!, (float)a[2]!, (float)a[3]!) : Quaternion.Identity;
    private static JsonObject? Sim(SimRef? s) => s is null ? null : new JsonObject { ["h"] = s.Handle, ["name"] = s.Name };

    private static SimRef? Sim(JsonNode? n, Dictionary<ulong, SimRef> sims)
    {
        if (n is not JsonObject o)
            return null;
        ulong h = (ulong)o["h"]!;
        if (!sims.TryGetValue(h, out var s))
            sims[h] = s = new SimRef(h, (string)o["name"]!);
        return s;
    }

    /// <summary>Test hook: a prim written to its capture form and read back.</summary>
    internal static PrimData RoundTripPrim(PrimData p) => Prim(Prim(p));

    private static JsonObject Prim(PrimData p) => new JsonObject
    {
        ["local"] = p.LocalId, ["full"] = p.FullId.ToString(), ["parent"] = p.ParentLocalId, ["point"] = p.AttachmentPoint,
        ["pos"] = V(p.Position), ["rot"] = Q(p.Rotation), ["scale"] = V(p.Scale), ["vel"] = V(p.Velocity), ["ang"] = V(p.AngularVelocity),
        ["acc"] = V(p.Acceleration),
        ["anim"] = p.TextureAnim is { } t ? new JsonArray(t.Face, t.Flags, t.SizeX, t.SizeY, t.Start, t.Length, t.Rate) : null,
        // the shape block and the legacy face colours.
        ["shape"] = p.Shape is { } sh ? WithFlags(sh, new JsonArray(sh.ProfileCurve, sh.ProfileHole, sh.ProfileBegin, sh.ProfileEnd, sh.ProfileHollow, sh.PathCurve,
            sh.PathBegin, sh.PathEnd, sh.PathScaleX, sh.PathScaleY, sh.PathShearX, sh.PathShearY, sh.PathTwist, sh.PathTwistBegin, sh.PathRadiusOffset,
            sh.PathTaperX, sh.PathTaperY, sh.PathRevolutions, sh.PathSkew, sh.SculptType, sh.SculptId.ToString())) : null,
        // per-face texture entries.
        ["tex"] = p.Textures is { } ft ? new JsonArray(new[] { (FaceTexture?)ft.Default }.Concat(ft.Overrides).Select(t => t is { } x
            ? (JsonNode)new JsonArray(x.TextureId.ToString(), x.RepeatU, x.RepeatV, x.OffsetU, x.OffsetV, x.Rotation, (int)x.TexGen, x.Fullbright, x.Glow) : null).ToArray()) : null,
        ["colors"] = p.Colors is { } fc ? new JsonArray(new[] { (Rgba?)fc.Default }.Concat(fc.Overrides).Select(c => c is { } x ? (JsonNode)new JsonArray(x.R, x.G, x.B, x.A) : null).ToArray()) : null,
    }.WithMaterials(p.Materials).WithLight(p.Light, p.Projector);

    /// <summary>Per-face material references, written only when the prim has any, so earlier captures and their hashes are unchanged.</summary>
    private static JsonObject WithMaterials(this JsonObject o, FaceMaterials? m)
    {
        if (m is not null)
            o["mat"] = new JsonArray(m.All.Select(f => f is { } x
                ? (JsonNode)new JsonArray(x.Bump, x.Shiny, x.MaterialId.ToString(), x.RenderMaterialId.ToString()) : null).ToArray());
        return o;
    }

    /// <summary>A prim's light and projector, written only when it has them, so earlier captures and their hashes are unchanged.</summary>
    private static JsonObject WithLight(this JsonObject o, PrimLight? l, PrimProjector? p)
    {
        if (l is { } x) o["light"] = new JsonArray(x.Color.X, x.Color.Y, x.Color.Z, x.Intensity, x.Radius, x.Falloff, x.Cutoff);
        if (l is not null && p is { } y) o["proj"] = new JsonArray(y.Texture.ToString(), y.Fov, y.Focus, y.Ambiance);
        return o;
    }

    private static PrimLight? Light(JsonNode? n) => n is JsonArray a
        ? new PrimLight(new Vector3((float)a[0]!, (float)a[1]!, (float)a[2]!), (float)a[3]!, (float)a[4]!, (float)a[5]!, (float)a[6]!) : null;

    private static PrimProjector? Projector(JsonNode? n) => n is JsonArray a
        ? new PrimProjector(Guid.Parse((string)a[0]!), (float)a[1]!, (float)a[2]!, (float)a[3]!) : null;

    private static FaceMaterials? Materials(JsonNode? n)
    {
        if (n is not JsonArray a || a.Count == 0 || a[0] is not JsonArray)
            return null;
        static FaceMaterial? M(JsonNode? m) => m is JsonArray x
            ? new FaceMaterial((byte)(int)x[0]!, (byte)(int)x[1]!, Guid.Parse((string)x[2]!), Guid.Parse((string)x[3]!)) : null;
        return new FaceMaterials(M(a[0])!.Value, a.Skip(1).Select(M));
    }

    /// <summary>The sculpt flags are written only when set, so earlier captures and their hashes are unchanged.</summary>
    private static JsonArray WithFlags(Viewer.Core.Meshing.PrimShape sh, JsonArray a)
    {
        if (sh.SculptFlags != 0) a.Add(sh.SculptFlags);
        return a;
    }

    private static Viewer.Core.Meshing.PrimShape? Shape(JsonNode? n)
    {
        if (n is not JsonArray a)
            return null;
        double D(int i) => (double)a[i]!;
        return new()
        {
            ProfileCurve = (int)a[0]!, ProfileHole = (int)a[1]!, ProfileBegin = D(2), ProfileEnd = D(3), ProfileHollow = D(4), PathCurve = (int)a[5]!,
            PathBegin = D(6), PathEnd = D(7), PathScaleX = D(8), PathScaleY = D(9), PathShearX = D(10), PathShearY = D(11), PathTwist = D(12),
            PathTwistBegin = D(13), PathRadiusOffset = D(14), PathTaperX = D(15), PathTaperY = D(16), PathRevolutions = D(17), PathSkew = D(18), SculptType = (int)a[19]!,
            SculptId = a.Count > 20 && a[20] is { } sid ? Guid.Parse((string)sid!) : Guid.Empty,
            SculptFlags = a.Count > 21 && a[21] is { } sf ? (int)sf : 0,   // (absent before)
        };
    }

    private static FaceTextures? Textures(JsonNode? n)
    {
        if (n is not JsonArray a || a.Count == 0 || a[0] is not JsonArray)
            return null;
        static FaceTexture? T(JsonNode? t) => t is JsonArray x
            ? new FaceTexture(Guid.Parse((string)x[0]!), (float)x[1]!, (float)x[2]!, (float)x[3]!, (float)x[4]!, (float)x[5]!, (TexGen)(int)x[6]!, (bool)x[7]!, (float)x[8]!) : null;
        return new FaceTextures(T(a[0])!.Value, a.Skip(1).Select(T));
    }

    private static FaceColors? Colors(JsonNode? n)
    {
        if (n is not JsonArray a || a.Count == 0 || a[0] is not JsonArray)
            return null;
        static Rgba? C(JsonNode? c) => c is JsonArray x ? new Rgba((float)x[0]!, (float)x[1]!, (float)x[2]!, (float)x[3]!) : null;
        return new FaceColors(C(a[0])!.Value, a.Skip(1).Select(C));
    }

    private static PrimData Prim(JsonObject o)
    {
        TextureAnim? anim = null;
        if (o["anim"] is JsonArray a)
            anim = new((int)a[0]!, (uint)a[1]!, (int)a[2]!, (int)a[3]!, (float)a[4]!, (float)a[5]!, (float)a[6]!);
        return new((uint)o["local"]!, Guid.Parse((string)o["full"]!), (uint)o["parent"]!, (int)o["point"]!, V(o["pos"]), Q(o["rot"]), V(o["scale"]),
            V(o["vel"]), V(o["ang"]), V(o["acc"]), anim, Shape(o["shape"]), Colors(o["colors"]), Textures(o["tex"]), Materials(o["mat"]), Light(o["light"]), Projector(o["proj"]));
    }

    private static JsonObject Av(AvatarData a) => new()
    {
        ["local"] = a.LocalId, ["agent"] = a.AgentId.ToString(), ["name"] = a.Name, ["pos"] = V(a.Position), ["rot"] = Q(a.Rotation),
        ["scale"] = V(a.Scale), ["vel"] = V(a.Velocity), ["seat"] = a.ParentLocalId,
    };

    // "seat" is absent from older captures (standing)
    private static AvatarData Av(JsonObject o) =>
        new((uint)o["local"]!, Guid.Parse((string)o["agent"]!), (string)o["name"]!, V(o["pos"]), Q(o["rot"]), V(o["scale"]), V(o["vel"]), o["seat"] is { } s ? (uint)s : 0);

    private static JsonObject RecordJson(CaptureRecord r)
    {
        var o = new JsonObject { ["t"] = r.T, ["cur"] = r.CurrentSim, ["n"] = r.ConnectedSims };
        switch (r.Event)
        {
            case LoginProgressed e: o["e"] = "login"; o["phase"] = e.Phase.ToString(); break;
            case SimConnected e:
                o["e"] = "simConnected"; o["sim"] = Sim(e.Sim); o["sx"] = e.SimSizeX; o["sy"] = e.SimSizeY; o["serverBaked"] = e.ServerBakedAppearance;
                // the region's terrain texturing settings
                if (e.WaterHeight is { } wh) o["water"] = wh;   // (optional; absent before)
                if (e.IsEstateManager is { } em) o["estate"] = em;   // (optional; absent before)
                if (e.Terrain is { } t)
                    o["terrain"] = new JsonObject
                    {
                        ["details"] = new JsonArray(t.Details.Select(d => (JsonNode)JsonValue.Create(d.ToString())).ToArray()),
                        ["start"] = new JsonArray(t.StartHeights.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
                        ["range"] = new JsonArray(t.HeightRanges.Select(v => (JsonNode)JsonValue.Create(v)).ToArray()),
                    };
                break;
            case EventQueueRunning e: o["e"] = "eq"; o["sim"] = Sim(e.Sim); break;
            case ParcelEnvironmentReceived pe: o["e"] = "parcelEnvironment"; o["sim"] = Sim(pe.Sim); o["parcel"] = pe.ParcelId; o["llsd"] = pe.Llsd; o["why"] = pe.Why; break;
            case EnvironmentReceived e: o["e"] = "environment"; o["sim"] = Sim(e.Sim); o["llsd"] = e.Llsd; o["why"] = e.Why; break;   // the region's environment answer
            case MovementComplete e: o["e"] = "movementComplete"; o["sim"] = Sim(e.Sim); break;
            case Disconnected: o["e"] = "disconnected"; break;
            case ObjectUpdated e: o["e"] = "object"; o["sim"] = Sim(e.Sim); o["prim"] = Prim(e.Prim); o["att"] = e.EventIsAttachment; o["parentAv"] = e.SimParentAvatar is null ? null : Av(e.SimParentAvatar); break;
            case AvatarUpdated e: o["e"] = "avatar"; o["sim"] = Sim(e.Sim); o["av"] = Av(e.Avatar); break;
            case ObjectMoved e:
                o["e"] = "move"; o["sim"] = Sim(e.Sim); o["local"] = e.LocalId; o["isAv"] = e.IsAvatar; o["pos"] = V(e.Position); o["rot"] = Q(e.Rotation);
                o["vel"] = V(e.Velocity); o["acc"] = V(e.Acceleration); o["ang"] = V(e.AngularVelocity);
                break;
            case RegionTimeDilation e: o["e"] = "dilation"; o["sim"] = Sim(e.Sim); o["value"] = e.Dilation; break;
            case SelfMoved e:
                var m = e.Motion;
                o["e"] = "self"; o["sim"] = Sim(e.Sim); o["pos"] = V(m.Position); o["rot"] = Q(m.Rotation); o["vel"] = V(m.Velocity); o["ang"] = V(m.AngularVelocity);
                o["acc"] = V(m.Acceleration); o["arr"] = m.ArrivalMs; o["mv"] = new JsonArray(m.Movement.Forward, m.Movement.Back, m.Movement.Run, m.Movement.Fly);
                break;
            case AvatarAppearanceReceived e:
                var ap = e.Appearance;
                o["e"] = "appearance"; o["sim"] = Sim(e.Sim); o["agent"] = ap.AgentId.ToString();
                o["faces"] = new JsonArray(ap.FaceTextures.Select(f => (JsonNode)JsonValue.Create(f == Guid.Empty ? "" : f.ToString())).ToArray());
                o["vp"] = Convert.ToBase64String(ap.VisualParams.Raw.ToArray());
                o["vpOrder"] = new JsonArray(ap.VisualParams.WireOrder.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
                o["ver"] = ap.AppearanceVersion; o["cof"] = ap.CofVersion;
                break;
            case AvatarHoverReceived e:
                o["e"] = "hover"; o["sim"] = Sim(e.Sim); o["agent"] = e.AgentId.ToString();
                if (e.Z is { } hz) o["z"] = hz;
                break;
            case AvatarAnimationsReceived e:
                // the animations an avatar plays: [animation id, sequence] per entry
                o["e"] = "anims"; o["sim"] = Sim(e.Sim); o["agent"] = e.AgentId.ToString();
                o["list"] = new JsonArray(e.Playing.Select(p => (JsonNode)new JsonArray(p.AnimationId.ToString(), p.Sequence)).ToArray());
                break;
            case ObjectsKilled e: o["e"] = "kill"; o["sim"] = Sim(e.Sim); o["ids"] = new JsonArray(e.LocalIds.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()); break;
            case LandPatch e:
                o["e"] = "patch"; o["sim"] = Sim(e.Sim); o["x"] = e.X; o["y"] = e.Y; o["size"] = e.PatchSize;
                o["h"] = e.Heights is null ? null : Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(e.Heights.AsSpan()));
                break;
            case MaterialOverridesReceived e:
                // the glTF overrides of one object: [face, override JSON] per face
                o["e"] = "overrides"; o["sim"] = Sim(e.Sim); o["local"] = e.LocalId;
                o["faces"] = new JsonArray(e.Faces.Select(f => (JsonNode)new JsonArray(f.Face, f.Json)).ToArray());
                break;
            case RenderMaterialsReceived e:
                // an object's PBR material ids: [face, id] per entry, null when the parameter was removed
                o["e"] = "renderMaterials"; o["sim"] = Sim(e.Sim); o["local"] = e.LocalId;
                o["faces"] = e.Faces is null ? null : new JsonArray(e.Faces.Select(f => (JsonNode)new JsonArray(f.Face, f.MaterialId.ToString())).ToArray());
                break;
            case LegacyMaterialsReceived e:
                o["e"] = "legacyMaterials"; o["sim"] = Sim(e.Sim);
                o["mats"] = new JsonArray(e.Materials.Select(m => (JsonNode)new JsonArray(m.Id.ToString(), Map(m.Normal), Map(m.Specular),
                    new JsonArray(m.SpecularColor.R, m.SpecularColor.G, m.SpecularColor.B, m.SpecularColor.A), m.SpecularExponent,
                    m.EnvironmentIntensity, (int)m.DiffuseAlphaMode, m.AlphaMaskCutoff)).ToArray());
                break;
            case GodLevelReceived e: o["e"] = "godLevel"; o["level"] = e.Level; break;
            case CameraConstraintReceived e: o["e"] = "camPlane"; o["sim"] = Sim(e.Sim); o["plane"] = new JsonArray(e.Plane.X, e.Plane.Y, e.Plane.Z, e.Plane.W); break;
            case MapBlockSize e: o["e"] = "mapBlock"; o["gx"] = e.GridX; o["gy"] = e.GridY; o["sx"] = e.SizeX; o["sy"] = e.SizeY; o["name"] = e.Name; break;
            default: o["e"] = "unknown:" + r.Event.GetType().Name; break;
        }
        return o;
    }

    private static TransportEvent EventFrom(JsonObject o, Dictionary<ulong, SimRef> sims)
    {
        string kind = (string)o["e"]!;
        SimRef? sim = Sim(o["sim"], sims);
        switch (kind)
        {
            case "login": return new LoginProgressed(Enum.Parse<LoginPhase>((string)o["phase"]!), null);
            case "simConnected":
                return new SimConnected(sim!, (int)o["sx"]!, (int)o["sy"]!, (bool)o["serverBaked"]!, o["terrain"] is JsonObject tj
                    ? new Viewer.Core.World.RegionTerrain(
                        [.. tj["details"]!.AsArray().Select(d => Guid.Parse((string)d!))],
                        [.. tj["start"]!.AsArray().Select(v => (float)v!)],
                        [.. tj["range"]!.AsArray().Select(v => (float)v!)])
                    : null, o["water"] is { } wv ? (float)wv : null, o["estate"] is { } ev ? (bool)ev : null);
            case "eq": return new EventQueueRunning(sim!);
            case "parcelEnvironment": return new ParcelEnvironmentReceived(sim!, (int)o["parcel"]!, (string?)o["llsd"], (string?)o["why"]);
            case "environment": return new EnvironmentReceived(sim!, (string?)o["llsd"], (string?)o["why"]);
            case "movementComplete": return new MovementComplete(sim!);
            case "disconnected": return new Disconnected("scrubbed");
            case "object": return new ObjectUpdated(sim, Prim(o["prim"]!.AsObject()), (bool)o["att"]!, o["parentAv"] is JsonObject pa ? Av(pa) : null);
            case "avatar": return new AvatarUpdated(sim, Av(o["av"]!.AsObject()));
            case "dilation": return new RegionTimeDilation(sim, (float)o["value"]!);
            case "move": return new ObjectMoved(sim, (uint)o["local"]!, (bool)o["isAv"]!, V(o["pos"]), Q(o["rot"]), V(o["vel"]), V(o["acc"]), V(o["ang"]));
            case "self":
                var mv = o["mv"]!.AsArray();
                return new SelfMoved(sim, new SelfMotion(V(o["pos"]), Q(o["rot"]), V(o["vel"]), V(o["ang"]), V(o["acc"]), (double)o["arr"]!,
                    new MovementFlags((bool)mv[0]!, (bool)mv[1]!, (bool)mv[2]!, (bool)mv[3]!)));
            case "appearance":
                var faces = o["faces"]!.AsArray().Select(n => (string)n! is { Length: > 0 } s ? Guid.Parse(s) : Guid.Empty).ToImmutableArray();
                var order = o["vpOrder"]!.AsArray().Select(n => (int)n!).ToImmutableArray();
                var vp = new VisualParamSet(Convert.FromBase64String((string)o["vp"]!), order);
                return new AvatarAppearanceReceived(sim, new AvatarAppearanceData(Guid.Parse((string)o["agent"]!), faces, vp, (int)o["ver"]!, (int)o["cof"]!));
            case "hover":
                return new AvatarHoverReceived(sim, Guid.Parse((string)o["agent"]!), o["z"] is { } z ? (float)z : null);
            case "anims":
                return new AvatarAnimationsReceived(sim, Guid.Parse((string)o["agent"]!),
                    o["list"]!.AsArray().Select(n => new PlayingAnimation(Guid.Parse((string)n![0]!), (int)n[1]!)).ToList());
            case "kill": return new ObjectsKilled(sim, o["ids"]!.AsArray().Select(n => (uint)n!).ToArray());
            case "patch":
                float[]? h = null;
                if (o["h"] is JsonValue hv)
                    h = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(Convert.FromBase64String((string)hv!)).ToArray();
                return new LandPatch(sim, (int)o["x"]!, (int)o["y"]!, (int)o["size"]!, h);
            case "overrides":
                return new MaterialOverridesReceived(sim, (uint)o["local"]!,
                    o["faces"]!.AsArray().Select(f => new FaceMaterialOverride((int)f![0]!, (string)f[1]!)).ToList());
            case "renderMaterials":
                return new RenderMaterialsReceived(sim, (uint)o["local"]!, o["faces"] is JsonArray rf
                    ? rf.Select(f => new RenderMaterialEntry((int)f![0]!, Guid.Parse((string)f[1]!))).ToList() : null);
            case "legacyMaterials":
                return new LegacyMaterialsReceived(sim, o["mats"]!.AsArray().Select(m =>
                {
                    var x = m!.AsArray();
                    var c = x[3]!.AsArray();
                    return new LegacyMaterial(Guid.Parse((string)x[0]!), Map(x[1]), Map(x[2]), new Rgba((float)c[0]!, (float)c[1]!, (float)c[2]!, (float)c[3]!),
                        (byte)(int)x[4]!, (byte)(int)x[5]!, (LegacyAlphaMode)(int)x[6]!, (byte)(int)x[7]!);
                }).ToList());
            case "godLevel": return new GodLevelReceived((int)o["level"]!);
            case "camPlane":
            {
                var pl = o["plane"]!.AsArray();
                return new CameraConstraintReceived(sim, new System.Numerics.Vector4((float)pl[0]!, (float)pl[1]!, (float)pl[2]!, (float)pl[3]!));
            }
            case "mapBlock": return new MapBlockSize((int)o["gx"]!, (int)o["gy"]!, (int)o["sx"]!, (int)o["sy"]!, (string?)o["name"]);
            default: throw new InvalidDataException("unknown capture event " + kind);
        }
    }

    private static JsonArray Map(MaterialMap m) => new(m.TextureId.ToString(), m.OffsetU, m.OffsetV, m.RepeatU, m.RepeatV, m.Rotation);

    private static MaterialMap Map(JsonNode? n)
    {
        var a = n!.AsArray();
        return new(Guid.Parse((string)a[0]!), (float)a[1]!, (float)a[2]!, (float)a[3]!, (float)a[4]!, (float)a[5]!);
    }

    internal static JsonObject SummaryJson(StateSummary s) => JsonSerializer.SerializeToNode(s)!.AsObject();

    internal static StateSummary SummaryFrom(JsonObject o) => o.Deserialize<StateSummary>()!;
}
