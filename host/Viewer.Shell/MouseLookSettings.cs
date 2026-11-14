// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The mouse-look and crosshair values in force.</summary>
public readonly record struct MouseLookValues(bool Enabled, bool Crosshairs, float Sensitivity, bool Invert, bool Smooth);

/// <summary>
/// The mouse-look settings in force: the saved Preferences values, except where the older environment variable of the same meaning
/// is set (NV_ENABLE_MOUSELOOK, NV_SHOW_CROSSHAIRS, NV_MOUSE_SENSITIVITY, NV_INVERT_MOUSE, NV_MOUSE_SMOOTH), which wins so tests
/// and tools keep working. A variable with a value that does not read is ignored.
/// </summary>
public static class MouseLookSettings
{
    public static MouseLookValues Resolve(SettingsService settings, Func<string, string?> env)
    {
        float sensitivity = (float)settings.Get(Settings.MouseSensitivity);
        if (float.TryParse(env("NV_MOUSE_SENSITIVITY"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) && float.IsFinite(s))
            sensitivity = Math.Clamp(s, 0f, 15f);
        return new MouseLookValues(
            Flag(env("NV_ENABLE_MOUSELOOK")) ?? settings.Get(Settings.EnableMouselook),
            Flag(env("NV_SHOW_CROSSHAIRS")) ?? settings.Get(Settings.ShowCrosshairs),
            sensitivity,
            Flag(env("NV_INVERT_MOUSE")) ?? settings.Get(Settings.InvertMouse),
            Flag(env("NV_MOUSE_SMOOTH")) ?? settings.Get(Settings.MouseSmooth));
    }

    private static bool? Flag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "0" or "false" or "off" or "no" => false,
        "1" or "true" or "on" or "yes" => true,
        _ => null,
    };
}
