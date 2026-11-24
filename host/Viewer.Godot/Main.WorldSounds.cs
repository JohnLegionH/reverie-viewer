// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Avatars;
using Viewer.Core.Session;
using Viewer.Core.World;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// Sounds in the world in the running viewer: each session's sound events go to a <see cref="WorldSounds"/> service made for that
/// session (its own grid sound fetches, through the content cache), which plays them through <see cref="WorldSoundPlayer"/>. Every frame
/// the service gets the camera as its listener, the objects' and avatars' places from the world state, and the footsteps of the drawn
/// avatars (their foot points against the official ground).
/// </summary>
public partial class Main
{
    /// <summary>The attachment points the official skeleton puts at the feet joints (avatar_lad.xml: Left Foot 7, Right Foot 8).</summary>

    private WorldSoundPlayer? _worldPlayer;
    private WorldSounds? _worldSounds;
    private ViewerSession? _worldSoundsFor;
    private Action<TransportEvent>? _worldSoundHandler;
    private readonly ConcurrentQueue<TransportEvent> _worldSoundEvents = new();
    private readonly Dictionary<Guid, FootstepSampler> _feet = new();

    /// <summary>The own avatar's last footstep frames (time, each foot's height over the ground under it, whether a step animation
    /// played, the foot that came down), for the automation's world_sounds feet action.</summary>
    private readonly Queue<(double T, float Left, float Right, bool Steps, string Foot, bool OnLand, int Material)> _selfFeet = new();
    private const int SelfFeetFrames = 900;
    private readonly WalkSummary _selfWalk = new();
    private Dictionary<Guid, ObjectState>? _soundObjects;
    private WorldSnapshot? _soundSnapshot;

    /// <summary>The sounds in the world (null until the shell is made, or between sessions).</summary>
    public WorldSounds? WorldSounds => _worldSounds;

    private void InstallWorldSounds(SettingsService settings)
    {
        _worldPlayer = new WorldSoundPlayer(settings, () => DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized);
        AddChild(_worldPlayer);
    }

    /// <summary>Every frame: follows the session, hands its sound events to the service, plays footsteps, and places the voices.</summary>
    private void UpdateWorldSounds()
    {
        if (_worldPlayer is not { } player)
            return;
        player.ApplyMaster();
        var session = Model?.Session;
        bool inWorld = session is not null && !session.IsEnded && session.AgentId != Guid.Empty;
        var wanted = inWorld ? session : null;
        UpdateParcelMusic(wanted);
        if (!ReferenceEquals(_worldSoundsFor, wanted) && !_soundsFed)
            FollowSession(wanted, player);
        if (_worldSounds is not { } sounds)
            return;
        while (_worldSoundEvents.TryDequeue(out var e))
            sounds.Receive(e);
        _soundSnapshot = null;
        _soundObjects = null;
        if (_worldSoundsFor is { } s)
        {
            var origin = s.World.OriginOffset;
            player.ToEngine = p => ToEngine3(p + origin);
            if (s.Region is { } region)
                sounds.AgentRegion = Handle(region);
            Footsteps(s, sounds);
            SitLog(s);
            Wind(s);
            var camera = GetViewport().GetCamera3D();
            var listener = camera is null ? Vector3Sl(default) : Vector3Sl(camera.GlobalPosition) - origin;
            sounds.Update(listener, Underwater(s, listener));
        }
        else
            sounds.Update(_fedListener, false);
    }

    // the wind (llvieweraudio.cpp audio_update_wind): the region's wind at the agent, less the agent's velocity, in the agent's frame;
    // heard only while flying, faded over two seconds, no louder than master x ambient (WindSound, WindGenerator)
    private AudioStreamPlayer? _windPlayer;
    private AudioStreamGeneratorPlayback? _windPlayback;
    private readonly WindGenerator _wind = new();
    private float _windMax;
    private float[] _windLeft = [], _windRight = [];
    private System.Numerics.Vector3 _windFelt;
    private float _windLeftRms, _windRightRms;   // the last batch's loudness on each side (for checks)

