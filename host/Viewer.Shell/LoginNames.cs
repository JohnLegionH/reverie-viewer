// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>The saved login names on this computer.</summary>
public static class LoginNames
{
    /// <summary>The username the login screen shows: the saved one, else an older saved first and last name joined.</summary>
    public static string Load(SettingsService settings)
    {
        if (settings.GetText(Settings.LoginUserNameKey) is { Length: > 0 } saved)
            return saved;
        string first = settings.GetText(Settings.LoginFirstNameKey) ?? "";
        return first.Length == 0 ? "" : LoginName.Display(first, settings.GetText(Settings.LoginLastNameKey) ?? "");
    }

    /// <summary>Keeps <paramref name="userName"/> when <paramref name="remember"/> is on, and forgets any saved name when it is off.</summary>
    public static void Save(SettingsService settings, bool remember, string userName)
    {
        settings.SetText(Settings.LoginUserNameKey, remember ? userName.Trim() : "");
        // the older pair is not kept beside it
        settings.SetText(Settings.LoginFirstNameKey, "");
        settings.SetText(Settings.LoginLastNameKey, "");
    }

    /// <summary>Forgets the saved first and last name. True when a name was saved. The remember switch is left as it is.</summary>
    public static bool Clear(SettingsService settings)
    {
        bool had = Load(settings).Length > 0;
        settings.SetText(Settings.LoginUserNameKey, "");
        settings.SetText(Settings.LoginFirstNameKey, "");
        settings.SetText(Settings.LoginLastNameKey, "");
        return had;
    }
}
