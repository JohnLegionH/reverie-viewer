// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Avatars;
using Viewer.Core.World;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A decoded level is let go when its owner releases it, also when the release comes while the decode is still running (the owner asked for another level meanwhile).</summary>
public class TextureSourceReleaseTests
{
    private static byte[] Picture() => CrowdBakeImages.Make(8, "upper", 128, 1, false);

    private static void WaitUntil(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(done(), what);
    }

    [Fact]
    public void A_level_released_while_its_decode_is_still_running_is_not_kept_when_it_lands()
    {
        var gate = new TaskCompletionSource<AssetFetchResult>();
        using var src = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => gate.Task));
        var id = Guid.NewGuid();
        Assert.Equal(MeshAvailability.Pending, src.TryGetLevel(id, 1, out _));
        src.ReleaseLevel(id, 1);   // the owner moved on to another level before this one arrived
        gate.SetResult(AssetFetchResult.Ok(Picture()));
        WaitUntil(() => src.Pending == 0, "the decode never finished");
        Assert.Equal(0, src.HeldLevels);
        // asked for again later, it is fetched and decoded afresh
        MeshAvailability again = MeshAvailability.Pending;
        WaitUntil(() => (again = src.TryGetLevel(id, 1, out _)) != MeshAvailability.Pending, "the level was never made again");
        Assert.Equal(MeshAvailability.Ready, again);
        Assert.Equal(1, src.HeldLevels);
        src.ReleaseLevel(id, 1);
        Assert.Equal(0, src.HeldLevels);
    }

    [Fact]
    public void A_level_released_and_asked_for_again_while_its_decode_runs_is_kept_for_the_second_asker()
    {
        var gate = new TaskCompletionSource<AssetFetchResult>();
        using var src = new TextureSource(new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => gate.Task));
        var id = Guid.NewGuid();
        Assert.Equal(MeshAvailability.Pending, src.TryGetLevel(id, 1, out _));
        src.ReleaseLevel(id, 1);
        Assert.Equal(MeshAvailability.Pending, src.TryGetLevel(id, 1, out _));   // wanted again before it landed
        gate.SetResult(AssetFetchResult.Ok(Picture()));
        WaitUntil(() => src.Pending == 0, "the decode never finished");
        Assert.Equal(MeshAvailability.Ready, src.TryGetLevel(id, 1, out _));
        Assert.Equal(1, src.HeldLevels);
    }
}
