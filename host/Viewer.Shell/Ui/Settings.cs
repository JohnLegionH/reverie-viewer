// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json.Nodes;

namespace Viewer.Shell;

/// <summary>One typed setting: its key in <c>settings.json</c>, its default, and the rule a stored or new value must meet.
/// <paramref name="Fix"/> turns an acceptable value into the one kept (a clamp) or returns null to refuse it.</summary>
public sealed record Setting<T>(string Key, T Default, Func<T, T?> Fix) where T : struct;

/// <summary>The settings the shell and its features read. New settings default to the viewer's earlier behaviour.</summary>
public static class Settings
{
    /// <summary>Whether chat and instant message lines show their time.</summary>
    public static readonly Setting<bool> ChatTimestamps = new("chat_timestamps", true, v => v);

    /// <summary>Whether others are told when this user is typing (local chat and instant messages).</summary>
    public static readonly Setting<bool> ChatSendTyping = new("chat_send_typing", true, v => v);

    /// <summary>How many lines the nearby chat history keeps (50 to 5,000).</summary>
    public static readonly Setting<int> ChatHistoryLines = new("chat_history_lines", 500, v => Math.Clamp(v, 50, 5000));

    /// <summary>The text size of chat and instant message lines, in pixels (10 to 32).</summary>
    public static readonly Setting<int> ChatFontSize = new("chat_font_size", 15, v => Math.Clamp(v, 10, 32));

    /// <summary>Whether a friend coming online or going offline is announced in nearby chat (and in an open conversation with them).</summary>
    public static readonly Setting<bool> FriendOnlineNotices = new("friend_online_notices", true, v => v);

    /// <summary>The mini-map's zoom in pixels per region (32 to 4,096; the official default is 128).</summary>
    public static readonly Setting<double> MiniMapScale = new("minimap_scale", 128, v => double.IsFinite(v) ? Math.Clamp(v, 32, 4096) : null);

    /// <summary>Whether the mini-map turns with the camera (the official default is yes).</summary>
    public static readonly Setting<bool> MiniMapRotate = new("minimap_rotate", true, v => v);

    /// <summary>The world map's zoom in pixels per region (8 to 4,096; the official default is 128).</summary>
    public static readonly Setting<double> WorldMapScale = new("world_map_scale", 128, v => double.IsFinite(v) ? Math.Clamp(v, 8, 4096) : null);

    /// <summary>When avatars' name tags show: 0 never, 1 always, 2 for a while after a tag is made and then fading (the official default is always).</summary>
    public static readonly Setting<int> NameTagMode = new("avatar_name_tag_mode", 1, v => v is >= 0 and <= 2 ? v : null);

    /// <summary>Whether the user's own avatar has a name tag (the official default is yes).</summary>
    public static readonly Setting<bool> NameTagShowSelf = new("name_tag_show_self", true, v => v);

    /// <summary>Whether a tag shows the avatar's group title (the official default is yes).</summary>
    public static readonly Setting<bool> NameTagGroupTitles = new("name_tag_group_titles", true, v => v);

    /// <summary>Whether a tag shows display names where the grid has them (the official default is yes).</summary>
    public static readonly Setting<bool> NameTagDisplayNames = new("name_tag_display_names", true, v => v);

    /// <summary>Whether a tag shows the account name under a display name (the official default is yes).</summary>
    public static readonly Setting<bool> NameTagUserNames = new("name_tag_user_names", true, v => v);

    /// <summary>Whether a friend's tag has the friend colour (the official default is no).</summary>
    public static readonly Setting<bool> NameTagFriends = new("name_tag_friends", false, v => v);

    /// <summary>The inventory's sort, as the official InventorySortOrder setting: +1 by date (else by name), +2 folders always by name, +4 system
    /// folders on top. Folders here are always by name with system folders on top, so only the date bit changes anything; the default keeps
    /// this viewer's earlier sort by name.</summary>
    public static readonly Setting<int> InventorySortOrder = new("InventorySortOrder", 6, v => v is >= 0 and <= 7 ? v : null);

