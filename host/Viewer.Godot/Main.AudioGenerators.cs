// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// The lifetime of the host's audio generators (the wind and the parcel music).
/// In Godot 4.7 an AudioStreamGeneratorPlayback keeps only a plain pointer to its AudioStreamGenerator
/// (servers/audio/effects/audio_stream_generator.h), and every mix of the playback reads the generator's rate through it
/// (AudioStreamPlaybackResampled::mix). A stopped playback is mixed once more, to fade out, before the audio server takes it off its
/// list (AudioServer::_mix_step), and at quit the engine frees the scene, and with it a player's generator, while the audio thread
/// still runs (Main::cleanup frees the main loop before audio_server->finish). A generator freed in that window is read after it
/// was freed. So the host keeps each generator in a field; to let one go it stops the player under the audio server's lock and keeps
/// the generator until a <see cref="MixStepWitness"/> has seen a whole mix step run after the stop. Only then is it released.
/// </summary>
public partial class Main
{
    /// <summary>The longest the quit waits for the audio thread to mix past the stop. A running output mixes every few
    /// milliseconds; an output that does not mix at all (no device) cannot mix the playbacks either, until the engine stops it.</summary>
    private const int AudioQuitWaitMs = 1000;

    private sealed class RetiredGenerator(AudioStreamPlayer player, AudioStreamGenerator generator, AudioStreamGeneratorPlayback? playback, MixStepWitness witness)
    {
        public AudioStreamPlayer Player { get; } = player;
        public AudioStreamGenerator Generator { get; } = generator;
        public AudioStreamGeneratorPlayback? Playback { get; } = playback;
        public MixStepWitness Witness { get; } = witness;
    }

    private readonly List<RetiredGenerator> _retiredGenerators = new();

    /// <summary>What the quit's witnesses saw for each generator released: its player, callbacks and frames since the stop.</summary>
    private readonly List<string> _releasedAtQuit = new();

    /// <summary>Stops the player and retires its generator: the fields let go at once, the generator itself stays referenced
    /// until <see cref="PruneRetiredGenerators"/> sees a whole mix step after the stop.</summary>
    private void ReleaseGenerator(AudioStreamPlayer? player, ref AudioStreamGeneratorPlayback? playback, ref AudioStreamGenerator? generator)
    {
        if (player is null || generator is null)
        {
            playback = null;
            generator = null;
            return;
        }
        double stoppedAt;
        // under the lock no driver callback is running, so every callback the witness counts started after the stop
        AudioServer.Lock();
        try
        {
            player.Stop();
            stoppedAt = Time.GetTicksUsec();
        }
        finally { AudioServer.Unlock(); }
        _retiredGenerators.Add(new RetiredGenerator(player, generator, playback, new MixStepWitness(stoppedAt)));
        playback = null;
        generator = null;
    }

    /// <summary>Releases every retired generator whose witness is done; returns how many are still waiting.</summary>
    private int PruneRetiredGenerators()
    {
        if (_retiredGenerators.Count == 0)
            return 0;
        if (LastMixCallback() is { } last)
            foreach (var r in _retiredGenerators)
                r.Witness.Observe(last.StartUsec, last.Frames);
        for (int i = _retiredGenerators.Count - 1; i >= 0; i--)
        {
            var r = _retiredGenerators[i];
            if (!r.Witness.Done)
                continue;
            _retiredGenerators.RemoveAt(i);
            if (GodotObject.IsInstanceValid(r.Player) && r.Player.Stream == r.Generator)
                r.Player.Stream = null;
            _releasedAtQuit.Add($"{r.Player.Name}:{r.Witness.Callbacks}/{r.Witness.Frames}");
            r.Playback?.Dispose();
            r.Generator.Dispose();
        }
        return _retiredGenerators.Count;
    }

    /// <summary>The audio server's last finished driver callback: its start on the engine's tick clock and its length in frames.
    /// Null when two readings disagree (a callback ran between them); the next look reads again.</summary>
    private static (double StartUsec, int Frames)? LastMixCallback()
    {
        double before = Time.GetTicksUsec();
        double since = AudioServer.GetTimeSinceLastMix();
        double toNext = AudioServer.GetTimeToNextMix();
        double sinceAgain = AudioServer.GetTimeSinceLastMix();
        double after = Time.GetTicksUsec();
        return MixStepWitness.Reading(before, since, toNext, sinceAgain, after, AudioServer.GetMixRate());
    }

    /// <summary>At quit, before the engine's shutdown: every generator is stopped and kept until the audio thread has mixed past
    /// the stop, then released. Logs quit.audio with what was released, how long the wait took, and for each generator released the
    /// driver callbacks and frames its witness counted after the stop.</summary>
    private void ReleaseAudioGenerators()
    {
        _releasedAtQuit.Clear();
        ReleaseGenerator(_windPlayer, ref _windPlayback, ref _windStream);
        ReleaseGenerator(_musicPlayer, ref _musicPlayback, ref _musicStream);
        int retired = _retiredGenerators.Count;
        var clock = Stopwatch.StartNew();
        int waiting = PruneRetiredGenerators();
        while (waiting > 0 && clock.ElapsedMilliseconds < AudioQuitWaitMs)
        {
            System.Threading.Thread.Sleep(2);
            waiting = PruneRetiredGenerators();
        }
        if (waiting > 0)
        {
            // no whole mix step seen in the time allowed (an output that is not mixing, for example a device being re-opened, can
            // start mixing again before the engine stops its audio thread). The managed wrappers are disposed when the engine's
            // scripting shuts down, before the audio thread stops (Main::cleanup: finish_languages before audio_server->finish), so
            // each generator and playback gets one engine reference of its own that is never given back: they are never freed in
            // this process, and the audio thread can never read a freed generator.
            foreach (var r in _retiredGenerators)
            {
                r.Generator.Reference();
                r.Playback?.Reference();
            }
            Log.Warn("quit.audio-not-mixed", CoreLog.F("generators", waiting), CoreLog.F("ms", clock.ElapsedMilliseconds));
            _retiredGenerators.Clear();
        }
        Log.Info("quit.audio", CoreLog.F("released", retired - waiting), CoreLog.F("kept", waiting), CoreLog.F("ms", clock.ElapsedMilliseconds),
            CoreLog.F("witnessed", _releasedAtQuit.Count == 0 ? "none" : string.Join(",", _releasedAtQuit)));
    }
}
