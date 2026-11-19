// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The Reverie look given to a window built with the classic theme: every control in it, and every control added to it later (rows
/// made when a list fills), takes the Reverie component of its role (<see cref="ReverieRestyleRules"/>). Only the theme type
/// variation changes: no control is made, moved or removed, and no text, state or handler is touched, so the window does what it did.
/// Main thread only.
/// </summary>
public static class ReverieRestyle
{
    /// <summary>The windows a resident uses most, which take the Reverie look now; the rest follow in a later step.</summary>
    public static readonly IReadOnlySet<string> FirstHalf = new HashSet<string>(StringComparer.Ordinal)
    {
        InventoryFeature.FloaterId, ChatFeature.NearbyId, ChatFeature.ConversationsId, PeopleFeature.PeopleId,
        TravelFeature.MiniMapId, TravelFeature.MapId, TravelFeature.TeleportId, AppearanceFeature.FloaterId, OutfitFeature.FloaterId,
        NotificationFeature.ListId, PreferencesFeature.FloaterId, InteractFeature.InspectId, InteractFeature.PayId, InteractFeature.LandId,
        Main.TexturePreviewId,
    };

    private static readonly StringName Restyled = "reverie_restyled";

    /// <summary>Off only in the render harness, to list a window's controls as the classic look built them (the before list).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Restyles a window's content (and footer) when the window is one of <see cref="FirstHalf"/>.</summary>
    public static void ApplyIfListed(string floaterId, Control content)
    {
        if (FirstHalf.Contains(floaterId))
            Apply(content);
    }

    /// <summary>Restyles <paramref name="root"/> and everything under it, now and as children are added.</summary>
    public static void Apply(Node root, RestylePlace place = RestylePlace.Window)
    {
        if (Enabled)
            Visit(root, place);
    }

    private static void Visit(Node node, RestylePlace place)
    {
        if (node.HasMeta(Restyled))
            return;
        node.SetMeta(Restyled, true);
        Restyle(node, place);
        node.ChildEnteredTree += child => Visit(child, place);
        // internal children too: an option button's list and a field's own menu are popups of the dropdown menu's look
        foreach (var child in node.GetChildren(true))
            Visit(child, place);
    }

    private static void Restyle(Node node, RestylePlace place)
    {
        switch (node)
        {
            // a number box's own field and a tree's rename field are sized by their owner: they keep the owner's look
            case LineEdit when node.GetParent() is SpinBox or Window:
                break;
            case Control control:
                bool toggle = control is BaseButton { ToggleMode: true };
                if (ReverieRestyleRules.VariationFor(control.GetClass(), control.ThemeTypeVariation.ToString(), control is BaseButton { } b && b is Button { Flat: true }, place, toggle) is { } v)
                    control.ThemeTypeVariation = v;
                // a push button in a column is sized to its label, never stretched to the column's width
                if (place == RestylePlace.Window && ReverieRestyleRules.SizedToLabel(control.GetClass(), toggle, control.GetParent() is VBoxContainer,
                        expands: control.SizeFlagsHorizontal != Control.SizeFlags.Fill))
                    control.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
                break;
            case PopupMenu popup:
                if (ReverieRestyleRules.VariationFor(popup.GetClass(), popup.ThemeTypeVariation.ToString(), false, place) is { } p)
                    popup.ThemeTypeVariation = p;
                break;
        }
    }
}
