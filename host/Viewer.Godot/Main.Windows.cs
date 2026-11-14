// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

#if NV_AUTOMATION
using System;
using System.Linq;

namespace Viewer.Godot;

public partial class Main
{
    /// <summary>The open-window command (development builds only): opens any window of the shell by its id or its title (Preferences
    /// included), so a live check can capture it; without a name it lists the ids. It answers ids and flags, never text.</summary>
    public object? OpenWindowCall(string? name)
    {
        if (_shell is null)
            return null;
        var floaters = _shell.Shell.Floaters;
        if (string.IsNullOrWhiteSpace(name))
            return new { windows = floaters.Specs.Select(s => s.Id).ToArray() };
        var spec = floaters.Specs.FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_shell.Shell.Strings.Get(s.TitleKey), name, StringComparison.OrdinalIgnoreCase));
        if (spec is null)
            return new { opened = false, reason = "unknown_window" };
        floaters.Open(spec.Id);
        return new { opened = floaters.IsOpen(spec.Id), id = spec.Id };
    }
}
#endif
