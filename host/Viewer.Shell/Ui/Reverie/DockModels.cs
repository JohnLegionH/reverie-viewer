// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>One entry of a top-bar dropdown, read from the menu model each time the menu opens.</summary>
/// <param name="CommandId">What choosing it runs (a command of <see cref="ShellCommands"/>); none for an entry that only opens a submenu.</param>
public sealed record TopMenuEntryView(string Id, string LabelKey, string? CommandId, string? Icon, string? Shortcut, bool Enabled, bool? Checked,
    IReadOnlyList<TopMenuEntryView>? Children);

/// <summary>One menu of the top bar: the app menu first, then the official top-level menus.</summary>
public sealed record TopMenuView(string Id, string TitleKey, bool IsApp, IReadOnlyList<TopMenuEntryView> Entries);

/// <summary>
/// The top bar's menus, read from the menu model the features fill: the app menu (About, Preferences, Add-ons, Check for updates, Log
/// out, Quit) and then the official viewer's top-level menus in their order (Me, Communicate, World, Build, Content, Help), each with
/// every entry the model holds. Each entry carries its icon for the dropdown's icon column. An entry whose command is not registered
/// shows as disabled.
/// </summary>
public static class TopBarMenuMap
{
    public const string AppMenuId = "app";

    /// <summary>The app menu's entries: label key and command.</summary>
    public static readonly (string Id, string LabelKey, string CommandId, string? Icon)[] AppEntries =
    [
        ("app.about", "menu.app.about", "floater.about", null),
        ("app.preferences", "menu.app.preferences", "floater.preferences", null),
        ("app.addons", "menu.app.addons", "floater.addons", LucideIcons.AddOn),
        ("app.updates", "menu.app.updates", "app.check_updates", null),
        ("app.logout", "menu.app.logout", "app.logout", LucideIcons.Back),
        ("app.quit", "menu.app.quit", "app.quit", LucideIcons.Close),
    ];

    public static IReadOnlyList<TopMenuView> Build(MenuModel menus, ShellCommands commands) => Array.Empty<TopMenuView>();

    private static TopMenuEntryView View(MenuEntry e, ShellCommands commands) =>
        e.Children is { } kids
            ? new TopMenuEntryView(e.Id, e.LabelKey, null, IconFor(e.Id), e.Shortcut, e.IsEnabled, null, kids.Select(k => View(k, commands)).ToArray())
            : new TopMenuEntryView(e.Id, e.LabelKey, e.CommandId, IconFor(e.Id), e.Shortcut, e.IsEnabled && commands.Has(e.CommandId), e.Checked is null ? null : e.IsChecked, null);

    private static readonly Dictionary<string, string> Icons = new()
    {
        ["world.teleport"] = LucideIcons.Teleport, ["world.home"] = LucideIcons.Home, ["world.set_home"] = LucideIcons.Home, ["world.map"] = LucideIcons.Map,
        ["world.minimap"] = LucideIcons.MiniMap, ["world.landmark"] = LucideIcons.Landmark, ["world.environment"] = LucideIcons.Grid, ["world.about_land"] = LucideIcons.Teleport,
        ["communicate.chat"] = LucideIcons.Chat, ["communicate.conversations"] = LucideIcons.Conversations, ["communicate.people"] = LucideIcons.People,
        ["communicate.notifications"] = LucideIcons.Notices, ["content.inventory"] = LucideIcons.Inventory, ["content.outfits"] = LucideIcons.Outfits,
        ["me.appearance"] = LucideIcons.Appearance, ["me.overrider"] = LucideIcons.Overrider, ["me.addons"] = LucideIcons.AddOn, ["me.quit"] = LucideIcons.Close,
        ["build.create"] = LucideIcons.Build,
    };

    /// <summary>The icon for a menu entry: its own, else one by the menu it is in (World entries all have one), else none.</summary>
    public static string? IconFor(string entryId) => null;
}

/// <summary>What a dock button does when clicked: runs something, turns on or off, or turns on or off with a menu on its chevron.</summary>
public enum DockItemKind { Action, Toggle, ToggleWithMenu }

/// <summary>One entry of a dock button's menu (its chevron or right-click): a label, an optional icon, an optional check mark, or a separator.</summary>
public sealed record DockMenuEntry(string Label, string? Icon = null, bool? Checked = null, bool Separator = false);

