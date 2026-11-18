// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The theme type variations of the Reverie components, by name, as the engine theme defines them.</summary>
public static class ReverieVariations
{
    public const string Primary = "RvPrimary", Secondary = "RvSecondary", IconButton = "RvIconButton", ListRow = "RvListRow",
        Muted = "RvMuted", Caption = "RvCaption", CardHeading = "RvCardHeading", Popup = "RvPopup",
        Select = "RvSelect", Field = "RvField", List = "RvList", Tree = "RvTree", Tabs = "RvTabs", MenuPanel = "RvMenuPanel";
}

/// <summary>Where a control stands: in a window, or in a menu drawn as a panel of buttons (the right-click menu).</summary>
public enum RestylePlace { Window, Menu }

/// <summary>
/// How a window built with the classic theme takes the Reverie look without changing what it does: each control keeps its node,
/// name, text, state and handlers, and only its theme type variation changes, from the classic one to the Reverie component of the
/// same role (UI spec section 4: buttons, the dropdown menu, list rows, tab bar, fields). A control already in a Reverie variation,
/// or in a classic variation with no Reverie counterpart (danger buttons, the active toolbar button), keeps it. Pure.
/// </summary>
public static class ReverieRestyleRules
{
    /// <summary>The Reverie variation for a control of engine class <paramref name="kind"/> whose variation is
    /// <paramref name="current"/> ("" for none), or null to leave it as it is.</summary>
    public static string? VariationFor(string kind, string current, bool flat, RestylePlace place = RestylePlace.Window)
    {
        current ??= "";
        if (place == RestylePlace.Menu)
        {
            // the right-click menu is a panel of buttons: drawn as the dropdown menu with its rows (UI spec section 4, Dropdown menu)
            if (kind == "Button" && current.Length == 0)
                return ReverieVariations.ListRow;
            if (kind == "PanelContainer" && current.Length == 0)
                return ReverieVariations.MenuPanel;
        }
        return (kind, current) switch
        {
            ("Button", "") => flat ? ReverieVariations.IconButton : ReverieVariations.Secondary,
            ("Button", "PrimaryButton") => ReverieVariations.Primary,
            ("Button", "FlatButton" or "ToolbarButton") => ReverieVariations.IconButton,
            ("MenuButton", "") => flat ? ReverieVariations.IconButton : ReverieVariations.Secondary,
            ("OptionButton", "") => ReverieVariations.Select,
            ("Label", "MutedLabel") => ReverieVariations.Muted,
            ("Label", "CaptionLabel") => ReverieVariations.Caption,
            ("Label", "TitleLabel") => ReverieVariations.CardHeading,
            ("LineEdit", "") => ReverieVariations.Field,
            ("ItemList", "") => ReverieVariations.List,
            ("Tree", "") => ReverieVariations.Tree,
            ("TabContainer", "") => ReverieVariations.Tabs,
            ("PopupMenu", "") => ReverieVariations.Popup,
            _ => null,
        };
    }
}
