// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Viewer.Core.Diagnostics;

namespace Viewer.Godot;

/// <summary>
/// The host's log sink for now: redacted entries to the process's stderr through System.Console, NOT through Godot's
/// print functions (those feed the engine logger, which feeds this sink). The same lines also go to the session's log file (SessionLogFile).
/// Entries below the minimum level are not written: information and above unless NV_LOG_LEVEL names another (debug, info, warn, error).
/// </summary>
public sealed class ConsoleLogSink : ILogSink
{
    private readonly LogLevel _minimum = LevelFilterSink.Parse(Environment.GetEnvironmentVariable("NV_LOG_LEVEL"));

    public void Write(LogEntry entry)
    {
        if (entry.Level < _minimum)
            return;
        try { Console.Error.WriteLine(entry.Format()); } catch { }
    }
}
