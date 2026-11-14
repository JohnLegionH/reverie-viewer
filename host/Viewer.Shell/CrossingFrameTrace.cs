// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Development aid, off by default: one line for every frame from one second before a region crossing to one second after it (<see cref="Variable"/> = 1, and a log
/// folder). The frames of the last second are kept in memory; a crossing (<see cref="Mark"/>) writes them and every frame of the next second to a file of its own
/// under the folder's <see cref="SubFolder"/>. Events noted between frames (<see cref="Note"/>) go into the next frame's line. Without the switch or without a folder
/// nothing is built, kept or written. Files are written by a worker from plain strings. Engine-free; <see cref="Frame"/>, <see cref="Mark"/> and <see cref="Note"/>
/// from one thread.
/// </summary>
public sealed class CrossingFrameTrace
{
    public const string Variable = "NV_CROSSING_FRAMES";
    public const string SubFolder = "crossing-frames";

    public static bool SwitchedOn => Environment.GetEnvironmentVariable(Variable) == "1";

    private readonly string? _folder;
    private readonly double _window;
    private readonly Queue<(double At, string Line)> _recent = new();
    private readonly List<string> _notes = new();
    private List<string>? _open;
    private string _openWhy = "";
    private double _until = double.NegativeInfinity;
    private int _count;
    private Task _writing = Task.CompletedTask;

    /// <param name="folder">The log folder (null or empty: nothing is ever written).</param>
    /// <param name="on">The switch (<see cref="SwitchedOn"/> in the viewer).</param>
    public CrossingFrameTrace(string? folder, bool on, double windowSeconds = 1.0)
    {
        _folder = on && !string.IsNullOrEmpty(folder) ? folder : null;
        _window = windowSeconds;
    }

    /// <summary>Whether anything is recorded at all.</summary>
    public bool Active => _folder is not null;

    /// <summary>Files handed to the worker so far.</summary>
    public int FilesWritten { get; private set; }

    /// <summary>An event at <paramref name="now"/> (a message applied, a hand-over step); it goes into the next frame's line.</summary>
    public void Note(double now, string text)
    {
        if (!Active) return;
        _notes.Add($"{now:0.0000} {text}");
    }

    /// <summary>A crossing at <paramref name="now"/>: the last second's frames are kept and the next second's are added; a crossing while a window is open extends it.</summary>
    public void Mark(double now, string why)
    {
        if (!Active) return;
        if (_open is null)
        {
            _open = new List<string>();
            _openWhy = why;
            foreach (var (at, line) in _recent)
                if (at >= now - _window - 1e-9) _open.Add(line);
            _recent.Clear();
        }
        _until = now + _window;
    }

    /// <summary>One frame at <paramref name="now"/>; <paramref name="line"/> is called only when the trace is on.</summary>
    public void Frame(double now, Func<string> line)
    {
        if (!Active) return;
        if (_open is not null && now > _until + 1e-9) Close();
        string text = line();
        if (_notes.Count > 0)
        {
            text += " | events: " + string.Join(" ; ", _notes);
            _notes.Clear();
        }
        if (_open is not null)
        {
            _open.Add(text);
            return;
        }
        _recent.Enqueue((now, text));
        while (_recent.Count > 0 && _recent.Peek().At < now - _window - 1e-9) _recent.Dequeue();
    }

    /// <summary>Writes a window still open and waits for the worker (tests; the viewer's shutdown).</summary>
    public void Flush()
    {
        if (_open is not null) Close();
        _writing.Wait();
    }

    private void Close()
    {
        var lines = _open!.ToArray();
        string path = Path.Combine(_folder!, SubFolder, $"{++_count:000}-{_openWhy}.txt");
        _open = null;
        FilesWritten++;
        var before = _writing;
        _writing = Task.Run(() =>
        {
            before.Wait();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines);
        });
    }
}
