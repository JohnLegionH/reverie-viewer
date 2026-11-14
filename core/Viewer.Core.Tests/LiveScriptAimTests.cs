// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The live scripts that take evidence pictures of a person aim the camera with the aim command (tools\aim-lib.ps1), so each picture is
/// framed on its target and judged by the picture check. These tests read the scripts as text: a script that turns the follow camera
/// by hand (the orbit command) or takes a bare screenshot of its target again would bring back pictures of the wrong avatar.
/// No shared state: each member only reads files.
/// </summary>
public sealed class LiveScriptAimTests
{
    private static string Tools => Path.Combine(TestPaths.RepoRoot, "tools");

    public static IEnumerable<object[]> PictureScripts => [["avatar-live.ps1"], ["ao-live.ps1"]];

    [Theory]
    [MemberData(nameof(PictureScripts))]
    public void A_script_that_takes_pictures_of_a_person_uses_the_aim_command_and_the_picture_check(string script)
    {
        string text = File.ReadAllText(Path.Combine(Tools, script));
        Assert.Contains("aim-lib.ps1", text);
        Assert.Contains("Shoot-Aimed", text);
        Assert.Contains("aimFailures", text);
    }

    [Theory]
    [MemberData(nameof(PictureScripts))]
    public void A_script_that_takes_pictures_of_a_person_does_not_turn_the_follow_camera_by_hand(string script)
    {
        string text = File.ReadAllText(Path.Combine(Tools, script));
        Assert.DoesNotContain("orbit_around_deg", text);
        Assert.DoesNotContain("orbit_over_deg", text);
        Assert.DoesNotContain("\"screenshot\"", text);   // pictures go through Shoot-Aimed, which saves the report and judges the picture
    }

    [Fact]
    public void The_library_saves_a_report_beside_each_picture_and_has_it_judged()
    {
        string text = File.ReadAllText(Path.Combine(Tools, "aim-lib.ps1"));
        Assert.Contains(".aim.json", text);
        Assert.Contains("aim-check.ps1", text);
        Assert.Contains("Stop-Aim", text);
    }
}

/// <summary>The live ground-sit and body check needs the avatar body, so it must not run without a character folder, and its dump and ground search must be
/// the ones that need no picture and look past the first region. No shared state: each member only reads files.</summary>
public sealed class GroundSitLiveScriptTests
{
    private static string Script => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tools", "groundsit-live.ps1"));

    [Fact]
    public void The_script_is_blocked_without_a_character_folder_because_no_body_is_drawn_without_one()
    {
        string text = Script;
        Assert.Contains("NV_CHARACTER_DIR", text);
        Assert.Contains("BLOCKED", text);
        Assert.Contains("avatar_body", text);   // and it checks that the body is drawn before it reads anything drawn
    }

    [Fact]
    public void The_face_dump_is_taken_without_a_picture_and_the_ground_search_names_a_region_by_grid_cell()
    {
        string text = Script;
        Assert.Contains("face-dump-live.json", text);
        Assert.DoesNotContain("Shoot-Aimed", text);
        Assert.Contains("grid_x", text);
        Assert.Contains("plain.regions", text);
    }
}
