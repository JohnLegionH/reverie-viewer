// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// How opaque a floating window is. The official viewer has two settings, one for the window that has the focus and one for the
/// others (<c>ActiveFloaterTransparency</c> 1.0 and <c>InactiveFloaterTransparency</c> 0.95 in its settings.xml, both shown on its
/// colours page); this viewer keeps the same two names and defaults. The values are opacities: 1 is solid, 0 is invisible.
/// The behaviour is taken from the official viewer's documented settings, not its code.
/// </summary>
public static class FloaterOpacity
{
    /// <summary>The focused window's opacity by default.</summary>
    public const float DefaultActive = 1.0f;

    /// <summary>The other windows' opacity by default.</summary>
    public const float DefaultInactive = 0.95f;

    /// <summary>The opacity a window is drawn with. A theme whose panel is solid (High contrast) keeps every window solid.</summary>
    public static float For(bool focused, float active, float inactive, float themePanelAlpha) =>
        themePanelAlpha >= 1f ? 1f : Math.Clamp(focused ? active : inactive, 0f, 1f);
}

/// <summary>The two window opacity sliders of the Interface page: an edit is a preview, Save keeps both, Cancel puts back what was saved.</summary>
public sealed class FloaterOpacityPrefs(SettingsService settings)
{
    /// <summary>The settings service the page edits.</summary>
    public SettingsService Service => settings;

    public float Active => settings.Get(Settings.ActiveFloaterTransparency);

    public float Inactive => settings.Get(Settings.InactiveFloaterTransparency);

    public bool SetActive(float value) => settings.SetPreview(Settings.ActiveFloaterTransparency, value);

    public bool SetInactive(float value) => settings.SetPreview(Settings.InactiveFloaterTransparency, value);

    /// <summary>The settings keys the page edits.</summary>
    public IReadOnlyList<string> Keys { get; } = [Settings.ActiveFloaterTransparency.Key, Settings.InactiveFloaterTransparency.Key];

    public bool IsChanged => settings.PendingKeys.Intersect(Keys).Any();

    public bool Save() => settings.Commit(Keys);

    public void Cancel() => settings.Rollback(Keys);

    /// <summary>Previews both at the official defaults.</summary>
    public void Reset()
    {
        SetActive(FloaterOpacity.DefaultActive);
        SetInactive(FloaterOpacity.DefaultInactive);
    }
}
