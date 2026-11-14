// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Avatars;
using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>One avatar as the presentation draws it: who it is, where (smoothed), its bakes and what it plays.</summary>
public sealed class AvatarEntry
{
    internal AvatarEntry(ObjectId id, Guid agentId, bool isSelf, IAvatarMotion? motion = null)
    {
        Id = id;
        AgentId = agentId;
        IsSelf = isSelf;
        Motion = motion ?? new ObjectMotion();   // every avatar, the user's own and others, moves as the official viewer moves an object (LLVOAvatar::idleUpdate)
    }

    public ObjectId Id { get; }
    public Guid AgentId { get; }
    public bool IsSelf { get; }
    public string Name { get; internal set; } = "";
    /// <summary>The avatar's size as the region states it (its height is Z).</summary>
    public Vector3 Size { get; internal set; }
    /// <summary>The avatar's own body height as its shape gives it (the official body size), null until its shape is known.</summary>
    public float? BodyHeight { get; private set; }
    /// <summary>The hover the avatar is drawn with while standing (metres).</summary>
    public float BodyHover { get; private set; }
    /// <summary>Records the avatar's own body height and hover, worked out from its shape by the presentation.</summary>
    public void SetOwnBody(float height, float hover)
    {
        BodyHeight = height > 0.1f && float.IsFinite(height) ? height : null;
        BodyHover = float.IsFinite(hover) ? hover : 0f;
    }
    /// <summary>How far above the soles it is given the drawing puts the pelvis at rest (its pelvis-to-foot height with the hover and any worn
    /// mesh's pelvis offset), null until the drawing has said. A seated avatar is placed by its root, so its soles are the root less this.</summary>
    public float? PelvisAboveSoles { get; private set; }
    /// <summary>Records <see cref="PelvisAboveSoles"/> from the drawing.</summary>
    public void SetPelvisAboveSoles(float height) => PelvisAboveSoles = float.IsFinite(height) && height > 0 ? height : null;
    /// <summary>The object it sits on (0 when standing).</summary>
    public uint Seat { get; internal set; }
    public IAvatarMotion Motion { get; }
    /// <summary>The state of the region that has the avatar now, as last taken while this entry was held (null: none yet).</summary>
    internal Viewer.Core.World.AvatarState? Followed { get; set; }
    /// <summary>Its server bakes by slot (empty until its appearance arrives); <see cref="BakesVersion"/> counts changes.</summary>
    public IReadOnlyDictionary<string, Guid> Bakes { get; internal set; } = new Dictionary<string, Guid>();
    public int BakesVersion { get; internal set; }
    /// <summary>The visual parameters last received for this avatar (null before any), and a count that moves when they change.</summary>
    public VisualParamSet? VisualParams { get; internal set; }
    public int ShapeVersion { get; internal set; }
    /// <summary>The hover height its last AvatarAppearance message stated (null: no such block).</summary>
    public float? Hover { get; internal set; }
    /// <summary>What the region says it plays; <see cref="AnimationsVersion"/> counts changes.</summary>
    public IReadOnlyList<PlayingAnimation> Playing { get; internal set; } = [];
    public int AnimationsVersion { get; internal set; }
    public int NameVersion { get; internal set; }
    /// <summary>The presentation's own handle for what it made for this avatar (null until made).</summary>
    public object? Handle { get; set; }
}

/// <summary>
/// The avatars of one session's region, kept for drawing. <see cref="Apply"/> takes the world state's changes on the main
/// thread (through the presentation coordinator): an avatar is added when the region first describes it, moved by its
/// updates (smoothed by <see cref="AvatarMotion"/>), given its bakes and animations as they arrive, and departs when the
/// region removes it or the region changes; every departed entry is queued (<see cref="TakeDeparted"/>) so whatever the
/// presentation made for it is freed. No engine type; main thread only.
/// </summary>
public sealed partial class AvatarScene
{
    private readonly WorldState _world;
    private readonly Guid _self;
    private Func<double> _now;
    private readonly Dictionary<ObjectId, AvatarEntry> _entries = new();
    private readonly Queue<AvatarEntry> _departed = new();
    private int _epoch = -1;

