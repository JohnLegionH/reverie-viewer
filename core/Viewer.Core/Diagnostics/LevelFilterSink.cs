// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Diagnostics;

/// <summary>A sink that passes on only the entries at or above a minimum level.</summary>
public sealed class LevelFilterSink(ILogSink inner, LogLevel minimum) : ILogSink
{
    /// <summary>What is shown unless a level is asked for: information and above. Debug entries are for diagnosis.</summary>
    public static LogLevel DefaultMinimum => LogLevel.Info;

    public LogLevel Minimum { get; } = minimum;

    /// <summary>A level from its name (debug, info, warn, error; any case); the default when the text is empty or not a level.</summary>
    public static LogLevel Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "info" => LogLevel.Info,
        "warn" or "warning" => LogLevel.Warn,
        "error" => LogLevel.Error,
        _ => DefaultMinimum,
    };

    public void Write(LogEntry entry)
    {
        if (entry.Level >= Minimum)
            inner.Write(entry);
    }
}
