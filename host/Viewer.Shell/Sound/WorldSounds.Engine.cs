// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Session;

namespace Viewer.Shell.Sound;

public sealed partial class WorldSounds
{
    private readonly SettingsService _settings;
    private readonly IWorldSoundOutput _output;
    private readonly GridSounds _grid;
    private readonly ParcelSoundMap _parcels;
    private readonly Func<double> _clock;
    private readonly Dictionary<Guid, Source> _attached = new();
    private readonly List<Source> _shots = new();
    private readonly Dictionary<Guid, (AttachedSoundReceived Event, double At)> _postponed = new();
    private readonly HashSet<Guid> _typing = new();
    private readonly List<WorldSoundDecision> _recent = new();
    private int _nextVoice = 1;

    public WorldSounds(SettingsService settings, IWorldSoundOutput output, GridSounds grid, ParcelSoundMap parcels, Func<double> clock)
    {
        _settings = settings;
        _output = output;
        _grid = grid;
        _parcels = parcels;
        _clock = clock;
    }

    /// <summary>The own agent (its typing stays an interface sound, and its own gesture sounds always play).</summary>
    public Guid Self { get; set; }

    /// <summary>The region the agent is in; positions given to the engine side are in its frame.</summary>
    public ulong AgentRegion { get; set; }

    /// <summary>Where an object is, by its full id; an attachment answers its wearer's place (as the official viewer places it). Null: not in view.</summary>
    public Func<Guid, SoundPlace?> LocateObject { get; set; } = _ => null;

    /// <summary>Where an avatar is, by its agent id. Null: not in view.</summary>
    public Func<Guid, SoundPlace?> LocateAvatar { get; set; } = _ => null;

    /// <summary>Whether an object is blocked (the block list by id).</summary>
    public Func<Guid, bool> ObjectMuted { get; set; } = _ => false;

    /// <summary>A region's width in metres, by region handle (a parcel overlay is laid out for its region's real size).</summary>
    public Func<ulong, int> RegionWidth { get; set; } = _ => 256;

    /// <summary>Whether a resident's object sounds are blocked (blocked outright, or the object-sounds flag).</summary>
    public Func<Guid, bool> OwnerSoundsMuted { get; set; } = _ => false;

    /// <summary>The typing sound: the grid's id and, when it plays our file, its bytes.</summary>
    public Func<(Guid Id, byte[]? Ours)> TypingSound { get; set; } = () => (Guid.Empty, null);

    /// <summary>The sources now.</summary>
    public IReadOnlyList<WorldSoundView> Sources => _attached.Values.Concat(_shots).Select(s => s.View()).ToArray();

    /// <summary>The latest decisions, oldest first.</summary>
    public IReadOnlyList<WorldSoundDecision> Recent => _recent.ToArray();

    /// <summary>How many voices the engine side was asked to start.</summary>
    public int Started { get; private set; }

    public void ClearRecent() => _recent.Clear();

    /// <summary>Where the grid's copy of a sound stands (for checks).</summary>
    public GridSoundState GridState(Guid soundId) => _grid.StateOf(soundId);