    /// <summary>Whether the whole inventory is fetched in the background after arrival (on by default; off keeps the older
    /// behaviour of fetching a folder only when it is opened or a search needs it).</summary>
    public static readonly Setting<bool> InventoryBackgroundFetch = new("inventory_background_fetch", true, v => v);

    /// <summary>Whether the animation overrider is used: the user's own animation set plays in place of the grid's default
    /// animations (off by default, as in the Firestorm viewer).</summary>
    public static readonly Setting<bool> AnimationOverrider = new("animation_overrider", false, v => v);

    /// <summary>The key under which the name of the animation set used last is kept.</summary>
    public const string AnimationOverriderSetKey = "animation_overrider_set";

    /// <summary>Whether the login screen keeps the username (never the password) on this computer.</summary>
    public static readonly Setting<bool> LoginRememberName = new("login_remember_name", false, v => v);
    /// <summary>Whether the login screen keeps the login hash of the next successful login (off by default, as the official viewer).</summary>
    public static readonly Setting<bool> LoginRememberPassword = new("login_remember_password", false, v => v);

    /// <summary>Whether the disclosure shown at the first run was accepted (<see cref="Disclosures"/>).</summary>
    public static readonly Setting<bool> DisclosureFirstRunAccepted = new("disclosure_first_run_accepted", false, v => v);
    /// <summary>Whether the disclosure shown before the first Second Life login was accepted (<see cref="Disclosures"/>).</summary>
    public static readonly Setting<bool> DisclosureSecondLifeAccepted = new("disclosure_second_life_accepted", false, v => v);

    /// <summary>The settings keys of the remembered username (kept only while <see cref="LoginRememberName"/> is on).</summary>
    public const string LoginFirstNameKey = "login_first", LoginLastNameKey = "login_last", LoginUserNameKey = "login_username";

    /// <summary>Whether the agent was flying when the last session ended; the next login starts flying only then (the official viewer's
    /// FlyingAtExit, written at quit in llappviewer.cpp and read in llagent.cpp, LLAgent::init). Not a preference.</summary>
    public static readonly Setting<bool> FlyingAtExit = new("flying_at_exit", false, v => v);

    // ---- The window's own memory (WindowMemory); not a preference ----

    public static readonly Setting<bool> WindowMaximized = new(WindowMemory.MaximizedKey, false, v => v);
    public static readonly Setting<int> WindowX = new(WindowMemory.XKey, 0, v => v);
    public static readonly Setting<int> WindowY = new(WindowMemory.YKey, 0, v => v);
    public static readonly Setting<int> WindowWidth = new(WindowMemory.WidthKey, 0, v => v);
    public static readonly Setting<int> WindowHeight = new(WindowMemory.HeightKey, 0, v => v);
    public static readonly Setting<int> WindowMonitorX = new(WindowMemory.MonitorXKey, 0, v => v);
    public static readonly Setting<int> WindowMonitorY = new(WindowMemory.MonitorYKey, 0, v => v);

    // ---- Preferences pages (see PrefDef and PreferenceCatalog) ----

    /// <summary>The farthest objects are built and asked for, in metres (64 to 1,024; 128 by default, the official viewer's level for most graphics cards).</summary>
    public static readonly Setting<int> DrawDistance = new("draw_distance", 128, v => Math.Clamp(v, 64, 1024));

    /// <summary>Which version of the graphics defaults the stored settings were last brought to; a newer version resets the values it changed once.</summary>
    public static readonly Setting<int> GraphicsDefaultsVersion = new("graphics_defaults_version", 0, v => v);
    public const int CurrentGraphicsDefaultsVersion = 1;

    /// <summary>The largest texture level kept, in texels across: 256, 512, 1,024 or 2,048 (2,048 is the viewer's rule today).</summary>
    public static readonly Setting<int> MaxTextureResolution = new("max_texture_resolution", 2048, v => v is 256 or 512 or 1024 or 2048 ? v : null);

