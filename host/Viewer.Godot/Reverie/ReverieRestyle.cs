// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The Reverie look given to a window built with the classic theme: every control in it, and every control added to it later (rows
/// made when a list fills), takes the Reverie component of its role (<see cref="ReverieRestyleRules"/>). The theme type variation
/// changes, and a push button in a column is sized to its label (its horizontal size flag): no control is made, moved or removed, and
/// no text, state or handler is touched, so the window does what it did.
/// Main thread only.
/// </summary>
public static class ReverieRestyle
{
    /// <summary>The windows, by exact id, that take the Reverie look now; the rest follow in later steps. Windows made once per item
    /// (a notecard, a script, an add-on's window) are listed by prefix instead: <see cref="IsListed"/>.</summary>
    private static readonly IReadOnlySet<string> Listed = new HashSet<string>(StringComparer.Ordinal)
    {
        InventoryFeature.FloaterId, ChatFeature.NearbyId, ChatFeature.ConversationsId, PeopleFeature.PeopleId,
        TravelFeature.MiniMapId, TravelFeature.MapId, TravelFeature.TeleportId, AppearanceFeature.FloaterId, OutfitFeature.FloaterId,
        NotificationFeature.ListId, PreferencesFeature.FloaterId, InteractFeature.InspectId, InteractFeature.PayId, InteractFeature.LandId,
        Main.TexturePreviewId, ScriptQueueWindow.FloaterId,
        ColourPickerWindow.FloaterId, FacePickerWindows.ItemPickerId, FolderPickPanel.FloaterId, GridPanel.FloaterId,
        InteractFeature.ContentsId, InteractFeature.BuyId, NotificationFeature.ObjectId,
        GesturePanel.FloaterId, AddonsPanel.FloaterId, PersonalLightingFeature.FloaterId,
    };

    private static readonly StringName Restyled = "reverie_restyled";

    /// <summary>Off only in the render harness, to list a window's controls as the classic look built them (the before list).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Whether the window <paramref name="floaterId"/> takes the Reverie look: its exact id is listed, or it is one of the windows
    /// made once per item.</summary>
    public static bool IsListed(string floaterId) => Listed.Contains(floaterId) || ReverieRestyleRules.IsPerItemWindow(floaterId);

    /// <summary>Restyles a window's content (and footer) when the window is listed (<see cref="IsListed"/>).</summary>
    public static void ApplyIfListed(string floaterId, Control content)
    {
        if (IsListed(floaterId))
            Apply(content);
    }

    /// <summary>
    /// The hook for a part that is not a floater (the reconnect notice, a login dialog, a toast stack, a right-click menu): it calls this
    /// itself on its own root once built, with no window list to be on. Restyles <paramref name="root"/> and everything under it, now and
    /// as children are added.
    /// </summary>
    public static void Apply(Node root, RestylePlace place = RestylePlace.Window)
    {
        if (Enabled)
            Visit(root, place);
    }

    /// <summary>
    /// Sets a classic variation on a control after the restyle ran, and keeps the Reverie component: a restyled control that code sets
    /// back to a classic variation (a count that turns to the error look and back) would otherwise lose the restyle. A control the restyle
    /// has not reached, or one with no Reverie counterpart for that variation, takes <paramref name="classic"/> as given.
    /// </summary>
    public static void SetVariation(Control control, string classic) =>
        control.ThemeTypeVariation = Enabled && control.HasMeta(Restyled)
            ? ReverieRestyleRules.AfterSet(control.GetClass(), classic, control is Button { Flat: true }, RestylePlace.Window, control is BaseButton { ToggleMode: true }, restyled: true)
            : classic;

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
            // a colour swatch draws its colour (and the check behind a see-through one) itself: no variation, so no pill around it
            case ColourChip:
                break;
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