    /// <param name="now">Seconds on the presentation's clock (tests pass a fake).</param>
    public AvatarScene(WorldState world, Guid selfAgentId, Func<double> now)
    {
        _world = world;
        _self = selfAgentId;
        _now = now;
    }

    /// <summary>Animations the user's own avatar plays in this viewer only (an add-on's preview): they join what the region
    /// says the own avatar plays, and nothing is sent to the region, so nobody else sees them.</summary>
    public IReadOnlyList<Guid> LocalPreview
    {
        get => _localPreview;
        set
        {
            _localPreview = [.. value];
            _previewSequence++;
            if (Self is { } self)
                TakeAnimations(self);   // shown at once, not at the region's next animation message
        }
    }

    private Guid[] _localPreview = [];
    private int _previewSequence = 1_000_000;

    public int Count => _entries.Count;
    /// <summary>The scene's clock (seconds), which every avatar's motion is timed on.</summary>
    public double Now => _now();
    public IReadOnlyCollection<AvatarEntry> Entries => _entries.Values;
    public AvatarEntry? Self => _entries.Values.FirstOrDefault(e => e.IsSelf && !_held.Contains(e.Id)) ?? _entries.Values.FirstOrDefault(e => e.IsSelf);

    /// <summary>Whether the user's own avatar is left out of the picture: in mouse-look the official viewer draws neither the avatar, nor
    /// what it wears, nor its name (llagent.cpp, needsRenderAvatar; llvoavatar.cpp name tag), unless its FirstPersonAvatarVisible setting is on.</summary>
    public bool HideSelf { get; set; }

    /// <summary>Whether the presentation draws this avatar (with its name and what it wears).</summary>
    public bool Draws(AvatarEntry e) => !(HideSelf && e.IsSelf);

    /// <summary>The avatar of this region visit with the simulator's local id <paramref name="localId"/> (the id attachments name
    /// as their parent); null when it is not here.</summary>
    public AvatarEntry? ByLocalId(uint localId) => _entries.TryGetValue(new ObjectId(_epoch, localId), out var e) ? e : null;

    /// <summary>The user's own avatar's heading as the keyboard turns it (radians about up from east), drawn instead of the
    /// region's while the avatar stands (null: the region's). The region follows the body rotation it is sent, a round trip
    /// later; drawing the turn at once is what the official viewer does with its own agent.</summary>
    public float? SelfHeading { get; set; }
    public int Arrivals { get; private set; }
    public int Departures { get; private set; }

    /// <summary>Takes one world change (main thread). Changes of other kinds are ignored.</summary>
    public void Apply(WorldChange change)
    {
        switch (change.Kind)
        {
            case WorldChangeKind.RegionReset:
                if (IsStaleReset(change)) return;   // this region visit's avatars were already taken (through the fast lane): the reset only repeats it
                StartEpoch(change.Id.Epoch);
                return;
            case WorldChangeKind.AvatarAdded or WorldChangeKind.AvatarChanged or WorldChangeKind.AvatarMoved:
                if (change.Id.Epoch < _epoch) return;
                Track(change.Id);
                return;
            case WorldChangeKind.AvatarRemoved:
                if (HoldHandoffs && change.Handoff && _entries.ContainsKey(change.Id)) { _held.Add(change.Id); return; }   // another region took it over: it stays drawn until that region draws it
                if (_entries.Remove(change.Id, out var gone)) Depart(gone);
                return;
            case WorldChangeKind.AppearanceChanged:
                foreach (var e in _entries.Values.Where(e => e.AgentId == change.AgentId)) TakeBakes(e);
                return;
            case WorldChangeKind.AnimationsChanged:
                foreach (var e in _entries.Values.Where(e => e.AgentId == change.AgentId)) TakeAnimations(e);
                return;
        }
    }

    /// <summary>The region visit the scene holds avatars of (-1 before any).</summary>
    public int Epoch => _epoch;

    /// <summary>Whether a region reset is one the scene is already past: avatar changes of its visit (or a later one) were applied before it came
    /// (the avatars' fast lane applies them ahead of the objects queued before them, and the reset comes later in the ordinary queue).</summary>
    public bool IsStaleReset(WorldChange reset) => reset.Kind == WorldChangeKind.RegionReset && reset.Id.Epoch <= _epoch;

