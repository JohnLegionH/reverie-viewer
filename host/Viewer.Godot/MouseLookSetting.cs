// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Godot;
using Viewer.Core.Avatars;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The mouse-look settings in force: the Preferences values (the official viewer's settings of the same meaning, with its defaults
/// when nothing is saved), except where the older environment variable of the same meaning is set (NV_ENABLE_MOUSELOOK,
/// NV_SHOW_CROSSHAIRS, NV_MOUSE_SENSITIVITY, NV_INVERT_MOUSE, NV_MOUSE_SMOOTH), which wins so tests and tools keep working. The rule
/// itself is <see cref="MouseLookSettings"/>.
/// </summary>
internal static class MouseLookSetting
{
    /// <summary>The settings the host reads (set at start-up; null before that, which reads the defaults).</summary>
    public static SettingsService? Source { get; set; }

    private static MouseLookValues Now() =>
        MouseLookSettings.Resolve(Source ?? new SettingsService(null), System.Environment.GetEnvironmentVariable);

    public static bool Enabled() => Now().Enabled;
    public static bool ShowCrosshairs() => Now().Crosshairs;

    /// <summary>Sets the controls' mouse settings.</summary>
    public static void Apply(AgentControlInput input)
    {
        var v = Now();
        input.InvertMouse = v.Invert;
        input.SmoothMouse = v.Smooth;
        input.MouseSensitivity = v.Sensitivity;
    }
}

