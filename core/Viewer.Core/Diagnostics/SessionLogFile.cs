// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>Gives each entry to every sink; a sink that fails does not keep the others from it.</summary>
public sealed class TeeLogSink(params ILogSink[] sinks) : ILogSink
{
    public void Write(LogEntry entry)
    {
        foreach (var sink in sinks)
        {
            try { sink.Write(entry); } catch (Exception) { /* logging never throws */ }
        }
    }
}

/// <summary>
/// The viewer's log file for this session, kept the way the official viewer keeps its log (one current log and the one before it as the
/// old log): at the start of a session the current file becomes the old file (replacing the old one before it) and a new current file is
/// begun. It holds the same redacted lines the console shows. Nothing but these two files in the folder is ever touched. A folder that
/// cannot be used gives a log that writes nothing; logging never throws.
/// </summary>
public sealed class SessionLogFile : ILogSink, IDisposable
{
    public const string CurrentName = "viewer.log", OldName = "viewer.old.log";

    private readonly object _gate = new();
    private StreamWriter? _writer;

    /// <summary>The file being written; null when the folder could not be used.</summary>
    public string? Path { get; private set; }

    private SessionLogFile() { }

    /// <summary>Starts the session's log in <paramref name="folder"/>. <paramref name="continueCurrent"/> goes on in the current file instead
    /// of starting a new one (the viewer starting itself again within one run, which must not push the earlier session's old log out).</summary>
    public static SessionLogFile Open(string folder, bool continueCurrent)
    {
        var log = new SessionLogFile();
        try
        {
            Directory.CreateDirectory(folder);
            string current = System.IO.Path.Combine(folder, CurrentName), old = System.IO.Path.Combine(folder, OldName);
            if (!continueCurrent && File.Exists(current))
            {
                try { File.Move(current, old, overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* another copy holds it: carry on in it */ }
            }
            var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            log._writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            log.Path = current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log._writer = null;
            log.Path = null;
        }
        return log;
    }

    public void Write(LogEntry entry)
    {
        lock (_gate)
        {
            if (_writer is null)
                return;
            try { _writer.WriteLine(entry.Format()); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* the disk is full or gone: the console still has the line */ }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
