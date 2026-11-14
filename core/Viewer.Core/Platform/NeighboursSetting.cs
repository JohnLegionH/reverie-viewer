// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Platform;

/// <summary>
/// Whether the viewer connects to, and draws, the regions around the one the agent is in. On unless turned off. Order of precedence:
/// the start-up option <c>--neighbours=on</c> or <c>--neighbours=off</c>, then the key <c>neighbours</c> (true or false) in
/// <c>settings.json</c> in the per-user settings folder (<see cref="DataDirectories.Settings"/>), then the default, on. A value that
/// is not understood is ignored.
/// </summary>
public static class NeighboursSetting
{
    public const string Key = "neighbours";
    public const string Option = "--neighbours=";

    /// <summary>Pure: the setting given the start-up arguments and the text of the settings file (null when there is none).</summary>
    public static bool Resolve(IEnumerable<string> args, string? settingsJson)
    {
        foreach (var a in args)
        {
            if (!a.StartsWith(Option, StringComparison.OrdinalIgnoreCase)) continue;
            string v = a[Option.Length..];
            if (v.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            if (v.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
        }
        if (!string.IsNullOrWhiteSpace(settingsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(settingsJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(Key, out var v)
                    && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    return v.GetBoolean();
            }
            catch (System.Text.Json.JsonException) { /* an unreadable file is ignored */ }
        }
        return true;
    }

    /// <summary>The setting for this user: the start-up arguments, then the user's settings file, then on.</summary>
    public static bool ForCurrentUser(IEnumerable<string> args)
    {
        string? text = null;
        try { text = MergeSetting.ReadFile(DataDirectories.ForCurrentUser()); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { /* no usable settings folder: the default stands */ }
        return Resolve(args, text);
    }
}