    // one avatar.sit line per sit of the own avatar, measured from the posed skeleton a second after the sit animation has reached the pose it
    // holds (the grid's default sit plays almost three seconds into the seated pose); never later than SitLogLatest after the sit
    private const double SitLogDelay = 1.0, SitLogLatest = 6.0;
    private uint _sitLogSeat;
    private double _sitLogSince = -1;
    private bool _sitLogged;

    private void SitLog(ViewerSession s)
    {
        if (_presenter?.Avatars is not { } av || av.Scene.Self is not { } me)
            return;
        if (me.Seat == 0)
        {
            _sitLogSeat = 0; _sitLogSince = -1; _sitLogged = false;
            return;
        }
        double now = Time.GetTicksMsec() / 1000.0;
        if (me.Seat != _sitLogSeat) { _sitLogSeat = me.Seat; _sitLogSince = now; _sitLogged = false; }
        if (_sitLogged)
            return;
        float settles = av.TryTopAnimation(me.Id.LocalId, out _, out _, out float st) ? st : 0f;
        if (now - _sitLogSince < Math.Min(SitLogDelay + settles, SitLogLatest) || SitReportFor(s, av, me) is not { } report)
            return;
        _sitLogged = true;
        Log.Info("avatar.sit", report.Fields().Select(f => Viewer.Core.Diagnostics.CoreLog.F(f.Key, f.Value)).ToArray());
    }

    // the sit line's numbers for the own avatar now (null while it is not drawn)
    private SitReport? SitReportFor(ViewerSession s, Presentation.AvatarPresenter av, AvatarEntry me)
    {
        if (!av.TrySeatedPose(me.Id.LocalId, out var pose))
            return null;
        av.TryOwnBody(me.Id.LocalId, out float bodyHeight, out _);
        var (seatPos, seatRot) = me.Motion.At(av.Scene.Now);   // a seated avatar's place and turn relative to its seat, as the region states them
        // a seat without a sit target is answered with the offset that was sent (turned and moved into the root's frame for a child prim)
        bool target = false;
        var sent = _interact?.LastSitOffset ?? System.Numerics.Vector3.Zero;
        if (_interact?.LastSitResponse is { } resp)
        {
            var expected = sent;
            if (s.World.Object(new ObjectId(me.Id.Epoch, me.Seat)) is { ParentLocalId: not 0 } child)
                expected = System.Numerics.Vector3.Transform(sent, child.Rotation) + child.Position;
            target = System.Numerics.Vector3.Distance(resp.SitPosition, expected) > 0.01f;
        }
        var (anim, priority) = av.TryTopAnimation(me.Id.LocalId, out var top, out int pr, out _) ? (top, (int?)pr) : (Guid.Empty, null);
        string kind = SeatPlacement.SitKind(anim, _addonStarted.Contains(anim));
        string name = Viewer.Core.Avatars.AgentAnimations.Names.TryGetValue(anim, out var n) ? n : anim == Guid.Empty ? "none" : anim.ToString();
        return new SitReport(target, sent, seatPos, seatRot, SeatTop(s, pose.Root), pose, bodyHeight, name, kind, priority);
    }

