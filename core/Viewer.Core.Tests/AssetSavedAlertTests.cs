// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;
using Viewer.Shell.Travel;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A grid's "notecard updated" line after a save is the editor's status, not a pop-up: it is told apart from the other alerts by its words,
/// raised for the editors, and made into no notice. Pure functions and a model with no clock, so the class runs in parallel.
/// </summary>
public class AssetSavedAlertTests
{
    [Theory]
    [InlineData("Notecard updated")]
    [InlineData("/Notecard updated.")]
    [InlineData("Notecard saved")]
    [InlineData("Script saved.")]
    [InlineData("The notecard was updated")]
    [InlineData("Notecard uploaded!")]
    [InlineData("Saved notecard")]
    public void A_short_line_saying_a_notecard_or_script_was_saved_or_updated_is_an_asset_saved_alert(string text)
    {
        Assert.Equal(AlertKind.AssetSaved, ServerAlerts.Classify(text, "", false));
        Assert.Equal(AlertKind.AssetSaved, ServerAlerts.Classify(text, "", true));   // even sent as an error
    }

    [Theory]
    [InlineData("Home position set.", AlertKind.HomeSet)]
    [InlineData("You do not have access to the parcel", AlertKind.ParcelNoAccess)]
    [InlineData("The region will restart in 5 minutes", AlertKind.Tip)]
    [InlineData("Your notecard folder is full and the script could not be updated because the object is locked down for everyone", AlertKind.Tip)]
    [InlineData("Updated terrain", AlertKind.Tip)]
    public void Other_alerts_keep_their_kind(string text, AlertKind kind) => Assert.Equal(kind, ServerAlerts.Classify(text, "", false));

    [Fact]
    public void An_asset_saved_alert_makes_no_notice_and_is_raised_with_its_words_for_the_editors()
    {
        var model = new NoticeModel(() => 0);
        var heard = new List<string>();
        int changes = 0;
        model.AssetSaved += heard.Add;
        model.Changed += () => changes++;
        model.Receive(new ServerAlert("/Notecard updated", "", false));
        Assert.Equal(["Notecard updated"], heard);
        Assert.Empty(model.Notices);
        Assert.Equal(0, changes);
        model.Receive(new ServerAlert("The region is busy", "", false));   // any other alert is still a notice
        Assert.Single(model.Notices);
        Assert.Single(heard);
    }
}