    /// <summary>Takes one session event (sounds, parcels, animations); others are ignored.</summary>
    public void Receive(TransportEvent e)
    {
        switch (e)
        {
            case SoundTriggered t:
                Trigger(t);
                break;
            case AttachedSoundReceived a:
                if (LocateObject(a.ObjectId) is null)
                {
                    // the object has not arrived yet: keep the newest sound for it a while (a null sound drops the kept one)
                    if (a.SoundId != Guid.Empty)
                        _postponed[a.ObjectId] = (a, _clock());
                    else
                        _postponed.Remove(a.ObjectId);
                    Decide(WorldSoundOrigin.Attached, a.ObjectId, a.SoundId, "postponed");
                }
                else
                    Attach(a);
                break;
            case AttachedSoundGainChanged g:
                if (_attached.TryGetValue(g.ObjectId, out var src))
                    src.Gain = Clamp01(g.Gain);
                break;
            case SoundPreloaded pre:
                if (LocateObject(pre.ObjectId) is not null && !ObjectMuted(pre.ObjectId) && !OwnerSoundsMuted(pre.OwnerId))
                {
                    _grid.Request(pre.SoundId);
                    Decide(WorldSoundOrigin.Attached, pre.ObjectId, pre.SoundId, "preload");
                }
                break;
            case AgentParcelChanged parcel:
                int perEdge = parcel.Bitmap is { Length: > 0 } b ? (int)Math.Round(Math.Sqrt(b.Length * 8.0)) : 64;
                _parcels.SetAgentParcel(parcel.Sim.Handle, parcel.SoundLocal, parcel.Bitmap, (int)(perEdge * ParcelSoundMap.GridStep));
                break;
            case ParcelOverlayReceived overlay when overlay.Sim is { } sim:
                _parcels.SetOverlay(sim.Handle, overlay.Chunk, overlay.Data, RegionWidth(sim.Handle));   // a packet does not say how large its region is
                break;
            case AvatarAnimationsReceived an:
                bool typing = an.Playing.Any(p => p.AnimationId == StepSounds.Type);
                bool started = false;
                if (typing)
                    started = _typing.Add(an.AgentId);
                else
                    _typing.Remove(an.AgentId);
                if (started && an.AgentId != Self)
                    Typing(an.AgentId);
                break;
        }
    }

    /// <summary>An avatar's foot came down: plays the step sound there (land, or the material of the object stood on).</summary>
    public void Step(Guid avatarId, SoundPlace foot, bool onLand, int material)
    {
        var sound = StepSounds.For(onLand, material);
        if (!_parcels.CanHear(foot.RegionHandle, foot.Local))
            Decide(WorldSoundOrigin.Step, avatarId, sound, "parcel");
        else if (OwnerSoundsMuted(avatarId))
            Decide(WorldSoundOrigin.Step, avatarId, sound, "owner muted");
        else
            AddShot(WorldSoundOrigin.Step, avatarId, avatarId, sound, null, FootstepDetector.StepGain, foot, WorldSoundKind.Ambient);
    }

