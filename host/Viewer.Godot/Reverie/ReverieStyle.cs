// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// The Reverie components' looks, as type variations of the one engine theme (added by <see cref="ShellTheme.Build"/>): every
/// colour, radius and size comes from the design tokens (<see cref="ReverieTheme"/>), for the variant whose bar the current tokens
/// carry and with their accent. States: hover is the selected surface at 60 %, pressed and open the selected surface, keyboard focus
/// a 2-pixel accent outline 2 pixels outside the control (never removed), disabled at the one disabled strength (<see cref="ControlStates"/>, 45 %) with no hover.
/// </summary>
public static class ReverieStyle
{
    public const string TopBar = "RvTopBar", MenuTitle = "RvMenuTitle", MenuTitleOpen = "RvMenuTitleOpen", AppTitle = "RvAppTitle", AppTitleOpen = "RvAppTitleOpen", AppName = "RvAppName",
        IconButton = "RvIconButton", Location = "RvLocation", LocationEdit = "RvLocationEdit", LocationButton = "RvLocationButton",
        Region = "RvRegion", Status = "RvStatus", Muted = "RvMuted", Primary = "RvPrimary", Secondary = "RvSecondary", Chip = "RvChip",
        Card = "RvCard", CardWarning = "RvCardWarning", WarnText = "RvWarnText", CardHeading = "RvCardHeading", ListRow = "RvListRow",
        ListRowSelected = "RvListRowSelected", Sidebar = "RvSidebar", Overline = "RvOverline", Track = "RvTrack", Segment = "RvSegment",
        SegmentSelected = "RvSegmentSelected", Window = "RvWindow", TitleBar = "RvTitleBar", WindowTitle = "RvWindowTitle",
        Popup = "RvPopup", Slider = "RvSlider", Switch = "RvSwitch", Stepper = "RvStepper", Caption = "RvCaption",
        Dock = "RvDock", DockButton = "RvDockButton", DockButtonOn = "RvDockButtonOn", DockButtonAttention = "RvDockButtonAttention",
        DockLabel = "RvDockLabel", DockDivider = "RvDockDivider", DockDot = "RvDockDot", Pill = "RvPill",
        Select = "RvSelect", Field = "RvField", List = "RvList", TreeList = "RvTree", Tabs = "RvTabs", MenuPanel = "RvMenuPanel",
        Check = "RvCheck", TabBar = "RvTabBar", SectionHeader = "RvSectionHeader", SectionDivider = "RvSectionDivider", TextArea = "RvTextArea";

    /// <summary>The variant the tokens are: light when the bar is light.</summary>
    public static ReverieVariant VariantOf(ThemeTokens t) => ReverieTheme.VariantFor(t);

    /// <summary>The design tokens behind the current engine tokens.</summary>
    public static ReverieTokens TokensFor(ThemeTokens t) => ReverieTheme.LookFor(t).Tokens;

    // the look being built: its border width, the factor on every type size, and whether rows and segments are outlined (set at the
    // start of Add, read by the helpers below; main thread only, as the theme is built)
    private static int _bw = 1;
    private static float _typeScale = 1f;
    private static bool _outlined;

    private static Color C(UiColour c) => ShellTheme.C(c);

    private static Color A(UiColour c, float alpha) => new(c.R, c.G, c.B, alpha);

    private static int Px(float v) => (int)MathF.Round(v);

    /// <summary>A type size of the design, times the look's text factor.</summary>
    private static int Type(float size) => Px(size * _typeScale);

