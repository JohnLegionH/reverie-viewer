// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;

namespace Viewer.Shell;

/// <summary>
/// ADR-019: the route from the engine's logger callbacks (Godot's Logger._LogError / _LogMessage, in Viewer.Godot) into the
/// core's redacted log. Every piece of engine text travels as <see cref="EngineText"/>, which cannot be rendered raw: only
/// the core's Redactor reads it, scrubbing URL paths, "$1$" digests and session-id-shaped values.
/// </summary>
public sealed class EngineLogRoute(CoreLog log)
{
    private readonly CoreLog _log = log.For("engine");
    private long _errors;
    private long _warnings;

    /// <summary>Engine errors (not warnings) routed so far, from any thread (the lifecycle check wants none).</summary>
    public long Errors => System.Threading.Interlocked.Read(ref _errors);

    /// <summary>Engine warnings routed so far, from any thread.</summary>
    public long Warnings => System.Threading.Interlocked.Read(ref _warnings);

    /// <summary>Godot's Logger.ErrorType: 0 error, 1 warning, 2 script, 3 shader.</summary>
    public void Error(int errorType, string function, string file, int line, string code, string rationale)
    {
        var fields = new[]
        {
            CoreLog.F("type", errorType switch { 1 => "warning", 2 => "script", 3 => "shader", _ => "error" }),
            CoreLog.F("where", new EngineText($"{file}:{line} {function}")),
            CoreLog.F("code", new EngineText(code)),
            CoreLog.F("rationale", new EngineText(rationale)),
        };
        if (errorType == 1)
        {
            System.Threading.Interlocked.Increment(ref _warnings);
            _log.Warn("engine.warning", fields);
        }
        else
        {
            System.Threading.Interlocked.Increment(ref _errors);
            _log.Error("engine.error", null, fields);
        }
    }

    /// <summary>A printed engine message (print, printerr, the engine's own notices).</summary>
    public void Message(string message, bool isError)
    {
        if (isError)
            _log.Warn("engine.stderr", CoreLog.F("text", new EngineText(message)));
        else
            _log.Info("engine.message", CoreLog.F("text", new EngineText(message)));
    }
}
