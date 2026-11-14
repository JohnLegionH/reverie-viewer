// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Viewer.Core.Diagnostics;

/// <summary>
/// A small file that says where a test run is, so a run that stops producing output (and is killed at its timeout)
/// still shows where it was. It holds the run's phase, the frames counted so far, the cycle and region size (lifecycle
/// runs) and the last step started, with the step's start time and frame. A write is asked for when a step starts (so a
/// step that never returns is the one named) and every <see cref="FramesPerWrite"/> frames (so the frame count shows
/// whether frames were still being drawn when it stopped).
/// The file is written by its own background thread: the calling thread only records the state and signals, so a slow
/// write (measured up to about 10 ms on Windows when written inline) never lands in a frame, and the last step is still
/// written when the calling thread stops for good right after starting it. Each write goes to a side file that then
/// replaces the heartbeat, so a kill mid-write leaves the previous whole heartbeat, never a torn one.
/// Measurement only: nothing in the run reads it back. A write that fails is counted and the run goes on.
/// Cost on the calling thread: a short lock and a signal per step and per <see cref="FramesPerWrite"/> frames; the
/// writer's own time is kept (<see cref="Writes"/>, <see cref="WriteMsTotal"/>, <see cref="WriteMsMax"/>).
/// </summary>
public sealed class RunHeartbeat : IDisposable
{
    /// <summary>Frames between frame-driven writes: at 60 frames a second, about one write a second. Steps ask for a write
    /// at once, so this only bounds how stale the frame count can be.</summary>
    public const int FramesPerWrite = 60;

    private readonly string _path, _side;
    private readonly int _pid = Environment.ProcessId;
    private readonly object _gate = new();
    private readonly Thread _writer;
    private bool _stop;
    private long _asked, _written;   // write requests made, and the last request the file reflects

    // the state, under _gate
    private string _phase = "start", _step = "";
    private long _frame, _steps, _stepFrame, _drawn;
    private string _window = "";
    private int _cycle = -1, _size;
    private DateTime _stepUtc = DateTime.UtcNow;

    // the writer's own counters (read once the writer is quiet: after Flush or Dispose)
    private int _writes, _failures;
    private double _msTotal, _msMax;

