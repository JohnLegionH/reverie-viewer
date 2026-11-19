// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// Opens every menu button's and select's menu where <see cref="DropdownPlacement"/> puts it: below the button when it fits, else
/// above it, so the menu never covers its own button (the engine would slide it up over the button). It watches every node that joins
/// the tree, so every window's menu buttons and selects follow it. Main thread only.
/// </summary>
public static class DropdownPlacer
{
    private static readonly StringName Hooked = "dropdown_placed";
    private static SceneTree? _watched;

    /// <summary>Starts watching <paramref name="tree"/> (once per tree) and hooks the buttons already in it.</summary>
    public static void Watch(SceneTree tree)
    {
        if (_watched == tree)
            return;
        _watched = tree;
        tree.NodeAdded += Hook;
        Walk(tree.Root);
    }

    private static void Walk(Node n)
    {
        Hook(n);
        foreach (var child in n.GetChildren(true))
            Walk(child);
    }

    /// <summary>Hooks one button: its menu is placed as it is about to open.</summary>
    public static void Hook(Node n)
    {
        PopupMenu? popup = n switch { MenuButton m => m.GetPopup(), OptionButton o => o.GetPopup(), _ => null };
        if (popup is null || n.HasMeta(Hooked))
            return;
        n.SetMeta(Hooked, true);
        var button = (Control)n;
        popup.AboutToPopup += () => Place(button, popup);
    }

    private static void Place(Control button, PopupMenu popup)
    {
        if (!GodotObject.IsInstanceValid(button) || !button.IsInsideTree())
            return;
        var xf = button.GetScreenTransform();
        var size = button.Size * xf.Scale;
        var anchor = new PlacementRect((int)xf.Origin.X, (int)xf.Origin.Y, (int)size.X, (int)size.Y);
        var menu = popup.GetContentsMinimumSize();
        int width = (int)System.MathF.Max(menu.X, popup.Size.X);
        // an embedded menu stays inside the viewport that draws it; a menu of its own stays on the screen
        Rect2I area = popup.IsEmbedded()
            ? new Rect2I(Vector2I.Zero, (Vector2I)button.GetViewport().GetVisibleRect().Size)
            : DisplayServer.ScreenGetUsableRect(popup.CurrentScreen);
        var (x, y) = DropdownPlacement.Place(anchor, width, (int)menu.Y, new PlacementRect(area.Position.X, area.Position.Y, area.Size.X, area.Size.Y));
        popup.Position = new Vector2I(x, y);
    }
}
