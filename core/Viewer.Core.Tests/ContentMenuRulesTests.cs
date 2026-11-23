// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Build;
using Viewer.Shell.Ui;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>Which Contents actions apply to an item, and the reason a greyed one gives. No engine; pure rules, so the class runs in parallel.</summary>
public class ContentMenuRulesTests
{
    private static readonly Guid Me = Guid.Parse("4b1d2e3f-5a6b-4c7d-8e9f-0a1b2c3d4e01");
    private const uint Modify = 1u << 14, Copy = 1u << 15, Transfer = 1u << 13;

    private static TaskItem Item(int assetType, uint ownerMask) => new(Guid.NewGuid(), "Item", assetType, 0, Me, ownerMask, 0);

    [Fact]
    public void With_no_item_chosen_every_action_is_greyed_with_the_choose_an_item_reason()
    {
        foreach (var a in ContentMenuRules.All)
            Assert.Equal((false, "disabled.content.pick"), ContentMenuRules.State(a, null, true, true, Me));
    }

    [Fact]
    public void Properties_and_copy_apply_to_any_chosen_item()
    {
        var item = Item(7, 0);
        Assert.True(ContentMenuRules.State(ContentAction.Properties, item, false, false, Me).Enabled);
        Assert.True(ContentMenuRules.State(ContentAction.Copy, item, false, false, Me).Enabled);
    }

    [Fact]
    public void Open_applies_to_scripts_notecards_and_textures_only()
    {
        Assert.True(ContentMenuRules.State(ContentAction.Open, Item(7, Modify | Copy), true, true, Me).Enabled);
        Assert.Equal((false, "disabled.content.open"), ContentMenuRules.State(ContentAction.Open, Item(1, Modify | Copy), true, true, Me));
    }

    [Fact]
    public void Rename_needs_the_object_and_the_item_to_be_modifiable()
    {
        Assert.True(ContentMenuRules.State(ContentAction.Rename, Item(7, Modify | Copy | Transfer), true, true, Me).Enabled);
        Assert.Equal((false, "disabled.content.rename"), ContentMenuRules.State(ContentAction.Rename, Item(7, Modify | Copy | Transfer), false, true, Me));
        Assert.Equal((false, "disabled.content.rename"), ContentMenuRules.State(ContentAction.Rename, Item(7, Copy | Transfer), true, true, Me));
    }

    [Fact]
    public void Delete_applies_when_the_object_can_be_modified_or_is_owned_and_is_greyed_with_a_reason_otherwise()
    {
        var item = Item(7, Copy);
        Assert.True(ContentMenuRules.State(ContentAction.Delete, item, true, false, Me).Enabled);
        Assert.True(ContentMenuRules.State(ContentAction.Delete, item, false, true, Me).Enabled);
        Assert.Equal((false, "disabled.content.delete"), ContentMenuRules.State(ContentAction.Delete, item, false, false, Me));
    }

    [Fact]
    public void Every_reason_has_text_and_is_a_shared_disabled_reason()
    {
        var s = StringTable.English();
        foreach (var key in new[] { "disabled.content.pick", "disabled.content.open", "disabled.content.rename", "disabled.content.delete" })
        {
            Assert.Contains(key, DisabledReasons.Keys);
            Assert.NotEqual(key, s.Get(key));
        }
        foreach (var a in ContentMenuRules.All)
            Assert.NotEqual(ContentMenuRules.TextKey(a), s.Get(ContentMenuRules.TextKey(a)));
    }
}
