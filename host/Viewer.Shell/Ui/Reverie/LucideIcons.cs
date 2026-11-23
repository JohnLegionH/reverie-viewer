// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The Reverie interface's icons, by their Lucide name (Lucide, ISC licence; the files are in <c>Assets/Icons/Lucide</c>).</summary>
public static class LucideIcons
{
    public const string Chat = "message-square", Conversations = "messages-square", People = "users", Teleport = "map-pin", Map = "map",
        MiniMap = "compass", Inventory = "package", Outfits = "shirt", Appearance = "user", Overrider = "person-standing", Build = "pencil-ruler",
        AddOn = "plug", Notices = "bell", Back = "arrow-left", Home = "house", Landmark = "star", Grid = "globe", Down = "chevron-down",
        Submenu = "chevron-right", Gestures = "face-slightly-smiling", Speak = "mic", Return = "undo-2", Search = "search", Sort = "list-filter",
        Folder = "folder", Trash = "trash-2", Add = "plus", Minimise = "minus", Close = "x", Locked = "lock", Warning = "triangle-alert",
        Check = "check", Show = "eye", Hide = "eye-off", Help = "circle-question-mark", More = "ellipsis", Volume = "volume-2", Muted = "volume-x";

    /// <summary>Every icon the interface may ask for.</summary>
    public static IReadOnlyList<string> All { get; } = typeof(LucideIcons).GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Distinct().Order().ToList();

    /// <summary>The icon as svg text with its stroke drawn in <paramref name="colour"/>, or null for an unknown name.</summary>
    public static string? Svg(string name, UiColour colour)
    {
        if (!All.Contains(name))
            return null;
        using var stream = typeof(LucideIcons).Assembly.GetManifestResourceStream($"Viewer.Shell.Lucide.{name}.svg");
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        string svg = reader.ReadToEnd();
        int start = svg.IndexOf("<svg", StringComparison.Ordinal);
        return start < 0 ? null : svg[start..].Replace("currentColor", colour.Hex);
    }
}
