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
    public void A_reading_that_took_longer_than_a_tenth_of_a_millisecond_is_refused()
    {
        // quick: the start is the clock before the read less the time since the last mix; frames from both times
        var quick = MixStepWitness.Reading(10_000, 0.004, 0.006, 0.00402, 10_020, 48000);
        Assert.NotNull(quick);
        Assert.Equal(6_000, quick.Value.StartUsec, 1);
        Assert.Equal(480, quick.Value.Frames);
        // the read waited on the driver lock for 3 ms (a mix in progress): its start would be 3 ms early, so it is refused
        Assert.Null(MixStepWitness.Reading(10_000, 0.001, 0.009, 0.001, 13_000, 48000));
        // the two readings of the start disagree (a callback ran between them)
        Assert.Null(MixStepWitness.Reading(10_000, 0.009, 0.001, 0.0001, 10_050, 48000));
    }

    [Fact]
    public void A_callback_read_late_and_again_on_time_counts_once()
    {
        var w = new MixStepWitness(Stop);
        // one 480-frame callback that started at Stop + 5 ms: a poll blocked on the lock during the next mix reads it 3 ms late
        foreach (var r in new[]
                 {
                     MixStepWitness.Reading(Stop + 9_000, 0.001, 0.009, 0.001, Stop + 12_000, 48000),   // refused
                     MixStepWitness.Reading(Stop + 9_000, 0.004, 0.006, 0.00401, Stop + 9_010, 48000),
                     MixStepWitness.Reading(Stop + 12_000, 0.007, 0.003, 0.00701, Stop + 12_010, 48000),
                 })
            if (r is { } got)
                w.Observe(got.StartUsec, got.Frames);
        Assert.Equal(1, w.Callbacks);
        Assert.Equal(480, w.Frames);
        Assert.False(w.Done);
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
