// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Godot.Presentation;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// ADR-019: Godot's custom logger (Godot 4.5+ Logger, registered with OS.AddLogger) handing every engine error, warning
/// and printed message to <see cref="EngineLogRoute"/>, which logs it redacted. A re-entrancy guard keeps anything the
/// route itself might print from looping back.
/// </summary>
public partial class EngineLogger : Logger
{
    [ThreadStatic] private static bool _inside;
    private readonly EngineLogRoute? route;

    public EngineLogger() { }

    public EngineLogger(EngineLogRoute route) => this.route = route;

    public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify, int errorType,
        global::Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        // the backtraces (engine objects) are released on the main thread, pinned, not left to a finalizer; this may run
        // on any thread that logged
        LoggedErrorReleases.Hand(scriptBacktraces);
        if (_inside || route is null) return;
        _inside = true;
        try { route.Error(errorType, function, file, line, code, rationale); }
        finally { _inside = false; }
    }

    public override void _LogMessage(string message, bool error)
    {
        if (_inside || route is null) return;
        _inside = true;
        try { route.Message(message, error); }
        finally { _inside = false; }
    }
}
