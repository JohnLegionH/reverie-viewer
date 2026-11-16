// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

/// <summary>
/// The Build menu, laid out as the official viewer's (menu_viewer.xml, the BuildTools menu; read as behaviour): only the entries that work
/// are shown, each at its official place, so later ones slot in between. Create stands where the official "Select Build Tool" list is
/// (its Create Tool, Ctrl+4); Upload is the official Upload list with Image... (Ctrl+U), Model... and Bulk..., and this viewer's Bulk models...
/// right after Model....
/// </summary>
public static class BuildMenu
{
    public const string MenuId = "build", CreateId = "build.create", UploadId = "build.upload", ImageId = "build.upload.image", BulkId = "build.upload.bulk",
        ModelId = "build.upload.model", BulkModelsId = "build.upload.bulk_models";
    public const string CreateCommand = "build.create", ImageCommand = "build.upload.image", BulkCommand = "build.upload.bulk",
        ModelCommand = "build.upload.model", BulkModelsCommand = "build.upload.bulk_models";

    /// <summary>The places of the entries, in the official menu's order (gaps leave room for the entries still to come).</summary>
    public const int CreateOrder = 20, UploadOrder = 900;

    public const string ToggleId = "build.toggle", FocusId = "build.tool.focus", MoveId = "build.tool.move", EditId = "build.tool.edit",
        EditLinkedId = "build.edit_linked", LinkId = "build.link", UnlinkId = "build.unlink";
    public const string ToggleCommand = "build.toggle", FocusCommand = "build.tool.focus", MoveCommand = "build.tool.move", EditCommand = "build.tool.edit",
        EditLinkedCommand = "build.edit_linked", LinkCommand = "build.link", UnlinkCommand = "build.unlink";

    /// <summary>The edit tools' entries at their official places (menu_viewer.xml): Build (Ctrl+B) first, the tools Focus, Move and Edit
    /// (Ctrl+1 to Ctrl+3) before Create, then Edit Linked Parts, Link (Ctrl+L) and Unlink (Ctrl+Shift+L). <paramref name="edit"/> gives the
    /// build mode the ticks show; <paramref name="hasSelection"/> enables Link and Unlink.</summary>
    public static void InstallEditing(MenuModel menus, Func<bool> inWorld, Func<Viewer.Shell.Build.EditController?> edit, Func<bool> hasSelection)
    {
        if (!menus.Menus.Any(m => m.Id == MenuId))
            menus.AddMenu(MenuId, "menu.build");
        bool Tool(Viewer.Shell.Build.BuildTool t) => edit() is { BuildMode: true } e && e.Tool == t;
        bool Selected() => inWorld() && hasSelection();
        menus.AddEntry(MenuId, new MenuEntry(ToggleId, "menu.build.toggle", ToggleCommand, inWorld, ToggleOrder, () => edit()?.BuildMode ?? false, Shortcut: "control|B"));
        menus.AddEntry(MenuId, new MenuEntry(FocusId, "menu.build.tool.focus", FocusCommand, inWorld, CreateOrder - 3, () => Tool(Viewer.Shell.Build.BuildTool.Focus), Shortcut: "control|1"));
        menus.AddEntry(MenuId, new MenuEntry(MoveId, "menu.build.tool.move", MoveCommand, inWorld, CreateOrder - 2, () => Tool(Viewer.Shell.Build.BuildTool.Move), Shortcut: "control|2"));
        menus.AddEntry(MenuId, new MenuEntry(EditId, "menu.build.tool.edit", EditCommand, inWorld, CreateOrder - 1, () => Tool(Viewer.Shell.Build.BuildTool.Edit), Shortcut: "control|3"));
        menus.AddEntry(MenuId, new MenuEntry(EditLinkedId, "menu.build.edit_linked", EditLinkedCommand, inWorld, EditLinkedOrder, () => edit()?.EditLinked ?? false));
        menus.AddEntry(MenuId, new MenuEntry(LinkId, "menu.build.link", LinkCommand, Selected, LinkOrder, Shortcut: "control|L"));
        menus.AddEntry(MenuId, new MenuEntry(UnlinkId, "menu.build.unlink", UnlinkCommand, Selected, LinkOrder + 1, Shortcut: "control|shift|L"));
        // Options: the grid's entries (menu_viewer.xml's Options list); they work in build mode, so G and Shift+X type as letters elsewhere
        bool Building() => inWorld() && edit() is { BuildMode: true };
        bool BuildingWithSelection() => Building() && hasSelection();
        menus.AddSubmenu(MenuId, OptionsId, "menu.build.options", OptionsOrder);
        menus.AddSubEntry(OptionsId, new MenuEntry(GridSnapId, "menu.build.grid.snap", GridSnapCommand, Building, 10, () => edit()?.SnapEnabled ?? false, Shortcut: "G"));
        menus.AddSubEntry(OptionsId, new MenuEntry(GridSnapXYId, "menu.build.grid.snap_xy", GridSnapXYCommand, BuildingWithSelection, 20, Shortcut: "shift|X"));
        menus.AddSubEntry(OptionsId, new MenuEntry(GridUseSelectionId, "menu.build.grid.use_selection", GridUseSelectionCommand, BuildingWithSelection, 30, Shortcut: "shift|G"));
        menus.AddSubEntry(OptionsId, new MenuEntry(GridOptionsId, "menu.build.grid.options", GridOptionsCommand, inWorld, 40, Shortcut: "control|shift|B"));
        // Undo and Redo close the Build menu, after Upload (menu_viewer.xml); on when the selection allows them (llselectmgr.cpp canUndo, canRedo)
        menus.AddEntry(MenuId, new MenuEntry(UndoId, "menu.build.undo", UndoCommand, () => inWorld() && edit() is { CanUndo: true }, UndoOrder, Shortcut: "control|Z"));
        menus.AddEntry(MenuId, new MenuEntry(RedoId, "menu.build.redo", RedoCommand, () => inWorld() && edit() is { CanRedo: true }, UndoOrder + 1, Shortcut: "control|Y"));
    }

