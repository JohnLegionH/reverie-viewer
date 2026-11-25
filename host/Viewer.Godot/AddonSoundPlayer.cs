// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Addons;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// The engine side of add-ons' sounds: a player per voice, 3D for a placed sound and plain for an ambient one, at the gain the host
/// says (distance included; the master level is the engine's master bus). A voice has a bus of its own, so its low-pass muffles only it.
/// Players and their sounds are freed when a voice stops; the buses, at most as many as the most voices at once, are used again.
/// Main thread only.
/// </summary>
public sealed partial class AddonSoundPlayer : Node3D, IAddonSoundOutput
{
    private sealed record Voice(Node Player, int Bus);

    private readonly SettingsService _settings;
    private readonly Dictionary<int, Voice> _voices = new();
    private readonly Stack<int> _freeBuses = new();   // by slot number
    private int _slots;

    public AddonSoundPlayer(SettingsService settings)
    {
        _settings = settings;
        Name = "addon_sounds";
    }

    /// <summary>Converts the current region's metres to the engine's frame.</summary>
    public Func<System.Numerics.Vector3, Vector3> ToEngine { get; set; } = p => new Vector3(p.X, p.Z, -p.Y);

    public bool Start(int voice, byte[] bytes, bool loop, System.Numerics.Vector3? position, float gain, float? lowPassHz)
    {
        if (WorldSoundPlayer.Decode(bytes, loop) is not { } stream)
            return false;
        int slot = _freeBuses.Count > 0 ? _freeBuses.Pop() : MakeBus(_slots++);
        string bus = BusName(slot);
        Node player = position is null
            ? new AudioStreamPlayer { Bus = bus, Stream = stream }
            : new AudioStreamPlayer3D
            {
                Bus = bus,
                Stream = stream,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,   // the host applies the fall-off
                MaxDistance = 0,
                MaxDb = 0,
                AttenuationFilterDb = 0,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
            };
        AddChild(player);
        _voices[voice] = new Voice(player, slot);
        Place(voice, position, gain, lowPassHz);
        if (player is AudioStreamPlayer3D placed) placed.Play();
        else ((AudioStreamPlayer)player).Play();
        return true;
    }

    public void Place(int voice, System.Numerics.Vector3? position, float gain, float? lowPassHz)
    {
        if (!_voices.TryGetValue(voice, out var v))
            return;
        float db = gain > 0 ? Mathf.LinearToDb(gain) : -80f;
        if (v.Player is AudioStreamPlayer3D placed)
        {
            if (position is { } at)
                placed.Position = ToEngine(at);
            placed.PanningStrength = AudioLevels.PanningStrength(_settings);
            placed.VolumeDb = db;
        }
        else
            ((AudioStreamPlayer)v.Player).VolumeDb = db;
        int bus = AudioServer.GetBusIndex(BusName(v.Bus));
        bool cut = lowPassHz is not null;
        if (cut && AudioServer.GetBusEffect(bus, 0) is AudioEffectLowPassFilter filter && filter.CutoffHz != lowPassHz!.Value)
            filter.CutoffHz = lowPassHz.Value;
        if (AudioServer.IsBusEffectEnabled(bus, 0) != cut)
            AudioServer.SetBusEffectEnabled(bus, 0, cut);
    }

    public void Stop(int voice)
    {
        if (!_voices.Remove(voice, out var v))
            return;
        if (v.Player is AudioStreamPlayer3D placed) placed.Stop();
        else ((AudioStreamPlayer)v.Player).Stop();
        RemoveChild(v.Player);
        v.Player.Free();
        _freeBuses.Push(v.Bus);
    }

    public bool IsPlaying(int voice) => _voices.TryGetValue(voice, out var v)
        && (v.Player is AudioStreamPlayer3D placed ? placed.Playing : ((AudioStreamPlayer)v.Player).Playing);

    private static string BusName(int slot) => "Addon" + slot;

    private static int MakeBus(int slot)
    {
        if (AudioServer.GetBusIndex(BusName(slot)) < 0)
        {
            AudioServer.AddBus();
            int bus = AudioServer.BusCount - 1;
            AudioServer.SetBusName(bus, BusName(slot));
            AudioServer.SetBusSend(bus, "Master");
            AudioServer.AddBusEffect(bus, new AudioEffectLowPassFilter());
            AudioServer.SetBusEffectEnabled(bus, 0, false);
        }
        return slot;
    }

    /// <summary>What the engine holds, for the checks: each voice's player as it is now.</summary>
    public object[] State() => _voices.Select(pair =>
    {
        int bus = AudioServer.GetBusIndex(BusName(pair.Value.Bus));
        bool cut = AudioServer.IsBusEffectEnabled(bus, 0);
        var at = (pair.Value.Player as AudioStreamPlayer3D)?.Position;
        float db = pair.Value.Player is AudioStreamPlayer3D p3 ? p3.VolumeDb : ((AudioStreamPlayer)pair.Value.Player).VolumeDb;
        return (object)new
        {
            voice = pair.Key, playing = IsPlaying(pair.Key), bus_send = AudioServer.GetBusSend(bus).ToString(), placed = at is not null, volume_db = db,
            engine_position = at is { } a ? new[] { a.X, a.Y, a.Z } : null,
            at_seconds = pair.Value.Player is AudioStreamPlayer3D s3 ? s3.GetPlaybackPosition() : ((AudioStreamPlayer)pair.Value.Player).GetPlaybackPosition(),
            low_pass = cut && AudioServer.GetBusEffect(bus, 0) is AudioEffectLowPassFilter f ? f.CutoffHz : (float?)null,
        };
    }).ToArray();
}
