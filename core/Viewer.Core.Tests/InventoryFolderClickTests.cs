// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Inventory folders open and close on a click only, as the official viewer's folder view does (indra/llui/llfolderviewitem.cpp
/// LLFolderViewFolder::handleMouseDown and handleDoubleClick, indra/llui/llfolderview.cpp handleKeyHere): a click on the arrow
/// toggles the folder, a click on the name selects it, a double-click on the name toggles it, a right-click selects the row and
/// opens its menu without toggling anything; Right opens, Left closes or moves to the parent. The only opening without a click is
/// while an item is being dragged: a closed folder held under it opens after FolderAutoOpenDelay (0.75 s in the official
/// settings.xml). A pointer merely passing over a folder, with no drag or with a drag whose button is no longer held, opens
/// nothing. Pure, runs in parallel.
/// </summary>
public class InventoryFolderClickTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid();

    [Fact]
    public void A_click_on_a_folders_arrow_toggles_it()
    {
        Assert.Equal(TreeAction.Toggle, FolderTreeInput.Press(RowButton.Left, RowPart.Arrow, isFolder: true, doubleClick: false));
    }

    [Fact]
    public void A_click_on_a_name_selects_it_and_toggles_nothing()
    {
        Assert.Equal(TreeAction.Select, FolderTreeInput.Press(RowButton.Left, RowPart.Name, isFolder: true, doubleClick: false));
        Assert.Equal(TreeAction.Select, FolderTreeInput.Press(RowButton.Left, RowPart.Name, isFolder: false, doubleClick: false));
    }

    [Fact]
    public void A_double_click_on_a_folders_name_selects_and_toggles_it_and_on_an_item_opens_it()
    {
        Assert.Equal(TreeAction.SelectAndToggle, FolderTreeInput.Press(RowButton.Left, RowPart.Name, isFolder: true, doubleClick: true));
        Assert.Equal(TreeAction.Open, FolderTreeInput.Press(RowButton.Left, RowPart.Name, isFolder: false, doubleClick: true));
    }

    [Theory]
    [InlineData(RowPart.Arrow, true)]
    [InlineData(RowPart.Name, true)]
    [InlineData(RowPart.Name, false)]
    public void A_right_click_anywhere_on_a_row_selects_it_and_opens_its_menu_without_toggling(RowPart part, bool isFolder)
    {
        Assert.Equal(TreeAction.SelectAndMenu, FolderTreeInput.Press(RowButton.Right, part, isFolder, doubleClick: false));
        Assert.Equal(TreeAction.SelectAndMenu, FolderTreeInput.Press(RowButton.Right, part, isFolder, doubleClick: true));
    }

    [Fact]
    public void Right_opens_a_closed_folder_and_Left_closes_an_open_one()
    {
        Assert.Equal(TreeAction.Expand, FolderTreeInput.Key(TreeKey.Right, isFolder: true, expanded: false, hasParentRow: true));
        Assert.Equal(TreeAction.Collapse, FolderTreeInput.Key(TreeKey.Left, isFolder: true, expanded: true, hasParentRow: true));
    }

    [Fact]
    public void Left_on_a_closed_folder_or_an_item_moves_to_the_parent_and_at_the_top_closes()
    {
        Assert.Equal(TreeAction.SelectParent, FolderTreeInput.Key(TreeKey.Left, isFolder: true, expanded: false, hasParentRow: true));
        Assert.Equal(TreeAction.SelectParent, FolderTreeInput.Key(TreeKey.Left, isFolder: false, expanded: false, hasParentRow: true));
        Assert.Equal(TreeAction.Collapse, FolderTreeInput.Key(TreeKey.Left, isFolder: true, expanded: false, hasParentRow: false));
    }

    [Fact]
    public void Up_and_Down_move_the_selection()
    {
        Assert.Equal(TreeAction.MoveUp, FolderTreeInput.Key(TreeKey.Up, isFolder: true, expanded: false, hasParentRow: true));
        Assert.Equal(TreeAction.MoveDown, FolderTreeInput.Key(TreeKey.Down, isFolder: false, expanded: false, hasParentRow: true));
    }

    [Fact]
    public void The_drag_open_delay_is_the_official_setting()
    {
        Assert.Equal(0.75, DragAutoOpen.OfficialDelaySeconds);
        Assert.Equal(750, new DragAutoOpen().DelayMs);
        Assert.Equal(1200, new DragAutoOpen(1.2).DelayMs);
    }

    [Fact]
    public void Hovering_with_no_drag_opens_nothing_however_long()
    {
        var auto = new DragAutoOpen();
        for (long t = 0; t <= 5000; t += 100)
            Assert.Equal(Guid.Empty, auto.Step(A, folderClosed: true, dragging: false, buttonHeld: false, t));
        Assert.Equal(Guid.Empty, auto.Step(B, folderClosed: true, dragging: false, buttonHeld: true, 6000));
        Assert.Equal(Guid.Empty, auto.Step(B, folderClosed: true, dragging: false, buttonHeld: true, 9000));
    }

    [Fact]
    public void A_drag_whose_button_is_no_longer_held_opens_nothing_and_is_ended()
    {
        var auto = new DragAutoOpen();
        for (long t = 0; t <= 5000; t += 100)
            Assert.Equal(Guid.Empty, auto.Step(A, folderClosed: true, dragging: true, buttonHeld: false, t));
        Assert.True(DragAutoOpen.IsStaleDrag(dragging: true, buttonHeld: false));
        Assert.False(DragAutoOpen.IsStaleDrag(dragging: true, buttonHeld: true));
        Assert.False(DragAutoOpen.IsStaleDrag(dragging: false, buttonHeld: false));
    }

    [Fact]
    public void A_dragged_item_held_over_a_closed_folder_opens_it_once_after_the_delay()
    {
        var auto = new DragAutoOpen();
        Assert.Equal(Guid.Empty, auto.Step(A, true, true, true, 1000));
        Assert.Equal(Guid.Empty, auto.Step(A, true, true, true, 1700));
        Assert.Equal(A, auto.Step(A, true, true, true, 1750));
        Assert.Equal(Guid.Empty, auto.Step(A, true, true, true, 1800));   // once
        Assert.Equal(Guid.Empty, auto.Step(A, false, true, true, 3000));  // it is open now
    }

    [Fact]
    public void Moving_to_another_folder_starts_the_wait_again()
    {
        var auto = new DragAutoOpen();
        Assert.Equal(Guid.Empty, auto.Step(A, true, true, true, 0));
        Assert.Equal(Guid.Empty, auto.Step(B, true, true, true, 600));
        Assert.Equal(Guid.Empty, auto.Step(B, true, true, true, 1300));
        Assert.Equal(B, auto.Step(B, true, true, true, 1350));
    }

    [Fact]
    public void Leaving_the_folders_or_letting_go_resets_the_wait()
    {
        var auto = new DragAutoOpen();
        Assert.Equal(Guid.Empty, auto.Step(A, true, true, true, 0));
        Assert.Equal(Guid.Empty, auto.Step(Guid.Empty, true, true, true, 500));
        Assert.Equal(Guid.Empty, auto.Step(A, true, true, true, 600));
        Assert.Equal(Guid.Empty, auto.Step(A, true, true, true, 1300));
        Assert.Equal(A, auto.Step(A, true, true, true, 1350));
        var again = new DragAutoOpen();
        Assert.Equal(Guid.Empty, again.Step(A, true, true, true, 0));
        Assert.Equal(Guid.Empty, again.Step(A, true, false, false, 400));
        Assert.Equal(Guid.Empty, again.Step(A, true, true, true, 500));
        Assert.Equal(Guid.Empty, again.Step(A, true, true, true, 1200));
        Assert.Equal(A, again.Step(A, true, true, true, 1250));
    }

    private static string Panels => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "InventoryPanels.cs"));

    [Fact]
    public void The_engines_own_unfold_on_drag_hover_is_off_and_the_tree_uses_the_rules_above()
    {
        string text = Panels;
        Assert.Matches(@"EnableDragUnfolding\s*=\s*false", text);
        Assert.Contains("FolderTreeInput.Press(", text);
        Assert.Contains("FolderTreeInput.Key(", text);
        Assert.Matches(@"DragAutoOpen\.IsStaleDrag\(", text);
        Assert.Matches(@"GuiCancelDrag\(\)", text);
    }

    [Fact]
    public void No_tree_in_the_viewer_unfolds_its_folders_under_a_passing_drag()
    {
        string host = Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"new Tree\s*\{[^}]*\}"))
                if (!Regex.IsMatch(m.Value, @"EnableDragUnfolding\s*=\s*false"))
                    bad.Add(Path.GetFileName(file));
        Assert.True(bad.Count == 0, "trees that unfold under a drag: " + string.Join(", ", bad));
    }

    [Fact]
    public void A_right_press_on_the_tree_is_taken_before_the_engine_can_toggle_a_folder()
    {
        string text = Panels;
        var gui = Regex.Match(text, @"public override void _GuiInput\(InputEvent e\)[\s\S]*?\n    \}");
        Assert.True(gui.Success, "the tree handles its own input");
        Assert.Contains("MouseButton.Right", gui.Value);
        Assert.Contains("AcceptEvent()", gui.Value);
    }
}
