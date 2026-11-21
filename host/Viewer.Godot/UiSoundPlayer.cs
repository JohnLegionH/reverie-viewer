// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// The engine side of the interface sounds: a "UI" audio bus whose level is the interface sound level from the settings, a few
/// players on it (so a click does not cut off an alert), and the grid's Ogg Vorbis bytes decoded once per sound id. Main thread only.
/// </summary>
public sealed partial class UiSoundPlayer : Node, IUiSoundOutput
{
    /// <summary>The bus every interface sound plays on.</summary>
    public const string BusName = "UI";

    private const int Voices = 6;

    private readonly SettingsService _settings;
    private readonly List<AudioStreamPlayer> _players = new();
    private readonly Dictionary<Guid, AudioStream?> _grid = new();   // by the bytes' content key

    /// <summary>The grid sounds started, newest last: slot, id and the MD5 of the bytes played (for checks).</summary>
    public List<(UiSoundSlot Slot, Guid Id, string Md5)> Played { get; } = new();
    private int _next;

    public UiSoundPlayer(SettingsService settings)
    {
        _settings = settings;
        Name = "ui_sounds";
    }

    /// <summary>How many sounds the engine was asked to play (for checks).</summary>
    public int Started { get; private set; }

    public override void _Ready()
    {
        EnsureBus();
        for (int i = 0; i < Voices; i++)
        {
            var p = new AudioStreamPlayer { Name = "voice_" + i, Bus = BusName };
            AddChild(p);
            _players.Add(p);
        }
        ApplyLevel();
        _settings.Changed += key =>
        {
            if (key == UiSoundSettings.Volume.Key || key == UiSoundSettings.MuteAll.Key)
                ApplyLevel();
        };
    }

    /// <summary>The UI bus, made once under the master bus.</summary>
    public static int EnsureBus()
    {
        int bus = AudioServer.GetBusIndex(BusName);
        if (bus >= 0)
            return bus;
        AudioServer.AddBus();
        bus = AudioServer.BusCount - 1;
        AudioServer.SetBusName(bus, BusName);
        AudioServer.SetBusSend(bus, "Master");
        return bus;
    }

    private void ApplyLevel()
    {
        int bus = EnsureBus();
        double level = _settings.Get(UiSoundSettings.Volume);
        AudioServer.SetBusVolumeDb(bus, level > 0 ? (float)Mathf.LinearToDb(level) : -80f);
        AudioServer.SetBusMute(bus, _settings.Get(UiSoundSettings.MuteAll));
    }

    // the bus carries the interface level; a player carries the slot's own share of the gain the service asked for
    private float PlayerDb(float gain)
    {
        double level = _settings.Get(UiSoundSettings.Volume);
        double own = level > 0 ? gain / level : 0;
        return own > 0 ? (float)Mathf.LinearToDb(own) : -80f;
    }

    public bool PlayGrid(UiSoundSlot slot, Guid id, byte[] ogg, float gain)
    {
        var key = GridSoundCache.ContentKey(ogg);   // by the bytes: the same id holds other audio on another grid
        Played.Add((slot, id, key.ToString("N")));
        if (Played.Count > UiSounds.RecentLimit)
            Played.RemoveAt(0);
        if (!_grid.TryGetValue(key, out var stream))
        {
            AudioStreamOggVorbis? decoded = null;
            try { decoded = AudioStreamOggVorbis.LoadFromBuffer(ogg); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { decoded = null; }
            _grid[id] = stream = decoded is not null && decoded.GetLength() > 0 ? decoded : null;
        }
        if (stream is null)
            return false;
        Start(stream, gain);
        return true;
    }

    private void Start(AudioStream stream, float gain)
    {
        if (_players.Count == 0)
            return;
        var p = _players[_next];
        _next = (_next + 1) % _players.Count;
        p.Stream = stream;
        p.VolumeDb = PlayerDb(gain);
        p.Play();
        Started++;
    }
}
