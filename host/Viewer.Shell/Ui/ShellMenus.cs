// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>What a command does when a menu entry or toolbar button is chosen.</summary>
public sealed class ShellCommands
{
    private readonly Dictionary<string, Action> _commands = new();

    public void Register(string commandId, Action run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!_commands.TryAdd(commandId, run))
            throw new ArgumentException($"command '{commandId}' is already registered", nameof(commandId));
    }

    public bool Has(string commandId) => _commands.ContainsKey(commandId);

    /// <summary>Runs the command; false when there is none.</summary>
    public bool Execute(string commandId)
    {
        if (!_commands.TryGetValue(commandId, out var run))
            return false;
        run();
        return true;
    }
}

/// <summary>One entry of a menu. <paramref name="LabelKey"/> is also its accessible name.</summary>
/// <param name="Shortcut">The entry's keyboard shortcut as the official menus write it ("control|U"), or none.</param>
public sealed record MenuEntry(string Id, string LabelKey, string CommandId, Func<bool>? Enabled = null, int Order = 100,
    Func<bool>? Checked = null, IReadOnlyList<MenuEntry>? Children = null, string? Shortcut = null, string? LabelArg = null)
{
    public bool IsEnabled => Enabled?.Invoke() ?? true;

    /// <summary>Whether the entry shows a check mark (the item in force, in a group of choices).</summary>
    public bool IsChecked => Checked?.Invoke() ?? false;
}

public sealed record MenuDefinition(string Id, string LabelKey, IReadOnlyList<MenuEntry> Entries);

/// <summary>The menu bar as data: menus in order, each with its entries in order. Feature code adds its entries here.</summary>
public sealed class MenuModel
{
    private readonly List<(string Id, string LabelKey, List<MenuEntry> Entries)> _menus = new();

    /// <summary>Raised after a menu or an entry is added, so a menu bar already drawn can draw itself again.</summary>
    public event Action? Changed;

    /// <summary>The menus' places along the bar; a menu not in this list follows them in the order it was added.</summary>
    private static readonly string[] BarOrder = ["me", "communicate", "world", "build", "content", "help"];

    public IReadOnlyList<MenuDefinition> Menus =>
        _menus
            .Select((m, added) => (Menu: m, Place: Array.IndexOf(BarOrder, m.Id) is var at and >= 0 ? at : BarOrder.Length, Added: added))
            .OrderBy(x => x.Place).ThenBy(x => x.Added)
            .Select(x => new MenuDefinition(x.Menu.Id, x.Menu.LabelKey, x.Menu.Entries.OrderBy(e => e.Order)   // OrderBy is stable
                .Select(e => e.Children is { } kids ? e with { Children = kids.OrderBy(k => k.Order).ToArray() } : e).ToArray()))
            .ToArray();

    public void AddMenu(string id, string labelKey)
    {
        if (_menus.Any(m => m.Id == id))
            throw new ArgumentException($"menu '{id}' already exists", nameof(id));
        _menus.Add((id, labelKey, new List<MenuEntry>()));
        Changed?.Invoke();
    }

    public void AddEntry(string menuId, MenuEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = ViewerShortcuts.WithOfficial(entry);   // the official shortcut, when the entry has none of its own
        var menu = _menus.FirstOrDefault(m => m.Id == menuId);
        if (menu.Entries is null)
            throw new ArgumentException($"no menu '{menuId}'", nameof(menuId));
        if (AllEntries().Any(e => e.Id == entry.Id))
            throw new ArgumentException($"entry '{entry.Id}' already exists", nameof(entry));
        menu.Entries.Add(entry);
        Changed?.Invoke();
    }

    /// <summary>Removes an entry from a menu (an add-on's item when the add-on is switched off). False when there was none.</summary>
    public bool RemoveEntry(string menuId, string entryId)
    {
        var menu = _menus.FirstOrDefault(m => m.Id == menuId);
        if (menu.Entries is null || menu.Entries.RemoveAll(e => e.Id == entryId) == 0)
            return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Adds a submenu to a menu: one entry of that menu that opens a list of entries (added with <see cref="AddSubEntry"/>).</summary>
    public void AddSubmenu(string menuId, string id, string labelKey, int order = 100) =>
        AddEntry(menuId, new MenuEntry(id, labelKey, "", Order: order, Children: new List<MenuEntry>()));

    /// <summary>Adds an entry to a submenu made with <see cref="AddSubmenu"/>.</summary>
    public void AddSubEntry(string submenuId, MenuEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = ViewerShortcuts.WithOfficial(entry);
        var sub = _menus.SelectMany(m => m.Entries).FirstOrDefault(e => e.Id == submenuId);
        if (sub?.Children is not List<MenuEntry> kids)
            throw new ArgumentException($"no submenu '{submenuId}'", nameof(submenuId));
        if (AllEntries().Any(e => e.Id == entry.Id))
            throw new ArgumentException($"entry '{entry.Id}' already exists", nameof(entry));
        kids.Add(entry);
        Changed?.Invoke();
    }

    private IEnumerable<MenuEntry> AllEntries() =>
        _menus.SelectMany(m => m.Entries).SelectMany(e => e.Children is { } kids ? kids.Prepend(e) : [e]);

    /// <summary>The entry whose shortcut is <paramref name="shortcut"/> ("control|U"), or null.</summary>
    public MenuEntry? ForShortcut(string shortcut) => AllEntries().FirstOrDefault(e => e.Shortcut is { } s && string.Equals(s, shortcut, StringComparison.OrdinalIgnoreCase));

    /// <summary>Chooses an entry: runs its command unless it is disabled. False when nothing ran.</summary>
    public bool Activate(string entryId, ShellCommands commands)
    {
        var entry = AllEntries().FirstOrDefault(e => e.Id == entryId);
        return entry is { IsEnabled: true } && commands.Execute(entry.CommandId);
    }
}

/// <summary>One button of the bottom toolbar.</summary>
/// <param name="Icon">The icon drawn above the label (a name from <see cref="UiIcons"/>), or none.</param>
/// <param name="FloaterId">The window the button opens; the button shows as active while it is open.</param>
public sealed record ToolbarButton(string Id, string LabelKey, string CommandId, string? Icon = null, string? FloaterId = null)
{
    /// <summary>Whether the button shows pressed: exactly while the window it opens is open (the official viewer asks each frame whether the
    /// command is running, which for these buttons is Floater.IsOpen), so it clears when that window closes.</summary>
    public bool IsPressed(FloaterManager floaters) => FloaterId is { } id && floaters.IsOpen(id);
}

/// <summary>The toolbar as data. Empty until feature code adds its buttons; an empty toolbar takes no room.</summary>
public sealed class ToolbarModel
{
    private readonly List<ToolbarButton> _buttons = new();

    public IReadOnlyList<ToolbarButton> Buttons => _buttons.ToArray();

    /// <summary>Raised after a button is added, so a toolbar already drawn can draw itself again.</summary>
    public event Action? Changed;

    public void Add(ToolbarButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (_buttons.Any(b => b.Id == button.Id))
            throw new ArgumentException($"button '{button.Id}' already exists", nameof(button));
        _buttons.Add(button);
        Changed?.Invoke();
    }
}
