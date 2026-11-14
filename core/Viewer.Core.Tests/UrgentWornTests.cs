// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A worn object is applied to the prims at once, ahead of the queue (its wearer is waiting to see it). The avatars must also be told that
/// something worn changed, or what a worn mesh hides of the body is worked out once, before the pieces arrive, and never again.
/// </summary>
public class UrgentWornTests
{
    private static (List<string> Calls, Action<WorldChange> P, Action<WorldChange> A, Action<WorldChange> W) Spies()
    {
        var calls = new List<string>();
        return (calls, _ => calls.Add("prims"), _ => calls.Add("avatars"), _ => calls.Add("worn"));
    }

    [Theory]
    [InlineData(WorldChangeKind.ObjectAdded)]
    [InlineData(WorldChangeKind.ObjectChanged)]
    public void A_worn_object_that_arrives_or_changes_goes_to_the_prims_and_marks_its_wearer(WorldChangeKind kind)
    {
        var (calls, p, a, w) = Spies();
        PresentationPolicy.ApplyUrgent(new WorldChange(kind, new ObjectId(1, 7)), p, a, w);
        Assert.Equal(["prims", "worn"], calls);
    }

    [Theory]
    [InlineData(WorldChangeKind.AvatarAdded)]
    [InlineData(WorldChangeKind.AvatarMoved)]
    [InlineData(WorldChangeKind.AppearanceChanged)]
    [InlineData(WorldChangeKind.AnimationsChanged)]
    public void An_avatar_change_goes_to_the_avatars_only(WorldChangeKind kind)
    {
        var (calls, p, a, w) = Spies();
        PresentationPolicy.ApplyUrgent(new WorldChange(kind, new ObjectId(1, 7)), p, a, w);
        Assert.Equal(["avatars"], calls);
    }
}
