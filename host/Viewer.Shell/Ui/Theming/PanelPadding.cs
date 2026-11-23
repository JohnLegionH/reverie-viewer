// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The inner padding every window, panel, dialog and list keeps between its edge and its content, so no text touches an edge
/// (UI-STANDARD U10). One value, in interface units: the interface size scales it, so on screen it is 12 px at 100 %, 18 at 150 %, 24 at 200 %.
/// Every container that pads its content reads <see cref="Inner"/> from here instead of a number of its own. Plain data, tested without the engine.
/// </summary>
public static class PanelPadding
{
    /// <summary>The padding, in interface units.</summary>
    public const float Inner = 12;

    /// <summary>The padding on screen, in pixels, at an interface size in percent.</summary>
    public static float OnScreen(int percent) => Inner * percent / 100f;

    /// <summary>One padded surface: its name and the padding on each side, in interface units.</summary>
    public sealed record Surface(string Name, float Left, float Right, float Top, float Bottom);

    /// <summary>Every window and panel frame that pads its content, as the engine builds them.</summary>
    public static IReadOnlyList<Surface> Surfaces { get; } =
    [
        new("floater.content", Inner, Inner, Inner, Inner),
        new("floater.footer", Inner, Inner, Inner, Inner),
        new("reverie.window.content", Inner, Inner, Inner, Inner),
        new("list", Inner, Inner, Inner, Inner),
        new("tree", Inner, Inner, Inner, Inner),
        new("text.area", Inner, Inner, Inner, Inner),
        new("preferences.page", Inner, Inner, Inner, Inner),
    ];
}
