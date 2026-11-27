// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell.Sound;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The witness that a stopped audio generator can no longer be mixed. Godot 4.7's audio server mixes a stopped playback once more
/// (its fade-out) in the next mix step and takes it off its playback list in that same step (servers/audio/audio_server.cpp,
/// _mix_step). A mix step is 512 frames (AudioServer::buffer_size) and runs inside a driver callback, which holds the driver lock
/// for the whole callback. So once driver callbacks that started after the stop have delivered more than 512 frames, a whole mix
/// step has started and finished after the stop. The witness asks for twice that.
/// </summary>
public class MixStepWitnessTests
{
    private const double Stop = 1_000_000;   // the stop's time, in microseconds

    [Fact]
    public void Callbacks_that_started_before_the_stop_do_not_count()
    {
        var w = new MixStepWitness(Stop);
        w.Observe(Stop - 30_000, 480);
        w.Observe(Stop - 20_000, 480);
        w.Observe(Stop - 10_000, 480);
        Assert.False(w.Done);
        Assert.Equal(0, w.Callbacks);
        w.Observe(Stop + 5_000, 480);
        w.Observe(Stop + 15_000, 480);
        w.Observe(Stop + 25_000, 480);
        Assert.True(w.Done);
        Assert.Equal(3, w.Callbacks);
        Assert.Equal(1440, w.Frames);
    }

    [Fact]
    public void Less_than_two_mix_steps_of_frames_is_not_enough()
    {
        var w = new MixStepWitness(Stop);
        w.Observe(Stop + 5_000, 480);
        w.Observe(Stop + 15_000, 480);
        Assert.False(w.Done);   // 960 frames: fewer than 2 x 512
        w.Observe(Stop + 25_000, 64);
        Assert.True(w.Done);    // 1024 frames
    }

    [Fact]
    public void The_same_callback_seen_by_several_polls_counts_once()
    {
        var w = new MixStepWitness(Stop);
        for (int i = 0; i < 5; i++)
            w.Observe(Stop + 5_000 + i * 3, 480);   // one callback, its start read five times with a few microseconds of error
        Assert.False(w.Done);
        Assert.Equal(1, w.Callbacks);
        w.Observe(Stop + 15_000, 600);
        Assert.True(w.Done);
        Assert.Equal(2, w.Callbacks);
    }

    [Fact]
    public void One_long_callback_is_enough()
    {
        var w = new MixStepWitness(Stop);
        w.Observe(Stop + 1_000, 4096);
        Assert.True(w.Done);
    }

    [Fact]
    public void Frames_of_the_last_callback_come_from_the_time_since_it_and_the_time_to_the_next()
    {
        Assert.Equal(480, MixStepWitness.FramesOf(0.004, 0.006, 48000));
        Assert.Equal(441, MixStepWitness.FramesOf(0.0, 0.01, 44100));
        Assert.Equal(0, MixStepWitness.FramesOf(0.02, -0.03, 48000));   // never negative
        Assert.Equal(479, MixStepWitness.FramesOf(0.004, 0.0059999, 48000));   // rounded down, never up
    }
}
