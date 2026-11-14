// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Actions;

/// <summary>The commands the viewer runs itself (<see cref="ILocalCommand"/>), for the pipeline's local executor. The interface hands over
/// what the system does (opening an address); the core decides which command means which.</summary>
public static class LocalCommands
{
    /// <summary>An executor for the pipeline: opening an address calls <paramref name="openUrl"/> (true when the system took it); any other command is refused.</summary>
    public static Func<TransportCommand, CommandDispatch> Executor(Func<string, bool> openUrl) => command => command switch
    {
        OpenUrlCommand open => openUrl(open.Url) ? CommandDispatch.Handed : CommandDispatch.NotSent("open_failed"),
        _ => CommandDispatch.NotSent("unknown_local_command"),
    };
}