    /// <summary>The frame-rate limit: 0 for none, else 15 to 360 frames a second.</summary>
    public static readonly Setting<int> FpsLimit = new("fps_limit", 0, v => v < 0 ? null : v == 0 ? 0 : Math.Clamp(v, 15, 360));

    /// <summary>Vertical sync (on: the engine's own default).</summary>
    public static readonly Setting<bool> Vsync = new("vsync", true, v => v);

    /// <summary>Whether the frame-time log is written (off). It is kept in the settings file only; Preferences does not show it.</summary>
    public static readonly Setting<bool> FrameTimeLog = new("frame_time_log", false, v => v);

    /// <summary>Whether the sky draws clouds.</summary>
    public static readonly Setting<bool> CloudsEnabled = new("clouds_enabled", true, v => v);

    /// <summary>Whether the regions around the agent's are connected to and drawn; the same key the session's own setting reads (on unless turned off).</summary>
    public static readonly Setting<bool> NeighbourRegions = new("neighbours", true, v => v);

    /// <summary>How long a nearby chat line stays whole over the view, in seconds (0: until pushed out by newer lines; up to 600). The
    /// official viewer's NearbyToastLifeTime, 23.</summary>
    public static readonly Setting<int> ChatOverlaySeconds = new("chat_overlay_seconds", 23, v => Math.Clamp(v, 0, 600));

    /// <summary>How long a nearby chat line stays at half opacity after its time is up, in seconds. The official viewer's
    /// NearbyToastFadingTime, 3.</summary>
    public static readonly Setting<int> ChatOverlayFadeSeconds = new("chat_overlay_fade_seconds", 3, v => Math.Clamp(v, 0, 60));

    /// <summary>Tap a direction twice and hold to run (the official viewer's AllowTapTapHoldRun).</summary>
    public static readonly Setting<bool> DoubleTapRun = new("double_tap_run", true, v => v);

    /// <summary>Fly by holding the jump key (the official viewer's AutomaticFly).</summary>
    public static readonly Setting<bool> AutomaticFly = new("automatic_fly", true, v => v);

    /// <summary>Arrow keys still move the avatar while a text field has the keyboard (the official viewer's ArrowKeysAlwaysMove).</summary>
    public static readonly Setting<bool> ArrowKeysAlwaysMove = new("arrow_keys_always_move", false, v => v);

    /// <summary>Mouse-look sensitivity (0 to 15; the official default is 3).</summary>
    public static readonly Setting<double> MouseSensitivity = new("mouse_sensitivity", 3.0, v => double.IsFinite(v) ? Math.Clamp(v, 0, 15) : null);

    public static readonly Setting<bool> InvertMouse = new("invert_mouse", false, v => v);
    public static readonly Setting<bool> EnableMouselook = new("enable_mouselook", true, v => v);
    public static readonly Setting<bool> ShowCrosshairs = new("show_crosshairs", true, v => v);
    public static readonly Setting<bool> MouseSmooth = new("mouse_smooth", false, v => v);

    /// <summary>How much the follow camera smooths its position (0 turns it off; 1 is the official default; up to 4).</summary>
    public static readonly Setting<double> CameraSmoothing = new("camera_smoothing", 1.0, v => double.IsFinite(v) ? Math.Clamp(v, 0, 4) : null);

    /// <summary>Lets the camera go beyond its normal bounds: past the draw distance, without a zoom limit, and far under the land (the
    /// official viewer's DisableCameraConstraints, off by default).</summary>
    public static readonly Setting<bool> DisableCameraConstraints = new("disable_camera_constraints", false, v => v);

    /// <summary>The content cache's size limit in megabytes (256 to 65,536; 1,024, the cap it has always had, by default).</summary>
    public static readonly Setting<int> CacheSizeMb = new("cache_size_mb", 1024, v => Math.Clamp(v, 256, 65536));

    /// <summary>Whether accounts show their online status to their friends; the stored key adds the account.</summary>
    public static readonly Setting<bool> ShareOnlineStatus = new("share_online_status", true, v => v);

