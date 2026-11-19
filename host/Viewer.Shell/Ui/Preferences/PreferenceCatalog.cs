// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// Every setting that Preferences shows on its ordinary pages, in page order and display order. The pages are drawn from this list and
/// the tests run over it, so a setting cannot be on a page without a default, a range, a place in the settings file and a note
/// on whether it needs a restart.
/// </summary>
public static class PreferenceCatalog
{
    /// <summary>The ids each choice offers.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["default", .. StringTable.Available];
    public static IReadOnlyList<string> Presets { get; } = [.. GraphicsPresets.Levels, GraphicsPresets.Custom];
    public static IReadOnlyList<string> Backends { get; } = ["auto", "vulkan", "d3d12"];
    public static IReadOnlyList<string> WindowModes { get; } = ["windowed", "borderless", "fullscreen"];
    public static IReadOnlyList<string> StartLocations { get; } = ["last", "home"];

    private static TypedPref<T> P<T>(Setting<T> s, PrefPage page, PrefApply apply, double min = 0, double max = 0, PrefScope scope = PrefScope.Global)
        where T : struct => new(s, page, apply, scope, min, max);

    /// <summary>Every setting, page by page.</summary>
    public static IReadOnlyList<PrefDef> All { get; } =
    [
        // General
        new TextPref(Settings.UiLanguageKey, PrefPage.General, PrefApply.Restart, "default", Languages),
        P(Settings.NameTagMode, PrefPage.General, PrefApply.Live, 0, 2),
        P(Settings.NameTagShowSelf, PrefPage.General, PrefApply.Live),
        P(Settings.NameTagGroupTitles, PrefPage.General, PrefApply.Live),
        P(Settings.NameTagDisplayNames, PrefPage.General, PrefApply.Live),
        P(Settings.NameTagUserNames, PrefPage.General, PrefApply.Live),
        P(Settings.NameTagFriends, PrefPage.General, PrefApply.Live),
        new TextPref(Settings.LoginStartLocationKey, PrefPage.General, PrefApply.NextUse, "last", StartLocations),

        // Graphics
        new TextPref(Settings.GraphicsPresetKey, PrefPage.Graphics, PrefApply.Live, GraphicsPresets.Default, Presets),
        P(Settings.DrawDistance, PrefPage.Graphics, PrefApply.Live, 64, 1024),
        P(Settings.MaxTextureResolution, PrefPage.Graphics, PrefApply.Live, 256, 2048),
        P(Settings.FpsLimit, PrefPage.Graphics, PrefApply.Live, 0, 360),
        P(Settings.Vsync, PrefPage.Graphics, PrefApply.Live),
        P(Settings.CloudsEnabled, PrefPage.Graphics, PrefApply.Live),
        P(Settings.NeighbourRegions, PrefPage.Graphics, PrefApply.NextUse),
        new TextPref(Settings.GraphicsBackendKey, PrefPage.Graphics, PrefApply.Restart, "auto", Backends),
        new TextPref(Settings.WindowModeKey, PrefPage.Graphics, PrefApply.Live, "fullscreen", WindowModes),

        // Sound (the interface sounds; each sound's own source, level and switch are listed under these, see PreferencesModel)
        // the levels over everything, object sounds in the world, and ambient sounds (footsteps), as the official Sound & Media page
        P(Sound.AudioLevels.Master, PrefPage.Sound, PrefApply.Live, 0, 1),
        P(Sound.AudioLevels.MuteMaster, PrefPage.Sound, PrefApply.Live),
        P(Sound.AudioLevels.Mono, PrefPage.Sound, PrefApply.Live),
        P(Sound.AudioLevels.Sounds, PrefPage.Sound, PrefApply.Live, 0, 1),
        P(Sound.AudioLevels.MuteSounds, PrefPage.Sound, PrefApply.Live),
        P(Sound.AudioLevels.Ambient, PrefPage.Sound, PrefApply.Live, 0, 1),
        P(Sound.AudioLevels.MuteAmbient, PrefPage.Sound, PrefApply.Live),
        P(Sound.AudioLevels.CollisionSounds, PrefPage.Sound, PrefApply.Live),
        P(Sound.AudioLevels.GestureSounds, PrefPage.Sound, PrefApply.Live),
        P(Sound.AudioLevels.FootstepFallback, PrefPage.Sound, PrefApply.Live),
        P(Sound.UiSoundSettings.Volume, PrefPage.Sound, PrefApply.Live, 0, 1),
        P(Sound.UiSoundSettings.MuteAll, PrefPage.Sound, PrefApply.Live),
        P(Sound.UiSoundSettings.MuteWhenMinimized, PrefPage.Sound, PrefApply.Live),
        P(Sound.UiSoundSettings.MoneyThreshold, PrefPage.Sound, PrefApply.Live, 0, 1_000_000),
        P(Sound.UiSoundSettings.HealthThreshold, PrefPage.Sound, PrefApply.Live, 0, 100),

        // Chat
        P(Settings.ChatFontSize, PrefPage.Chat, PrefApply.Live, 10, 32),
        P(Settings.ChatOverlaySeconds, PrefPage.Chat, PrefApply.Live, 0, 600),
        P(Settings.ChatTimestamps, PrefPage.Chat, PrefApply.Live),

        // Move & View
        P(Settings.DoubleTapRun, PrefPage.MoveView, PrefApply.Live),
        P(Settings.AutomaticFly, PrefPage.MoveView, PrefApply.Live),
        P(Settings.ArrowKeysAlwaysMove, PrefPage.MoveView, PrefApply.Live),
        P(Settings.MouseSensitivity, PrefPage.MoveView, PrefApply.Live, 0, 15),
        P(Settings.InvertMouse, PrefPage.MoveView, PrefApply.Live),
        P(Settings.EnableMouselook, PrefPage.MoveView, PrefApply.Live),
        P(Settings.ShowCrosshairs, PrefPage.MoveView, PrefApply.Live),
        P(Settings.MouseSmooth, PrefPage.MoveView, PrefApply.Live),
        P(Settings.CameraSmoothing, PrefPage.MoveView, PrefApply.Live, 0, 4),
        P(Settings.DisableCameraConstraints, PrefPage.MoveView, PrefApply.Live),

        // Privacy
        P(Settings.LoginRememberName, PrefPage.Privacy, PrefApply.Live),
        P(Settings.ShareOnlineStatus, PrefPage.Privacy, PrefApply.Live, scope: PrefScope.PerAccount),

        // Cache
        new TextPref(Settings.CacheFolderKey, PrefPage.Cache, PrefApply.Restart, "", null, PrefKind.Folder),
        P(Settings.CacheSizeMb, PrefPage.Cache, PrefApply.Restart, 256, 65536),

        // Building: what Create makes
        P(NewPrimSettings.SizeX, PrefPage.Building, PrefApply.Live, 0.01, 64),
        P(NewPrimSettings.SizeY, PrefPage.Building, PrefApply.Live, 0.01, 64),
        P(NewPrimSettings.SizeZ, PrefPage.Building, PrefApply.Live, 0.01, 64),
        new TextPref(NewPrimSettings.MaterialKey, PrefPage.Building, PrefApply.Live, "wood", NewPrimSettings.Materials),
        P(NewPrimSettings.Red, PrefPage.Building, PrefApply.Live, 0, 1),
        P(NewPrimSettings.Green, PrefPage.Building, PrefApply.Live, 0, 1),
        P(NewPrimSettings.Blue, PrefPage.Building, PrefApply.Live, 0, 1),
        P(NewPrimSettings.Transparency, PrefPage.Building, PrefApply.Live, 0, 90),
        P(NewPrimSettings.Glow, PrefPage.Building, PrefApply.Live, 0, 1),
        P(NewPrimSettings.FullBright, PrefPage.Building, PrefApply.Live),
        new TextPref(NewPrimSettings.TextureKey, PrefPage.Building, PrefApply.Live, NewPrimDefaultsText.Plywood, null, PrefKind.Text),
        P(NewPrimSettings.Physical, PrefPage.Building, PrefApply.Live),
        P(NewPrimSettings.Temporary, PrefPage.Building, PrefApply.Live),
        P(NewPrimSettings.Phantom, PrefPage.Building, PrefApply.Live),
        P(NewPrimSettings.NextOwnerCopy, PrefPage.Building, PrefApply.Live),
        P(NewPrimSettings.NextOwnerModify, PrefPage.Building, PrefApply.Live),
        P(NewPrimSettings.NextOwnerTransfer, PrefPage.Building, PrefApply.Live),
    ];

    /// <summary>The new-prim texture setting's default as text.</summary>
    private static class NewPrimDefaultsText
    {
        public static readonly string Plywood = Viewer.Core.Building.NewPrimDefaults.Plywood.ToString();
    }

    /// <summary>The settings of one page, in the order shown.</summary>
    public static IReadOnlyList<PrefDef> OfPage(PrefPage page) => All.Where(d => d.Page == page).ToArray();

    /// <summary>The setting with this key, or null.</summary>
    public static PrefDef? Find(string key) => All.FirstOrDefault(d => d.Key == key);
}
