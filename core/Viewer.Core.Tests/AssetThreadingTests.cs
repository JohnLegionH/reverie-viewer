// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Concurrent;
using Viewer.Core.Assets;
using Viewer.Core.Meshing;
using Viewer.Core.Net;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Asset fetches and the content store's work (read, decrypt, encrypt, write) run on
/// the thread pool whatever the caller's synchronization context. A single-threaded context stands in for the engine's
/// main thread (Godot posts continuations to its main thread the same way). Then: many fetches at once from pool threads.
/// Runs in parallel with other classes: it uses only its own stores, its own temp folders and its own context
/// thread, no process-wide state.
/// </summary>
public class AssetThreadingTests : IDisposable
{
    private readonly string _root = TestTemp.NewFolderPath("asset-threads");

    public void Dispose() => TestTemp.Delete(_root);   // only the unique folder this instance was handed

    private ContentStore Store(string sub) => new(new ContentStoreOptions { Root = Path.Combine(_root, sub) });

    private static byte[] Data(Guid id, int n) { var b = new byte[n]; new Random(id.GetHashCode()).NextBytes(b); return b; }

    private static int Tid => Environment.CurrentManagedThreadId;

    /// <summary>One thread that runs everything posted to it, in order: the stand-in for the engine's main thread.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Call, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (call, state) in _queue.GetConsumingEnumerable()) call(state);
            }) { IsBackground = true, Name = "stand-in main thread" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        /// <summary>Runs <paramref name="start"/> on this thread (as the engine's main thread starts a fetch) and hands back
        /// the task it starts.</summary>
        public async Task<T> Start<T>(Func<Task<T>> start)
        {
            var started = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ => { try { started.SetResult(start()); } catch (Exception e) { started.SetException(e); } }, null);
            return await (await started.Task.ConfigureAwait(false)).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Records the threads a fetcher runs on: where it starts (the content store's read and decrypt run there,
    /// synchronously in its call) and where its result is ready (with a synchronous inner fetch, as the harness's local
    /// files are, the encrypt and write run there too).</summary>
    private sealed class Threads
    {
        public readonly ConcurrentBag<int> Seen = new();

        public Func<Guid, CancellationToken, Task<AssetFetchResult>> Around(Func<Guid, CancellationToken, Task<AssetFetchResult>> f) => async (id, ct) =>
        {
            Seen.Add(Tid);
            var r = await f(id, ct).ConfigureAwait(false);
            Seen.Add(Tid);
            return r;
        };
    }

    private static readonly Func<Guid, byte[], (MeshGeometry?, MeshRefusal?)> NoDecode = (_, _) => (null, null);

    [Fact]
    public async Task A_mesh_fetch_started_on_the_main_thread_runs_the_fetch_and_the_content_store_off_it()
    {
        using var main = new SingleThreadContext();
        var store = Store("mesh");
        var id = Guid.NewGuid();
        var bytes = Data(id, 200_000);
        var threads = new Threads();
        var inner = new Threads();
        var decodeThreads = new ConcurrentBag<int>();
        (MeshGeometry?, MeshRefusal?) Decode(Guid _, byte[] b) { decodeThreads.Add(Tid); Assert.Equal(bytes, b); return (null, null); }

        // 1. a miss everywhere: the content store is read (a miss), the transport fetches, the store encrypts and writes
        var view = store.View("grid", "s1");
        var fetch = threads.Around(view.Caching("mesh", inner.Around((_, _) => Task.FromResult(AssetFetchResult.Ok(bytes)))));
        using (var meshes = new SessionMeshes(new SessionAssets(new SharedAssetStore(), _ => true, fetch), Decode))
            await main.Start(() => meshes.GetAsync(id, CancellationToken.None));
        Assert.True(view.TryGet("mesh", id, out var written));
        Assert.Equal(bytes, written);

        // 2. a content-store hit (a new session, its own shared store): the read and decrypt
        var hits = new Threads();
        var shared = new SharedAssetStore();
        using (var meshes = new SessionMeshes(new SessionAssets(shared, _ => true, hits.Around(store.View("grid", "s2").Caching("mesh", (_, _) => throw new InvalidOperationException("not from the network")))), Decode))
        {
            var r = await main.Start(() => meshes.GetAsync(id, CancellationToken.None));
            Assert.Equal("content", r.Source);
            // 3. a shared-store hit completes at once: the copy and the decode still off the main thread
            meshes.Forget(id);
            r = await main.Start(() => meshes.GetAsync(id, CancellationToken.None));
            Assert.Equal("shared", r.Source);
        }

        Assert.NotEmpty(threads.Seen);
        Assert.NotEmpty(inner.Seen);
        Assert.NotEmpty(hits.Seen);
        Assert.Equal(3, decodeThreads.Count);
        Assert.DoesNotContain(main.ThreadId, threads.Seen);
        Assert.DoesNotContain(main.ThreadId, inner.Seen);
        Assert.DoesNotContain(main.ThreadId, hits.Seen);
        Assert.DoesNotContain(main.ThreadId, decodeThreads);
    }

    [Fact]
    public async Task A_level_fetch_started_on_the_main_thread_runs_the_partial_fetcher_and_its_store_off_it()
    {
        using var main = new SingleThreadContext();
        var view = Store("level").View("grid", "s1");
        var id = Guid.NewGuid();
        var file = Data(id, 300_000);
        // a readable SIZ (1024 x 1024), so the level's estimate applies and the first answer stays partial
        byte[] siz = [0xFF, 0x4F, 0xFF, 0x51, 0, 47, 0, 0, 0, 0, 4, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        siz.CopyTo(file, 0);
        var rangeThreads = new ConcurrentBag<int>();
        Task<RangeAnswer> Range(Guid _, long from, long? to, int cap, CancellationToken ct)
        {
            rangeThreads.Add(Tid);   // the store's reads (complete, then partial entry) ran just before, in this same call
            long end = Math.Min(to ?? file.Length - 1, file.Length - 1);
            return Task.FromResult(new RangeAnswer(RangeAnswerKind.Part, 206, file[(int)from..(int)(end + 1)], from, file.Length, null, end + 1 - from));
        }
        var fetcher = new PartialTextureFetcher(Range, (_, _) => Task.FromResult(AssetFetchResult.Ok(file)), view, firstRequest: _ => 2_000);
        var level = new ConcurrentBag<int>();
        Func<Guid, int, bool, CancellationToken, Task<AssetFetchResult>> fetchLevel = async (i, l, m, ct) =>
        {
            level.Add(Tid);
            var r = await fetcher.FetchLevelAsync(i, l, m, ct).ConfigureAwait(false);
            level.Add(Tid);   // the partial entry's encrypt and write (a synchronous range answer) ran here
            return r;
        };
        using var assets = new SessionAssets(new SharedAssetStore(), _ => true, (_, _) => Task.FromResult(AssetFetchResult.Ok(file)), fetchLevel: fetchLevel);

        var part = await main.Start(() => assets.GetLevelAsync(id, 4, more: false, CancellationToken.None));
        Assert.True(part.Partial);
        Assert.True(view.TryGetPartial("texture", id, out var kept, out _));
        Assert.Equal(file.AsSpan(0, kept.Length).ToArray(), kept);
        var whole = await main.Start(() => assets.GetLevelAsync(id, 0, more: false, CancellationToken.None));
        Assert.Equal(file, whole.Bytes.ToArray());

        Assert.NotEmpty(rangeThreads);
        Assert.NotEmpty(level);
        Assert.DoesNotContain(main.ThreadId, rangeThreads);
        Assert.DoesNotContain(main.ThreadId, level);
    }

    /// <summary>One session, many fetches at once from pool threads, ids repeated: each id fetched once (the in-flight
    /// dedupe, then the shared store), every result byte-identical to a single-threaded run, every content-store entry
    /// intact and readable after, nothing corrupt, no temp file left. 20 rounds of 1,500 requests over 48 ids.</summary>
    [Fact]
    public async Task Many_fetches_at_once_fetch_each_id_once_and_leave_the_content_store_intact()
    {
        var ids = Enumerable.Range(0, 48).Select(_ => Guid.NewGuid()).ToArray();
        int Size(Guid id) => 1_000 + (int)((uint)id.GetHashCode() % 120_000);

        // the single-threaded run: what each id's bytes are
        var expected = new Dictionary<Guid, byte[]>();
        using (var single = new SessionAssets(new SharedAssetStore(), _ => true, Store("single").View("grid", "s").Caching("mesh", (id, _) => Task.FromResult(AssetFetchResult.Ok(Data(id, Size(id)))))))
            foreach (var id in ids)
                expected[id] = (await single.GetAsync(id, CancellationToken.None)).Bytes.ToArray();

        for (int round = 0; round < 20; round++)
        {
            var store = Store("many-" + round);
            var network = new ConcurrentDictionary<Guid, int>();
            Func<Guid, CancellationToken, Task<AssetFetchResult>> transport = async (id, _) =>
            {
                network.AddOrUpdate(id, 1, (_, n) => n + 1);
                await Task.Yield();   // the answer comes later, on another thread, as the network's does
                return AssetFetchResult.Ok(Data(id, Size(id)));
            };
            using var assets = new SessionAssets(new SharedAssetStore(), _ => true, store.View("grid", "s").Caching("mesh", transport));
            var results = new ConcurrentBag<(Guid Id, AssetFetchResult R)>();
            await Parallel.ForEachAsync(Enumerable.Range(0, 1_500), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, _) =>
            {
                var id = ids[(i * 7 + round) % ids.Length];
                results.Add((id, await assets.GetAsync(id, CancellationToken.None)));
            });

            Assert.All(results, x => Assert.Equal(AssetFetchStatus.Ok, x.R.Status));
            Assert.All(results, x => Assert.True(expected[x.Id].AsSpan().SequenceEqual(x.R.Bytes.Span)));
            Assert.Equal(ids.Length, network.Count);
            Assert.All(network, kv => Assert.Equal(1, kv.Value));
            var reopened = Store("many-" + round).View("grid", "s");   // the same root and key, read cold
            foreach (var id in ids)
            {
                Assert.True(reopened.TryGet("mesh", id, out var back));
                Assert.Equal(expected[id], back);
            }
            Assert.Equal(0, store.CorruptEntriesRemoved);
            Assert.Empty(Directory.GetFiles(store.Root, "*.tmp"));
        }
    }

    /// <summary>Concurrent reads and writes of the same entries: four sessions, each with its own shared store, over one
    /// content store, so every request goes to the content store and the same ids are read and written at once. No read
    /// ever sees a torn file (nothing corrupt, every result exact). 20 rounds of 4 x 400 requests over 12 ids.</summary>
    [Fact]
    public async Task Concurrent_reads_and_writes_of_the_same_entry_never_tear()
    {
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        for (int round = 0; round < 20; round++)
        {
            var store = Store("same-" + round);
            var sessions = Enumerable.Range(0, 4).Select(s => new SessionAssets(new SharedAssetStore(), _ => true,
                store.View("grid", "s" + s).Caching("mesh", async (id, _) => { await Task.Yield(); return AssetFetchResult.Ok(Data(id, 64_000)); }))).ToArray();
            try
            {
                var bad = 0;
                await Parallel.ForEachAsync(Enumerable.Range(0, 1_600), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, _) =>
                {
                    var id = ids[i % ids.Length];
                    var r = await sessions[(i / ids.Length) % sessions.Length].GetAsync(id, CancellationToken.None);
                    if (r.Status != AssetFetchStatus.Ok || !r.Bytes.Span.SequenceEqual(Data(id, 64_000))) Interlocked.Increment(ref bad);
                });
                Assert.Equal(0, bad);
                Assert.Equal(0, store.CorruptEntriesRemoved);
                Assert.Empty(Directory.GetFiles(store.Root, "*.tmp"));
                foreach (var id in ids)
                {
                    Assert.True(store.View("grid", "x").TryGet("mesh", id, out var back));
                    Assert.Equal(Data(id, 64_000), back);
                }
            }
            finally { foreach (var s in sessions) s.Dispose(); }
        }
    }

    /// <summary>Partial entries written at once by several threads: the longest prefix stays (a shorter one never replaces
    /// it). 50 rounds of 16 writers.</summary>
    [Fact]
    public async Task Concurrent_partial_writes_keep_the_longest_prefix()
    {
        var store = Store("partial");
        var view = store.View("grid", "s");
        for (int round = 0; round < 50; round++)
        {
            var id = Guid.NewGuid();
            var file = Data(id, 160_000);
            var lengths = Enumerable.Range(1, 16).Select(k => k * 10_000).OrderBy(_ => Random.Shared.Next()).ToArray();
            using var go = new ManualResetEventSlim();
            var writers = lengths.Select(n => Task.Run(() => { go.Wait(); view.PutPartial("texture", id, file.AsSpan(0, n), file.Length); })).ToArray();
            go.Set();
            await Task.WhenAll(writers);
            Assert.True(view.TryGetPartial("texture", id, out var kept, out var total));
            Assert.Equal(file.Length, total);
            Assert.Equal(lengths.Max(), kept.Length);
            Assert.Equal(file.AsSpan(0, kept.Length).ToArray(), kept);
        }
        Assert.Equal(0, store.CorruptEntriesRemoved);
    }
}