    /// <summary>Whether this account shows its online status to its friends (one key per account).</summary>
    public static Setting<bool> ShareOnlineStatusOf(string account) => ShareOnlineStatus with { Key = ShareOnlineStatus.Key + "_" + account };

    /// <summary>Opacity of the window that has the focus (the official viewer's ActiveFloaterTransparency).</summary>
    public static readonly Setting<float> ActiveFloaterTransparency = new("ActiveFloaterTransparency", 1.0f, v => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : null);

    /// <summary>Opacity of the windows that do not have the focus (the official viewer's InactiveFloaterTransparency).</summary>
    public static readonly Setting<float> InactiveFloaterTransparency = new("InactiveFloaterTransparency", 0.95f, v => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : null);

    /// <summary>Whether the time-of-day choice made in the World > Environment menu is kept for the next login (the official viewer's
    /// EnvironmentPersistAcrossLogin, off by default there as here).</summary>
    public static readonly Setting<bool> EnvironmentPersistAcrossLogin = new("EnvironmentPersistAcrossLogin", false, v => v);

    /// <summary>Whether a teleport that has ended puts the region's own environment back in place of the choice made in the Environment menu (the official
    /// viewer's SwitchToSharedEnvAfterTeleport, off by default for an existing user).</summary>
    public static readonly Setting<bool> SwitchToSharedEnvAfterTeleport = new("SwitchToSharedEnvAfterTeleport", false, v => v);

    /// <summary>The key of the kept time-of-day choice (text).</summary>
    public const string LocalEnvironmentSkyKey = "LocalEnvironmentSky";

    /// <summary>The keys of the choices kept as text.</summary>
    public const string UiLanguageKey = "ui_language", GraphicsPresetKey = "graphics_preset", GraphicsBackendKey = "graphics_backend",
        WindowModeKey = "window_mode", LoginStartLocationKey = "login_start_location", CacheFolderKey = "cache_folder";

    /// <summary>When an account last logged out, in whole seconds since 1970 (0: never). One key per account, so accounts that
    /// share a computer keep their own. The inventory's Recent tab lists what is newer than this.</summary>
    public static Setting<long> LastLogoutOf(Guid agent) => new("last_logout_" + agent.ToString("N"), 0L, v => v >= 0 ? v : null);
}

/// <summary>Reads and writes the logout time that the Recent tab compares with.</summary>
public static class LogoutTime
{
    /// <summary>The time the account last logged out, or null when none is kept.</summary>
    public static DateTimeOffset? Previous(SettingsService settings, Guid agent)
    {
        long seconds = settings.Get(Settings.LastLogoutOf(agent));
        return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    /// <summary>Keeps <paramref name="now"/> as the account's logout time.</summary>
    public static void Record(SettingsService settings, Guid agent, DateTimeOffset now) =>
        settings.Set(Settings.LastLogoutOf(agent), Math.Max(0, now.ToUnixTimeSeconds()));
}

/// <summary>Where the settings text lives (a file in the per-user settings folder; a string in tests).</summary>
public interface ISettingsStore
{
    /// <summary>The stored text, or null when there is none or it cannot be read.</summary>
    string? Read();

    /// <summary>Replaces the stored text. False when it could not be written (the values stay in memory).</summary>
    bool Write(string text);
}

/// <summary>
/// The typed settings service: reads <c>settings.json</c> once, hands out typed values with their defaults, accepts changes
/// only through each setting's rule, raises <see cref="Changed"/> for a real change, and writes the file back keeping every
/// key it does not know (other parts of the viewer read their own keys from the same file). A stored value of the wrong type
/// or one the rule refuses is ignored and the default used, so a mistyped file never breaks the viewer. Main thread only.
/// </summary>
public sealed class SettingsService
{
    private readonly ISettingsStore? _store;
    private readonly JsonObject _doc;

