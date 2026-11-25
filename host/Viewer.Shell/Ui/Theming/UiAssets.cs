// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The icons the interface draws, by the name of their file in <c>Assets/Icons</c> (Tabler Icons, outline set).</summary>
public static class UiIcons
{
    public const string Chat = "message";
    public const string Conversations = "messages";
    public const string Inventory = "package";
    public const string Outfits = "shirt";
    public const string Notifications = "bell";
    public const string People = "users";
    public const string Teleport = "map-pin";
    public const string Map = "map";
    public const string MiniMap = "compass";
    public const string Home = "home";
    public const string Preferences = "settings";
    public const string Minimize = "minus";
    public const string Restore = "restore";   // our own drawing, not a Tabler icon
    public const string Close = "x";
    public const string Connection = "wifi";
    public const string Pin = "map-pin";
    public const string Down = "chevron-down";
    public const string Account = "user";
    public const string Secure = "lock";

    /// <summary>Every icon the interface may ask for.</summary>
    public static IReadOnlyList<string> All { get; } = typeof(UiIcons).GetFields()
        .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).Distinct().Order().ToList();
}

/// <summary>The fonts and icon drawings that travel inside the assembly.</summary>
public static class UiAssets
{
    /// <summary>Stroke width of every icon, in the icon's own 24-unit grid.</summary>
    public const double IconStroke = 1.5;

    /// <summary>The icon as svg text with the stroke width set and the stroke drawn in <paramref name="colour"/>, or null for an unknown name.</summary>
    public static string? IconSvg(string name, UiColour colour)
    {
        if (!UiIcons.All.Contains(name))
            return null;
        string? svg = Text($"Viewer.Shell.Icons.{name}.svg");
        if (svg is null)
            return null;
        int start = svg.IndexOf("<svg", StringComparison.Ordinal);
        if (start < 0)
            return null;
        return svg[start..]
            .Replace("stroke-width=\"2\"", $"stroke-width=\"{IconStroke.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"")
            .Replace("currentColor", colour.Hex);
    }

    /// <summary>The font file's bytes ("Inter-Regular", "Inter-SemiBold", "NotoSans-Regular", "NotoSans-Bold"), or empty for an unknown name.</summary>
    public static byte[] Font(string name)
    {
        if (name is not ("Inter-Regular" or "Inter-SemiBold" or "NotoSans-Regular" or "NotoSans-Bold"))
            return [];
        using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream($"Viewer.Shell.Fonts.{name}.ttf");
        return stream is null ? [] : ReadAll(stream);
    }

    private static byte[] Bytes(string resource)
    {
        using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream(resource);
        return stream is null ? [] : ReadAll(stream);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The login screen's default background picture (JPEG bytes), or empty if it is missing.</summary>
    public static byte[] DefaultSplash() => Bytes("Viewer.Shell.Splash.login-splash.jpg");

    private static string? Text(string resource)
    {
        using var stream = typeof(UiAssets).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
