// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// The engine side of sounds in the world: a 3D player per voice on the "World" bus, placed where the service says (its frame converted
/// to the engine's) at the gain it says. The service already applies the official fall-off with distance, so the players' own distance
/// attenuation is off and they only pan; the master level is the engine's master bus (it covers the interface sounds too, as the
/// official master level does). Grid sounds are Ogg Vorbis, decoded once per sound id (a looping and a one-shot copy); our own files are
/// WAV. Main thread only.
/// </summary>
public sealed partial class WorldSoundPlayer : Node3D, IWorldSoundOutput
{
    /// <summary>The bus every sound in the world plays on.</summary>
    public const string BusName = "World";

    private readonly SettingsService _settings;
    private readonly Func<bool> _minimized;
    private readonly Dictionary<int, AudioStreamPlayer3D> _voices = new();
    private readonly Stack<AudioStreamPlayer3D> _free = new();
    private readonly Dictionary<(Guid Id, bool Loop), AudioStream?> _streams = new();
    private float _appliedMaster = -1;
    private float _panning = 1f;

    public WorldSoundPlayer(SettingsService settings, Func<bool> minimized)
    {
        _settings = settings;
        _minimized = minimized;
        Name = "world_sounds";
    }

    /// <summary>Converts the service's frame (metres, the agent region's axes) to the engine's.</summary>
    public Func<System.Numerics.Vector3, Vector3> ToEngine { get; set; } = p => new Vector3(p.X, p.Z, -p.Y);

    /// <summary>How strongly the voices are put to one side now: 0 with Mono on (for checks).</summary>
    public float Panning => _panning;

    /// <summary>How many voices are playing now (for checks).</summary>
    public int Playing
    {
        get
        {
            int n = 0;
            foreach (var p in _voices.Values)
                if (p.Playing)
                    n++;
            return n;
        }
    }

    public override void _Ready()
    {
        int bus = AudioServer.GetBusIndex(BusName);
        if (bus < 0)
        {
            AudioServer.AddBus();
            bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(bus, BusName);
            AudioServer.SetBusSend(bus, "Master");
        }
    }

    /// <summary>Every frame: the master bus follows the master level (muted, or minimized with mute when minimized on, is silence).</summary>
    public void ApplyMaster()
    {
        // Mono: no sound in the world is put to one side, so each reaches both ears at the same gain
        float panning = AudioLevels.PanningStrength(_settings);
        if (panning != _panning)
        {
            _panning = panning;
            foreach (var v in _voices.Values) v.PanningStrength = panning;
            foreach (var v in _free) v.PanningStrength = panning;
        }
        float master = AudioLevels.MasterLevel(_settings, _minimized());
        if (master == _appliedMaster)
            return;
        _appliedMaster = master;
        int bus = AudioServer.GetBusIndex("Master");
        AudioServer.SetBusVolumeDb(bus, master > 0 ? Mathf.LinearToDb(master) : -80f);
        AudioServer.SetBusMute(bus, master <= 0);
    }

    public bool Start(int voice, Guid soundId, byte[] bytes, bool loop)
    {
        if (!_streams.TryGetValue((soundId, loop), out var stream))
            _streams[(soundId, loop)] = stream = Decode(bytes, loop);
        if (stream is null)
            return false;
        var p = _free.Count > 0 ? _free.Pop() : MakePlayer();
        p.Stream = stream;
        _voices[voice] = p;
        p.Play();
        return true;
    }

    public void Stop(int voice)
    {
        if (!_voices.Remove(voice, out var p))
            return;
        p.Stop();
        p.Stream = null;
        _free.Push(p);
    }

    public void Place(int voice, System.Numerics.Vector3 position, float gain)
    {
        if (!_voices.TryGetValue(voice, out var p))
            return;
        p.Position = ToEngine(position);
        p.VolumeDb = gain > 0 ? Mathf.LinearToDb(gain) : -80f;
    }

    public bool IsPlaying(int voice) => _voices.TryGetValue(voice, out var p) && p.Playing;

    public double PlaybackPosition(int voice) => _voices.TryGetValue(voice, out var p) && p.Playing ? p.GetPlaybackPosition() : 0;

    private AudioStreamPlayer3D MakePlayer()
    {
        var p = new AudioStreamPlayer3D
        {
            Bus = BusName,
            AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,   // the service applies the official fall-off
            MaxDistance = 0,                                                           // no far limit (OpenAL's default)
            MaxDb = 0,
            DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
            AttenuationFilterDb = 0,                                                   // no air absorption: the official engine has none
            PanningStrength = _panning,                                                // none with Mono on
        };
        AddChild(p);
        return p;
    }

    // a grid sound (Ogg Vorbis) or one of our files (WAV); null when it does not decode
    private static AudioStream? Decode(byte[] bytes, bool loop)
    {
        if (GridSounds.LooksLikeOgg(bytes))
        {
            AudioStreamOggVorbis? ogg = null;
            try { ogg = AudioStreamOggVorbis.LoadFromBuffer(bytes); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ogg = null; }
            if (ogg is null || ogg.GetLength() <= 0)
                return null;
            ogg.Loop = loop;
            return ogg;
        }
        if (WavFile.Parse(bytes) is { Bits: 16 } wav)
        {
            var stream = new AudioStreamWav
            {
                Data = wav.Pcm,
                Format = AudioStreamWav.FormatEnum.Format16Bits,
                MixRate = wav.Rate,
                Stereo = wav.Channels == 2,
            };
            if (loop)
            {
                stream.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                stream.LoopEnd = wav.Pcm.Length / (2 * Math.Max(1, wav.Channels));
            }
            return stream;
        }
        return null;
    }
}
