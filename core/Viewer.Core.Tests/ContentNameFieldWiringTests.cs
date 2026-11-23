// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The Contents name and description fields are Godot controls, so what they do with Esc and with the window closing is proven by the live
/// runs (real key and pointer input on a test region). These tests only pin the wiring those runs depend on, read from the host source:
/// a field that takes Esc itself, and a keyboard close that sends a name still being typed before build mode ends.
/// Read-only file scans; each test is independent, so the class runs in parallel.
/// </summary>
public class ContentNameFieldWiringTests
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", file));

    [Theory]
    [InlineData("_propName")]
    [InlineData("_propDesc")]
    public void Each_text_field_of_the_item_takes_Esc_itself_so_the_shell_does_not_let_go_of_it_first(string field)
    {
        // ShellRoot._Input lets go of a focused text field on Esc unless the field carries this mark; a released field saves what was typed
        Assert.Contains($"{field}.SetMeta(ShellRoot.TakesEscapeMeta, true);", Source("EditContentTab.cs"));
        Assert.Contains($"{field}.GuiInput += e => CancelOnEscape(e, {field});", Source("EditContentTab.cs"));
    }

    [Fact]
    public void Toggling_build_mode_off_sends_a_name_being_typed_before_build_mode_ends()
    {
        string main = Source("Main.Edit.cs");
        int start = main.IndexOf("private void ToggleBuild()", StringComparison.Ordinal);
        int commit = main.IndexOf("ContentTab.CommitPendingEdit();", start, StringComparison.Ordinal);
        int toggle = main.IndexOf("e.ToggleBuild();", start, StringComparison.Ordinal);
        Assert.True(commit > 0 && toggle > 0 && commit < toggle, "the pending name must be committed before e.ToggleBuild()");
    }
}
