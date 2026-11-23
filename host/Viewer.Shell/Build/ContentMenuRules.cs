// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Shell.Build;

/// <summary>The five actions the Contents section offers for a chosen item, as buttons and in the right-click menu.</summary>
public enum ContentAction { Open, Properties, Rename, Delete, Copy }

/// <summary>
/// Whether each Contents action applies, and if not, why (the string key of the reason, shown as the greyed entry's tooltip, UI-BASICS H2).
/// The buttons and the right-click menu both read it, so they never disagree; the menu lists every action and greys the ones that do not apply.
/// Rules after the official object inventory (llpanelobjectinventory.cpp): an item's rename needs the object and the item to allow
/// modifying, removal needs the agent to be able to modify the object or to own it.
/// </summary>
public static class ContentMenuRules
{
    /// <summary>Every action in the order the menu lists them.</summary>
    public static IReadOnlyList<ContentAction> All { get; } = [ContentAction.Open, ContentAction.Properties, ContentAction.Rename, ContentAction.Delete, ContentAction.Copy];

    /// <summary>The string key of an action's menu text.</summary>
    public static string TextKey(ContentAction action) => "edit.content." + action switch
    {
        ContentAction.Open => "open", ContentAction.Properties => "properties", ContentAction.Rename => "rename", ContentAction.Delete => "delete", _ => "copy",
    };

    public static (bool Enabled, string? ReasonKey) State(ContentAction action, TaskItem? item, bool objectModify, bool youOwn, Guid agent)
    {
        if (item is null)
            return (false, "disabled.content.pick");
        return action switch
        {
            ContentAction.Open => TaskItemRules.OpenKind(item) == TaskOpenKind.None ? (false, "disabled.content.open") : (true, null),
            ContentAction.Rename => objectModify && TaskItemRules.CanRename(item, agent) ? (true, null) : (false, "disabled.content.rename"),
            ContentAction.Delete => objectModify || youOwn ? (true, null) : (false, "disabled.content.delete"),
            _ => (true, null),
        };
    }
}