    public const string NextPartId = "build.next_part", PreviousPartId = "build.previous_part", OwnedOnlyId = "build.owned_only", TakeIntoFolderId = "build.take_into_folder";
    public const string NextPartCommand = "build.next_part", PreviousPartCommand = "build.previous_part", OwnedOnlyCommand = "build.owned_only",
        TakeIntoFolderCommand = "build.take_into_folder";

    /// <summary>Select Next Part (Ctrl+.) and Select Previous Part (Ctrl+,) after Edit Linked Parts, Select Only My Objects in Options, and
    /// Take Into Folder after Unlink (menu_viewer.xml's places for the first three).</summary>
    public static void InstallSelectionHelpers(MenuModel menus, Func<bool> inWorld, Func<Viewer.Shell.Build.EditController?> edit) { }

    /// <summary>Undo and Redo stand last in the Build menu, after Upload.</summary>
    public const int UndoOrder = 2000;

    /// <summary>The Undo and Redo entries and their commands.</summary>
    public const string UndoId = "build.undo", RedoId = "build.redo", UndoCommand = "build.undo", RedoCommand = "build.redo";

    public const string OptionsId = "build.options", GridSnapId = "build.grid.snap", GridSnapXYId = "build.grid.snap_xy", GridUseSelectionId = "build.grid.use_selection",
        GridOptionsId = "build.grid.options";
    public const string GridSnapCommand = "build.grid.snap", GridSnapXYCommand = "build.grid.snap_xy", GridUseSelectionCommand = "build.grid.use_selection",
        GridOptionsCommand = "build.grid.options";
    /// <summary>Options stands after Link and Unlink, before Upload.</summary>
    public const int OptionsOrder = 800;

    /// <summary>The places of the edit entries: Build first, Edit Linked Parts and Link after the tools.</summary>
    public const int ToggleOrder = 10, EditLinkedOrder = 30, LinkOrder = 40;

    /// <summary>Adds the Build menu and its entries; <paramref name="inWorld"/> enables them while the agent is in a region.</summary>
    public static void Install(MenuModel menus, Func<bool> inWorld)
    {
        if (!menus.Menus.Any(m => m.Id == MenuId))
            menus.AddMenu(MenuId, "menu.build");
        menus.AddEntry(MenuId, new MenuEntry(CreateId, "menu.build.create", CreateCommand, inWorld, CreateOrder, Shortcut: "control|4"));
        menus.AddSubmenu(MenuId, UploadId, "menu.build.upload", UploadOrder);
        menus.AddSubEntry(UploadId, new MenuEntry(ImageId, "menu.build.upload.image", ImageCommand, inWorld, 10, Shortcut: "control|U"));
        menus.AddSubEntry(UploadId, new MenuEntry(ModelId, "menu.build.upload.model", ModelCommand, inWorld, 40));
        menus.AddSubEntry(UploadId, new MenuEntry(BulkModelsId, "menu.build.upload.bulk_models", BulkModelsCommand, inWorld, 45));
        menus.AddSubEntry(UploadId, new MenuEntry(BulkId, "menu.build.upload.bulk", BulkCommand, inWorld, 60));
    }
}