    /// <summary>Every frame: the listener (the camera, in the agent region's frame), whether it is under water. Starts, places and stops voices.</summary>
    public void Update(Vector3 listener, bool underwater)
    {
        double now = _clock();

        foreach (var (id, kept) in _postponed.ToArray())
        {
            if (now - kept.At > PostponeSeconds)
            {
                _postponed.Remove(id);
                Decide(WorldSoundOrigin.Attached, id, kept.Event.SoundId, "postpone expired");
            }
            else if (LocateObject(id) is not null)
            {
                _postponed.Remove(id);
                Attach(kept.Event);
            }
        }

        _grid.Pump();   // after the kept sounds, so a fetch they started that is already answered counts this frame
        var all = _attached.Values.Concat(_shots).ToList();

        // where each source is, whether it can be heard, and whether it has finished
        foreach (var s in all)
        {
            if (s.Voice != 0)
            {
                double pos = _output.PlaybackPosition(s.Voice);
                s.LoopedNow = pos < s.LastPosition;
                s.LastPosition = pos;
                if (!_output.IsPlaying(s.Voice))
                {
                    s.Voice = 0;
                    if (s.Queued is { } next)
                    {
                        // the queued sound follows the one that ended
                        s.SoundId = next;
                        s.Queued = null;
                        s.Born = now;
                        s.Played = false;
                    }
                    else if (!s.Loop)
                        s.State = WorldSoundState.Done;
                }
            }
            if (s.State == WorldSoundState.Done)
                continue;
            Locate(s, now);
            if (s.State == WorldSoundState.Done)
                continue;
            float distance = Vector3.Distance(s.Position, listener);
            s.Distance = distance;
            s.Why = Silence(s, distance);
            float level = AudioLevels.KindLevel(_settings, s.Kind);
            s.FinalGain = WorldSoundMath.ChannelGain(s.Gain, level, false) * WorldSoundMath.DistanceGain(distance, underwater);
            if (s.Ours is null && _grid.StateOf(s.SoundId) is GridSoundState.NotOnGrid or GridSoundState.Undecodable)
            {
                // a sound the grid lacks, a step included, plays nothing
                s.State = WorldSoundState.Done;
                Decide(s.Origin, s.ObjectId, s.SoundId, "not on grid");
            }
        }

        // the channel limit: the loudest and nearest audible sources first (llaudioengine.cpp updatePriority)
        var wanted = all.Where(s => s.State != WorldSoundState.Done && s.Why is null)
            .OrderByDescending(s => WorldSoundMath.Priority(s.Gain, s.Distance)).ToList();
        var granted = wanted.Take(WorldSoundMath.MaxChannels).ToHashSet();
        foreach (var s in wanted.Skip(WorldSoundMath.MaxChannels))
            s.Why = "no channel";

        var master = all.Where(s => s.SyncMaster && s.Voice != 0 && s.State != WorldSoundState.Done)
            .OrderByDescending(s => WorldSoundMath.Priority(s.Gain, s.Distance)).FirstOrDefault();
        foreach (var s in all)
        {
            if (s.State == WorldSoundState.Done)
                continue;
            if (!granted.Contains(s))
            {
                if (s.Voice != 0)
                {
                    _output.Stop(s.Voice);
                    s.Voice = 0;
                    if (!s.Loop)
                        s.State = WorldSoundState.Done;   // a one-shot that lost its channel does not start over
                }
                if (s.State != WorldSoundState.Done)
                    s.State = WorldSoundState.Silent;
                continue;
            }
            if (s.Voice == 0)
            {
                byte[]? bytes = s.Ours ?? _grid.BytesOf(s.SoundId);
                if (bytes is null)
                {
                    _grid.Request(s.SoundId);
                    s.State = WorldSoundState.Fetching;
                    continue;
                }
                // a sync slave starts when its master goes round (llaudioengine.cpp setupChannel); with no master it starts at once
                if (s.SyncSlave && master is not null && master != s && !master.LoopedNow)
                {
                    s.State = WorldSoundState.WaitingSync;
                    continue;
                }
                int voice = _nextVoice++;
                if (!_output.Start(voice, s.SoundId, bytes, s.Loop))
                {
                    if (s.Ours is null)
                        _grid.MarkUndecodable(s.SoundId);
                    s.State = WorldSoundState.Done;
                    Decide(s.Origin, s.ObjectId, s.SoundId, "undecodable");
                    continue;
                }
                Started++;
                s.Voice = voice;
                s.LastPosition = 0;
                s.PlayStarted = now;
                s.Played = true;
            }
            s.State = WorldSoundState.Playing;
            _output.Place(s.Voice, s.Position, s.FinalGain);
        }

        // ages (llaudioengine.cpp isDone): a one-shot that never played, a silenced one, and a non-loop that has played too long
        foreach (var s in all)
        {
            if (s.State == WorldSoundState.Done || s.Loop)
                continue;
            double age = now - s.Born;
            if (s.Voice == 0 && !s.Played && age > (s.Why is not null && s.Why != "no channel" ? MaxMutedSeconds : MaxUnplayedSeconds))
                s.State = WorldSoundState.Done;
            else if (s.Voice != 0 && now - s.PlayStarted > MaxAgeSeconds)
            {
                _output.Stop(s.Voice);
                s.Voice = 0;
                s.State = WorldSoundState.Done;
            }
        }

        foreach (var (id, s) in _attached.ToArray())
            if (s.State == WorldSoundState.Done)
                _attached.Remove(id);
        _shots.RemoveAll(s => s.State == WorldSoundState.Done);
    }

    /// <summary>Stops every voice and forgets every source (the session ended).</summary>
    public void Reset()
    {
        foreach (var s in _attached.Values.Concat(_shots))
            if (s.Voice != 0)
                _output.Stop(s.Voice);
        _attached.Clear();
        _shots.Clear();
        _postponed.Clear();
        _typing.Clear();
    }

    // ---------------- the messages ----------------

