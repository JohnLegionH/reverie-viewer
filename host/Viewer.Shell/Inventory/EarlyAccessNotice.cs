// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;

namespace Viewer.Shell.Inventory;

/// <summary>The one warning shown the first time a person picks an early-access engine, kept once per install in the settings file.</summary>
public sealed class EarlyAccessNotice(SettingsService settings)
{
    /// <summary>Set once the warning has been shown; it is never shown again on this install.</summary>
    public static readonly Setting<bool> Warned = new("script_early_access_warned", false, v => v);

    /// <summary>String-table key of the warning text.</summary>
    public const string TextKey = "script.engine.early_warning";

    /// <summary>True when picking this entry should show the warning now: the entry is early access and the warning has not been shown on this
    /// install. Answering true records it, so the next pick answers false.</summary>
    public bool TakeWarning(ScriptChoice choice)
    {
        if (!choice.EarlyAccess || settings.Get(Warned))
            return false;
        settings.Set(Warned, true);
        return true;
    }
}