    private void StartEpoch(int epoch)
    {
        foreach (var e in _entries.Values) Depart(e);
        _entries.Clear();
        _held.Clear();
        _epoch = epoch;
    }

    private void Track(ObjectId id)
    {
        // the state as it is now (it may be newer than the change): gone already means nothing to draw
        if (_world.Avatar(id) is not { } a) return;
        if (id.Epoch > _epoch) StartEpoch(id.Epoch);   // the first avatar of a new region visit: what the earlier visit left goes, as a reset would do
        _held.Remove(id);   // the region sends it again: it is a live avatar here, not one kept for a handoff
        if (!_entries.TryGetValue(id, out var e))
        {
            bool self = a.AgentId == _self && _self != Guid.Empty;
            _entries[id] = e = new AvatarEntry(id, a.AgentId, self, OwnMotion?.Join(a.AgentId));   // an avatar carries on as the one object it is in the region left (llviewerobjectlist.cpp: found by its full id, moved to the new region)
            Arrivals++;
            TakeBakes(e);
            TakeAnimations(e);
        }
        e.Followed = null;   // the region's own updates move it now
        if (e.Name != a.Name) { e.Name = a.Name; e.NameVersion++; }
        e.Size = a.Scale;
        if (e.Seat != a.ParentLocalId)
        {
            e.Seat = a.ParentLocalId;
            InRegion(e);
            e.Motion.Snap(a.Position, a.Velocity, a.Rotation, _now());   // a sit or a stand is taken at once (the frame changes)
            return;
        }
        InRegion(e);
        e.Motion.Update(a.Position, a.Velocity, a.Rotation, _now());
    }

    /// <summary>Tells an avatar's motion that its statements now come from this region: where the region lies, how wide it is, which places outside it
    /// the viewer knows a region at, and its time dilation (llviewerobject.cpp interpolateLinearMotion and idleUpdate read them from the object's region).
    /// A seated avatar's place is relative to its seat, so it has no region edge.</summary>
    private void InRegion(AvatarEntry e)
    {
        if (e.Motion is not ObjectMotion m) return;
        if (_world.Region is not { } region || _world.Avatar(e.Id)?.ParentLocalId is not 0)
        {
            m.Region = null;
            m.TimeDilation = _world.TimeDilation;
            return;
        }
        var origin = _world.OriginOffset;
        var world = _world;
        m.Region = new MotionRegion(origin, region.SizeX, HasRegionAt is { } has ? p => has(p - origin) : null, () => world.TimeDilation);
    }

