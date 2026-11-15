// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Viewer.Core.Interaction;
using Viewer.Core.World;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class S1Smoke
{
    // The right-click menu, every item of it, through the engine's pointer input: the menus for an object (own, for sale, worn), another
    // avatar, the own avatar and a spot of land are drawn, and each item is clicked with a press and a release at its button as the
    // operating system delivers them, through its sub-menus where it has some. Choosing an item closes the menu and chooses that item once
    // (the official menus close on a choice, llmenugl.cpp); a sub-menu's own button opens it and keeps the menu open; a disabled item does
    // nothing and leaves the menu open; the menu itself opens no window.
    private async Task MenuChoiceCheck()
    {
        var problems = new List<string>();
        var shell = new ShellRoot(new ShellController(StringTable.English(), () => { }));
        var panels = InteractFeature.Install(shell);
        AddChild(shell);
        async Task Frames(int n) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
        async Task Click(Control c)
        {
            // an item of a long list (the attachment points, the clothes) is scrolled into view first, as a person scrolls to it
            for (Node? n = c.GetParent(); n is not null && n != panels.Menu; n = n.GetParent())
                if (n is ScrollContainer scroll)
                {
                    scroll.EnsureControlVisible(c);
                    await Frames(2);
                    break;
                }
            var at = c.GetGlobalRect().GetCenter();
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
            await Frames(1);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
            await Frames(2);
        }
        Button? ButtonOf(string id) => panels.Menu.FindChild("entry_" + id.Replace('/', '_'), true, false) as Button
            ?? panels.Menu.FindChild("entry_" + id, true, false) as Button;
        const uint mine = ObjectFlagBits.YouOwner | ObjectFlagBits.Modify | ObjectFlagBits.Move | ObjectFlagBits.Copy | ObjectFlagBits.Transfer;
        var own = new PrimInteraction(0, mine);
        var worn = new[] { new WornEntry(Guid.NewGuid(), "Shirt", Viewer.Core.Inventory.WearableType.Shirt), new WornEntry(Guid.NewGuid(), "Hat", IsObject: true) };
        var menus = new (string Name, IReadOnlyList<ContextEntry> Entries)[]
        {
            ("object", ContextMenus.ForObject(new ObjectMenuContext(own, own, false, false, false))),
            ("object_for_sale", ContextMenus.ForObject(new ObjectMenuContext(new PrimInteraction(0, ObjectFlagBits.Copy), new PrimInteraction(0, ObjectFlagBits.Copy), false, false, false, ForSale: true, CanBuy: true))),
            ("object_sitting_on", ContextMenus.ForObject(new ObjectMenuContext(own, own, false, true, true))),
            ("attachment", ContextMenus.ForObject(new ObjectMenuContext(own, own, true, false, false, CanDetach: true))),
            ("avatar", ContextMenus.ForAvatar(new AvatarMenuContext(false, false))),
            ("self", ContextMenus.ForSelf(new SelfMenuContext(false, false, worn, worn))),
            ("land", ContextMenus.ForLand(new LandMenuContext(true, true))),
        };
        int leaves = 0, chosen = 0, disabled = 0, subs = 0;
        var opened = new List<string>();
        shell.Shell.Floaters.Opened += id => opened.Add(id);
        foreach (var (name, entries) in menus)
        {
            // every leaf, with the sub-menu entries that lead to it
            var paths = new List<(List<ContextEntry> Via, ContextEntry Leaf)>();
            void Walk(IReadOnlyList<ContextEntry> es, List<ContextEntry> via)
            {
                foreach (var e in es)
                    if (e.Children is { Count: > 0 } kids && e.Enabled)
                        Walk(kids, [.. via, e]);
                    else
                        paths.Add((via, e));
            }
            Walk(entries, []);
            foreach (var (via, leaf) in paths)
            {
                var picked = new List<string>();
                panels.Menu.Open(new Vector2(240, 160), entries, id => picked.Add(id), GetViewport().GetVisibleRect().Size);
                await Frames(2);
                bool reached = true;
                foreach (var step in via)
                {
                    if (ButtonOf(step.Id) is not { } sb) { problems.Add($"{name}: no button for sub-menu {step.Id}"); reached = false; break; }
                    await Click(sb);
                    subs++;
                    if (!panels.Menu.IsOpen) { problems.Add($"{name}: the sub-menu {step.Id} closed the menu"); reached = false; break; }
                    if (picked.Count != 0) { problems.Add($"{name}: the sub-menu {step.Id} chose [{string.Join(",", picked)}]"); reached = false; break; }
                }
                if (!reached) { panels.Menu.Close(); continue; }
                if (ButtonOf(leaf.Id) is not { } b) { problems.Add($"{name}: no button for {leaf.Id}"); panels.Menu.Close(); continue; }
                leaves++;
                await Click(b);
                if (leaf.Enabled)
                {
                    chosen++;
                    if (panels.Menu.IsOpen) problems.Add($"{name}: choosing {leaf.Id} left the menu open");
                    if (picked.Count != 1 || picked[0] != leaf.Id) problems.Add($"{name}: choosing {leaf.Id} chose [{string.Join(",", picked)}]");
                }
                else
                {
                    disabled++;
                    if (picked.Count != 0) problems.Add($"{name}: the disabled {leaf.Id} chose [{string.Join(",", picked)}]");
                    if (!panels.Menu.IsOpen) problems.Add($"{name}: the disabled {leaf.Id} closed the menu");
                }
                var nowOpen = opened.ToArray();
                opened.Clear();
                if (nowOpen.Length > 0) problems.Add($"{name}: {leaf.Id} opened {string.Join(",", nowOpen)} through the menu itself");
                panels.Menu.Close();
                await Frames(1);
            }
        }
        _result["menu_choice"] = new Dictionary<string, object?> { ["items"] = leaves, ["chosen"] = chosen, ["disabled"] = disabled, ["submenu_clicks"] = subs, ["problems"] = problems };
        Log($"menu choice: {leaves} items ({chosen} chosen, {disabled} disabled), {subs} sub-menu clicks, {problems.Count} problems{(problems.Count > 0 ? ": " + string.Join(" | ", problems.Take(12)) : "")}");
        RemoveChild(shell);
        shell.QueueFree();
        if (problems.Count > 0 || chosen == 0)
            _failures.Add("right-click menu items chosen through the engine's pointer input (see menu_choice)");
    }
}