    // a one-shot at a point (llviewermessage.cpp process_sound_trigger's checks, in its order)
    private void Trigger(SoundTriggered t)
    {
        string? drop =
            !_parcels.CanHear(t.RegionHandle, t.Position) ? "parcel"
            : OwnerSoundsMuted(t.OwnerId) ? "owner muted"
            : ObjectMuted(t.ObjectId) ? "object muted"
            : t.ParentId != Guid.Empty && ObjectMuted(t.ParentId) ? "parent muted"
            : t.ObjectId == t.OwnerId && t.OwnerId != Self && !_settings.Get(AudioLevels.GestureSounds) ? "gesture sounds off"
            : CollisionSounds.IsCollision(t.SoundId) && !_settings.Get(AudioLevels.CollisionSounds) ? "collision sounds off"
            : null;
        if (drop is not null)
        {
            Decide(WorldSoundOrigin.Trigger, t.ObjectId, t.SoundId, drop);
            return;
        }
        AddShot(WorldSoundOrigin.Trigger, t.ObjectId, t.OwnerId, t.SoundId, null, t.Gain, new SoundPlace(t.RegionHandle, t.Position), WorldSoundKind.Object);
    }

    // an object's own sound (llviewerobject.cpp setAttachedSound)
    private void Attach(AttachedSoundReceived a)
    {
        if (ObjectMuted(a.ObjectId) || OwnerSoundsMuted(a.OwnerId))
        {
            Decide(WorldSoundOrigin.Attached, a.ObjectId, a.SoundId, "muted");
            return;
        }
        _attached.TryGetValue(a.ObjectId, out var src);
        bool loop = (a.Flags & SoundFlagBits.Loop) != 0;
        if (a.SoundId == Guid.Empty)
        {
            if (src is null)
                return;
            if (src.Loop || (a.Flags & SoundFlagBits.Stop) != 0)
            {
                // a loop goes at once; anything else only on the stop flag
                if (src.Voice != 0)
                    _output.Stop(src.Voice);
                _attached.Remove(a.ObjectId);
                Decide(WorldSoundOrigin.Attached, a.ObjectId, src.SoundId, "stopped");
            }
            return;
        }
        if (a.FromObjectUpdate && src is not null && src.SoundId == a.SoundId && src.State != WorldSoundState.Done)
        {
            // object updates repeat the object's sound fields: the same sound again only takes the new level and radius
            src.Gain = Clamp01(a.Gain);
            if (a.CutoffRadius > 0)
                src.Cutoff = a.CutoffRadius;
            return;
        }
        if (loop && src is { Loop: true } && src.SoundId == a.SoundId && src.State != WorldSoundState.Done)
        {
            Decide(WorldSoundOrigin.Attached, a.ObjectId, a.SoundId, "already looping");
            return;
        }
        bool queue = (a.Flags & SoundFlagBits.Queue) != 0;
        if (src is not null && queue && src.Voice != 0)
        {
            src.Queued = a.SoundId;
            src.Loop = loop;
            src.Gain = Clamp01(a.Gain);
            _grid.Request(a.SoundId);
            Decide(WorldSoundOrigin.Attached, a.ObjectId, a.SoundId, "queued");
            return;
        }
        if (src is not null && src.Voice != 0)
            _output.Stop(src.Voice);   // a new sound stops the one playing first
        _attached[a.ObjectId] = new Source
        {
            Origin = WorldSoundOrigin.Attached, ObjectId = a.ObjectId, OwnerId = a.OwnerId, SoundId = a.SoundId, Gain = Clamp01(a.Gain),
            Loop = loop, SyncMaster = (a.Flags & SoundFlagBits.SyncMaster) != 0, SyncSlave = (a.Flags & SoundFlagBits.SyncSlave) != 0,
            Cutoff = a.CutoffRadius > 0 ? a.CutoffRadius : src?.Cutoff ?? 0, Kind = WorldSoundKind.Object, Born = _clock(),
            Place = LocateObject(a.ObjectId) ?? default,
        };
        _grid.Request(a.SoundId);
        Decide(WorldSoundOrigin.Attached, a.ObjectId, a.SoundId, "accepted");
    }

