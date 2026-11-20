// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The theme type variations of the Reverie components, by name, as the engine theme defines them.</summary>
public static class ReverieVariations
{
    public const string Primary = "RvPrimary", Secondary = "RvSecondary", IconButton = "RvIconButton", ListRow = "RvListRow",
        Muted = "RvMuted", Caption = "RvCaption", CardHeading = "RvCardHeading", Popup = "RvPopup",
        Select = "RvSelect", Field = "RvField", List = "RvList", Tree = "RvTree", Tabs = "RvTabs", MenuPanel = "RvMenuPanel",
        Check = "RvCheck", TabBar = "RvTabBar", SectionHeader = "RvSectionHeader", TextArea = "RvTextArea";
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
    public static string? VariationFor(string kind, string current, bool flat, RestylePlace place = RestylePlace.Window, bool toggle = false)
    {
        current ??= "";
        // a button that stays pressed to show which page or preset is chosen (a sidebar, a set of presets) is a list row: no outline
        // when not chosen, the selected surface when chosen, as the Gestures window's rows (UI spec section 4, List row)
        if (toggle && kind == "Button" && current.Length == 0 && place == RestylePlace.Window)
            return ReverieVariations.ListRow;
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
            // a text area (the notecard, the script editor, a certificate text) has the field's face; a code editor keeps its own colours
            ("TextEdit" or "CodeEdit", "") => ReverieVariations.TextArea,
            ("Tree", "") => ReverieVariations.Tree,
            ("TabContainer", "") => ReverieVariations.Tabs,
            ("PopupMenu", "") => ReverieVariations.Popup,
            // a check box is a tick and its label, as the official viewer draws one, with no bar around the row
            ("CheckBox", "") => ReverieVariations.Check,
            // one tab style: switching views within a window is the segmented control
            ("TabBar", "") => ReverieVariations.TabBar,
            _ => null,
        };
    }

    /// <summary>Whether a window id names a window made once per item (a notecard, a script), which takes the restyle by its prefix.</summary>
    public static bool IsPerItemWindow(string id)
    {
        foreach (var prefix in PerItemPrefixes)
            if (id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The prefixes of the windows made once per item: "notecard.N" and "script.N". A later step adds the add-on windows' prefix
    /// when those windows take the restyle.</summary>
    private static readonly string[] PerItemPrefixes = ["notecard.", "script."];

    /// <summary>The variation a control takes when window code sets <paramref name="classic"/> on it after the restyle ran.</summary>
    public static string AfterSet(string kind, string classic, bool flat, RestylePlace place, bool toggle, bool restyled) =>
        restyled ? VariationFor(kind, classic, flat, place, toggle) ?? classic : classic;

    /// <summary>Whether a button standing in a column (where it would be stretched to the column's width) is sized to its label
    /// instead: every push button, not a row that stays pressed, and not one its window asks to grow.</summary>
    public static bool SizedToLabel(string kind, bool toggle, bool inColumn, bool expands) =>
        kind is "Button" or "MenuButton" && !toggle && inColumn && !expands;
}
