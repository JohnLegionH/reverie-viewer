// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Addons;
using Viewer.Shell;
using Viewer.Shell.Addons;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The box shown before an add-on the user installed is turned on: it says plainly that the add-on runs with full access
/// to the computer, and lists its permissions as what it says it uses. Built-in add-ons show no box. Pure, runs in parallel.
/// </summary>
public class AddonConsentTests
{
    private static readonly StringTable En = StringTable.English();

    private static AddonManifest Manifest(params string[] permissions) =>
        new("hello", "Hello Sample", "1.0", "Someone", "0BSD", "0.1", null, permissions, "hello.dll");

    [Fact]
    public void Turning_on_an_outside_add_on_says_it_runs_with_full_access_and_lists_what_it_says_it_uses()
    {
        string? text = AddonConsent.Text(En, Manifest(AddonPermissions.Window, AddonPermissions.ChatSend), builtIn: false);

        Assert.NotNull(text);
        Assert.Contains("Hello Sample", text);
        Assert.Contains("runs with full access to your computer, like any program you install — only turn on add-ons you trust", text);
        Assert.Contains("What it says it uses:", text);
        Assert.Contains("• add windows and menu items", text);
        Assert.Contains("• say things in local chat as you", text);
        Assert.DoesNotContain("trusted code", text);
    }

    [Fact]
    public void A_built_in_add_on_shows_no_box()
    {
        Assert.Null(AddonConsent.Text(En, Manifest(AddonPermissions.Window), builtIn: true));
    }

    [Fact]
    public void An_add_on_that_asks_for_nothing_still_gets_the_warning()
    {
        string? text = AddonConsent.Text(En, Manifest(), builtIn: false);
        Assert.NotNull(text);
        Assert.Contains("full access to your computer", text);
        Assert.Contains("(nothing beyond its own window and settings)", text);
    }
}