/// <summary>One dock button: its icon and name, what it runs, and its state (on or off, attention with a count, its menu).</summary>
public sealed class DockItem
{
    public DockItem(string id, string label, string icon, DockItemKind kind, string group, string? commandId = null, string? floaterId = null,
        string? owner = null, string? tooltip = null)
    {
        Id = id; Label = label; Icon = icon; Kind = kind; Group = group; CommandId = commandId; FloaterId = floaterId; Owner = owner; Tooltip = tooltip;
    }

    public string Id { get; }

    /// <summary>For a built-in button, the string key of its name; for an add-on's, the name itself (already cut to fit).</summary>
    public string Label { get; }

    /// <summary>A name from <see cref="LucideIcons"/>, or an add-on's own single-colour svg text.</summary>
    public string Icon { get; }
    public DockItemKind Kind { get; }
    public string Group { get; }
    public string? CommandId { get; }
    public string? FloaterId { get; }

    /// <summary>The add-on that asked for it; null for the viewer's own buttons.</summary>
    public string? Owner { get; }
    public string? Tooltip { get; }
    public bool On { get; internal set; }
    public bool Attention { get; internal set; }
    public int? AttentionCount { get; internal set; }
    public IReadOnlyList<DockMenuEntry> Menu { get; internal set; } = Array.Empty<DockMenuEntry>();

    /// <summary>The name under the button: a toggle reads "&lt;name&gt; · On" or "· Off"; Notices with a count reads "&lt;n&gt; notices".</summary>
    public string Shown(StringTable strings) => "";
}

/// <summary>
/// The dock's contents, in its groups: Talk (Chat, Convos, People), World (Teleport, Map, Mini-map), Me (Inventory, Outfits,
/// Appearance), Make (Build, then the add-ons' items) and Alerts (Notices). An add-on asks for its items through the add-on interface:
/// they go in Make after Build in the order the add-ons asked, at most two per add-on, with the name cut to ten characters, and they go
/// the moment the add-on is turned off.
/// </summary>
public sealed class DockModel
{
    public const int LabelMax = 10, ItemsPerAddon = 2;
    public static readonly string[] GroupOrder = ["talk", "world", "me", "make", "alerts"];

    private readonly List<DockItem> _items = new();

    /// <summary>Raised after an item came, went, or changed its state.</summary>
    public event Action? Changed;

    public static DockModel Standard() => new();

    /// <summary>The groups in order, each with its items in order (an empty group is left out).</summary>
    public IReadOnlyList<(string Group, IReadOnlyList<DockItem> Items)> Groups => Array.Empty<(string, IReadOnlyList<DockItem>)>();

    public IReadOnlyList<DockItem> Items => _items;

    public DockItem? Item(string id, string? owner = null) => null;

    /// <summary>An add-on's item, in Make after Build and after the items asked for before it; null when refused (the add-on already has
    /// two, or one with that id).</summary>
    public DockItem? AddAddonItem(string owner, string id, string label, DockItemKind kind, string? icon = null, string? tooltip = null,
        IReadOnlyList<DockMenuEntry>? menu = null) => null;

    public bool RemoveAddonItem(string owner, string id) => false;

    /// <summary>Every item of an add-on goes at once (it was turned off). How many went.</summary>
    public int RemoveAddon(string owner) => 0;

    public void SetOn(DockItem item, bool on) { }

    public void SetAttention(DockItem item, bool attention, int? count = null) { }

    public void SetMenu(DockItem item, IReadOnlyList<DockMenuEntry> menu) { }

    /// <summary>A name cut to ten characters, the last one an ellipsis when it was longer.</summary>
    public static string CutLabel(string label) => label;

    private void Raise() => Changed?.Invoke();
}

/// <summary>What the context slot at the right of the dock shows.</summary>
public enum ContextSlotKind { None, StandUp, StopFlying, BackTo }

/// <summary>The context slot's one action, by priority: Stand up while seated, Stop flying while flying, Back to the previous region
/// for a minute after a teleport; otherwise the slot is hidden.</summary>
public static class ContextSlot
{
    public const double BackSeconds = 60;

    public static ContextSlotKind Pick(bool sitting, bool flying, double secondsSinceTeleport, bool hasPrevious) => ContextSlotKind.None;
}