    public SettingsService(ISettingsStore? store)
    {
        _store = store;
        JsonObject? doc = null;
        try { doc = JsonNode.Parse(store?.Read() ?? "{}") as JsonObject; }
        catch (System.Text.Json.JsonException) { /* an unreadable file: defaults, and the next change writes a fresh one */ }
        _doc = doc ?? new JsonObject();
    }

    /// <summary>Brings the stored graphics values to the current defaults once. The official viewer resets a person's graphics settings when
    /// its feature table version changes. Here a draw distance of 1024 (the earlier default) is dropped, so the new default applies; any other
    /// stored value is kept. Returns the value that was dropped, or null.</summary>
    public int? MigrateGraphicsDefaults()
    {
        if (Get(Settings.GraphicsDefaultsVersion) >= Settings.CurrentGraphicsDefaultsVersion)
            return null;
        int? dropped = null;
        if (_doc[Settings.DrawDistance.Key] is JsonValue v && v.TryGetValue(out int stored) && stored == 1024)
        {
            _doc.Remove(Settings.DrawDistance.Key);
            dropped = stored;
        }
        _doc[Settings.GraphicsDefaultsVersion.Key] = JsonValue.Create(Settings.CurrentGraphicsDefaultsVersion);
        Save();
        if (dropped is not null)
            Changed?.Invoke(Settings.DrawDistance.Key);
        return dropped;
    }

    /// <summary>Raised with the key of a setting whose value changed.</summary>
    public event Action<string>? Changed;

    public T Get<T>(Setting<T> setting) where T : struct
    {
        if (_doc[setting.Key] is JsonValue v && v.TryGetValue(out T stored) && setting.Fix(stored) is T kept)
            return kept;
        return setting.Default;
    }

    /// <summary>Whether a value is stored under the key (a value that is only the setting's default is not).</summary>
    public bool Contains(string key) => _doc.ContainsKey(key);

    /// <summary>A stored text value by key, or null when there is none.</summary>
    public string? GetText(string key) => _doc[key] is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    /// <summary>Keeps a text value under a key. False when it could not be written.</summary>
    public bool SetText(string key, string value)
    {
        bool previewed = _unsaved.Remove(key);
        if (!previewed && GetText(key) == value)
            return true;
        bool changed = GetText(key) != value;
        _doc[key] = JsonValue.Create(value);
        bool written = Save();
        if (changed)
            Changed?.Invoke(key);
        return written;
    }

    // a key with an unsaved preview -> the node it had in the file (null: it was not in the file)
    private readonly Dictionary<string, JsonNode?> _unsaved = new();

    /// <summary>The keys whose value is a preview that has not been committed or rolled back.</summary>
    public IReadOnlyCollection<string> PendingKeys => _unsaved.Keys.ToArray();

    /// <summary>Sets a value in memory only (a preview): <see cref="Changed"/> is raised and <see cref="Get{T}"/> returns it, but nothing is written
    /// until <see cref="Commit"/>; <see cref="Rollback"/> puts the saved value back. False when the rule refused it.</summary>
    public bool SetPreview<T>(Setting<T> setting, T value) where T : struct
    {
        if (setting.Fix(value) is not T kept)
            return false;
        if (EqualityComparer<T>.Default.Equals(Get(setting), kept))
            return true;
        Remember(setting.Key);
        _doc[setting.Key] = JsonValue.Create(kept);
        Changed?.Invoke(setting.Key);
        return true;
    }

    /// <summary>The text counterpart of <see cref="SetPreview{T}"/>.</summary>
    public bool SetTextPreview(string key, string value)
    {
        if (GetText(key) == value)
            return true;
        Remember(key);
        _doc[key] = JsonValue.Create(value);
        Changed?.Invoke(key);
        return true;
    }

    /// <summary>Writes the previewed values of these keys to the file and ends their preview. False when the file could not be written.</summary>
    public bool Commit(IEnumerable<string> keys)
    {
        bool any = false;
        foreach (var key in keys)
            any |= _unsaved.Remove(key);
        return !any || Save();
    }

