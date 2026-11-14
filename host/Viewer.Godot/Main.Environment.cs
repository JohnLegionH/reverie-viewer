// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The World > Environment menu and its shortcut keys: the sky choice, shown by the world presenter.</summary>
public partial class Main
{
    private EnvironmentChoice? _envChoice;
    private bool _envMenuInstalled;
    private readonly Viewer.Core.World.PersonalLighting _personalLighting = new();

    /// <summary>The built-in skies fetched for this login (a new store per login: another grid serves other assets); null before the first login.</summary>
    private FixedSkyStore? _fixedSkies;
    private Viewer.Core.Assets.ISettingsTransport? _settingsTransport;

    /// <summary>The choice made in the menu (or with the F9 key, or --sky= at start); created the first time it is needed, once the settings exist.</summary>
    private EnvironmentChoice EnvChoice => _envChoice ??= MakeEnvironmentChoice();

    private EnvironmentChoice MakeEnvironmentChoice()
    {
        var c = new EnvironmentChoice(_settings);
        c.Prepare = PrepareSky;
        c.Changed += OnSkyChoice;
        c.Failed += OnSkyFailed;
        c.CloudsPausedChanged += paused => { if (_presenter is not null) _presenter.CloudsPaused = paused; Log.Info("sky.clouds", CoreLog.F("paused", paused)); };
        var fromArgs = SkyFromArgs(OS.GetCmdlineUserArgs());
        if (fromArgs != SkyChoice.Region) c.Set(fromArgs);
        return c;
    }

    /// <summary>Records the transport a login made, and starts a fresh set of built-in skies for it.</summary>
    private void BindSettingsTransport(object transport)
    {
        _settingsTransport = transport as Viewer.Core.Assets.ISettingsTransport;
        _fixedSkies = new FixedSkyStore(_settingsTransport is { } st ? st.FetchSettingsAsync : null);
    }

    /// <summary>
    /// Loads a built-in sky before the menu puts it in force, as the official viewer's menu loads its settings asset first
    /// (LLEnvironment::setEnvironment with the known sky id). Before any login there is no grid to ask, and the choice is set at once
    /// (the presenter then shows the region's day cycle at that point of the day).
    /// </summary>
    private async System.Threading.Tasks.Task<bool> PrepareSky(SkyChoice choice)
    {
        if (_settingsTransport is null || _fixedSkies is not { } store) return true;
        bool ok = await store.LoadAsync(choice);   // resumes on the main thread
        Log.Info("sky.asset", CoreLog.F("choice", SkyChoices.Label(choice)), CoreLog.F("id", KnownSkies.AssetId(choice)?.ToString("D") ?? ""), CoreLog.F("loaded", ok), CoreLog.F("why", ok ? "" : store.LastFailure));
        return ok;
    }

    private void OnSkyChoice(SkyChoice choice)
    {
        if (_presenter is not null) { _presenter.FixedSkies = _fixedSkies; _presenter.Sky = choice; _presenter.RefreshSky(); }
        Log.Info("sky.choice", CoreLog.F("choice", SkyChoices.Label(choice)));
        if (_strings is not null) GetWindow().Title = _strings.Get("sky.title", SkyChoices.Label(choice));
    }

    /// <summary>A built-in sky the grid could not serve: the sky in force stays, and a notice says so (the official viewer's FailedToFindSettings does the same).</summary>
    private void OnSkyFailed(SkyChoice choice)
    {
        string why = _fixedSkies?.LastFailure ?? "";
        Log.Warn("sky.choice.failed", CoreLog.F("choice", SkyChoices.Label(choice)), CoreLog.F("why", why));
        _notes?.Model.Post(new Viewer.Shell.Notifications.Notification
        {
            Kind = Viewer.Shell.Notifications.NotificationKind.Tip,
            Body = Viewer.Shell.Notifications.NoticeText.Raw($"Could not load the sky {SkyChoices.Label(choice)} from the grid ({why})."),
            UniqueKey = "sky-failed",
        });
    }

    /// <summary>
    /// After a login: a choice kept from the last run (only when EnvironmentPersistAcrossLogin is on) is a built-in sky that must be
    /// fetched again. When the grid cannot serve it the region's own environment shows.
    /// </summary>
    private async void ReloadKeptSky()
    {
        var kept = EnvChoice.Current;
        if (kept == SkyChoice.Region || _presenter is null) return;
        if (await PrepareSky(kept)) _presenter?.RefreshSky();
        else EnvChoice.Set(SkyChoice.Region);
    }

    /// <summary>Adds the Environment submenu to the World menu (once; the World menu is made with the travel windows).</summary>
    private void InstallEnvironmentMenu()
    {
        if (_envMenuInstalled || _shell is null) return;
        _envMenuInstalled = true;
        PersonalLightingFeature.Install(_shell, _personalLighting, () => _presenter?.Environment?.Frame);
        EnvironmentMenu.Install(_shell.Shell.Menus, _shell.Shell.Commands, EnvChoice, OpenPersonalLighting);
    }

    /// <summary>The official shortcuts of the Environment menu: control+shift+O, Y, T, N and Z (the engine's letter keys carry their capital letter's code).</summary>
    private static string? EnvironmentShortcut(Key key) =>
        key is >= Key.A and <= Key.Z && EnvironmentMenu.OfficialNameForKey((char)key) is { } name ? EnvironmentMenu.ShortcutCommand(name) : null;

    /// <summary>Sets a Personal Lighting control as the window does (a slider's value or a swatch's colour), or resets them all; null when the request is not one.</summary>
    public object? AutomationPersonal(System.Text.Json.JsonElement body)
    {
        if (body.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (body.TryGetProperty("reset", out var reset) && reset.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            _personalLighting.Reset();
            return new { active = false, gamma = _personalLighting.Gamma };
        }
        if (!body.TryGetProperty("field", out var f) || f.ValueKind != System.Text.Json.JsonValueKind.String
            || !Enum.TryParse<Viewer.Core.World.PersonalField>(f.GetString(), true, out var field)) return null;
        if (body.TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number) _personalLighting.SetValue(field, v.GetDouble());
        else if (body.TryGetProperty("colour", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Array && c.GetArrayLength() == 3
                 && c[0].ValueKind == System.Text.Json.JsonValueKind.Number && c[1].ValueKind == System.Text.Json.JsonValueKind.Number && c[2].ValueKind == System.Text.Json.JsonValueKind.Number)
            _personalLighting.SetColour(field, new System.Numerics.Vector3((float)c[0].GetDouble(), (float)c[1].GetDouble(), (float)c[2].GetDouble()));
        else return null;
        return new { active = _personalLighting.IsActive, gamma = _personalLighting.Gamma };
    }

    private void OpenPersonalLighting() => _shell?.Shell.Floaters.Open(PersonalLightingFeature.FloaterId);
}