    // the top of the prim under the pelvis (the seat), in the drawn frame; null when none is found within 2 m below it
    private float? SeatTop(ViewerSession s, System.Numerics.Vector3 pelvis)
    {
        var origin = s.World.OriginOffset;
        float? top = null;
        foreach (var box in StepGround.Boxes(s.World.Snapshot().Objects.Values.ToArray(), pelvis + origin, 3f))
        {
            var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, box.Rotation);
            var c = box.Center - origin;
            float boxTop = c.Z + MathF.Abs(up.Z) * box.Size.Z / 2 + MathF.Sqrt(Math.Max(0f, 1 - up.Z * up.Z)) * MathF.Max(box.Size.X, box.Size.Y) / 2;
            bool under = MathF.Abs(c.X - pelvis.X) <= box.Size.Length() / 2 && MathF.Abs(c.Y - pelvis.Y) <= box.Size.Length() / 2;
            if (under && boxTop <= pelvis.Z + 0.2f && boxTop > pelvis.Z - 2f && (top is null || boxTop > top))
                top = boxTop;
        }
        return top;
    }

    private void Wind(ViewerSession s)
    {
        if (_windPlayer is null)
        {
            _windPlayer = new AudioStreamPlayer { Name = "wind", Stream = new AudioStreamGenerator { MixRate = WindGenerator.SampleRate, BufferLength = 0.15f } };
            AddChild(_windPlayer);
            _windPlayer.Play();
            _windPlayback = _windPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;
        }
        if (_windPlayback is not { } playback)
            return;
        var settings = UserSettings(null);
        bool minimized = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Minimized;
        float master = AudioLevels.MasterLevel(settings, minimized), ambient = AudioLevels.KindLevel(settings, WorldSoundKind.Ambient);
        bool flying = _driver?.Input.Flying ?? false;
        _windMax = WindSound.StepMaxGain(_windMax, flying, master, ambient, (float)GetProcessDeltaTime());
        var me = s.World.LastSelfMotion?.Motion;
        var region = s.Region;
        var regionWind = me is not null && region is not null && s.World.Wind is { } w ? w.At(me.Position.X, me.Position.Y, region.SizeX, region.SizeY) : System.Numerics.Vector3.Zero;
        _windFelt = WindSound.Relative(regionWind, me?.Velocity ?? default, me?.Rotation ?? System.Numerics.Quaternion.Identity);
        _wind.TargetGain = WindSound.Gain(_windFelt) * _windMax;
        _wind.TargetFrequency = WindSound.CentreFrequency(_windFelt);
        _wind.TargetPanRight = WindSound.Pan(_windFelt);
        int frames = playback.GetFramesAvailable();
        if (frames <= 0)
            return;
        if (_windLeft.Length < frames) { _windLeft = new float[frames]; _windRight = new float[frames]; }
        // Mono: the wind's pan is not applied, so both ears hear it alike (the official generator's mono mode)
        _wind.Fill(_windLeft.AsSpan(0, frames), _windRight.AsSpan(0, frames), mono: settings.Get(AudioLevels.Mono));
        float sumL = 0, sumR = 0;
        for (int i = 0; i < frames; i++) { sumL += _windLeft[i] * _windLeft[i]; sumR += _windRight[i] * _windRight[i]; }
        _windLeftRms = MathF.Sqrt(sumL / frames);
        _windRightRms = MathF.Sqrt(sumR / frames);
        for (int i = 0; i < frames; i++)
            playback.PushFrame(new Vector2(_windLeft[i], _windRight[i]));
    }

#if NV_AUTOMATION
    /// <summary>A 50 ms 1 kHz tone as a 16-bit mono 44.1 kHz WAV: what the headless check's grid serves for every sound.</summary>
    private static byte[] CheckTone()
    {
        const int rate = 44100, samples = rate / 20;
        var bytes = new byte[44 + samples * 2];
        void Put(int at, string text) { for (int i = 0; i < text.Length; i++) bytes[at + i] = (byte)text[i]; }
        void Int(int at, int v, int n) { for (int i = 0; i < n; i++) bytes[at + i] = (byte)(v >> 8 * i); }
        Put(0, "RIFF"); Int(4, 36 + samples * 2, 4); Put(8, "WAVEfmt "); Int(16, 16, 4); Int(20, 1, 2); Int(22, 1, 2); Int(24, rate, 4);
        Int(28, rate * 2, 4); Int(32, 2, 2); Int(34, 16, 2); Put(36, "data"); Int(40, samples * 2, 4);
        for (int i = 0; i < samples; i++)
            Int(44 + i * 2, (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 8000), 2);
        return bytes;
    }