    /// <summary>Ends the preview of these keys, putting each saved value back and raising <see cref="Changed"/> for each that differs.</summary>
    public void Rollback(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (!_unsaved.Remove(key, out var original))
                continue;
            if (original is null)
                _doc.Remove(key);
            else
                _doc[key] = original;
            Changed?.Invoke(key);
        }
    }

    private void Remember(string key)
    {
        if (!_unsaved.ContainsKey(key))
            _unsaved[key] = _doc[key]?.DeepClone();
    }

    /// <summary>Writes the file as it was saved: every previewed key carries the value it had before its preview.</summary>
    private bool Save()
    {
        var saved = (JsonObject)_doc.DeepClone();
        foreach (var (key, original) in _unsaved)
        {
            if (original is null)
                saved.Remove(key);
            else
                saved[key] = original.DeepClone();
        }
        return _store?.Write(saved.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })) ?? true;
    }

    /// <summary>Like <see cref="Set{T}"/>, but also stores a value that equals the default when nothing is stored yet, so that
    /// <see cref="Contains"/> tells a kept value from none.</summary>
    public bool Store<T>(Setting<T> setting, T value) where T : struct
    {
        if (setting.Fix(value) is not T kept)
            return false;
        if (Contains(setting.Key) && EqualityComparer<T>.Default.Equals(Get(setting), kept))
            return true;
        bool changed = !EqualityComparer<T>.Default.Equals(Get(setting), kept);
        _doc[setting.Key] = JsonValue.Create(kept);
        Save();
        if (changed)
            Changed?.Invoke(setting.Key);
        return true;
    }

    /// <summary>Sets a value through its rule. False when the rule refused it (nothing changes).</summary>
    public bool Set<T>(Setting<T> setting, T value) where T : struct
    {
        if (setting.Fix(value) is not T kept)
            return false;
        bool previewed = _unsaved.Remove(setting.Key);
        if (!previewed && EqualityComparer<T>.Default.Equals(Get(setting), kept))
            return true;
        bool changed = !EqualityComparer<T>.Default.Equals(Get(setting), kept);
        _doc[setting.Key] = JsonValue.Create(kept);
        Save();
        if (changed)
            Changed?.Invoke(setting.Key);
        return true;
    }
}

/// <summary>The settings file in a folder, written whole through a temporary file beside it so a crash never leaves half a file.</summary>
public sealed class SettingsFile(string folder) : ISettingsStore
{
    public const string FileName = "settings.json";

    public string Path => System.IO.Path.Combine(folder, FileName);

    public string? Read()
    {
        try { return File.Exists(Path) ? File.ReadAllText(Path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public bool Write(string text)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string temp = Path + ".new";
            File.WriteAllText(temp, text);
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// The shell's look as data: a dark panel palette and the colours chat lines use. Chosen for this viewer (not taken from
/// any other); every text colour keeps at least the WCAG AA contrast of 4.5:1 against the panel it is drawn on, which a test
/// checks. The engine host turns it into an engine theme.
/// </summary>
public sealed record ThemeSpec(
    UiColour Panel, UiColour PanelBorder, UiColour Text, UiColour MutedText, UiColour Accent, UiColour Field,
    UiColour ChatOwn, UiColour ChatAgent, UiColour ChatObject, UiColour ChatSystem, UiColour ChatTyping, UiColour Error,
    int FontSize)
{
    /// <summary>The default look's values, in this shape (they come from the Classic theme pack).</summary>
    public static ThemeSpec Default { get; } = ThemePresets.Get(ThemePresets.Classic).ToSpec();

    /// <summary>The colours that carry text, by name (for the contrast check).</summary>
    public IReadOnlyList<(string Name, UiColour Colour)> TextColours =>
    [
        (nameof(Text), Text), (nameof(MutedText), MutedText), (nameof(Accent), Accent), (nameof(ChatOwn), ChatOwn),
        (nameof(ChatAgent), ChatAgent), (nameof(ChatObject), ChatObject), (nameof(ChatSystem), ChatSystem),
        (nameof(ChatTyping), ChatTyping), (nameof(Error), Error),
    ];
}