    public RunHeartbeat(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("the heartbeat path must be absolute", nameof(path));
        _path = path;
        _side = path + ".writing";
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "test run heartbeat" };
        _writer.Start();
    }

    public string FilePath => _path;
    public long Frame { get { lock (_gate) return _frame; } }
    public int Cycle { get { lock (_gate) return _cycle; } }
    public string Step { get { lock (_gate) return _step; } }
    public long Steps { get { lock (_gate) return _steps; } }
    /// <summary>Writes asked for so far (a step, a phase, every <see cref="FramesPerWrite"/> frames); the writer
    /// covers several with one write when they come faster than it writes.</summary>
    public long WritesAsked { get { lock (_gate) return _asked; } }
    public int Writes { get { lock (_gate) return _writes; } }
    public int WriteFailures { get { lock (_gate) return _failures; } }
    public double WriteMsTotal { get { lock (_gate) return _msTotal; } }
    public double WriteMsMax { get { lock (_gate) return _msMax; } }

    /// <summary>The run's phase (startup, the mode, done); written at once.</summary>
    public void SetPhase(string phase)
    {
        lock (_gate) { _phase = phase; Ask(); }
    }

    /// <summary>The lifecycle cycle and region size the next steps belong to.</summary>
    public void SetCycle(int cycle, int size)
    {
        lock (_gate) { _cycle = cycle; _size = size; }
    }

    /// <summary>A step starts; written at once, before the step's work.</summary>
    public void StartStep(string step)
    {
        lock (_gate)
        {
            _step = step;
            _steps++;
            _stepUtc = DateTime.UtcNow;
            _stepFrame = _frame;
            Ask();
        }
    }

    /// <summary>One frame drawn; written every <see cref="FramesPerWrite"/> frames.</summary>
    public void CountFrame()
    {
        lock (_gate)
        {
            _frame++;
            if (_frame % FramesPerWrite == 0) Ask();
        }
    }

    /// <summary>One frame drawn (the engine's frame-drawn signal). Frames processed and frames drawn are counted apart: a
    /// window that cannot draw keeps the first going and stops the second. Written with the frame-driven writes.</summary>
    public void CountDrawn()
    {
        lock (_gate) _drawn++;
    }

    /// <summary>Frames drawn so far.</summary>
    public long Drawn { get { lock (_gate) return _drawn; } }

    /// <summary>The window's state as text (mode, whether it can draw, focus, size, position); written with the next
    /// write, so a stopped run's file says what the window was last seen to be.</summary>
    public void SetWindow(string state)
    {
        lock (_gate) _window = state;
    }

    /// <summary>Waits until the file reflects every change asked for so far (bounded); true when it does.</summary>
    public bool Flush(int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (_written < _asked)
            {
                long left = until - Environment.TickCount64;
                if (left <= 0 || _stop) return _written >= _asked;
                Monitor.Wait(_gate, (int)left);
            }
            return true;
        }
    }

    /// <summary>Writes what is pending, then stops the writer.</summary>
    public void Dispose()
    {
        Flush();
        lock (_gate) { _stop = true; Monitor.PulseAll(_gate); }
        _writer.Join(5000);
    }

    /// <summary>The heartbeat's text: one key=value per line (the state as it is now).</summary>
    public string Text()
    {
        lock (_gate) return TextLocked();
    }

    /// <summary>Reads a heartbeat file back into its keys (for the tools and tests that read one).</summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) d[line[..eq]] = line[(eq + 1)..].TrimEnd('\r');
        }
        return d;
    }

    private void Ask()
    {
        _asked++;
        Monitor.PulseAll(_gate);
    }

    private string TextLocked()
    {
        var ci = CultureInfo.InvariantCulture;
        var t = new StringBuilder(320);
        t.Append("written_utc=").Append(DateTime.UtcNow.ToString("o", ci)).Append('\n');
        t.Append("pid=").Append(_pid.ToString(ci)).Append('\n');
        t.Append("phase=").Append(_phase).Append('\n');
        t.Append("frame=").Append(_frame.ToString(ci)).Append('\n');
        t.Append("drawn=").Append(_drawn.ToString(ci)).Append('\n');
        t.Append("window=").Append(_window.Replace('\n', ' ')).Append('\n');
        t.Append("cycle=").Append(_cycle.ToString(ci)).Append('\n');
        t.Append("size=").Append(_size.ToString(ci)).Append('\n');
        t.Append("step=").Append(_step).Append('\n');
        t.Append("step_started_utc=").Append(_stepUtc.ToString("o", ci)).Append('\n');
        t.Append("step_frame=").Append(_stepFrame.ToString(ci)).Append('\n');
        t.Append("steps=").Append(_steps.ToString(ci)).Append('\n');
        return t.ToString();
    }

    private void WriterLoop()
    {
        while (true)
        {
            string text;
            long upTo;
            lock (_gate)
            {
                while (_written >= _asked && !_stop) Monitor.Wait(_gate);
                if (_written >= _asked && _stop) return;
                text = TextLocked();   // the latest state: requests made meanwhile are covered by this one write
                upTo = _asked;
            }
            long t0 = Stopwatch.GetTimestamp();
            bool ok = true;
            try
            {
                using (var f = new FileStream(_side, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    f.Write(bytes, 0, bytes.Length);
                    f.Flush(flushToDisk: false);   // to the system: what is written survives the process being killed
                }
                File.Move(_side, _path, overwrite: true);
            }
            catch (IOException) { ok = false; }
            catch (UnauthorizedAccessException) { ok = false; }
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            lock (_gate)
            {
                _writes++;
                if (!ok) _failures++;
                _msTotal += ms;
                if (ms > _msMax) _msMax = ms;
                _written = upTo;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