#endif

    private void FollowSession(ViewerSession? session, WorldSoundPlayer player)
    {
        if (_worldSoundsFor is { } old && _worldSoundHandler is { } h)
            old.Sound -= h;
        _worldSounds?.Reset();
        _worldSounds = null;
        _feet.Clear();
        while (_worldSoundEvents.TryDequeue(out _)) { }
        _worldSoundsFor = session;
        if (session is null || _soundTransport is not { } transport)
            return;
        var fetch = GridSoundFetch(session, transport);
        var settings = UserSettings(null);
        _worldSounds = new WorldSounds(settings, player, new GridSounds(fetch), new ParcelSoundMap(), () => Time.GetTicksMsec() / 1000.0)
        {
            Self = session.AgentId,
            LocateObject = id => LocateObject(session, id),
            LocateAvatar = id => session.World.AvatarOf(id) is { } a && session.Region is { } r
                ? new SoundPlace(Handle(r), a.Position - session.World.OriginOffset) : null,
            ObjectMuted = id => session.Mutes.IsMuted(id, null),
            RegionWidth = handle => session.Worlds.FirstOrDefault(w => w.Sim.Handle == handle)?.Region.SizeX ?? 256,
            OwnerSoundsMuted = id => session.Mutes.IsMuted(id, null, MuteFlag.ObjectSounds),
            TypingSound = () => TypingSound(settings),
        };
        _worldSoundHandler = e => _worldSoundEvents.Enqueue(e);
        session.Sound += _worldSoundHandler;
    }

    /// <summary>A region's handle from its grid position: the region's corner in metres, x in the high half and y in the low.</summary>
    private static ulong Handle(RegionInfo r) => (ulong)(uint)(r.GridX * 256) << 32 | (uint)(r.GridY * 256);

    private static Vector3 ToEngine3(System.Numerics.Vector3 p) => new(p.X, p.Z, -p.Y);

    private static System.Numerics.Vector3 Vector3Sl(Vector3 e) => new(e.X, -e.Z, e.Y);

    private static bool Underwater(ViewerSession s, System.Numerics.Vector3 listener) => s.Region?.WaterHeight is float w && listener.Z < w;

    // where an object is: a root by its own position, a child by its root's, an attachment by its wearer's (llaudiosourcevo.cpp getPosGlobal)
    private SoundPlace? LocateObject(ViewerSession session, Guid id)
    {
        if (session.Region is not { } region)
            return null;
        _soundSnapshot ??= session.World.Snapshot();
        _soundObjects ??= _soundSnapshot.Objects.Values.GroupBy(o => o.FullId).ToDictionary(g => g.Key, g => g.First());
        if (!_soundObjects.TryGetValue(id, out var o))
            return null;
        var origin = session.World.OriginOffset;
        for (int depth = 0; depth < 8; depth++)
        {
            if (o.WearerAgentId is { } wearer)
                return session.World.AvatarOf(wearer) is { } a ? new SoundPlace(Handle(region), a.Position - origin) : null;
            if (o.ParentLocalId == 0)
                return new SoundPlace(Handle(region), o.Position - origin);
            if (_soundSnapshot.Avatars.TryGetValue(o.ParentLocalId, out var sitter))
                return new SoundPlace(Handle(region), sitter.Position - origin);
            if (!_soundSnapshot.Objects.TryGetValue(o.ParentLocalId, out var parent))
                return null;
            if (parent.ParentLocalId == 0 && parent.WearerAgentId is null)
                return new SoundPlace(Handle(region), parent.Position + System.Numerics.Vector3.Transform(o.Position, parent.Rotation) - origin);
            o = parent;
        }
        return null;
    }

    // the typing sound plays the interface slot's choice: the grid's sound by its official id, or nothing when the slot is switched off
    private (Guid, byte[]?) TypingSound(SettingsService settings)
    {
        var info = UiSoundCatalog.Of(UiSoundSlot.Typing);
        return UiSoundSettings.Source(settings, UiSoundSlot.Typing) == UiSoundSource.Grid ? (info.GridId, null) : (Guid.Empty, null);
    }

    // each drawn avatar's feet against the ground under them, while it plays a walk, run or landing (llvoavatar.cpp updateFootstepSounds):
    // a foot joint is placed where the official viewer places it (the avatar's place less half its height, plus the joint's height over
    // the root in the pose), not where the drawing puts the body; the ground is the highest prim top within half a metre of the foot,
    // else the land, and the step plays that prim's material or the land step
    private void Footsteps(ViewerSession session, WorldSounds sounds)
    {
        if (_presenter?.Avatars is not { } avatars || session.Region is not { } region)
            return;
        var scene = avatars.Scene;
        var origin = session.World.OriginOffset;
        var seen = new HashSet<Guid>();
        foreach (var e in scene.Entries)
        {
            seen.Add(e.AgentId);
            if (!_feet.TryGetValue(e.AgentId, out var sampler))
                _feet[e.AgentId] = sampler = new FootstepSampler();
            var playing = session.World.Animations(e.AgentId);
            bool steps = StepSounds.MakesSteps(playing.Select(p => p.AnimationId));
            bool sitting = e.Seat != 0 || Viewer.Core.Avatars.AgentAnimations.IsSittingOnGround(playing);
            if (!steps || sitting)
            {
                sampler.Update(default, default, default, steps, sitting, (_, _) => null, []);
                if (e.IsSelf && _selfWalk.End() is { } walked)
                    Log.Info("sound.footsteps.walk", walked.Select(f => Viewer.Core.Diagnostics.CoreLog.F(f.Key, f.Value)).ToArray());
                continue;
            }
            if (!avatars.TryFootOfficial(e.Id.LocalId, left: true, out var l) || !avatars.TryFootOfficial(e.Id.LocalId, left: false, out var r))
                continue;
            float bodyHeight = avatars.TryOwnBody(e.Id.LocalId, out float own, out _) ? own : e.Size.Z;
            _soundSnapshot ??= session.World.Snapshot();
            var boxes = StepGround.Boxes(_soundSnapshot.Objects.Values.ToArray(), (l + r) / 2, 2f);
            var soles = avatars.TryDrawnSoles(e.Id.LocalId, out var s) ? s : (l + r) / 2;
            var hit = sampler.Update(l, r, soles, steps, sitting, (x, y) => scene.GroundAt(new System.Numerics.Vector3(x, y, 0)), boxes, bodyHeight);
            if (e.IsSelf)
            {
                if (_selfFeet.Count >= SelfFeetFrames)
                    _selfFeet.Dequeue();
                var lg = StepGround.Under(l, scene.GroundAt(l), boxes);
                var rg = StepGround.Under(r, scene.GroundAt(r), boxes);
                _selfFeet.Enqueue((Time.GetTicksMsec() / 1000.0, lg is { } a ? l.Z - a.Ground : float.NaN, rg is { } b ? r.Z - b.Ground : float.NaN, steps,
                    hit?.Foot.ToString() ?? "None", (lg?.OnLand ?? false) && (rg?.OnLand ?? false), hit?.Material ?? (lg is { OnLand: false } o ? o.Material : -1)));
                _selfWalk.Frame(lg is { } la ? l.Z - la.Ground : float.NaN, rg is { } ra ? r.Z - ra.Ground : float.NaN, sampler.LastGap, bodyHeight, e.Size.Z,
                    hit is not null, lg?.OnLand ?? false);
            }
            if (hit is null)
                continue;
            sounds.Step(e.AgentId, new SoundPlace(Handle(region), hit.At - origin), hit.OnLand, hit.Material);
        }
        foreach (var gone in _feet.Keys.Where(k => !seen.Contains(k)).ToArray())
            _feet.Remove(gone);
    }

    private bool _soundsFed;
    private readonly Dictionary<Guid, System.Threading.Tasks.Task<Viewer.Core.Assets.SoundProbe>> _probes = new();
    private System.Numerics.Vector3 _fedListener = System.Numerics.Vector3.Zero;   // set by the automation's feed (development builds)
    private readonly Dictionary<Guid, System.Numerics.Vector3> _fedObjects = new();

