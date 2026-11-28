// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Sound;

/// <summary>
/// Witnesses that the audio server can no longer mix a playback that was stopped at a known time.
/// In Godot 4.7 (servers/audio/audio_server.cpp) a stopped playback is mixed once more, to fade out, in the next mix step, and that
/// same step takes it off the server's playback list; nothing mixes it after that. A mix step is <see cref="ServerBufferFrames"/>
/// frames (AudioServer::buffer_size) and runs whole inside a driver callback (AudioServer::_driver_process), which holds the driver
/// lock from start to end (the WASAPI and dummy drivers' thread_func). The server reports the start of the last callback and its
/// length through get_time_since_last_mix and get_time_to_next_mix, both read under that lock, so a callback seen there has finished.
/// Once callbacks that started after the stop have delivered more frames than one mix step holds, a whole step began and finished
/// after the stop. The witness waits for twice that, so a frame count read a little short never ends the wait early. Callbacks the
/// poller misses between two looks only mean more frames went by than counted.
/// </summary>
public sealed class MixStepWitness
{
    /// <summary>The audio server's mix step, in frames (AudioServer::init, buffer_size = 512).</summary>
    public const int ServerBufferFrames = 512;

    /// <summary>Two readings of one callback's start differ by the time between the reads; callbacks are milliseconds apart.</summary>
    private const double SameCallbackUsec = 500;

    private readonly double _stoppedAt;
    private double _lastStart = double.NegativeInfinity;

    /// <param name="stoppedAtUsec">When the playbacks were stopped (the engine's tick clock, taken under the audio server's lock).</param>
    public MixStepWitness(double stoppedAtUsec) => _stoppedAt = stoppedAtUsec;

    /// <summary>A finished driver callback, as the audio server reports it: its start and the frames it delivered.</summary>
    public void Observe(double callbackStartUsec, int frames)
    {
        if (callbackStartUsec <= _stoppedAt || callbackStartUsec <= _lastStart + SameCallbackUsec)
            return;
        _lastStart = callbackStartUsec;
        Callbacks++;
        Frames += Math.Max(0, frames);
    }

    /// <summary>A whole mix step has run since the stop: the stopped playbacks are off the server's list.</summary>
    public bool Done => Frames >= 2 * ServerBufferFrames;

    /// <summary>The callbacks counted since the stop.</summary>
    public int Callbacks { get; private set; }

    /// <summary>The frames those callbacks delivered.</summary>
    public long Frames { get; private set; }

    /// <summary>The longest a reading may take. get_time_since_last_mix waits on the driver lock, which a callback holds while it mixes,
    /// so a slow reading may come after a later callback started and give a start that is too early. A start's error is at most
    /// the reading's length; at most this, two readings of one callback stay within <see cref="SameCallbackUsec"/>.</summary>
    public const double MaxReadingUsec = 100;

    /// <summary>The last finished callback from one reading: the tick clock before it, the time since the last mix, the time to the
    /// next, the time since the last mix read again, and the clock after it. Null when the reading took longer than
    /// <see cref="MaxReadingUsec"/>, or when its two starts disagree (a callback ran between them).</summary>
    public static (double StartUsec, int Frames)? Reading(double beforeUsec, double secondsSinceLastMix, double secondsToNextMix, double secondsSinceLastMixAgain, double afterUsec, double mixRate)
    {
        if (afterUsec - beforeUsec > MaxReadingUsec)
            return null;
        double start = beforeUsec - secondsSinceLastMix * 1e6, startAgain = afterUsec - secondsSinceLastMixAgain * 1e6;
        if (Math.Abs(start - startAgain) > MaxReadingUsec)
            return null;
        return (start, FramesOf(secondsSinceLastMix, secondsToNextMix, mixRate));
    }

    /// <summary>The last callback's length in frames: the time since it started plus the time to the next one is its length in
    /// seconds (AudioDriver::get_time_to_next_mix). Rounded down, never negative.</summary>
    public static int FramesOf(double secondsSinceLastMix, double secondsToNextMix, double mixRate) =>
        (int)Math.Max(0, Math.Floor((secondsSinceLastMix + secondsToNextMix) * mixRate + 1e-6));
}
