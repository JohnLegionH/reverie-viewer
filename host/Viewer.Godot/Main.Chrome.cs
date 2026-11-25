// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Globalization;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Grids;

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
        // the chat bar grows upward over the view: the newest lines stay above it
        float top = _nearbyLines.OffsetTop, bottom = _nearbyLines.OffsetBottom;
        _shell.Chat.HeightChanged += () =>
        {
            _nearbyLines.OffsetTop = top - _shell.Chat.ExtraHeight;
            _nearbyLines.OffsetBottom = bottom - _shell.Chat.ExtraHeight;
        };
        var bar = _shell.TopBar;
        bar.Home.Pressed += () => _shell.Shell.Commands.Execute("travel.home");
        bar.Back.Pressed += () => { if (_teleportHistory.Back() is { } back) TeleportTo(back, rememberDeparture: false); };
        bar.Location.Submitted += address => TeleportTo(address, rememberDeparture: true);
        bar.Location.Star.Pressed += MakeLandmark;
        if (!_shell.Shell.Commands.Has("app.logout"))
            _shell.Shell.Commands.Register("app.logout", () => _ = LogOutFromMenu());
        if (!_shell.Shell.Commands.Has("app.check_updates"))
            _shell.Shell.Commands.Register("app.check_updates", () => _ = CheckForUpdatesFromMenu(strings));
        WindowMenu.Install(_shell.Shell.Menus, () => GetWindow().Mode == Window.ModeEnum.Fullscreen);
        _shell.Shell.Commands.Register(WindowMenu.FullscreenCommand, ToggleFullscreen);
        _shell.Shell.Commands.Register(WindowMenu.MinimizeCommand, MinimizeWindow);
        _shell.RebuildMenus();
        model.BrandingChanged += branding => Callable.From(() => ApplyBranding(branding)).CallDeferred();
        ApplyBranding(model.Branding);   // a grid that answered before the bars were built
    }

    /// <summary>The grid's name goes in the menu bar; its accent is offered on the Interface page and used if the person chose it.</summary>
    private void ApplyBranding(GridBranding branding)
    {
        // in the world the top bar shows the grid of the region (Main.GridPresentation.cs); here, the grid chosen on the login screen, shown
        // safely (a name that poses as Second Life shows its host, two saved grids with one name both show theirs)
        if (_commsFor is null)
        {
            string? shown = branding.Name;
            if (shown is not null && _gridList?.Selected is { } chosen)
                shown = GridLabels.Brand(chosen with { Name = shown }, _gridList.All);
            _shell?.SetBrand(shown);
            _gridName = shown;
            UpdateWindowTitle();
        }
        _loginScreen?.ApplyBranding(branding);
        _gridAccent = branding.Accent;
        _preferences?.SetGridAccent(branding.Accent);
    }

    private readonly TeleportHistory _teleportHistory = new();
    private LocationAddress? _here;

    /// <summary>A place typed in the location field (a region name, region/x/y/z, or a hop or secondlife address), or Back: the
    /// teleport is asked for; the place left is remembered for Back.</summary>
    private void TeleportTo(LocationAddress address, bool rememberDeparture)
    {
        if (_travel is null)
            return;
        if (rememberDeparture && _here is { } from)
            _teleportHistory.Departed(from);
        // a place on another grid goes through that grid's map lookup (a Hypergrid address); one on the grid the agent is on, by its name
        string grid = _travelFor?.CurrentGrid ?? "";
        _travel.Teleport.ToAddress(address.Travel, grid.Length > 0 ? grid : null);
    }

    /// <summary>Log out (the app menu): the session ends and the login screen comes back.</summary>
    private async System.Threading.Tasks.Task LogOutFromMenu()
    {
        if (Model?.Session is not { IsEnded: false } s)
            return;
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await s.LogoutAsync(cts.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { GD.Print("logout: " + ex.GetType().Name); }
    }

    /// <summary>Check for updates (the app menu): asks the release page only when chosen, and says what it found in a notice.</summary>
    private async System.Threading.Tasks.Task CheckForUpdatesFromMenu(StringTable strings)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReverieViewer");
        var result = await UpdateCheck.RunAsync(async url =>
        {
            using var reply = await http.GetAsync(url);
            return ((int)reply.StatusCode, await reply.Content.ReadAsStringAsync());
        }, Viewer.Core.Identity.ViewerIdentity.Current.Version);
        string text = result.Outcome switch
        {
            UpdateOutcome.UpToDate => strings.Get("update.uptodate"),
            UpdateOutcome.Available => strings.Get("update.available", result.Version ?? ""),
            _ => strings.Get("update.failed"),
        };
        Callable.From(() => _shell?.ShowNotice(text)).CallDeferred();
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
            // the top bar's location field: the parcel (when it has a name), the region once, and the coordinates dimmer (spec 3)
            // while visiting another grid the region is shown with that grid's host; history keeps the grid of every place
            _shell.SetLocation(LocationField.Display(region.Name, session.AgentParcelName, where, speed, session.VisitedGrid));
            _here = new LocationAddress(region.Name, where, session.CurrentGrid.Length > 0 ? session.CurrentGrid : null);
        }
        _shell.SetUnread(_notePanels?.Toasts.Unread ?? 0);
        _shell.SetUnreadConversations(_comms?.Ims.Open.Sum(c => c.Unread) ?? 0);
    }
}
