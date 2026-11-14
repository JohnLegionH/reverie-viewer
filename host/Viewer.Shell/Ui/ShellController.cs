// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The shell as one object the engine host builds its controls from: the frame's regions, the floaters, the keyboard focus rule,
/// the menus, the toolbar and the commands they run. No engine type here, so everything is tested without the engine. It
/// ships a Viewer menu (Quit), a Help menu (About) and the About floater, and no toolbar button: feature code adds its
/// own menu entries, buttons, commands and floaters through <see cref="Menus"/>, <see cref="Toolbar"/>,
/// <see cref="Commands"/> and <see cref="Floaters"/>.
/// </summary>
public sealed class ShellController
{
    public const string AboutFloaterId = "about";

    /// <summary>The Licences floater: the viewer's licence, the third-party notices and the engine's own text.</summary>
    public const string LicencesFloaterId = "licences";

    public ShellController(StringTable strings, Action quit, IFloaterPlaces? places = null, ShellMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(quit);
        Strings = strings;
        Metrics = metrics ?? ShellMetrics.Default;
        Floaters = new FloaterManager(places);
        Focus = new InputFocus(Floaters);
        Menus = new MenuModel();
        Toolbar = new ToolbarModel();
        Commands = new ShellCommands();

        Floaters.Register(new FloaterSpec(AboutFloaterId, "about.title", new UiSize(520, 420), new UiSize(280, 180)));   // the longer About text scrolls inside it
        Floaters.Register(new FloaterSpec(LicencesFloaterId, "about.licences.title", new UiSize(640, 480), new UiSize(360, 240)));
        Commands.Register("app.quit", quit);
        Commands.Register("floater.about", () => Floaters.Toggle(AboutFloaterId));
        Commands.Register("floater.licences", () => Floaters.Toggle(LicencesFloaterId));
        Menus.AddMenu("me", "menu.me");
        Menus.AddEntry("me", new MenuEntry("me.quit", "menu.me.quit", "app.quit", Order: 1000));
        Menus.AddMenu("help", "menu.help");
        Menus.AddEntry("help", new MenuEntry("help.about", "menu.help.about", "floater.about"));
    }

    public StringTable Strings { get; }
    public ShellMetrics Metrics { get; }
    public FloaterManager Floaters { get; }
    public InputFocus Focus { get; }
    public MenuModel Menus { get; }
    public ToolbarModel Toolbar { get; }
    public ShellCommands Commands { get; }

    /// <summary>Which optional rows of the frame are shown (the toolbar shows while it has a button).</summary>
    public ChromeRows Chrome { get; set; }

    /// <summary>Lays the frame out for a window size and gives the floaters the main region to live in.</summary>
    /// <summary>As <see cref="Layout(UiSize)"/>, but a bar whose content needs more height than its design height gets what it needs
    /// (the drawing side measures it), so it never draws over the next row.</summary>
    public ShellRegions Layout(UiSize window, BarHeights measured) =>
        Layout(window, Metrics with
        {
            MenuBarHeight = Math.Max(Metrics.MenuBarHeight, measured.Menu),
            NavBarHeight = Math.Max(Metrics.NavBarHeight, measured.Nav),
            ChatBarHeight = Math.Max(Metrics.ChatBarHeight, measured.Chat),
            ToolbarHeight = Math.Max(Metrics.ToolbarHeight, measured.Toolbar),
        });

    public ShellRegions Layout(UiSize window) => Layout(window, Metrics);

    private ShellRegions Layout(UiSize window, ShellMetrics metrics)
    {
        var regions = ShellLayout.Compute(window, metrics, Chrome with { Toolbar = Toolbar.Buttons.Count > 0 });
        Floaters.SetBounds(regions.Main);
        return regions;
    }
}
