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
