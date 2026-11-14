// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Globalization;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The window frame's live parts: the bars that belong to a session in the world, the location field, the connection mark,
/// the bell's count, and the grid's own name in the menu bar.</summary>
public partial class Main
{
    private NearbyLines? _nearbyLines;
    private bool _chromeInWorld;
    private UiColour? _gridAccent;

    /// <summary>Connects the bars to the shell's commands and to the login model's view of the grid. Called once the shell is built.</summary>
    private void WireChrome(LoginModel model, StringTable strings)
    {
        if (_shell is null)
            return;
        _nearbyLines = new NearbyLines(strings, UserSettings(null));
        _shell.MainView.AddChild(_nearbyLines);
        _shell.Navigation.HomeButton.Pressed += () => _shell.Shell.Commands.Execute("travel.home");
        _shell.Navigation.LocationSubmitted += TeleportToTyped;
        model.BrandingChanged += branding => Callable.From(() => ApplyBranding(branding)).CallDeferred();
        ApplyBranding(model.Branding);   // a grid that answered before the bars were built
    }

    /// <summary>The grid's name goes in the menu bar; its accent is offered on the Interface page and used if the person chose it.</summary>
    private void ApplyBranding(GridBranding branding)
    {
        _shell?.SetBrand(branding.Name);
        _gridName = branding.Name;
        UpdateWindowTitle();
        _loginScreen?.ApplyBranding(branding);
        _gridAccent = branding.Accent;
        _preferences?.SetGridAccent(branding.Accent);
    }

    /// <summary>"Region", or "Region/x/y/z" with the position inside it.</summary>
    private void TeleportToTyped(string typed)
    {
        var parts = typed.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || _travel is null)
            return;
        float Coord(int at, float fallback) => parts.Length > at && float.TryParse(parts[at], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        _travel.Teleport.ToRegion(parts[0], new System.Numerics.Vector3(Coord(1, 128), Coord(2, 128), Coord(3, 30)));
    }

    private string? _gridName;
    private SettingsService? _titleSettings;
    private int _screen = -1;

    /// <summary>The operating system's window title: "Reverie Viewer", and once logged in the avatar's name and the grid's.</summary>
    private void UpdateWindowTitle()
    {
        if (_strings is null)
            return;
        string? avatar = _chromeInWorld && Model is { } m ? LoginName.Display(m.FirstName, m.LastName) : null;
        GetWindow().Title = WindowTitleText.For(_strings, avatar, _gridName);
    }

    /// <summary>When the window moves to a screen with another display scale the interface follows it, unless the person set a size
    /// of their own.</summary>
    private void FollowScreen()
    {
        int screen = GetWindow().CurrentScreen;
        if (screen == _screen)
            return;
        bool first = _screen < 0;
        _screen = screen;
        if (first || _titleSettings is null)
            return;
        var look = ThemeStore.Load(_titleSettings, null);
        if (!ThemeStore.ScaleOwn(_titleSettings, null, look))
            UiTheme.Apply(UiTheme.Tokens, DisplayScaleProbe.Reading());
    }

    private void UpdateChrome()
    {
        if (_shell is null)
            return;
        var session = Model?.Session;
        bool inWorld = session is { IsEnded: false } s && s.AgentId != Guid.Empty;
        FollowScreen();
        if (inWorld != _chromeInWorld)
        {
            _chromeInWorld = inWorld;
            UpdateWindowTitle();
            _shell.ShowWorldRows(inWorld);
            _shell.SetConnected(inWorld);
        }
        if (!inWorld)
            return;
        if (session!.Region is { } region)
        {
            // as the official location bar: "Parcel, Region" when the parcel has a name, the region alone when not, without coordinates
            // (the bar's default); the region's maturity text is not known to the session yet, so it is left out
            var (where, speed) = session.World.LastSelfMotion is { } self ? (self.Motion.Position - session.World.OriginOffset, self.Motion.Velocity.Length()) : (System.Numerics.Vector3.Zero, 0f);
            _shell.Navigation.SetLocation(LocationText.Build(LocationText.BarFormat(showCoordinates: false), region.Name, session.AgentParcelName, where, speed));
        }
        _shell.SetUnread(_notePanels?.Toasts.Unread ?? 0);
    }
}
