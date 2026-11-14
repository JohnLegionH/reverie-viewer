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
/// players on it (so a click does not cut off an alert), our files turned into engine streams once, and the grid's Ogg Vorbis bytes
/// decoded once per sound id. Main thread only.
/// </summary>
public sealed partial class UiSoundPlayer : Node, IUiSoundOutput
{
    /// <summary>The bus every interface sound plays on.</summary>
    public const string BusName = "UI";

    private const int Voices = 6;

    private readonly SettingsService _settings;
    private readonly List<AudioStreamPlayer> _players = new();
    private readonly Dictionary<UiSoundSlot, AudioStreamWav?> _ours = new();
    private readonly Dictionary<Guid, AudioStream?> _grid = new();
    private int _next;

    public UiSoundPlayer(SettingsService settings)
    {
        _settings = settings;
        Name = "ui_sounds";
    }

    /// <summary>The Sound Tuner's values: a tuned sound plays its tuned recipe (null: the shipped files only).</summary>
    public UiSoundTuning? Tuning
    {
        get => _tuning;
        set
        {
            if (_tuning is not null)
                _tuning.Changed -= Forget;
            _tuning = value;
            if (value is not null)
                value.Changed += Forget;
        }
    }

    private UiSoundTuning? _tuning;

    // a sound whose recipe changed is made again at its next play
    private void Forget(string file)
    {
        foreach (var info in UiSoundCatalog.All.Where(i => i.File == file))
            _ours.Remove(info.Slot);
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

    public void PlayOurs(UiSoundSlot slot, float gain)
    {
        if (!_ours.TryGetValue(slot, out var stream))
            _ours[slot] = stream = Load(slot);
        if (stream is not null)
            Start(stream, gain);
    }

    public bool PlayGrid(UiSoundSlot slot, Guid id, byte[] ogg, float gain)
    {
        if (!_grid.TryGetValue(id, out var stream))
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

    /// <summary>Plays one of our files that is not an interface slot (the Sound Tuner's footstep), tuned or shipped, at a gain.</summary>
    public void PlayFile(string file, float gain)
    {
        if (Load(file) is { } stream)
            Start(stream, gain);
    }

    private AudioStreamWav? Load(UiSoundSlot slot) => Load(UiSoundCatalog.Of(slot).File);

    private AudioStreamWav? Load(string file)
    {
        if ((_tuning?.WavFor(file) ?? UiSoundFiles.Read(file)) is not { } bytes || WavFile.Parse(bytes) is not { Bits: 16 } wav)
            return null;
        return new AudioStreamWav
        {
            Data = wav.Pcm,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = wav.Rate,
            Stereo = wav.Channels == 2,
        };
    }
}
