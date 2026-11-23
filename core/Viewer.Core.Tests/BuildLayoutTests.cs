// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using System.Text.RegularExpressions;
using Viewer.Shell;
using Viewer.Shell.Build;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Build window laid out as one column of sections (Placement, Shape, Look, Object, Behaviour, Contents) under a tool row, a mode row
/// and a hint line: each closed section's summary, the open sections kept across a restart, the settings search (it finds the official
/// viewer's names too), the hint line, and in the sources that the window draws sections and no tabs. No engine; each test makes its own
/// settings, so the class runs in parallel.
/// </summary>
public class BuildLayoutTests
{
    private static readonly StringTable S = StringTable.English();

    private sealed class MemoryStore : ISettingsStore
    {
        public string? Text;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; return true; }
    }

    private static readonly BuildFacts Floor = new(
        Position: new Vector3(128.5f, 64.25f, 22.1f), Size: new Vector3(10f, 10f, 0.2f), RotationDegrees: new Vector3(0, 0, 30),
        ShapeName: "Box", Hollow: 50, TwistDegrees: 90, FacesPicked: 2, SelectFaces: true, Texture: "Plywood",
        Name: "Floor", Locked: true, Phantom: true, Light: true, PhysicsShape: "Prim", ContentCount: 3);

    // ---------------- section summaries ----------------

    [Fact]
    public void Placement_summary_names_position_and_size()
    {
        string line = BuildLayout.Summary(BuildSection.Placement, Floor, S);
        Assert.Contains("128.5, 64.25, 22.1", line);
        Assert.Contains("10 × 10 × 0.2", line);
    }

    [Fact]
    public void Look_summary_names_the_picked_faces_and_texture()
    {
        Assert.Equal("2 faces picked, Plywood", BuildLayout.Summary(BuildSection.Look, Floor, S));
        Assert.Equal("1 face picked", BuildLayout.Summary(BuildSection.Look, Floor with { FacesPicked = 1, Texture = null }, S));
        Assert.Equal("All faces", BuildLayout.Summary(BuildSection.Look, Floor with { SelectFaces = false, Texture = null }, S));
    }

    [Fact]
    public void Shape_object_behaviour_and_contents_summaries_say_what_is_set()
    {
        Assert.Equal("Box, hollow 50 %, twisted", BuildLayout.Summary(BuildSection.Shape, Floor, S));
        Assert.Equal("Floor, Locked, Phantom", BuildLayout.Summary(BuildSection.Object, Floor, S));
        Assert.Equal("Light, Prim", BuildLayout.Summary(BuildSection.Behaviour, Floor, S));
        Assert.Equal("3 items", BuildLayout.Summary(BuildSection.Contents, Floor, S));
        // not asked for yet (the section has been closed): it says how to see them, not that they are loading
        Assert.Equal(S.Get("edit.summary.contents_closed"), BuildLayout.Summary(BuildSection.Contents, Floor with { ContentCount = null }, S));
        Assert.DoesNotContain("[[", S.Get("edit.summary.contents_closed"));
    }

    [Fact]
    public void Every_summary_says_nothing_is_selected_without_a_selection()
    {
        foreach (var section in BuildLayout.Sections)
            Assert.Equal(S.Get("edit.summary.none"), BuildLayout.Summary(section, null, S));
    }

    // ---------------- open sections remembered ----------------

    [Fact]
    public void Placement_look_and_object_are_open_the_first_time()
    {
        var memory = new BuildSectionMemory(new SettingsService(new MemoryStore()));
        Assert.Equal([false, true, false, true, true, false], BuildLayout.Sections.Select(memory.IsOpen).ToArray());   // Contents, Placement, Shape, Look, Object, Behaviour
    }

    [Fact]
    public void Open_and_closed_sections_are_kept_across_a_restart()
    {
        var store = new MemoryStore();
        var first = new BuildSectionMemory(new SettingsService(store));
        first.Set(BuildSection.Placement, false);
        first.Set(BuildSection.Contents, true);
        var again = new BuildSectionMemory(new SettingsService(store));   // the viewer started again: the settings read from the file
        Assert.False(again.IsOpen(BuildSection.Placement));
        Assert.True(again.IsOpen(BuildSection.Contents));
        Assert.True(again.IsOpen(BuildSection.Look));
        Assert.False(again.IsOpen(BuildSection.Shape));
    }

    [Fact]
    public void All_sections_closed_stays_closed_after_a_restart()
    {
        var store = new MemoryStore();
        var memory = new BuildSectionMemory(new SettingsService(store));
        foreach (var s in BuildLayout.Sections)
            memory.Set(s, false);
        Assert.All(BuildLayout.Sections, s => Assert.False(new BuildSectionMemory(new SettingsService(store)).IsOpen(s)));
    }

    // ---------------- search ----------------

    [Fact]
    public void The_official_name_Move_Tool_finds_Grab()
    {
        var hits = BuildLayout.Search("Move Tool", S);
        Assert.Contains(hits, r => r.Id == "tool_move");
        Assert.Equal("Grab", S.Get(hits.First(r => r.Id == "tool_move").LabelKey));
    }

    [Theory]
    [InlineData("Building Block Type", "prim_type", BuildSection.Shape)]
    [InlineData("full bright", "fullbright", BuildSection.Look)]
    [InlineData("Color", "colour", BuildSection.Look)]
    [InlineData("bounciness", "restitution_row", BuildSection.Behaviour)]
    [InlineData("Select Only My Objects", "owned_only", BuildSection.Object)]
    [InlineData("phantom", "phantom", BuildSection.Object)]
    [InlineData("snap", "snap", BuildSection.Placement)]
    [InlineData("new script", "new_script", BuildSection.Contents)]
    public void Search_finds_a_row_by_our_name_or_the_official_one_and_opens_its_section(string query, string id, BuildSection section)
    {
        var hits = BuildLayout.Search(query, S);
        Assert.Contains(hits, r => r.Id == id);
        Assert.Contains(section, BuildLayout.SectionsOf(hits));
    }

    [Fact]
    public void Search_narrows_to_matching_rows_only()
    {
        var hits = BuildLayout.Search("hollow", S);
        Assert.Equal(["hollow", "hollow_shape"], hits.Select(r => r.Id).ToArray());
        Assert.Equal([BuildSection.Shape], BuildLayout.SectionsOf(hits));
        Assert.Empty(BuildLayout.Search("   ", S));
        Assert.Empty(BuildLayout.Search("no such setting", S));
    }

    [Fact]
    public void Every_row_label_is_in_the_string_table_and_every_id_is_unique()
    {
        Assert.Equal(BuildLayout.Rows.Count, BuildLayout.Rows.Select(r => r.Id).Distinct().Count());
        // a missing key comes back as "[[key]]"
        foreach (var row in BuildLayout.Rows)
            Assert.DoesNotContain("[[", S.Get(row.LabelKey));
        foreach (var section in BuildLayout.Sections)
            Assert.DoesNotContain("[[", S.Get(BuildLayout.TitleKey(section)));
    }

    [Fact]
    public void The_search_line_names_what_a_search_found_and_what_it_did_not()
    {
        string hollow = BuildLayout.SearchLine("hollow", BuildLayout.Search("hollow", S), S);
        Assert.Contains("Shape", hollow);
        Assert.Contains("Hollow", hollow);
        Assert.Equal(S.Get("edit.search.none", "zzz"), BuildLayout.SearchLine("zzz", BuildLayout.Search("zzz", S), S));
        Assert.Contains("Grab", BuildLayout.SearchLine("Move Tool", BuildLayout.Search("Move Tool", S), S));
        Assert.Equal("", BuildLayout.SearchLine("  ", BuildLayout.Search("  ", S), S));
    }

    [Fact]
    public void A_tool_that_is_not_built_is_not_found_by_the_search()
    {
        Assert.DoesNotContain(BuildLayout.Rows, r => r.Id == "tool_land");
        Assert.Empty(BuildLayout.Search("terraform", S));
    }

    [Fact]
    public void A_texture_is_named_never_shown_as_a_long_id()
    {
        var plywood = new Guid("89556747-24cb-43ed-920b-47caed15465f");
        Assert.Equal("Plywood", BuildNames.Texture(plywood, null, S));
        Assert.Equal("My floor", BuildNames.Texture(plywood, "My floor", S));
        var other = new Guid("12345678-1234-4234-8234-123456789abc");
        string unknown = BuildNames.Texture(other, null, S);
        Assert.False(Guid.TryParse(unknown, out _));
        Assert.True(unknown.Length <= 14, unknown);
        Assert.StartsWith("12345678", unknown);
    }

    // ---------------- hint line ----------------

    [Fact]
    public void The_hint_follows_the_tool_and_a_held_modifier()
    {
        string move = BuildLayout.Hint(BuildTool.Edit, ManipMode.Move, false, false, false, S);
        string rotate = BuildLayout.Hint(BuildTool.Edit, ManipMode.Move, false, ctrl: true, shift: false, S);
        string stretch = BuildLayout.Hint(BuildTool.Edit, ManipMode.Move, false, ctrl: true, shift: true, S);
        Assert.Equal(S.Get("edit.hint.move"), move);
        Assert.Equal(S.Get("edit.hint.rotate"), rotate);
        Assert.Equal(S.Get("edit.hint.stretch"), stretch);
        Assert.Equal(S.Get("edit.hint.face"), BuildLayout.Hint(BuildTool.Edit, ManipMode.Move, true, false, false, S));
        Assert.Equal(S.Get("edit.hint.grab.lift"), BuildLayout.Hint(BuildTool.Move, ManipMode.Move, false, true, false, S));
        Assert.Equal(S.Get("edit.hint.create"), BuildLayout.Hint(BuildTool.Create, ManipMode.Move, false, false, false, S));
        // each hint names its modifiers
        Assert.Contains("Ctrl", move);
        Assert.Contains("Ctrl", S.Get("edit.hint.focus"));
        Assert.Contains("Ctrl", S.Get("edit.hint.grab"));
    }

    // ---------------- the window's sources ----------------

    private static string Host(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "host", "Viewer.Godot", name));

    [Fact]
    public void The_Build_window_draws_sections_not_tabs()
    {
        string panel = Host("EditPanel.cs");
        Assert.DoesNotContain("TabContainer", panel);
        Assert.Contains("BuildSectionMemory", panel);
        Assert.Contains("BuildLayout.Search", panel);
        Assert.Contains("BuildLayout.Summary", panel);
        Assert.Contains("BuildLayout.Hint", panel);
        // one segmented tool row with the four built tools (Land is not built, so it has no segment), and Undo and Redo beside the modes
        foreach (var tool in new[] { "Focus", "Move", "Edit", "Create" })
            Assert.Matches(new Regex(@"BuildTool\." + tool + @"\b"), panel);
        Assert.Contains("\"undo\"", panel);
        Assert.Contains("\"redo\"", panel);
    }

    [Fact]
    public void Every_catalogued_row_is_a_control_the_window_makes()
    {
        string all = Host("EditPanel.cs") + Host("EditTextureTab.cs") + Host("EditFeaturesTab.cs") + Host("EditContentTab.cs");
        foreach (var row in BuildLayout.Rows)
        {
            // the tool and mode buttons are named from their enums ("tool_" + the tool, "manip_" + the mode)
            if (row.Id.StartsWith("tool_", StringComparison.Ordinal) && Enum.TryParse<BuildTool>(row.Id[5..], true, out _) && all.Contains("\"tool_\" + tool"))
                continue;
            if (row.Id.StartsWith("manip_", StringComparison.Ordinal) && Enum.TryParse<ManipMode>(row.Id[6..], true, out _) && all.Contains("\"manip_\" + mode"))
                continue;
            string id = row.Id.EndsWith("_row", StringComparison.Ordinal) && !all.Contains('"' + row.Id + '"') ? row.Id[..^4] : row.Id;
            Assert.True(all.Contains('"' + id + '"'), $"no control named {row.Id}");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Viewer.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
