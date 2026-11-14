// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using System.Text.RegularExpressions;

namespace Viewer.Core.Diagnostics;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>A named value on a log entry. The value is rendered by the <see cref="Redactor"/>, never as-is.</summary>
public readonly record struct LogField(string Name, object? Value);

/// <summary>One structured, already-redacted log entry.</summary>
public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Source, string Event, IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(At.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture))
          .Append(' ').Append(Level.ToString().ToUpperInvariant()).Append(' ').Append(Source).Append(' ').Append(Event);
        foreach (var f in Fields)
            sb.Append(' ').Append(f.Key).Append('=').Append(f.Value.Contains(' ') ? "\"" + f.Value + "\"" : f.Value);
        return sb.ToString();
    }
}

/// <summary>Where entries go (a file writer in the host, a list in tests). Receives only redacted entries.</summary>
public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>
/// Structured logging with redaction by construction (ADR-019). There is no free-text message: an entry is an event
/// name (a fixed identifier such as "session.stage") plus named fields, and every field value goes through the
/// <see cref="Redactor"/>. An event or field name that is not a plain identifier is replaced, so text cannot be
/// smuggled in through the name either. Logging never throws.
/// One instance per session (its <see cref="Source"/> carries the session's log tag), so entries never cross sessions
/// in memory; the sink decides where they are written.
/// </summary>
public sealed class CoreLog
{
    private static readonly Regex Identifier = new(@"^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.Compiled);

    private readonly ILogSink _sink;
    private readonly Redactor _redactor;
    private readonly Func<DateTimeOffset> _clock;

    public string Source { get; }

    public CoreLog(ILogSink sink, string source, Redactor? redactor = null, Func<DateTimeOffset>? clock = null)
    {
        _sink = sink;
        _redactor = redactor ?? Redactor.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Source = Identifier.IsMatch(source) ? source : "invalid-source";
    }

    /// <summary>A log for a narrower source (e.g. one session), same sink and redactor.</summary>
    public CoreLog For(string source) => new(_sink, source, _redactor, _clock);

    public static LogField F(string name, object? value) => new(name, value);

    public void Debug(string evt, params LogField[] fields) => Write(LogLevel.Debug, evt, null, fields);
    public void Info(string evt, params LogField[] fields) => Write(LogLevel.Info, evt, null, fields);
    public void Warn(string evt, params LogField[] fields) => Write(LogLevel.Warn, evt, null, fields);
    public void Error(string evt, Exception? ex, params LogField[] fields) => Write(LogLevel.Error, evt, ex, fields);

    private void Write(LogLevel level, string evt, Exception? ex, LogField[] fields)
    {
        try
        {
            var rendered = new List<KeyValuePair<string, string>>(fields.Length + 1);
            foreach (var f in fields)
                rendered.Add(new(Identifier.IsMatch(f.Name) ? f.Name : "invalid-field", _redactor.Render(f.Value)));
            if (ex is not null)
                rendered.Add(new("error", _redactor.Render(ex)));
            _sink.Write(new LogEntry(_clock(), level, Source, Identifier.IsMatch(evt) ? evt : "invalid-event", rendered));
        }
        catch
        {
            // A failing sink must never take a session down.
        }
    }
}
