// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Shell;

/// <summary>
/// The time-of-day choice made in the World > Environment menu: the region's own environment (the default), or one of four fixed
/// skies. As in the official viewer (llviewermenu.cpp, World.EnvSettings) the choice is the viewer's own and stays when the avatar
/// teleports; it is kept for the next login only when the setting EnvironmentPersistAcrossLogin is on (llenvironment.cpp,
/// saveToSettings; the setting is off by default there too). Main thread only.
/// </summary>
public sealed class EnvironmentChoice
{
    private readonly SettingsService? _settings;

    public EnvironmentChoice(SettingsService? settings)
    {
        _settings = settings;
        if (settings is not null && settings.Get(Settings.EnvironmentPersistAcrossLogin)
            && SkyChoices.TryParse(settings.GetText(Settings.LocalEnvironmentSkyKey), out var kept))
            Current = kept;
    }

    /// <summary>Raised with the new choice after a real change.</summary>
    public event Action<SkyChoice>? Changed;

    public SkyChoice Current { get; private set; } = SkyChoice.Region;

    public void Set(SkyChoice choice)
    {
        if (choice == Current) return;
        Current = choice;
        if (_settings is not null && _settings.Get(Settings.EnvironmentPersistAcrossLogin))
            _settings.SetText(Settings.LocalEnvironmentSkyKey, SkyChoices.Label(choice).ToLowerInvariant());
        Changed?.Invoke(choice);
    }

    /// <summary>A teleport has ended: with SwitchToSharedEnvAfterTeleport on, the region's own environment is back in force (llagent.cpp handleTeleportFinished).</summary>
    public void TeleportFinished()
    {
        if (_settings is not null && _settings.Get(Settings.SwitchToSharedEnvAfterTeleport)) Set(SkyChoice.Region);
    }

    /// <summary>The next choice in the cycle Region, Sunrise, Midday, Sunset, Midnight, Region (the F9 key).</summary>
    public void Cycle() => _ = Request(SkyChoices.Next(Current));

    /// <summary>
    /// Loads what a built-in sky needs before it is put in force (its settings asset); null: no preparation, the choice is set at
    /// once (test renders and replays, which have no grid to fetch from). Returns false when it could not be loaded.
    /// </summary>
    public Func<SkyChoice, Task<bool>>? Prepare { get; set; }

    /// <summary>Raised when a choice could not be prepared; the choice in force is unchanged, as in the official viewer, whose menu then shows its notice and keeps the old sky.</summary>
    public event Action<SkyChoice>? Failed;

    private int _requests;

    /// <summary>
    /// Asks for a choice: prepared first when <see cref="Prepare"/> is set, and put in force only when that worked. Going back to the
    /// region's own environment needs no preparation. A later request replaces an earlier one that is still loading.
    /// </summary>
    public async Task Request(SkyChoice choice)
    {
        int mine = ++_requests;
        if (choice == Current) return;
        if (choice != SkyChoice.Region && Prepare is { } prepare)
        {
            bool ok;
            try { ok = await prepare(choice); }
            catch (Exception e) when (e is not OutOfMemoryException) { ok = false; }
            if (mine != _requests) return;   // another choice was asked for meanwhile
            if (!ok) { Failed?.Invoke(choice); return; }
        }
        Set(choice);
    }

    /// <summary>The clouds' drift is stopped (World > Environment > Pause Clouds; the official pauseCloudScroll and resumeCloudScroll).</summary>
    public bool CloudsPaused { get; private set; }

    /// <summary>Raised with the new state after the clouds are paused or resumed.</summary>
    public event Action<bool>? CloudsPausedChanged;

    public void ToggleCloudsPaused()
    {
        CloudsPaused = !CloudsPaused;
        CloudsPausedChanged?.Invoke(CloudsPaused);
    }
}

/// <summary>
/// The World > Environment submenu. Items and order follow the official viewer's menu_viewer.xml: Sunrise, Midday, Midday (Legacy),
/// Sunset, Midnight, Use Shared Environment (back to the region's own environment), Personal Lighting, then Pause Clouds. The official
/// viewer's shortcuts are control+shift+O (sunrise), Y (midday), T (midday, legacy), N (sunset) and Z (midnight).
/// </summary>
public static class EnvironmentMenu
{
    public const string CycleCommand = "env.cycle";

    /// <summary>The command a shortcut runs, by the official viewer's parameter name (sunrise, noon, sunset, midnight).</summary>
    public static string ShortcutCommand(string officialName) => "env.key." + officialName;

    /// <summary>The official name of the sky a control+shift+letter shortcut picks; null for a letter that is not one of them.</summary>
    public static string? OfficialNameForKey(char letter) => char.ToUpperInvariant(letter) switch
    {
        'O' => "sunrise",
        'Y' => "noon",
        'T' => "legacy noon",
        'N' => "sunset",
        'Z' => "midnight",
        _ => null,
    };

    public static void Install(MenuModel menu, ShellCommands commands, EnvironmentChoice choice, Action openPersonalLighting)
    {
        (string Item, string Official, SkyChoice Sky)[] items =
        [
            ("sunrise", "sunrise", SkyChoice.Sunrise),
            ("midday", "noon", SkyChoice.Midday),
            ("legacymidday", "legacy noon", SkyChoice.LegacyMidday),
            ("sunset", "sunset", SkyChoice.Sunset),
            ("midnight", "midnight", SkyChoice.Midnight),
            ("region", "region", SkyChoice.Region),
        ];
        menu.AddSubmenu("world", "world.environment", "menu.world.environment", order: 200);
        int order = 1;
        foreach (var (item, official, sky) in items)
        {
            var chosen = sky;
            commands.Register("env." + item, () => _ = choice.Request(chosen));
            if (item is not "region") commands.Register(ShortcutCommand(official), () => _ = choice.Request(chosen));
            menu.AddSubEntry("world.environment", new MenuEntry("env." + item, "menu.env." + item, "env." + item, Order: order++, Checked: () => choice.Current == chosen));
        }
        commands.Register(CycleCommand, choice.Cycle);
        commands.Register("env.personal", openPersonalLighting);
        menu.AddSubEntry("world.environment", new MenuEntry("env.personal", "menu.env.personal", "env.personal", Order: order++));
        commands.Register("env.pauseclouds", choice.ToggleCloudsPaused);
        menu.AddSubEntry("world.environment", new MenuEntry("env.pauseclouds", "menu.env.pauseclouds", "env.pauseclouds", Order: order, Checked: () => choice.CloudsPaused));
    }
}