    // someone else began typing (llvoavatar.cpp: the typing animation's start plays the typing sound at the avatar)
    private void Typing(Guid agent)
    {
        var (id, ours) = TypingSound();
        if (LocateAvatar(agent) is not { } place)
            return;
        if (!_parcels.CanHear(place.RegionHandle, place.Local))
            Decide(WorldSoundOrigin.Typing, agent, id, "parcel");
        else if (OwnerSoundsMuted(agent))
            Decide(WorldSoundOrigin.Typing, agent, id, "owner muted");
        else if (id != Guid.Empty || ours is not null)
            AddShot(WorldSoundOrigin.Typing, agent, agent, id, ours, 1f, place, WorldSoundKind.Object);
    }

    private void AddShot(WorldSoundOrigin origin, Guid obj, Guid owner, Guid sound, byte[]? ours, float gain, SoundPlace place, WorldSoundKind kind)
    {
        _shots.Add(new Source
        {
            Origin = origin, ObjectId = obj, OwnerId = owner, SoundId = sound, Ours = ours, Gain = Clamp01(gain), Place = place, Fixed = true,
            Kind = kind, Born = _clock(),
        });
        if (ours is null)
            _grid.Request(sound);
        Decide(origin, obj, sound, "accepted");
    }

    // ---------------- per frame ----------------

    private void Locate(Source s, double now)
    {
        if (!s.Fixed)
        {
            if (LocateObject(s.ObjectId) is { } p)
            {
                s.Place = p;
                s.GoneSince = -1;
            }
            else
            {
                // the object left view: its sound stays a little, silent, then goes
                if (s.GoneSince < 0)
                    s.GoneSince = now;
                if (now - s.GoneSince > ObjectGoneSeconds)
                {
                    if (s.Voice != 0)
                        _output.Stop(s.Voice);
                    s.Voice = 0;
                    s.State = WorldSoundState.Done;
                    return;
                }
            }
        }
        s.Position = InAgentFrame(s.Place);
    }

    // why a source cannot be heard now (null: it can): an object's sound follows its object leaving view, its cut-off radius, the block
    // list and the parcel rule every frame (llaudiosourcevo.cpp updateMute); a one-shot was judged when it came
    private string? Silence(Source s, float distance)
    {
        if (s.Origin != WorldSoundOrigin.Attached)
            return null;
        if (s.GoneSince >= 0)
            return "gone";
        if (!WorldSoundMath.InsideCutoff(distance, s.Cutoff))
            return "cutoff";
        if (ObjectMuted(s.ObjectId) || OwnerSoundsMuted(s.OwnerId))
            return "muted";
        if (!_parcels.CanHear(s.Place.RegionHandle, s.Place.Local))
            return "parcel";
        return null;
    }

    /// <summary>A place in the agent region's frame: a region handle holds the region's corner in metres (x in the high half, y in the low).</summary>
    public Vector3 InAgentFrame(SoundPlace p)
    {
        float dx = (long)(p.RegionHandle >> 32) - (long)(AgentRegion >> 32);
        float dy = (long)(p.RegionHandle & 0xFFFFFFFF) - (long)(AgentRegion & 0xFFFFFFFF);
        return p.Local + new Vector3(dx, dy, 0);
    }

    private static float Clamp01(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private void Decide(WorldSoundOrigin origin, Guid obj, Guid sound, string outcome)
    {
        _recent.Add(new WorldSoundDecision(origin, obj, sound, outcome));
        if (_recent.Count > RecentLimit)
            _recent.RemoveRange(0, _recent.Count - RecentLimit);
    }

    private sealed class Source
    {
        public WorldSoundOrigin Origin;
        public Guid ObjectId, OwnerId, SoundId;
        public Guid? Queued;
        public byte[]? Ours;
        public float Gain, Cutoff, Distance, FinalGain;
        public bool Loop, SyncMaster, SyncSlave, Fixed, LoopedNow, Played;
        public WorldSoundKind Kind;
        public SoundPlace Place;
        public Vector3 Position;
        public double Born, PlayStarted, LastPosition, GoneSince = -1;
        public int Voice;
        public WorldSoundState State = WorldSoundState.Fetching;
        public string? Why;

        public WorldSoundView View() => new(Voice, Origin, ObjectId, SoundId, State, Position, FinalGain, Loop, Why);
    }
}