    /// <summary>A face: fill, border colour and width, radius, and content margins left-right and top-bottom.</summary>
    public static StyleBoxFlat Face(Color fill, Color border, int borderWidth, int radius, float marginH, float marginV)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = border, AntiAliasing = true };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        box.ContentMarginLeft = box.ContentMarginRight = marginH;
        box.ContentMarginTop = box.ContentMarginBottom = marginV;
        return box;
    }

    /// <summary>The keyboard focus ring: a 2-pixel accent line 2 pixels outside the control.</summary>
    public static StyleBoxFlat FocusRing(ReverieTokens r, int radius)
    {
        var box = new StyleBoxFlat { DrawCenter = false, BorderColor = C(r.Accent), AntiAliasing = true };
        box.SetBorderWidthAll(2);
        box.SetCornerRadiusAll(radius + 4);
        box.SetExpandMarginAll(4);
        return box;
    }

    /// <summary>The floating shadow: 8 down, 24 blur, black at 35 %.</summary>
    public static void Shadow(StyleBoxFlat box)
    {
        box.ShadowColor = new Color(0, 0, 0, ReverieTheme.ShadowOpacity);
        box.ShadowSize = ReverieTheme.ShadowBlur / 2;
        box.ShadowOffset = new Vector2(0, ReverieTheme.ShadowOffsetY);
    }

    public static void Add(Theme theme, ThemeTokens tokens)
    {
        // a look that keeps its own (high contrast) gives its colours, border width and text size; rows and segments then carry an
        // outline, the selected one in the accent, as its classic controls do
        var look = ReverieTheme.LookFor(tokens);
        var r = look.Tokens;
        _bw = look.BorderWidth;
        _typeScale = look.TypeScale;
        _outlined = look.OutlinedRows;
        int bw = _bw;
        var rowBorder = _outlined ? C(r["border.control"]) : A(r["surface.base"], 0f);
        var rowSelectedBorder = _outlined ? C(r.Accent) : A(r["surface.base"], 0f);
        int rowBw = _outlined ? bw : 0;
        var clear = A(r["surface.base"], 0f);
        var hover = A(r["surface.selected"], 0.6f);
        var selected = C(r["surface.selected"]);
        int control = ReverieTheme.RadiusControl;
        var semibold = Fonts.SemiBold;

        // bars and windows
        Panel(theme, TopBar, Edge(C(r["surface.base"]), C(r["border.default"]), bottom: 1, margin: 8));
        var window = Face(C(r["surface.base"]), C(r["border.control"]), bw, ReverieTheme.RadiusWindow, 0, 0);
        Shadow(window);
        Panel(theme, Window, window);
        var titleBar = Face(clear, C(r["border.default"]), 0, 0, 12, 6);
        titleBar.BorderWidthBottom = 1;
        titleBar.CornerRadiusTopLeft = titleBar.CornerRadiusTopRight = ReverieTheme.RadiusWindow;
        Panel(theme, TitleBar, titleBar);
        LabelVariation(theme, WindowTitle, r["text.primary"], ReverieTheme.TypeTitle, semibold);
        Panel(theme, Sidebar, Face(C(r["surface.sunken"]), C(r["surface.sunken"]), 0, 0, 6, 6));
        Panel(theme, Card, Face(C(r["surface.card"]), C(r["border.default"]), bw, ReverieTheme.RadiusCard, 14, 12));
        Panel(theme, CardWarning, Face(C(r["warn.surface"]), C(r["warn.border"]), bw, ReverieTheme.RadiusCard, 14, 12));
        Panel(theme, Track, Face(C(r["surface.raised"]), C(r["border.control"]), bw, ReverieTheme.RadiusTrack, 2, 2));
        Panel(theme, Location, Face(C(r["surface.raised"]), C(r["border.control"]), bw, Px(ReverieTheme.LocationHeight / 2), 10, 0));

        // text
        LabelVariation(theme, AppName, r["text.primary"], ReverieTheme.TypeMenu, semibold);
        LabelVariation(theme, Region, r["text.primary"], ReverieTheme.TypeStatus, semibold);
        LabelVariation(theme, Status, r["text.secondary"], ReverieTheme.TypeStatus, null);
        LabelVariation(theme, Muted, r["text.muted"], ReverieTheme.TypeStatus, null);
        LabelVariation(theme, Caption, r["text.muted"], ReverieTheme.TypeCaption, null);
        LabelVariation(theme, WarnText, r["warn.text"], ReverieTheme.TypeBody, null);
        LabelVariation(theme, CardHeading, r["text.primary"], ReverieTheme.TypeBody, semibold);
        LabelVariation(theme, Overline, r["text.muted"], ReverieTheme.TypeOverline, semibold);

        // buttons: one family, each state from the table of states
        ButtonVariation(theme, MenuTitle, r, normal: clear, hover, selected, border: clear, 0, control, ReverieTheme.MenuTitlePadding, 4, r["text.primary"], ReverieTheme.TypeMenu, null);
        ButtonVariation(theme, MenuTitleOpen, r, normal: selected, selected, selected, border: clear, 0, control, ReverieTheme.MenuTitlePadding, 4, r["text.strong"], ReverieTheme.TypeMenu, null);
        // the app menu's title: the menu title in weight 600 with its accent mark (an accent-coloured picture, drawn untinted)
        ButtonVariation(theme, AppTitle, r, normal: clear, hover, selected, border: clear, 0, control, ReverieTheme.MenuTitlePadding, 4, r["text.primary"], ReverieTheme.TypeMenu, semibold);
        ButtonVariation(theme, AppTitleOpen, r, normal: selected, selected, selected, border: clear, 0, control, ReverieTheme.MenuTitlePadding, 4, r["text.strong"], ReverieTheme.TypeMenu, semibold);
        foreach (var name in new[] { AppTitle, AppTitleOpen })
            foreach (var icon in new[] { "icon_normal_color", "icon_hover_color", "icon_pressed_color", "icon_hover_pressed_color", "icon_focus_color" })
                theme.SetColor(icon, name, new Color(1, 1, 1));
        ButtonVariation(theme, IconButton, r, normal: clear, hover, selected, border: clear, 0, control, 6, 6, r["text.primary"], ReverieTheme.TypeBody, null);
        ButtonVariation(theme, LocationButton, r, normal: clear, A(r["surface.selected"], 0.35f), A(r["surface.selected"], 0.6f), border: clear, 0, Px(ReverieTheme.LocationHeight / 2), 2, 0, r["text.primary"], ReverieTheme.TypeStatus, null);
        ButtonVariation(theme, Secondary, r, normal: clear, hover, selected, border: C(r["border.control"]), bw, Px(ReverieTheme.ButtonHeight / 2), 16, 6, r["text.primary"], ReverieTheme.TypeSmall, null);
        ButtonVariation(theme, Primary, r, normal: C(r.Accent), C(r.Accent).Darkened(0.12f), C(r.Accent).Darkened(0.22f), border: C(r.Accent), bw, Px(ReverieTheme.ButtonHeight / 2), 18, 6, r.OnAccent, ReverieTheme.TypeBody, semibold);
        ButtonVariation(theme, Chip, r, normal: C(r["surface.raised"]), C(r["surface.selected"]), C(r["surface.selected.strong"]), border: C(r["border.control"]), bw, Px(ReverieTheme.ChipHeight / 2), 12, 3, r["text.primary"], ReverieTheme.TypeSmall, null);
        ButtonVariation(theme, ListRow, r, normal: clear, hover, selected, border: rowBorder, rowBw, control, 8, 4, r["text.primary"], ReverieTheme.TypeBody, null);
        // a row that stays pressed (a sidebar page, a preset) shows the selected face, with the accent outline when rows are outlined
        foreach (var state in new[] { "pressed", "hover_pressed" })
            theme.SetStylebox(state, ListRow, Face(selected, rowSelectedBorder, rowBw, control, 8, 4));
        theme.SetColor("font_pressed_color", ListRow, C(r["text.strong"]));
        theme.SetColor("font_hover_pressed_color", ListRow, C(r["text.strong"]));
        ButtonVariation(theme, ListRowSelected, r, normal: selected, selected, selected, border: rowSelectedBorder, rowBw, control, 8, 4, r["text.strong"], ReverieTheme.TypeBody, semibold);
        ButtonVariation(theme, Segment, r, normal: clear, hover, selected, border: rowBorder, rowBw, control, 12, 3, r["text.secondary"], ReverieTheme.TypeSmall, null);
        ButtonVariation(theme, SegmentSelected, r, normal: C(r["surface.selected.strong"]), C(r["surface.selected.strong"]), C(r["surface.selected.strong"]), border: rowSelectedBorder, rowBw, control, 12, 3, r["text.strong"], ReverieTheme.TypeSmall, semibold);
        foreach (var name in new[] { ListRow, ListRowSelected })
            theme.SetConstant("align_to_largest_stylebox", name, 0);

        // the dock and the floating pills: the base surface at 95 %, the dock's radius (a pill's is half its height), the floating
        // shadow; dock buttons in the same family of states, the "on" one on the selected surface, attention a 2-pixel accent ring
        var dockPanel = Face(A(r["surface.base"], ReverieTheme.FloatingOpacity), C(r["border.control"]), bw, ReverieTheme.RadiusDock, ReverieTheme.DockPadding, ReverieTheme.DockPadding);
        Shadow(dockPanel);
        Panel(theme, Dock, dockPanel);
        var pillPanel = Face(A(r["surface.base"], ReverieTheme.FloatingOpacity), C(r["border.control"]), bw, Px(ReverieTheme.PillHeight / 2), 14, 0);
        Shadow(pillPanel);
        Panel(theme, Pill, pillPanel);
        ButtonVariation(theme, DockButton, r, normal: clear, hover, selected, border: clear, 0, ReverieTheme.RadiusDockButton, 2, 4, r["text.primary"], ReverieTheme.TypeDock, null);
        ButtonVariation(theme, DockButtonOn, r, normal: selected, selected, selected, border: clear, 0, ReverieTheme.RadiusDockButton, 2, 4, r["text.strong"], ReverieTheme.TypeDock, null);
        ButtonVariation(theme, DockButtonAttention, r, normal: clear, hover, selected, border: C(r.Accent), 2, ReverieTheme.RadiusDockButton, 2, 4, r["text.strong"], ReverieTheme.TypeDock, null);
        LabelVariation(theme, DockLabel, r["text.secondary"], ReverieTheme.TypeDock, null);
        Panel(theme, DockDivider, Face(C(r["border.default"]), C(r["border.default"]), 0, 0, 0, 0));
        Panel(theme, DockDot, Face(C(r.Accent), C(r.Accent), 0, 4, 0, 0));

        // the location field's edit mode
        theme.SetTypeVariation(LocationEdit, "LineEdit");
        var pill = Face(C(r["surface.raised"]), C(r.Accent), bw, Px(ReverieTheme.LocationHeight / 2), 12, 0);
        theme.SetStylebox("normal", LocationEdit, pill);
        theme.SetStylebox("focus", LocationEdit, FocusRing(r, Px(ReverieTheme.LocationHeight / 2)));
        theme.SetColor("font_color", LocationEdit, C(r["text.primary"]));
        theme.SetColor("font_placeholder_color", LocationEdit, C(r["text.muted"]));
        theme.SetColor("caret_color", LocationEdit, C(r.Accent));
        theme.SetColor("selection_color", LocationEdit, A(r.Accent, 0.35f));
        theme.SetFontSize("font_size", LocationEdit, Type(ReverieTheme.TypeStatus));

        // the dropdown menu: a 16-pixel icon column, the label, the shortcut text right-aligned and muted, separators, submenu arrows
        theme.SetTypeVariation(Popup, "PopupMenu");
        var menuPanel = Face(C(r["surface.base"]), C(r["border.control"]), bw, control, 6, 6);
        Shadow(menuPanel);
        theme.SetStylebox("panel", Popup, menuPanel);
        theme.SetStylebox("hover", Popup, Face(selected, selected, 0, 6, 8, 4));
        var line = new StyleBoxLine { Color = C(r["border.default"]), Thickness = 1, GrowBegin = -6, GrowEnd = -6 };
        theme.SetStylebox("separator", Popup, line);
        theme.SetColor("font_color", Popup, C(r["text.primary"]));
        theme.SetColor("font_hover_color", Popup, C(r["text.strong"]));
        theme.SetColor("font_disabled_color", Popup, C(ControlStates.Faded(r["text.primary"])));
        theme.SetColor("font_accelerator_color", Popup, C(r["text.muted"]));
        theme.SetColor("font_separator_color", Popup, C(r["text.muted"]));
        theme.SetConstant("icon_max_width", Popup, Px(ReverieTheme.IconSmall));
        theme.SetConstant("h_separation", Popup, 10);
        theme.SetConstant("v_separation", Popup, 8);
        theme.SetFontSize("font_size", Popup, Type(ReverieTheme.TypeBody));

        // the slider: a raised track filled with the accent up to the knob
        theme.SetTypeVariation(Slider, "HSlider");
        var track = Face(C(r["surface.raised"]), C(r["border.control"]), bw, 3, 0, 3);
        theme.SetStylebox("slider", Slider, track);
        theme.SetStylebox("grabber_area", Slider, Face(C(r.Accent), C(r.Accent), 0, 3, 0, 3));
        theme.SetStylebox("grabber_area_highlight", Slider, Face(C(r.Accent), C(r.Accent), 0, 3, 0, 3));
        theme.SetStylebox("focus", Slider, FocusRing(r, 3));
        theme.SetIcon("grabber", Slider, Knob(C(r["text.strong"]), C(r["border.strong"]), ReverieTheme.SwitchKnob));
        theme.SetIcon("grabber_highlight", Slider, Knob(C(r["text.strong"]), C(r.Accent), ReverieTheme.SwitchKnob));
        theme.SetIcon("grabber_disabled", Slider, Knob(C(ControlStates.Faded(r["text.strong"])), C(r["border.strong"]), ReverieTheme.SwitchKnob));

        // the colours the self-drawn switch and stepper read
        theme.SetColor("on", Switch, C(r.Accent));
        theme.SetColor("knob_on", Switch, C(r.OnAccent));
        theme.SetColor("off_border", Switch, C(r["border.strong"]));
        theme.SetColor("knob_off", Switch, C(r["border.strong"]));
        theme.SetColor("focus", Switch, C(r.Accent));
        theme.SetColor("current", Stepper, C(r.Accent));
        theme.SetColor("current_text", Stepper, C(r.OnAccent));
        theme.SetColor("other", Stepper, C(r["surface.raised"]));
        theme.SetColor("other_border", Stepper, C(r["border.strong"]));
        theme.SetColor("text", Stepper, C(r["text.primary"]));
        theme.SetColor("muted", Stepper, C(r["text.muted"]));
        theme.SetColor("ok", Status, C(r["status.ok"]));
        theme.SetColor("accent", Status, C(r.Accent));
        theme.SetColor("muted", Status, C(r["text.muted"]));
        theme.SetColor("text", Status, C(r["text.primary"]));

        AddRestyleParts(theme, r, clear, hover, selected, control, semibold, rowBorder, rowSelectedBorder, rowBw);
    }

    /// <summary>The components the restyled classic windows take (ReverieRestyle): a dropdown (select) button, a text field, a list and a
    /// tree whose rows are list rows, a tab bar drawn as a segmented control, and a menu drawn as a panel of rows. Same tokens and states.</summary>
    private static void AddRestyleParts(Theme theme, ReverieTokens r, Color clear, Color hover, Color selected, int control, Font? semibold,
        Color rowBorder, Color rowSelectedBorder, int rowBw)
    {
        int bw = _bw;
        // the select: a secondary button that keeps the option button's arrow
        ButtonVariation(theme, Select, r, normal: clear, hover, selected, border: C(r["border.control"]), bw, control, 12, 6, r["text.primary"], ReverieTheme.TypeBody, null, "OptionButton");
        theme.SetColor("font_disabled_color", Select, C(ControlStates.Faded(r["text.primary"])));

        // the text field: the raised surface in the control's radius, the focus ring outside it
        theme.SetTypeVariation(Field, "LineEdit");
        theme.SetStylebox("normal", Field, Face(C(r["surface.raised"]), C(r["border.control"]), bw, control, 10, 4));
        theme.SetStylebox("read_only", Field, Face(C(r["surface.sunken"]), C(r["border.default"]), bw, control, 10, 4));
        theme.SetStylebox("focus", Field, FocusRing(r, control));
        theme.SetColor("font_color", Field, C(r["text.primary"]));
        theme.SetColor("font_placeholder_color", Field, C(r["text.muted"]));
        theme.SetColor("font_uneditable_color", Field, C(r["text.secondary"]));
        theme.SetColor("caret_color", Field, C(r.Accent));
        theme.SetColor("selection_color", Field, A(r.Accent, 0.35f));

        // the text area (a notecard, the script editor, a certificate text): the field's frame, for a TextEdit and a CodeEdit alike. Only the
        // frame changes: a code editor keeps its own text, syntax, gutter and line-number colours.
        theme.SetTypeVariation(TextArea, "TextEdit");
        theme.SetStylebox("normal", TextArea, Face(C(r["surface.raised"]), C(r["border.control"]), bw, control, 10, 6));
        theme.SetStylebox("read_only", TextArea, Face(C(r["surface.sunken"]), C(r["border.default"]), bw, control, 10, 6));
        theme.SetStylebox("focus", TextArea, FocusRing(r, control));

        // lists and trees: the sunken surface, rows in the list row's states (hover 60 %, selected on the selected surface)
        var sunken = Face(C(r["surface.sunken"]), C(r["border.default"]), bw, control, 4, 4);
        var rowSelected = Face(selected, rowSelectedBorder, rowBw, control, 6, 2);
        var rowHover = Face(hover, hover, 0, control, 6, 2);
        var none = new StyleBoxEmpty();
        foreach (var (name, type) in new[] { (List, "ItemList"), (TreeList, "Tree") })
        {
            theme.SetTypeVariation(name, type);
            theme.SetStylebox("panel", name, sunken);
            theme.SetStylebox("focus", name, FocusRing(r, control));
            theme.SetStylebox("selected", name, rowSelected);
            theme.SetStylebox("selected_focus", name, rowSelected);
            theme.SetStylebox("hovered", name, rowHover);
            theme.SetStylebox("hovered_selected", name, rowSelected);
            theme.SetStylebox("hovered_selected_focus", name, rowSelected);
            theme.SetColor("font_hovered_selected_color", name, C(r["text.strong"]));
            theme.SetStylebox("cursor", name, none);
            theme.SetStylebox("cursor_unfocused", name, none);
            theme.SetColor("font_color", name, C(r["text.primary"]));
            theme.SetColor("font_selected_color", name, C(r["text.strong"]));
            theme.SetColor("font_hovered_color", name, C(r["text.primary"]));
        }

        // the tab bar as the segmented control: a track, the open tab on the strong selected surface
        theme.SetTypeVariation(Tabs, "TabContainer");
        theme.SetStylebox("tabbar_background", Tabs, Face(C(r["surface.raised"]), C(r["border.control"]), bw, ReverieTheme.RadiusTrack, 2, 2));
        theme.SetStylebox("tab_selected", Tabs, Face(C(r["surface.selected.strong"]), rowSelectedBorder, rowBw, control, 12, 3));
        theme.SetStylebox("tab_unselected", Tabs, Face(clear, rowBorder, rowBw, control, 12, 3));
        theme.SetStylebox("tab_hovered", Tabs, Face(hover, rowBorder, rowBw, control, 12, 3));
        var tabFaded = clear;
        theme.SetStylebox("tab_disabled", Tabs, Face(tabFaded, tabFaded, 0, control, 12, 3));
        theme.SetStylebox("tab_focus", Tabs, FocusRing(r, control));
        theme.SetStylebox("panel", Tabs, Face(clear, clear, 0, 0, 0, 6));
        theme.SetColor("font_selected_color", Tabs, C(r["text.strong"]));
        theme.SetColor("font_unselected_color", Tabs, C(r["text.secondary"]));
        theme.SetColor("font_hovered_color", Tabs, C(r["text.primary"]));
        theme.SetColor("font_disabled_color", Tabs, C(ControlStates.Faded(r["text.secondary"])));
        theme.SetFontSize("font_size", Tabs, Type(ReverieTheme.TypeSmall));
        if (semibold is not null)
            theme.SetFont("font", Tabs, semibold);

        // the tab bar on its own (a TabBar, not a TabContainer) as the segments of the segmented control; the window puts a track
        // behind it where it can (Inventory)
        theme.SetTypeVariation(TabBar, "TabBar");
        theme.SetStylebox("tab_selected", TabBar, Face(C(r["surface.selected.strong"]), rowSelectedBorder, rowBw, control, 12, 3));
        theme.SetStylebox("tab_unselected", TabBar, Face(clear, rowBorder, rowBw, control, 12, 3));
        theme.SetStylebox("tab_hovered", TabBar, Face(hover, rowBorder, rowBw, control, 12, 3));
        theme.SetStylebox("tab_disabled", TabBar, Face(clear, clear, 0, control, 12, 3));
        theme.SetStylebox("tab_focus", TabBar, FocusRing(r, control));
        theme.SetColor("font_selected_color", TabBar, C(r["text.strong"]));
        theme.SetColor("font_unselected_color", TabBar, C(r["text.secondary"]));
        theme.SetColor("font_hovered_color", TabBar, C(r["text.primary"]));
        theme.SetColor("font_disabled_color", TabBar, C(ControlStates.Faded(r["text.secondary"])));
        theme.SetFontSize("font_size", TabBar, Type(ReverieTheme.TypeSmall));
        if (semibold is not null)
            theme.SetFont("font", TabBar, semibold);

        // the check box: the tick and its label, as the official viewer draws one, with no bar around the row; dimmed when off
        theme.SetTypeVariation(Check, "CheckBox");
        var bare = new StyleBoxEmpty { ContentMarginLeft = 2, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2 };
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
            theme.SetStylebox(state, Check, bare);
        theme.SetStylebox("focus", Check, FocusRing(r, 4));
        foreach (var colour in new[] { "font_color", "font_pressed_color", "font_focus_color" })
            theme.SetColor(colour, Check, C(r["text.primary"]));
        theme.SetColor("font_hover_color", Check, C(r["text.strong"]));
        theme.SetColor("font_hover_pressed_color", Check, C(r["text.strong"]));
        theme.SetColor("font_disabled_color", Check, C(ControlStates.Faded(r["text.primary"])));
        theme.SetConstant("h_separation", Check, 6);
        // the box itself: an outline that holds 3:1 against the surface (ticked: the accent with a tick; disabled: the same at 45 %)
        foreach (var (icon, ticked, off) in new[] { ("unchecked", false, false), ("checked", true, false), ("unchecked_disabled", false, true), ("checked_disabled", true, true) })
            theme.SetIcon(icon, Check, CheckBoxIcons.Make(r, ticked, off));
        theme.SetFontSize("font_size", Check, Type(ReverieTheme.TypeBody));

        // a collapsible section header: flat, a chevron icon, the label in weight 600; the window draws the thin divider under it.
        // Open or shut, the face stays flat: the chevron shows which.
        ButtonVariation(theme, SectionHeader, r, normal: clear, hover, clear, border: clear, 0, control, 4, 4, r["text.primary"], ReverieTheme.TypeBody, semibold);
        theme.SetStylebox("hover_pressed", SectionHeader, Face(hover, clear, 0, control, 4, 4));
        theme.SetTypeVariation(SectionDivider, "HSeparator");
        theme.SetStylebox("separator", SectionDivider, new StyleBoxLine { Color = C(r["border.default"]), Thickness = bw });
        theme.SetConstant("separation", SectionDivider, 2);

        // a menu drawn as a panel of buttons: the dropdown menu's face
        var menu = Face(C(r["surface.base"]), C(r["border.control"]), bw, control, 6, 6);
        Shadow(menu);
        Panel(theme, MenuPanel, menu);
    }

    private static StyleBoxFlat Edge(Color fill, Color border, int bottom, float margin)
    {
        var box = new StyleBoxFlat { BgColor = fill, BorderColor = border, BorderWidthBottom = bottom };
        box.ContentMarginLeft = box.ContentMarginRight = margin;
        box.ContentMarginTop = box.ContentMarginBottom = 0;
        return box;
    }

    private static void Panel(Theme theme, string name, StyleBox box)
    {
        theme.SetTypeVariation(name, "PanelContainer");
        theme.SetStylebox("panel", name, box);
    }

    private static void LabelVariation(Theme theme, string name, UiColour colour, float size, Font? font)
    {
        theme.SetTypeVariation(name, "Label");
        theme.SetColor("font_color", name, C(colour));
        theme.SetFontSize("font_size", name, Type(size));
        if (font is not null)
            theme.SetFont("font", name, font);
    }

    private static void ButtonVariation(Theme theme, string name, ReverieTokens r, Color normal, Color hover, Color pressed, Color border, int borderWidth,
        int radius, float marginH, float marginV, UiColour text, float size, Font? font, string baseType = "Button")
    {
        theme.SetTypeVariation(name, baseType);
        theme.SetStylebox("normal", name, Face(normal, border, borderWidth, radius, marginH, marginV));
        theme.SetStylebox("hover", name, Face(hover, border, borderWidth, radius, marginH, marginV));
        theme.SetStylebox("pressed", name, Face(pressed, border, borderWidth, radius, marginH, marginV));
        theme.SetStylebox("hover_pressed", name, Face(pressed, border, borderWidth, radius, marginH, marginV));
        var faded = normal;
        faded.A *= ControlStates.DisabledStrength;
        var fadedBorder = border;
        fadedBorder.A *= ControlStates.DisabledStrength;
        theme.SetStylebox("disabled", name, Face(faded, fadedBorder, borderWidth, radius, marginH, marginV));
        theme.SetStylebox("focus", name, FocusRing(r, radius));
        var c = C(text);
        foreach (var colour in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color",
                     "icon_normal_color", "icon_hover_color", "icon_pressed_color", "icon_hover_pressed_color", "icon_focus_color" })
            theme.SetColor(colour, name, c);
        theme.SetColor("font_disabled_color", name, C(ControlStates.Faded(text)));
        theme.SetColor("icon_disabled_color", name, C(ControlStates.Faded(text)));
        theme.SetFontSize("font_size", name, Type(size));
        theme.SetConstant("icon_max_width", name, Px(ReverieTheme.IconSmall));
        theme.SetConstant("h_separation", name, 6);
        if (font is not null)
            theme.SetFont("font", name, font);
    }

    private static readonly Dictionary<(Color, Color, float, int), ImageTexture> Knobs = new();

    /// <summary>A round knob of the given size, with a thin edge.</summary>
    public static ImageTexture Knob(Color fill, Color edge, float size)
    {
        var key = (fill, edge, size, 100);
        if (Knobs.TryGetValue(key, out var have))
            return have;
        int px = Math.Max(4, (int)MathF.Round(size));   // drawn at its own size in interface units (a slider draws its knob unscaled)
        var image = Image.CreateEmpty(px, px, false, Image.Format.Rgba8);
        float rad = px / 2f;
        for (int y = 0; y < px; y++)
            for (int x = 0; x < px; x++)
            {
                float d = new Vector2(x + 0.5f - rad, y + 0.5f - rad).Length();
                float cover = Math.Clamp(rad - d, 0f, 1f);
                if (cover <= 0)
                    continue;
                var c = d > rad - 1.5f ? edge : fill;
                image.SetPixel(x, y, new Color(c.R, c.G, c.B, c.A * cover));
            }
        var texture = ImageTexture.CreateFromImage(image);
        Knobs[key] = texture;
        return texture;
    }
}
