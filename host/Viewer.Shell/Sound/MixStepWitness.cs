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

    /// <summary>The last callback's length in frames: the time since it started plus the time to the next one is its length in
    /// seconds (AudioDriver::get_time_to_next_mix). Rounded down, never negative.</summary>
    public static int FramesOf(double secondsSinceLastMix, double secondsToNextMix, double mixRate) =>
        (int)Math.Max(0, Math.Floor((secondsSinceLastMix + secondsToNextMix) * mixRate + 1e-6));
}
