// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Shell;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Inventory folders open and close on a click only, as the official viewer's folder view does (indra/llui/llfolderviewitem.cpp
/// LLFolderViewFolder::handleMouseDown and handleDoubleClick, indra/llui/llfolderview.cpp handleKeyHere): a click on the arrow
/// toggles the folder, a click on the name selects it, a double-click on the name toggles it, a right-click selects the row and
/// opens its menu without toggling anything; Right opens, Left closes or moves to the parent. Nothing opens a folder without a
/// click or a key, not even an item dragged and held over it: the folder under a drag is only marked as the drop target, with the
/// text "Drop to move here", and the dragged item's own row gets a dashed outline. A drop into a closed folder still moves the item.
/// Pure, runs in parallel.
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
    public void An_item_dragged_over_a_folder_marks_that_folder_as_the_drop_target_and_its_own_row_as_the_source()
    {
        var cue = DragFeedback.During(dragged: A, folderUnderPointer: B, canDrop: true, dragging: true, buttonHeld: true);
        Assert.Equal(B, cue.Target);
        Assert.Equal(A, cue.Source);
    }

    [Fact]
    public void A_folder_the_item_cannot_move_into_is_not_marked_but_the_source_row_still_is()
    {
        var cue = DragFeedback.During(A, B, canDrop: false, dragging: true, buttonHeld: true);
        Assert.Equal(Guid.Empty, cue.Target);
        Assert.Equal(A, cue.Source);
        var self = DragFeedback.During(A, A, canDrop: true, dragging: true, buttonHeld: true);
        Assert.Equal(Guid.Empty, self.Target);   // a folder is never its own drop target
        Assert.Equal(A, self.Source);
    }

    [Fact]
    public void Without_a_drag_or_with_its_button_let_go_nothing_is_marked()
    {
        Assert.Equal(default, DragFeedback.During(A, B, canDrop: true, dragging: false, buttonHeld: true));
        Assert.Equal(default, DragFeedback.During(A, B, canDrop: true, dragging: true, buttonHeld: false));
        Assert.Equal(default, DragFeedback.During(Guid.Empty, B, canDrop: true, dragging: true, buttonHeld: true));
        var none = DragFeedback.During(A, Guid.Empty, canDrop: true, dragging: true, buttonHeld: true);
        Assert.Equal(Guid.Empty, none.Target);
        Assert.Equal(A, none.Source);
    }

    [Fact]
    public void A_drag_whose_button_is_no_longer_held_is_stale_and_ended()
    {
        Assert.True(DragFeedback.IsStaleDrag(dragging: true, buttonHeld: false));
        Assert.False(DragFeedback.IsStaleDrag(dragging: true, buttonHeld: true));
        Assert.False(DragFeedback.IsStaleDrag(dragging: false, buttonHeld: false));
    }

    [Fact]
    public void The_drop_target_text_is_drop_to_move_here()
    {
        Assert.Equal("inventory.drop.here", DragFeedback.TextKey);
        Assert.Equal("Drop to move here", StringTable.English().Get("inventory.drop.here"));
    }

    [Fact]
    public void No_folder_opens_on_a_timer_while_an_item_is_dragged_over_it()
    {
        // the drag-open of the official viewer (FolderAutoOpenDelay) is not used: hovering opens nothing, a drag included
        Assert.Null(typeof(FolderTreeInput).Assembly.GetType("Viewer.Shell.Inventory.DragAutoOpen"));
        var process = Regex.Match(Panels, @"public override void _Process\(double delta\)[\s\S]*?\n    \}");
        Assert.True(process.Success, "the tree looks at the drag every frame");
        Assert.DoesNotContain("Collapsed = false", process.Value);
        Assert.DoesNotContain("Collapsed=false", process.Value);
        Assert.Contains("DragFeedback.During(", process.Value);
    }

    [Fact]
    public void The_tree_draws_the_drop_target_outline_with_its_text_and_a_dashed_outline_on_the_dragged_row()
    {
        string text = Panels;
        var draw = Regex.Match(text, @"public override void _Draw\(\)[\s\S]*?\n    \}");
        Assert.True(draw.Success, "the tree draws the drag cue over its rows");
        Assert.Contains("DrawRect(", draw.Value);
        Assert.Contains("DrawDashedLine(", draw.Value);
        Assert.Contains("DragFeedback.TextKey", text);
    }

    private static string Panels => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "InventoryPanels.cs"));

    [Fact]
    public void The_engines_own_unfold_on_drag_hover_is_off_and_the_tree_uses_the_rules_above()
    {
        string text = Panels;
        Assert.Matches(@"EnableDragUnfolding\s*=\s*false", text);
        Assert.Contains("FolderTreeInput.Press(", text);
        Assert.Contains("FolderTreeInput.Key(", text);
        Assert.Matches(@"DragFeedback\.IsStaleDrag\(", text);
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