    private void TakeBakes(AvatarEntry e)
    {
        if (_world.DrawnVisualParams(e.AgentId) is { } vp && (e.VisualParams is null || !e.VisualParams.SameAs(vp))) { e.VisualParams = vp; e.ShapeVersion++; }
        if (_world.Appearance(e.AgentId)?.Hover is var hv && hv != e.Hover) { e.Hover = hv; e.ShapeVersion++; }
        var bakes = _world.Appearance(e.AgentId)?.Bakes ?? new Dictionary<string, Guid>();
        if (bakes.Count == e.Bakes.Count && bakes.All(kv => e.Bakes.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
        e.Bakes = bakes;
        e.BakesVersion++;
    }

    private void TakeAnimations(AvatarEntry e)
    {
        var playing = _world.Animations(e.AgentId);
        if (e.AgentId == _self && _self != Guid.Empty && _localPreview.Length > 0)
            playing = [.. playing, .. _localPreview.Select(id => new PlayingAnimation(id, _previewSequence))];
        if (playing.SequenceEqual(e.Playing)) return;
        e.Playing = playing;
        e.AnimationsVersion++;
    }

    private void Depart(AvatarEntry e)
    {
        OwnMotion?.Leave(e.Motion);
        _departed.Enqueue(e);
        Departures++;
    }

    /// <summary>The next avatar that left (main thread): free what was made for it.</summary>
    public bool TakeDeparted(out AvatarEntry entry) => _departed.TryDequeue(out entry!);

    /// <summary>Where <paramref name="e"/> is drawn now: the soles' position in region metres and the heading (radians
    /// about up from east). The region places an avatar by its centre, so the soles are half its height lower. A seated
    /// avatar is placed relative to its seat (one link level, as linksets are); false while the seat is unknown. Seated, the root is the
    /// place the region states plus the hover offset turned with the avatar, as the official viewer's seated branch puts it
    /// (<see cref="SeatPlacement.SeatedRoot"/>), and the soles handed back are that root less the drawing's pelvis-above-soles height, so
    /// the drawing hangs the pelvis exactly at the root.
    /// <para>A standing avatar's drawn soles are kept on or above the terrain, as the official viewer keeps an interpolated
    /// avatar above the land: between the update that says it reached the ground and the stop a frame later, the smoothing
    /// would carry a landing avatar on down into it. The floor is never above where the region itself puts the soles, so
    /// an avatar the region rests a little lower than the terrain samples is drawn where the region says.</para></summary>
    public bool Place(AvatarEntry e, out Vector3 soles, out float yaw)
    {
        var (p, r) = e.Motion.At(_now());
        if (e.Seat != 0)
        {
            if (!SeatPose(e.Id.Epoch, e.Seat, out var sp, out var sr)) { soles = default; yaw = 0; return false; }
            p = sp + Vector3.Transform(p, sr);
            r = sr * r;
        }
        float half = (e.Size.Z > 0.1f ? e.Size.Z : DefaultHeight) / 2;
        soles = new Vector3(p.X, p.Y, p.Z - half);
        if (e.Seat != 0 && e.PelvisAboveSoles is float pelvisAboveSoles)
        {
            var root = SeatPlacement.SeatedRoot(p, r, e.BodyHover);
            soles = new Vector3(root.X, root.Y, root.Z - pelvisAboveSoles);
        }
        // the drawn place is in the anchor frame; this region's land is read at the place in the region's own frame
        var origin = _world.OriginOffset;
        if (e.Seat == 0 && Ground(soles.X - origin.X, soles.Y - origin.Y) is float ground)
        {
            float floor = MathF.Min(ground, e.Motion.Stated.Z - half);
            if (soles.Z < floor) soles.Z = floor;
        }
        yaw = e.IsSelf && e.Seat == 0 && SelfHeading is float own ? own : AvatarMotion.Yaw(r);
        return true;
    }

    /// <summary>How far the drawn soles of <paramref name="e"/> are above the ground under them (negative when the ground is above them), 0 when
    /// the avatar is on a seat or neither land nor a foot plane is known there. The ground is the official one (<see cref="OfficialGround"/>,
    /// LLVOAvatar::getGround): the land, or the foot plane the region sent with the avatar when that is hit first, so an avatar on a deck sits on
    /// the deck. A constrained ground sit puts the pelvis there, not on where the region rests the avatar.</summary>
    public float GroundGap(AvatarEntry e, Vector3 soles)
    {
        if (e.Seat != 0) return 0f;
        return OfficialGround.HeightUnder(soles, GroundAt(soles), FootPlane(e)) is float ground ? soles.Z - ground : 0f;
    }

    /// <summary>For footsteps: how far <paramref name="foot"/> (a point in the drawn frame) is above the official ground under it, and whether that
    /// ground is the land (true) or the foot plane of something the avatar stands on (false). Null when neither is known there.</summary>
    public (float Above, bool OnLand)? FootGround(AvatarEntry e, Vector3 foot)
    {
        float? land = GroundAt(foot);
        if (OfficialGround.HeightUnder(foot, land, FootPlane(e)) is not float ground)
            return null;
        return (foot.Z - ground, land is not float l || ground <= l + 0.05f);
    }

    /// <summary>The foot plane the region sent with <paramref name="e"/> (normal in x, y, z, offset in w), in the drawn frame; all zero when it sent none.</summary>
    private Vector4 FootPlane(AvatarEntry e)
    {
        if (_world.Avatar(e.Id)?.FootPlane is not { } p || p == Vector4.Zero) return default;
        // the plane is stated in the region's own frame; the drawn frame is shifted by the origin offset
        var n = new Vector3(p.X, p.Y, p.Z);
        return new Vector4(n, p.W + Vector3.Dot(n, _world.OriginOffset));
    }

    /// <summary>The terrain's height under <paramref name="point"/> (a point in the drawn frame, as <see cref="Place"/> gives), null where it is not known.</summary>
    public float? GroundAt(Vector3 point)
    {
        var origin = _world.OriginOffset;
        return Ground(point.X - origin.X, point.Y - origin.Y);
    }

    /// <summary>Where the middle of <paramref name="e"/>'s body box is drawn now, and its heading as a rotation about up: the pose
    /// <see cref="Place"/> gives the body, for the name tag to hang from. False while the avatar cannot be placed.</summary>
    public bool DrawnPose(AvatarEntry e, out Vector3 centre, out Quaternion rotation)
    {
        if (!Place(e, out var soles, out float yaw))
        {
            centre = default;
            rotation = Quaternion.Identity;
            return false;
        }
        centre = soles + new Vector3(0, 0, (e.Size.Z > 0.1f ? e.Size.Z : DefaultHeight) / 2);
        rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw);
        return true;
    }

    /// <summary>The terrain's height at (<paramref name="x"/>, <paramref name="y"/>) in region metres, between the four
    /// nearest height samples (one a metre); null where a patch has not arrived or outside the region.</summary>
    public float? Ground(float x, float y)
    {
        if (_world.Region is not { } region || !float.IsFinite(x) || !float.IsFinite(y)
            || x < 0 || y < 0 || x > region.SizeX || y > region.SizeY) return null;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        if (Sample(region, x0, y0) is not float h00 || Sample(region, x0 + 1, y0) is not float h10
            || Sample(region, x0, y0 + 1) is not float h01 || Sample(region, x0 + 1, y0 + 1) is not float h11) return null;
        return (h00 * (1 - fx) + h10 * fx) * (1 - fy) + (h01 * (1 - fx) + h11 * fx) * fy;
    }

    /// <summary>The height sample at whole metres (gx, gy), the region's far edge taking its last sample. Patches are
    /// 16 m square, as the terrain the presentation draws reads them.</summary>
    private float? Sample(Viewer.Core.Session.RegionInfo region, int gx, int gy)
    {
        const int patch = 16;
        gx = Math.Clamp(gx, 0, region.SizeX - 1);
        gy = Math.Clamp(gy, 0, region.SizeY - 1);
        if (!_world.TryGetPatch(gx / patch, gy / patch, out var h) || h.Length < patch * patch) return null;
        return h[(gy % patch) * patch + gx % patch];
    }

    /// <summary>The height used when the region states none.</summary>
    public const float DefaultHeight = 1.9f;

    /// <summary>The region's objects that move or spin between its updates: an avatar sitting on one is drawn where the object is
    /// drawn now, as a seated avatar is a child of its seat. Null: seats are taken where the region last stated them.</summary>
    public ObjectInterpolator? Carried { get; set; }

    /// <summary>The rotation of the object <paramref name="e"/> sits on, where that object is drawn now; false when it does not sit or the seat is unknown.</summary>
    public bool TrySeatRotation(AvatarEntry e, out Quaternion rotation)
    {
        rotation = Quaternion.Identity;
        return e.Seat != 0 && SeatPose(e.Id.Epoch, e.Seat, out _, out rotation);
    }

    private bool SeatPose(int epoch, uint seat, out Vector3 position, out Quaternion rotation)
    {
        position = default;
        rotation = Quaternion.Identity;
        if (_world.Object(new ObjectId(epoch, seat)) is not { } o) return false;
        position = o.Position;
        rotation = o.Rotation;
        if (Carried is { } carried && carried.TryPlace(seat, out var seatPosition, out var seatRotation)) (position, rotation) = (seatPosition, seatRotation);
        if (o.ParentLocalId == 0) return true;
        if (_world.Object(new ObjectId(epoch, o.ParentLocalId)) is not { } root) return false;
        var (rootPosition, rootRotation) = (root.Position, root.Rotation);
        if (Carried is { } set && set.TryPlace(o.ParentLocalId, out var carriedPosition, out var carriedRotation)) (rootPosition, rootRotation) = (carriedPosition, carriedRotation);
        position = rootPosition + Vector3.Transform(position, rootRotation);
        rotation = rootRotation * rotation;
        return true;
    }
}
