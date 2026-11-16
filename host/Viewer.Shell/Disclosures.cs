// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// What Linden Lab's Third-Party Viewer Policy (https://secondlife.com/corporate/third-party-viewers, sections 1.c and 4.b) asks a
/// viewer to tell people before they use it: that it is not provided or supported by Linden Lab, where to get support, what it does
/// beyond the official viewer and where it falls short, that it can show content not rated General, and where its privacy policy is.
/// The viewer is unpacked rather than installed, so the text is shown once at the first run, and once more before the first Second
/// Life login; both answers are kept in the settings.
/// </summary>
public static class Disclosures
{
    /// <summary>The privacy policy, published with the source code.</summary>
    public const string PrivacyPolicyAddress = "https://github.com/JohnLegionH/reverie-viewer/blob/main/PRIVACY.md";

    /// <summary>Where to ask for help and report problems.</summary>
    public const string SupportAddress = "https://github.com/JohnLegionH/reverie-viewer/issues";

    /// <summary>The disclosure shown at the first run: accepting it continues, refusing it quits.</summary>
    public static LoginDialog FirstRun(StringTable s) => Make(s, s.Get("disclosure.title"), s.Get("disclosure.quit"));

    /// <summary>The disclosure shown before the first Second Life login: accepting it logs in, refusing it ends the attempt before
    /// anything is sent to Second Life.</summary>
    public static LoginDialog BeforeSecondLife(StringTable s) => Make(s, s.Get("disclosure.title.second_life"), s.Get("login.button.cancel"));

    private static LoginDialog Make(StringTable s, string title, string cancel) =>
        new(LoginDialogKind.Disclosure, title, s.Get("disclosure.body"), [], s.Get("disclosure.text", SupportAddress, PrivacyPolicyAddress),
            PrivacyPolicyAddress, null, null, s.Get("disclosure.accept"), cancel);

    /// <summary>Whether the first-run disclosure is still to be shown.</summary>
    public static bool FirstRunDue(SettingsService settings) => !settings.Get(Settings.DisclosureFirstRunAccepted);

    /// <summary>Records that the first-run disclosure was accepted.</summary>
    public static void FirstRunAccepted(SettingsService settings) => settings.Set(Settings.DisclosureFirstRunAccepted, true);

    /// <summary>Whether the disclosure is still to be shown before a Second Life login.</summary>
    public static bool SecondLifeDue(SettingsService settings) => !settings.Get(Settings.DisclosureSecondLifeAccepted);

    /// <summary>Records that the disclosure before the first Second Life login was accepted.</summary>
    public static void SecondLifeAccepted(SettingsService settings) => settings.Set(Settings.DisclosureSecondLifeAccepted, true);
}
