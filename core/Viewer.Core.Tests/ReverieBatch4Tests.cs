// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Viewer.Shell.Ui;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The second half of the new look, fourth part: the reconnect notice and login dialogs, the four upload windows, the sound tuner,
/// About and Licences, and the empty states, placeholders and disabled buttons. Source reads and pure rules; runs in parallel (no
/// process-wide state).
/// </summary>
public class ReverieBatch4Tests
{
    private static string Host(params string[] path) => File.ReadAllText(Path.Combine([TestPaths.RepoRoot, "host", "Viewer.Godot", .. path]));

    [Fact]
    public void The_upload_windows_and_the_sound_tuner_are_on_the_restyle_list_by_their_ids()
    {
        string restyle = Host("Reverie", "ReverieRestyle.cs");
        foreach (var id in new[] { "BuildFeature.ImageFloaterId", "BuildFeature.BulkFloaterId", "ModelFeature.ModelFloaterId", "ModelFeature.BulkModelsFloaterId", "SoundTunerPanel.FloaterId" })
            Assert.Contains(id, restyle);
    }

    [Fact]
    public void The_reconnect_notice_and_the_login_dialogs_restyle_themselves()
    {
        Assert.Contains("ReverieRestyle.Apply(panel)", Host("DropPanel.cs"));
        Assert.Contains("ReverieRestyle.Apply(panel)", Host("LoginDialogView.cs"));
    }

    [Fact]
    public void About_and_Licences_restyle_their_content()
    {
        string root = Host("ShellRoot.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(root, @"ReverieRestyle\.Apply\(box\);").Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    public void Mark_all_as_read_is_enabled_only_while_a_notice_waits(int waiting, bool enabled) =>
        Assert.Equal(enabled, ButtonAvailability.MarkAllReadEnabled(waiting));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Block_this_resident_is_enabled_only_with_a_friend_chosen(int chosen, bool enabled) =>
        Assert.Equal(enabled, ButtonAvailability.BlockResidentEnabled(chosen));

    [Fact]
    public void The_panels_use_those_rules()
    {
        Assert.Contains("ButtonAvailability.MarkAllReadEnabled", Host("NotificationPanels.cs"));
        Assert.Contains("ButtonAvailability.BlockResidentEnabled", Host("PeoplePanels.cs"));
    }

    [Theory]
    [InlineData("outfit.saved.none")]
    [InlineData("map.friend.placeholder")]
    [InlineData("teleport.offer_to.friend.placeholder")]
    [InlineData("teleport.offer_to.message.hint")]
    [InlineData("addons.choice.empty")]
    [InlineData("prefs.cache.choose")]
    [InlineData("prefs.cache.choose.title")]
    public void The_new_strings_exist(string key)
    {
        Assert.True(StringTable.English().Has(key));
    }

    [Fact]
    public void The_graphics_help_names_a_choice_and_no_command_line_switch()
    {
        string text = StringTable.English().Get("prefs.backend.notice");
        Assert.DoesNotContain("--", text);
        Assert.Contains("Automatic", text);
    }

    [Fact]
    public void The_panels_show_the_empty_states_and_placeholders()
    {
        Assert.Contains("outfit.saved.none", Host("OutfitPanels.cs"));
        Assert.Contains("map.friend.placeholder", Host("MapPanels.cs"));
        Assert.Contains("teleport.offer_to.friend.placeholder", Host("TravelPanels.cs"));
        Assert.Contains("teleport.offer_to.message.hint", Host("TravelPanels.cs"));
        Assert.Contains("addons.choice.empty", Host("AddonPanels.cs"));
        Assert.Contains("prefs.cache.choose", Host("PreferencePages.cs"));
    }
}

public class GestureDeleteQuestionTests
{
    private static Guid G(int n) => new($"00000000-0000-4000-8000-{n:D12}");
    private static Viewer.Shell.Inventory.GestureListRow Row(int n) => new(G(n), "g" + n, "", "None", false);

    [Fact]
    public void The_question_holds_while_every_gesture_it_names_is_listed()
    {
        Assert.True(Viewer.Shell.Inventory.GestureList.PendingStillValid([G(1), G(2)], [Row(1), Row(2), Row(3)]));
    }

    [Fact]
    public void The_question_is_dropped_when_a_gesture_it_names_is_gone()
    {
        Assert.False(Viewer.Shell.Inventory.GestureList.PendingStillValid([G(1), G(9)], [Row(1), Row(2)]));
    }
}
