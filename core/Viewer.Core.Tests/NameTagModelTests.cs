// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Shell;
using Viewer.Shell.NameTags;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Name tags as data: when a tag shows (mode, own tag, mouselook, apparent size, distance, fade), what it says (states, title, names,
/// colours), and where it hangs (the head, scaled by the body's size). Pure: no engine. Every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class NameTagModelTests
{
    private static readonly Guid Agent = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid OtherAgent = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000ee");

    private static string Words(string key) => key switch
    {
        "nametag.away" => "Away",
        "nametag.busy" => "Busy",
        "nametag.blocked" => "Blocked",
        "nametag.editing" => "Editing appearance",
        _ => key,
    };

    private static readonly NameTagSettings Default = new(NameTagMode.Always, ShowSelf: true, GroupTitles: true, DisplayNames: true, UserNames: true, FriendColour: false);

    private static NameTagInput Avatar(Guid? id = null, string name = "Test User", string title = "", Vector3? pos = null, Vector3? size = null,
        bool self = false, bool friend = false, bool blocked = false, string? display = null, Guid[]? anims = null) =>
        new(id ?? Agent, new AvatarNames(name, display), title, pos ?? new Vector3(100, 100, 30), Quaternion.Identity, size ?? new Vector3(0.45f, 0.6f, 1.8f),
            self, friend, blocked, anims ?? []);

    private static readonly NameTagCamera Camera = new(new Vector3(105, 100, 31), new Vector3(-1, 0, 0), new Vector3(0, 0, 1));

    // ---------------- the pure rules ----------------

    [Fact]
    public void The_apparent_angle_is_the_arc_the_bounding_radius_covers_and_a_tiny_range_counts_as_all_round()
    {
        Assert.Equal(MathF.Atan2(1f, 10f) * 180f / MathF.PI, NameTagModel.ApparentAngle(10f, 1f), 3);
        Assert.Equal(180f, NameTagModel.ApparentAngle(0.0005f, 1f));
        Assert.Equal(180f, NameTagModel.ApparentAngle(0f, 1f));
    }

    [Theory]
    [InlineData(180f, 1f)]
    [InlineData(5.5f, 1f)]
    [InlineData(5f, 1f)]
    [InlineData(3.5f, 0.5f)]
    [InlineData(2.5f, 0.1666667f)]
    [InlineData(2f, 0f)]
    [InlineData(1f, 0f)]
    [InlineData(0f, 0f)]
    public void The_apparent_angle_allows_full_opacity_above_five_degrees_a_ramp_to_two_and_none_below(float angle, float alpha) =>
        Assert.Equal(alpha, NameTagModel.AngleAlpha(angle), 4);

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(5f, 1f)]
    [InlineData(9f, 1f)]
    [InlineData(9.5f, 0.5f)]
    [InlineData(10f, 0f)]
    [InlineData(30f, 0f)]
    public void Fade_mode_holds_a_tag_for_nine_seconds_then_fades_it_out_over_the_tenth(float seconds, float alpha) =>
        Assert.Equal(alpha, NameTagModel.FadeAlpha(seconds), 4);

    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(20f, 1f)]
    [InlineData(22.5f, 0.5f)]
    [InlineData(25f, 0f)]
    [InlineData(80f, 0f)]
    public void Distance_leaves_a_tag_whole_to_twenty_metres_and_takes_it_away_by_twenty_five(float metres, float alpha) =>
        Assert.Equal(alpha, NameTagModel.DistanceAlpha(metres), 4);

    [Theory]
    [InlineData("Test", "Resident", "Test")]
    [InlineData("Test", "User", "Test User")]
    [InlineData("Test", "resident", "Test")]
    public void The_full_name_leaves_off_a_last_name_of_Resident(string first, string last, string expected) =>
        Assert.Equal(expected, NameTagModel.FullName(first, last));

    [Theory]
    [InlineData("Test User", "test.user")]
    [InlineData("Test Resident", "test")]
    [InlineData("Test", "test")]
    [InlineData("", "")]
    public void The_account_name_is_first_dot_last_in_lower_case(string full, string expected) =>
        Assert.Equal(expected, NameTagModel.UserName(full));

    // ---------------- the lines ----------------

    [Fact]
    public void A_plain_avatar_has_one_normal_white_line_with_its_name()
    {
        var line = Assert.Single(NameTagModel.Lines(Avatar(), Default, Words));
        Assert.Equal(new NameTagLine("Test User", NameTagSize.Normal, new UiColour(1, 1, 1)), line);
    }

    [Fact]
    public void A_group_title_is_a_small_line_above_the_name_only_when_the_setting_is_on()
    {
        var lines = NameTagModel.Lines(Avatar(title: "Member of Things"), Default, Words);
        Assert.Equal(["Member of Things", "Test User"], lines.Select(l => l.Text));
        Assert.Equal([NameTagSize.Small, NameTagSize.Normal], lines.Select(l => l.Size));
        Assert.Equal(["Test User"], NameTagModel.Lines(Avatar(title: "Member of Things"), Default with { GroupTitles = false }, Words).Select(l => l.Text));
    }

    [Fact]
    public void States_are_one_small_line_above_everything_in_a_fixed_order_and_only_when_one_holds()
    {
        var all = NameTagModel.Lines(Avatar(title: "T", blocked: true, anims: [NameTagAnimations.EditingAppearance, NameTagAnimations.Busy, NameTagAnimations.Away]), Default, Words);
        Assert.Equal(["Away, Busy, Blocked, Editing appearance", "T", "Test User"], all.Select(l => l.Text));
        Assert.Equal(NameTagSize.Small, all[0].Size);
        Assert.Equal(["Busy", "Test User"], NameTagModel.Lines(Avatar(anims: [NameTagAnimations.Busy]), Default, Words).Select(l => l.Text));
        Assert.Equal(["Test User"], NameTagModel.Lines(Avatar(anims: [Guid.NewGuid()]), Default, Words).Select(l => l.Text));
    }

    [Fact]
    public void A_friend_takes_the_friend_colour_only_when_the_setting_is_on()
    {
        var friend = new UiColour(0.447f, 0.784f, 0.663f);
        Assert.Equal(friend, NameTagModel.Lines(Avatar(friend: true), Default with { FriendColour = true }, Words).FirstOrDefault()?.Colour);
        Assert.Equal(new UiColour(1, 1, 1), NameTagModel.Lines(Avatar(friend: true), Default, Words).FirstOrDefault()?.Colour);
        Assert.Equal(new UiColour(1, 1, 1), NameTagModel.Lines(Avatar(friend: false), Default with { FriendColour = true }, Words).FirstOrDefault()?.Colour);
    }

    [Fact]
    public void A_display_name_goes_first_with_the_account_name_below_it_dimmed_unless_they_are_the_same()
    {
        var lines = NameTagModel.Lines(Avatar(display: "Dizzy"), Default, Words);
        Assert.Equal(["Dizzy", "test.user"], lines.Select(l => l.Text));
        Assert.Equal([NameTagSize.Normal, NameTagSize.Small], lines.Select(l => l.Size));
        Assert.Equal(new UiColour(0.83f, 0.83f, 0.83f), lines[1].Colour);
        Assert.Equal(["test user"], NameTagModel.Lines(Avatar(display: "test user"), Default, Words).Select(l => l.Text));   // the same name apart from case: the display line only
        Assert.Equal(["Dizzy"], NameTagModel.Lines(Avatar(display: "Dizzy"), Default with { UserNames = false }, Words).Select(l => l.Text));
        Assert.Equal(["test.user"], NameTagModel.Lines(Avatar(display: "Dizzy"), Default with { DisplayNames = false }, Words).Select(l => l.Text));
    }

    [Fact]
    public void Control_characters_in_a_title_or_name_become_question_marks_and_a_nameless_avatar_has_no_tag()
    {
        var lines = NameTagModel.Lines(Avatar(name: "Te\u0007st User", title: "Ti\u0001tle"), Default, Words);
        Assert.Equal(["Ti?tle", "Te?st User"], lines.Select(l => l.Text));
        Assert.Empty(NameTagModel.Lines(Avatar(name: ""), Default, Words));
    }

    // ---------------- where it hangs ----------------

    [Fact]
    public void The_anchor_is_the_head_lifted_by_the_body_ellipsoid_and_pulled_toward_the_camera()
    {
        var a = NameTagModel.Anchor(Avatar(pos: new Vector3(100, 100, 30), size: new Vector3(0.45f, 0.6f, 1.8f)),
            new NameTagCamera(new Vector3(110, 100, 30), new Vector3(-1, 0, 0), new Vector3(0, 0, 1)));
        Assert.Equal(100.18f, a.X, 3);   // 0.4 of the box's width, toward the camera
        Assert.Equal(100f, a.Y, 3);
        Assert.Equal(31.026f, a.Z, 3);   // soles 29.1, head 0.9 of 1.8 m above, plus 0.17 of 1.8 m
    }

    [Fact]
    public void A_drawn_head_joint_places_the_tag_instead_of_the_body_box_so_a_seated_avatars_tag_sits_just_above_its_head()
    {
        // seated: the box is still the standing height, but the head is low; the tag hangs from the head, lifted by 0.17 of the body's height at most
        var seated = Avatar(pos: new Vector3(100, 100, 30), size: new Vector3(0.45f, 0.6f, 1.8f)) with { Head = new Vector3(100, 100, 29.4f) };
        var a = NameTagModel.Anchor(seated, new NameTagCamera(new Vector3(110, 100, 30), new Vector3(-1, 0, 0), new Vector3(0, 0, 1)));
        Assert.InRange(a.Z, 29.4f, 29.4f + 0.17f * 1.8f + 0.001f);
        Assert.Equal(100.18f, a.X, 3);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(1.8f)]
    [InlineData(3.0f)]
    [InlineData(10.0f)]
    [InlineData(20.0f)]
    public void The_tag_rises_in_proportion_to_the_body_so_a_very_tall_avatar_keeps_it_above_the_head(float height)
    {
        var avatar = Avatar(pos: new Vector3(100, 100, 30 + height / 2), size: new Vector3(0.45f, 0.6f, height));
        var a = NameTagModel.Anchor(avatar, new NameTagCamera(new Vector3(130, 100, 30 + height / 2), new Vector3(-1, 0, 0), new Vector3(0, 0, 1)));
        Assert.Equal(1.07f, (a.Z - 30f) / height, 3);   // 0.9 head + 0.17 ellipsoid, both of the body's height
        Assert.True(a.Z > 30 + height, "the tag hangs above the top of the body");
    }

    // ---------------- the settings ----------------

    [Fact]
    public void The_settings_default_to_the_official_ones_and_take_a_changed_value_through_the_settings_service()
    {
        var settings = new SettingsService(null);
        Assert.Equal(Default, NameTagSettings.From(settings));
        Assert.True(settings.Set(Viewer.Shell.Settings.NameTagMode, 2));
        Assert.True(settings.Set(Viewer.Shell.Settings.NameTagFriends, true));
        Assert.True(settings.Set(Viewer.Shell.Settings.NameTagShowSelf, false));
        Assert.Equal(Default with { Mode = NameTagMode.Fade, FriendColour = true, ShowSelf = false }, NameTagSettings.From(settings));
        Assert.False(settings.Set(Viewer.Shell.Settings.NameTagMode, 3));   // refused: nothing changes
        Assert.Equal(NameTagMode.Fade, NameTagSettings.From(settings).Mode);
    }

    // ---------------- the model ----------------

    private static NameTagModel Model(Func<NameTagSettings> settings, Func<double> clock) => new(settings, clock, Words);

    [Fact]
    public void Off_shows_nothing_always_shows_the_avatars_in_view_with_their_anchor_and_lines()
    {
        double t = 0;
        var model = Model(() => Default with { Mode = NameTagMode.Off }, () => t);
        Assert.Empty(model.Update([Avatar()], Camera, false));
        var on = Model(() => Default, () => t);
        var plan = Assert.Single(on.Update([Avatar()], Camera, false));
        Assert.Equal(Agent, plan.AgentId);
        Assert.Equal(1f, plan.Alpha, 3);
        Assert.Equal("Test User", plan.Lines[0].Text);
        Assert.Equal(NameTagModel.Anchor(Avatar(), Camera), plan.Anchor);
        Assert.Equal(Vector3.Distance(plan.Anchor, Camera.Eye) - Avatar().Size.Length() / 2f, plan.Distance, 3);   // the tag is pushed toward the camera by the avatar's radius
    }

    [Fact]
    public void The_own_tag_needs_the_setting_and_the_mode_and_is_never_shown_in_mouselook()
    {
        double t = 0;
        var me = Avatar(self: true);
        Assert.Single(Model(() => Default, () => t).Update([me], Camera, false));
        Assert.Empty(Model(() => Default with { ShowSelf = false }, () => t).Update([me], Camera, false));
        Assert.Empty(Model(() => Default with { Mode = NameTagMode.Off }, () => t).Update([me], Camera, false));
        Assert.Empty(Model(() => Default, () => t).Update([me], Camera, true));
        Assert.Single(Model(() => Default with { ShowSelf = false }, () => t).Update([Avatar(OtherAgent)], Camera, false));   // another avatar's tag has no such rule
        Assert.Single(Model(() => Default, () => t).Update([Avatar(OtherAgent)], Camera, true));
    }

    [Fact]
    public void A_tag_in_fade_mode_shows_for_nine_seconds_fades_over_the_tenth_and_is_gone_after_and_a_new_text_starts_it_again()
    {
        double t = 100;
        var model = Model(() => Default with { Mode = NameTagMode.Fade }, () => t);
        Assert.Equal(1f, Assert.Single(model.Update([Avatar()], Camera, false)).Alpha, 3);
        t = 109; Assert.Equal(1f, Assert.Single(model.Update([Avatar()], Camera, false)).Alpha, 3);
        t = 109.5; Assert.Equal(0.5f, Assert.Single(model.Update([Avatar()], Camera, false)).Alpha, 3);
        t = 111; Assert.Empty(model.Update([Avatar()], Camera, false));
        t = 112; Assert.Single(model.Update([Avatar(title: "New Title")], Camera, false));   // the text changed: a new tag, its clock restarts
        t = 118; Assert.Equal(1f, Assert.Single(model.Update([Avatar(title: "New Title")], Camera, false)).Alpha, 3);
    }

    [Fact]
    public void An_avatar_that_leaves_view_starts_its_clock_again_when_it_returns()
    {
        double t = 0;
        var model = Model(() => Default with { Mode = NameTagMode.Fade }, () => t);
        model.Update([Avatar()], Camera, false);
        t = 20; Assert.Empty(model.Update([Avatar()], Camera, false));
        t = 21; model.Update([], Camera, false);
        t = 22; Assert.Single(model.Update([Avatar()], Camera, false));
    }

    [Fact]
    public void A_far_small_avatar_has_no_tag_a_tall_one_keeps_its_tag_until_the_distance_takes_it()
    {
        double t = 0;
        var model = Model(() => Default, () => t);
        var far = new NameTagCamera(new Vector3(700, 100, 31), new Vector3(-1, 0, 0), new Vector3(0, 0, 1));
        Assert.Empty(model.Update([Avatar()], far, false));   // under two degrees
        var tall = Avatar(pos: new Vector3(100, 100, 39), size: new Vector3(4f, 4f, 18f));
        var atTwentyTwoAndAHalf = new NameTagCamera(new Vector3(131.8f, 100, 39), new Vector3(-1, 0, 0), new Vector3(0, 0, 1));
        var plan = Assert.Single(model.Update([tall], atTwentyTwoAndAHalf, false));
        Assert.InRange(plan.Alpha, 0.3f, 0.7f);   // about 22.5 m from the tag: the distance fade is at work
        var thirty = new NameTagCamera(new Vector3(137.5f, 100, 39), new Vector3(-1, 0, 0), new Vector3(0, 0, 1));   // over 25 m from the tag
        Assert.Empty(model.Update([tall], thirty, false));
    }

    [Fact]
    public void A_tag_behind_the_camera_is_not_drawn_and_the_blocked_state_comes_from_the_input()
    {
        double t = 0;
        var model = Model(() => Default, () => t);
        var away = new NameTagCamera(new Vector3(105, 100, 31), new Vector3(1, 0, 0), new Vector3(0, 0, 1));
        Assert.Empty(model.Update([Avatar()], away, false));
        var plan = Assert.Single(model.Update([Avatar(blocked: true)], Camera, false));
        Assert.Equal("Blocked", plan.Lines[0].Text);
    }

    [Fact]
    public void Several_avatars_each_get_their_own_plan_in_the_order_given()
    {
        double t = 0;
        var model = Model(() => Default, () => t);
        var plans = model.Update([Avatar(Agent, "First One"), Avatar(OtherAgent, "Second One", pos: new Vector3(100, 98, 30))], Camera, false);
        Assert.Equal([Agent, OtherAgent], plans.Select(p => p.AgentId));
    }
}
