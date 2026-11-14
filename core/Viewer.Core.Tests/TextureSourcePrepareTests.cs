// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Viewer.Core.Imaging;
using Viewer.Core.Protocol;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// What <see cref="TextureSource.Prepare"/> makes off the main thread (the host's engine images) is
/// never lost: every object made is either handed out once and disposed when its level is released, or disposed at once.
/// Found by the render lifecycle check (engine objects grew across region changes on d3d12): a decode finishing after
/// Dispose left an object nobody would dispose.
/// </summary>
public class TextureSourcePrepareTests
{
    private sealed class Made : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    private static readonly Guid Id = new(7, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
    private static readonly byte[] Codestream = LmvTexture.Encode(64, 64, Enumerable.Range(0, 64 * 64 * 4).Select(i => (byte)(i * 7)).ToArray(), false);

    private static TextureSource Source(Func<Task> gate) => new(new SessionAssets(new SharedAssetStore(), _ => true, async (_, _) =>
    {
        await gate();
        return AssetFetchResult.Ok(Codestream);
    }));

    private static void WaitIdle(TextureSource s)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (s.Pending > 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.Equal(0, s.Pending);
    }

    [Fact]
    public void A_decode_that_finishes_after_Dispose_disposes_what_it_prepared()
    {
        var made = new List<Made>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var s = Source(() => Task.CompletedTask);
        s.Prepare = _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            var m = new Made();
            lock (made) made.Add(m);
            return m;
        };
        Assert.Equal(MeshAvailability.Pending, s.TryGetLevel(Id, 0, out _));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
        s.Dispose();          // while the background task is still preparing
        release.Set();
        WaitIdle(s);
        Assert.Single(made);
        Assert.True(made[0].Disposed, "an object prepared after Dispose was left undisposed");
    }

    [Fact]
    public void A_level_released_while_its_decode_is_in_flight_is_not_decoded_twice_and_its_object_is_disposed()
    {
        var made = new List<Made>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s = Source(() => gate.Task);
        s.Prepare = _ => { var m = new Made(); lock (made) made.Add(m); return m; };
        Assert.Equal(MeshAvailability.Pending, s.TryGetLevel(Id, 0, out _));
        s.Release(Id);        // evicted while its decode is in flight
        Assert.Equal(MeshAvailability.Pending, s.TryGetLevel(Id, 0, out _));   // asked again: the same decode, not a second
        gate.SetResult();
        WaitIdle(s);
        Assert.Single(made);
        Assert.Equal(MeshAvailability.Ready, s.TryGetLevel(Id, 0, out _, out var handed));
        Assert.Same(made[0], handed);
        Assert.False(made[0].Disposed);
        s.Dispose();
        Assert.True(made[0].Disposed, "an object still held at Dispose was left undisposed");
    }
}
