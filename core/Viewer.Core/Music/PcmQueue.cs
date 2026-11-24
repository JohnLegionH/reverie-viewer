// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Music;

/// <summary>
/// The decoded music between the thread that decodes it and the one that plays it: stereo float frames at the stream's own sample rate.
/// Writing waits while the queue holds about two seconds, so a decoder never runs ahead of the player and a station is read at the speed it plays.
/// </summary>
public sealed class PcmQueue
{
    /// <summary>The most frames held (about two seconds at 48 kHz); a writer waits for room.</summary>
    public const int Capacity = 96000;

    private readonly object _gate = new();
    private readonly float[] _data = new float[Capacity * 2];
    private int _head, _count, _rate;
    private long _total;

    /// <summary>The sample rate of the frames in the queue, 0 until the first frame is written.</summary>
    public int SampleRate { get { lock (_gate) return _rate; } }

    /// <summary>Every frame ever written since the queue was made (the count that shows audio was decoded).</summary>
    public long TotalFrames { get { lock (_gate) return _total; } }

    /// <summary>Frames waiting to be read.</summary>
    public int Buffered { get { lock (_gate) return _count; } }

    /// <summary>Throws away what is queued (a new stream is starting) and forgets the sample rate.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _head = _count = _rate = 0;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Adds <paramref name="frames"/> stereo frames at <paramref name="rate"/>; waits while the queue is full. Returns false when
    /// <paramref name="ct"/> was cancelled before every frame went in. A change of rate empties the queue first.</summary>
    public bool Write(float[] stereo, int frames, int rate, CancellationToken ct)
    {
        int done = 0;
        lock (_gate)
        {
            while (done < frames)
            {
                if (ct.IsCancellationRequested)
                    return false;
                if (rate != _rate)
                {
                    _head = _count = 0;
                    _rate = rate;
                }
                int room = Capacity - _count;
                if (room == 0)
                {
                    Monitor.Wait(_gate, 25);
                    continue;
                }
                int n = Math.Min(room, frames - done);
                for (int i = 0; i < n; i++)
                {
                    int at = (_head + _count + i) % Capacity * 2;
                    _data[at] = stereo[(done + i) * 2];
                    _data[at + 1] = stereo[(done + i) * 2 + 1];
                }
                _count += n;
                _total += n;
                done += n;
            }
        }
        return true;
    }

    /// <summary>Takes up to <paramref name="frames"/> frames into <paramref name="stereo"/>; returns how many it took.</summary>
    public int Read(float[] stereo, int frames)
    {
        lock (_gate)
        {
            int n = Math.Min(frames, _count);
            for (int i = 0; i < n; i++)
            {
                int at = (_head + i) % Capacity * 2;
                stereo[i * 2] = _data[at];
                stereo[i * 2 + 1] = _data[at + 1];
            }
            _head = (_head + n) % Capacity;
            _count -= n;
            if (n > 0)
                Monitor.PulseAll(_gate);
            return n;
        }
    }
}
