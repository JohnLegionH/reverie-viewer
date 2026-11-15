// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// What the development automation needs to check the login screen, the menus and the Preferences layout the way a person uses them:
/// the login screen filled and submitted with Enter, remember password's state (never a hash), which menu is open, and where each
/// control of a Preferences page sits against the part of the page that is visible.
/// </summary>
public partial class Main
{
    /// <summary>{action: state|submit}: submit fills the grid, name and (unless <paramref name="useSaved"/>) password fields, sets the remember
    /// password tick and presses Enter in the password field. Null when the login screen is not there.</summary>
    public object? LoginScreenCall(string action, string? grid, string? name, Secret? password, bool remember, bool useSaved, string? start = null)
    {
        if (_loginScreen is not { } screen || Model is not { } model)
            return null;
        if (action == "submit")
        {
            if (model.Busy || model.Session is { IsEnded: false })
                return new { error = "a session is active" };
            screen.Visible = true;
            if (grid is not null)
                screen.SetGrid(grid);
            if (start is not null)
                screen.SetStart(start);
            screen.TypeAndSubmit(name ?? "", useSaved || password is null ? "" : password.Reveal(), remember);
        }
        return new { busy = model.Busy, status = model.Status, failure = model.LastFailureCode, remember = screen.RememberState() };
    }

    /// <summary>The menu open now in the top bar (its id) and the inventory's item menu, as the menu toggles see them.</summary>
    public object MenuState() => new
    {
        top = _shell?.OpenMenu,
        top_popups_visible = _shell?.VisibleMenus() ?? [],
        top_popups_filled = _shell?.Menu.Popups.Count(p => p.ItemCount > 0) ?? 0,   // 0 until a menu of the top bar has been opened
        inventory_context = _inventoryPanel?.Context.Visible ?? false,
        open_windows = _shell?.Shell.Floaters.OpenFloaters.Select(f => f.Id).ToArray() ?? [],
        inventory_selected = _inventoryPanel?.SelectedItem is { } sel && sel != Guid.Empty ? sel.ToString() : null,
    };

    /// <summary>{page}: opens Preferences on <paramref name="page"/> and answers, for each control of the page, its top and bottom against the
    /// page's visible height, and whether it can be seen without scrolling. Null when Preferences is not there.</summary>
    public object? PrefsLayoutCall(string page)
    {
        if (_preferences is not { } prefs || _shell is null || !Enum.TryParse<PrefPage>(page, true, out var which))
            return null;
        _shell.Shell.Floaters.Open(PreferencesFeature.FloaterId);
        prefs.ShowCategory(which.ToString().ToLowerInvariant());
        if (prefs.PageView(which) is not { } view)
            return null;
        var visible = view.GetGlobalRect();
        var controls = new List<object>();
        foreach (var def in PreferenceCatalog.OfPage(which))
        {
            if (view.ControlFor(def.Key) is not { } c)
                continue;
            var r = c.GetGlobalRect();
            controls.Add(new
            {
                key = def.Key,
                top = (int)(r.Position.Y - visible.Position.Y),
                bottom = (int)(r.End.Y - visible.Position.Y),
                visible = c.IsVisibleInTree() && r.Position.Y >= visible.Position.Y && r.End.Y <= visible.End.Y + 0.5f,
            });
        }
        var window = prefs.GetGlobalRect();
        return new { page = which.ToString(), window = new { w = (int)window.Size.X, h = (int)window.Size.Y }, visible_height = (int)visible.Size.Y, controls };
    }
}
