// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.World;
using Viewer.Shell.Outfit;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The inventory items of the own worn objects, read from the world and kept briefly. Engine-free; every member builds its own world.</summary>
public class WornObjectsTests
{
    private static readonly Guid ItemA = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1");
    private static readonly Guid ItemB = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a2");
    private static readonly Guid ItemOther = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a3");

    [Fact]
    public void It_lists_the_items_of_the_own_worn_root_objects_only()
    {
        var r = new WireRig();
        r.Worn(10, 6, ItemA);
        r.Worn(11, 2, ItemB);
        r.Worn(12, 6, ItemOther, parent: 501);       // the other avatar's
        r.Worn(13, 3, Guid.Empty);                   // worn, item not stated
        r.Prim(14, new Vector3(5, 5, 5));            // not worn
        long now = 0;
        var worn = new WornObjects(r.World, WireRig.Me, () => now);
        Assert.Equal(new HashSet<Guid> { ItemA, ItemB }, worn.Items().ToHashSet());
    }

    [Fact]
    public void The_answer_is_reused_for_a_short_time_and_read_again_after_it()
    {
        var r = new WireRig();
        r.Worn(10, 6, ItemA);
        long now = 1000;
        var worn = new WornObjects(r.World, WireRig.Me, () => now);
        Assert.Equal([ItemA], worn.Items().ToArray());
        r.Worn(11, 2, ItemB);
        now += WornObjects.KeepMs - 1;
        Assert.Equal([ItemA], worn.Items().ToArray());   // still the earlier answer
        now += 1;
        Assert.Equal(new HashSet<Guid> { ItemA, ItemB }, worn.Items().ToHashSet());
    }
}
