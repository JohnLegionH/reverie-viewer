// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>One group of settings on a Preferences page: a flat header over its rows. A collapsed group (the page's "More") holds what few
/// people change; it is closed until opened and says in one line what it holds while it is closed.</summary>
public sealed record PrefSection(string Id, bool Collapsed, IReadOnlyList<string> Keys)
{
    /// <summary>The string key of the section's header text.</summary>
    public string TitleKey(PrefPage page) => "prefs.section." + page.ToString().ToLowerInvariant() + "." + Id;

    /// <summary>The string key of the one-line summary shown while a collapsed section is closed (its arguments are the settings' values, in order).</summary>
    public string SummaryKey(PrefPage page) => TitleKey(page) + ".summary";
}

/// <summary>
/// How each ordinary Preferences page groups its settings: the everyday settings under flat headers, the rarely used ones in a closed
/// "More" (UI-STANDARD U5, U6, U8). The Sound page and the Interface page are laid out by their own code. Every setting the catalogue
/// lists for a page is in exactly one section, in catalogue order within it (tested).
/// </summary>
public static class PreferencePageLayout
{
    private static PrefSection S(string id, params string[] keys) => new(id, false, keys);
    private static PrefSection More(params string[] keys) => new("more", true, keys);

    /// <summary>The sections of a page, top to bottom; empty for a page drawn by its own code.</summary>
    public static IReadOnlyList<PrefSection> For(PrefPage page) => page switch
    {
        PrefPage.General =>
        [
            S("language", Settings.UiLanguageKey),
            S("nametags", Settings.NameTagMode.Key, Settings.NameTagShowSelf.Key, Settings.NameTagGroupTitles.Key, Settings.NameTagDisplayNames.Key,
                Settings.NameTagUserNames.Key, Settings.NameTagFriends.Key),
            S("login", Settings.LoginStartLocationKey),
        ],
        PrefPage.Graphics =>
        [
            S("quality", Settings.GraphicsPresetKey, Settings.DrawDistance.Key, Settings.MaxTextureResolution.Key, Settings.CloudsEnabled.Key,
                Settings.NeighbourRegions.Key),
            S("display", Settings.Vsync.Key, Settings.WindowModeKey),
            More(Settings.FpsLimit.Key, Settings.EnvironmentPersistAcrossLogin.Key, Settings.SwitchToSharedEnvAfterTeleport.Key, Settings.GraphicsBackendKey),
        ],
        PrefPage.Chat =>
        [
            S("text", Settings.ChatFontSize.Key, Settings.ChatOverlaySeconds.Key, Settings.ChatTimestamps.Key),
            S("notices", Settings.FriendOnlineNotices.Key, Settings.FriendOnlineInChat.Key),
        ],
        PrefPage.MoveView =>
        [
            S("movement", Settings.DoubleTapRun.Key, Settings.AutomaticFly.Key, Settings.ArrowKeysAlwaysMove.Key),
            S("mouselook", Settings.MouseSensitivity.Key, Settings.InvertMouse.Key, Settings.EnableMouselook.Key, Settings.ShowCrosshairs.Key),
            More(Settings.MouseSmooth.Key, Settings.CameraSmoothing.Key, Settings.DisableCameraConstraints.Key),
        ],
        PrefPage.Privacy =>
        [
            S("login", Settings.LoginRememberName.Key),
            S("status", Settings.ShareOnlineStatus.Key),
        ],
        PrefPage.Cache => [S("cache", Settings.CacheFolderKey, Settings.CacheSizeMb.Key)],
        PrefPage.Building =>
        [
            S("size", NewPrimSettings.SizeX.Key, NewPrimSettings.SizeY.Key, NewPrimSettings.SizeZ.Key),
            S("look", NewPrimSettings.MaterialKey, NewPrimSettings.Red.Key, NewPrimSettings.Green.Key, NewPrimSettings.Blue.Key,
                NewPrimSettings.Transparency.Key, NewPrimSettings.Glow.Key, NewPrimSettings.FullBright.Key, NewPrimSettings.TextureKey),
            S("nextowner", NewPrimSettings.NextOwnerCopy.Key, NewPrimSettings.NextOwnerModify.Key, NewPrimSettings.NextOwnerTransfer.Key),
            More(NewPrimSettings.Physical.Key, NewPrimSettings.Temporary.Key, NewPrimSettings.Phantom.Key),
        ],
        _ => [],
    };
}