#if NV_AUTOMATION
    /// <summary>The automation's world_sounds command. state: the sources (where, at what gain, why silent), the decisions and the voices
    /// started; clear: forgets the decisions. Without a session (headless checks), feed puts a list of sound events (the messages as the
    /// adapter reads them) into a service made for the check, whose sounds all play a short test tone, with objects and the listener placed by
    /// the check: {events: [{kind: trigger|attached|gain|preload, sound, object, owner, gain, flags, x, y, z}], objects: [{id, x, y, z}],
    /// listener: [x, y, z]}. Answers ids, numbers and states, never sound bytes.</summary>
    public object? WorldSoundsCall(string action, System.Text.Json.JsonElement body)
    {
        switch (action)
        {
            case "state":
                return new
                {
                    in_world = _worldSoundsFor is not null,
                    fed = _soundsFed,
                    started = _worldSounds?.Started ?? 0,
                    playing = _worldPlayer?.Playing ?? 0,
                    panning = _worldPlayer?.Panning ?? 1f,
                    sources = (_worldSounds?.Sources ?? []).Select(v => new
                    {
                        origin = v.Origin.ToString(), obj = v.ObjectId, sound = v.SoundId, state = v.State.ToString(),
                        x = v.Position.X, y = v.Position.Y, z = v.Position.Z, gain = v.Gain, loop = v.Loop, why = v.Why,
                    }).ToArray(),
                    recent = (_worldSounds?.Recent ?? []).Select(d => new { origin = d.Origin.ToString(), obj = d.ObjectId, sound = d.SoundId, outcome = d.Outcome }).ToArray(),
                };
            case "clear":
                _worldSounds?.ClearRecent();
                _selfFeet.Clear();
                return new { cleared = true };
            case "object_spot" or "stand_on_object":
            {
                // a flat prim near the own avatar that a person could walk on (a floor, a deck, a platform): at least 1.5 m across, its top
                // level and above the land; stand_on_object teleports onto the middle of its top (a teleport inside the region, nothing changed)
                if (_worldSoundsFor is not { } ws || ws.World.LastSelfMotion is not { } me || ws.Region is not { } reg)
                    return new { error = "no session" };
                var snap = ws.World.Snapshot();
                var o = ws.World.OriginOffset;
                var here = me.Motion.Position + o;
                StepBox? best = null;
                foreach (var b in StepGround.Boxes(snap.Objects.Values.ToArray(), here, 100f))
                {
                    var up = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, b.Rotation);
                    float top = b.Center.Z + b.Size.Z / 2;
                    float? land = _presenter?.Avatars?.Scene.GroundAt(b.Center);
                    if (up.Z < 0.999f || b.Size.X < 1.5f || b.Size.Y < 1.5f || b.Size.Z > 2 || land is not float g || top < g + 0.3f)
                        continue;
                    if (best is null || b.Size.X * b.Size.Y > best.Value.Size.X * best.Value.Size.Y)   // the largest: room to walk
                        best = b;
                }
                if (best is not { } spot)
                    return new { found = false };
                var at = new System.Numerics.Vector3(spot.Center.X, spot.Center.Y, spot.Center.Z + spot.Size.Z / 2) - o;
                bool sent = action == "stand_on_object" && _travel is { } hub && hub.Teleport.ToHandle(Handle(reg), at + new System.Numerics.Vector3(0, 0, 1));
                return new { found = true, x = at.X, y = at.Y, z = at.Z, size_x = spot.Size.X, size_y = spot.Size.Y, material = spot.Material, sent };
            }
            case "seat":
            {
                // the own avatar's drawn pelvis and soles against the top of the prim under it (the seat), and its own and the region's heights
                if (_presenter?.Avatars is not { } av || av.Scene.Self is not { } me || _worldSoundsFor is not { } ss)
                    return new { error = "no avatar" };
                if (!av.TryPelvis(me.Id.LocalId, out var pelvis, out var drawnSoles, out float p2f))
                    return new { error = "not drawn" };
                float? top = SeatTop(ss, pelvis);
                av.TryOwnBody(me.Id.LocalId, out float bodyHeight, out float bodyHover);
                return new { seat = me.Seat, pelvis_z = pelvis.Z, soles_z = drawnSoles.Z, seat_top = top, pelvis_over_top = top is float t ? pelvis.Z - t : (float?)null,
                    body_height = bodyHeight, hover = bodyHover, region_height = me.Size.Z, pelvis_to_foot = p2f, sit_logged = _sitLogged,
                    line = me.Seat != 0 && SitReportFor(ss, av, me) is { } rep ? rep.Fields().ToDictionary(f => f.Key, f => f.Value) : null };
            }
            case "wind":
                return new { flying = _driver?.Input.Flying ?? false, max_gain = _windMax, target_gain = _wind.TargetGain, gain = _wind.Gain, frequency = _wind.Frequency,
                    pan_right = _wind.PanRight, felt_speed = _windFelt.Length(), region_wind = _worldSoundsFor?.World.Wind is not null,
                    mono = UserSettings(null).Get(AudioLevels.Mono), left_rms = _windLeftRms, right_rms = _windRightRms };
            case "feet":
                return new { frames = _selfFeet.Select(f => new { t = f.T, left = float.IsFinite(f.Left) ? f.Left : -9f, right = float.IsFinite(f.Right) ? f.Right : -9f, steps = f.Steps, foot = f.Foot, on_land = f.OnLand, material = f.Material }).ToArray() };
            case "probe":
            {
                var id = G(body, "sound");
                if (id == Guid.Empty || _soundTransport is not Viewer.Core.Assets.ISoundProbe probe)
                    return new { error = "probe needs a sound id and a session" };
                if (!_probes.TryGetValue(id, out var task))
                    _probes[id] = task = probe.ProbeSoundAsync(id, System.Threading.CancellationToken.None);
                if (!task.IsCompleted)
                    return new { pending = true };
                if (!task.IsCompletedSuccessfully)
                    return new { error = task.Exception?.GetBaseException().GetType().Name ?? "cancelled" };
                var p = task.Result;
                return new { status = p.Status, content_type = p.ContentType, size = p.Size, first_bytes = p.FirstBytes, why = p.Why,
                    grid_state = _worldSounds?.GridState(id).ToString() };
            }
            case "feed" when _worldSoundsFor is null && _worldPlayer is { } player:
            {
                if (!_soundsFed)
                {
                    _soundsFed = true;
                    var click = CheckTone();
                    var grid = new GridSounds((id, ct) => System.Threading.Tasks.Task.FromResult(Viewer.Core.Assets.AssetFetchResult.Ok(click)), b => WavFile.Parse(b) is not null);   // the check's grid serves a WAV test tone
                    _worldSounds = new WorldSounds(UserSettings(null), player, grid, new ParcelSoundMap(), () => Time.GetTicksMsec() / 1000.0)
                    {
                        LocateObject = id => _fedObjects.TryGetValue(id, out var p) ? new SoundPlace(0, p) : null,
                    };
                    player.ToEngine = ToEngine3;
                }
                if (body.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (body.TryGetProperty("listener", out var li) && li.GetArrayLength() == 3)
                        _fedListener = new(li[0].GetSingle(), li[1].GetSingle(), li[2].GetSingle());
                    if (body.TryGetProperty("objects", out var objs))
                        foreach (var o in objs.EnumerateArray())
                            _fedObjects[o.GetProperty("id").GetGuid()] = new(F(o, "x"), F(o, "y"), F(o, "z"));
                    if (body.TryGetProperty("events", out var evs))
                        foreach (var ev in evs.EnumerateArray())
                            if (FedEvent(ev) is { } e)
                                _worldSounds!.Receive(e);
                }
                return new { fed = true };
            }
            case "feed":
                return new { error = "feed works only without a session" };
            default:
                return new { error = "action must be state, clear, feed, feet or probe" };
        }

        static float F(System.Text.Json.JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetSingle() : 0f;
        static Guid G(System.Text.Json.JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetGuid(out var g) ? g : Guid.Empty;

        static TransportEvent? FedEvent(System.Text.Json.JsonElement e)
        {
            var kind = e.TryGetProperty("kind", out var k) ? k.GetString() : null;
            float gain = e.TryGetProperty("gain", out var gv) ? gv.GetSingle() : 1f;
            byte flags = e.TryGetProperty("flags", out var fv) ? fv.GetByte() : (byte)0;
            return kind switch
            {
                "trigger" => new SoundTriggered(null, G(e, "sound"), G(e, "owner"), G(e, "object"), Guid.Empty, 0, new(F(e, "x"), F(e, "y"), F(e, "z")), gain),
                "attached" => new AttachedSoundReceived(null, G(e, "object"), G(e, "sound"), G(e, "owner"), gain, flags),
                "gain" => new AttachedSoundGainChanged(null, G(e, "object"), gain),
                "preload" => new SoundPreloaded(null, G(e, "object"), G(e, "sound"), G(e, "owner")),
                _ => null,
            };
        }
    }
#endif
}
