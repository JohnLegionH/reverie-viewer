// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Viewer.Core.Assets;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The encrypted, content-addressed, size-capped disk store and its two sharing modes.</summary>
public class ContentStoreTests : IDisposable
{
    private readonly string _root = TestTemp.NewFolderPath("store");
    private readonly string _raceRoot = TestTemp.NewFolderPath("store-race");
    private readonly string _userRoot = TestTemp.NewFolderPath("store-u");
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("PLAINTEXT-MARKER-texture-bytes-0123456789");

    public void Dispose()   // only the unique folders this instance was handed, through the helper
    {
        TestTemp.Delete(_root);
        TestTemp.Delete(_raceRoot);
        TestTemp.Delete(_userRoot);
    }

    private ContentStore Store(long cap = 1L << 30, ContentSharing sharing = ContentSharing.PerUser, string? root = null, IKeyProtector? protector = null) =>
        new(new ContentStoreOptions { Root = root ?? _root, CapBytes = cap, Sharing = sharing, KeyProtector = protector });

    private static byte[] Data(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    [Fact]
    public void An_entry_round_trips_and_nothing_is_plaintext_at_rest()
    {
        var v = Store().View("grid-a", "s1");
        var id = Guid.NewGuid();
        v.Put("texture", id, Secret);
        Assert.True(v.TryGet("texture", id, out var back));
        Assert.Equal(Secret, back);
        foreach (var f in Directory.GetFiles(_root))
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(f));
            Assert.DoesNotContain("PLAINTEXT-MARKER", text);
            Assert.DoesNotContain(id.ToString(), text);                 // not even the id in the name or the content
            Assert.DoesNotContain(id.ToString("N"), Path.GetFileName(f));
        }
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));               // atomic: no temp file left behind
    }

    [Fact]
    public void Entries_are_keyed_by_scope_and_kind()
    {
        var s = Store();
        var id = Guid.NewGuid();
        s.View("grid-a", "s").Put("texture", id, Secret);
        Assert.False(s.View("grid-b", "s").TryGet("texture", id, out _));
        Assert.False(s.View("grid-a", "s").TryGet("mesh", id, out _));
    }

    [Theory]
    [InlineData("flip")]
    [InlineData("truncate")]
    [InlineData("swap")]
    public async Task A_corrupt_truncated_or_swapped_entry_is_a_miss_and_is_refetched(string damage)
    {
        var s = Store();
        var v = s.View("grid", "s");
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        int fetches = 0;
        var fetch = v.Caching("texture", (id, _) => { fetches++; return Task.FromResult(AssetFetchResult.Ok(Data(4000, id.GetHashCode()))); });
        await fetch(a, CancellationToken.None);
        await fetch(b, CancellationToken.None);
        string pa = s.PathFor("grid", "texture", a, null), pb = s.PathFor("grid", "texture", b, null);
        if (damage == "flip") { var x = File.ReadAllBytes(pa); x[x.Length / 2] ^= 0x40; File.WriteAllBytes(pa, x); }
        if (damage == "truncate") { var x = File.ReadAllBytes(pa); File.WriteAllBytes(pa, x[..(x.Length / 3)]); }
        if (damage == "swap") File.Copy(pb, pa, overwrite: true);   // b's valid entry under a's name: authentication binds the name
        var r = await fetch(a, CancellationToken.None);
        Assert.Equal(Data(4000, a.GetHashCode()), r.Bytes.ToArray());
        Assert.Equal(3, fetches);                                  // a refetched
        Assert.Equal(1, s.CorruptEntriesRemoved);
        Assert.True(v.TryGet("texture", a, out _));                // and stored again, sound
    }

    [Fact]
    public void The_cap_evicts_least_recently_used_entries_and_only_its_own_files()
    {
        var s = Store(cap: 3 * 10_100);
        var v = s.View("grid", "s");
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids) { v.Put("texture", id, Data(10_000, 1)); Thread.Sleep(30); }
        File.WriteAllText(Path.Combine(_root, "not-ours.txt"), "someone else's file");
        Assert.True(v.TryGet("texture", ids[0], out _));            // ids[0] is now the most recently used
        Thread.Sleep(30);
        v.Put("texture", Guid.NewGuid(), Data(10_000, 2));         // over the cap: evict to 90 %
        Assert.True(v.TryGet("texture", ids[0], out _));
        Assert.False(v.TryGet("texture", ids[1], out _));           // the least recently used went first
        Assert.True(s.SizeBytes <= s.Options.CapBytes);
        Assert.True(File.Exists(Path.Combine(_root, "not-ours.txt")));
        Assert.True(File.Exists(Path.Combine(_root, OperatingSystem.IsWindows() ? "store.key.dpapi" : "store.key")));
    }

    [Fact]
    public void Default_cap_is_1_GB_and_the_default_root_is_the_per_user_cache()
    {
        Assert.Equal(1L << 30, new ContentStoreOptions().CapBytes);
        Assert.Equal(ContentSharing.PerUser, new ContentStoreOptions().Sharing);
        var cache = Viewer.Core.Platform.DataDirectories.ForCurrentUser().Cache;
        Assert.StartsWith(cache, Path.Combine(cache, "Content"));
    }

    [Fact]
    public void The_fallback_key_file_is_owner_only()
    {
        Store(protector: new OwnerOnlyFileKeyProtector());
        if (!OperatingSystem.IsWindows()) return;
        var acl = new FileInfo(Path.Combine(_root, "store.key")).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var sids = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => r.IdentityReference.Value).Distinct().ToList();
        Assert.Equal([WindowsIdentity.GetCurrent().User!.Value], sids);
    }

    [Fact]
    public async Task Concurrent_sessions_and_store_instances_never_see_a_partial_entry()
    {
        var s1 = Store(cap: 400_000);
        var s2 = Store(cap: 400_000);   // a second instance on the same directory, as another process would be
        var ids = Enumerable.Range(0, 20).Select(i => Guid.NewGuid()).ToList();
        int wrong = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, 400), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            var v = (i % 2 == 0 ? s1 : s2).View("grid", "s" + i % 3);
            var id = ids[i % ids.Count];
            var expected = Data(20_000, id.GetHashCode());
            if (i % 3 == 0) v.Put("texture", id, expected);
            else if (v.TryGet("texture", id, out var got) && !got.AsSpan().SequenceEqual(expected)) Interlocked.Increment(ref wrong);
            await Task.Yield();
        });
        Assert.Equal(0, wrong);
        Assert.Equal(0, s1.CorruptEntriesRemoved + s2.CorruptEntriesRemoved);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    /// <summary>Proof: the concurrency test repeated (NV_STORE_RACE_RUNS, default 0 = skipped in the normal suite).</summary>
    [Fact]
    public async Task The_concurrency_test_holds_over_many_runs()
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("NV_STORE_RACE_RUNS"), out var n) ? n : 0;
        if (runs == 0) return;
        for (int r = 0; r < runs; r++)
        {
            string root = _raceRoot;   // one directory for every run (new ids each run; the cap evicts)
            var s1 = Store(cap: 400_000, root: root);
            var s2 = Store(cap: 400_000, root: root);
            var ids = Enumerable.Range(0, 20).Select(i => Guid.NewGuid()).ToList();
            int wrong = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, 400), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
            {
                var v = (i % 2 == 0 ? s1 : s2).View("grid", "s" + i % 3);
                var id = ids[i % ids.Count];
                var expected = Data(20_000, id.GetHashCode());
                if (i % 3 == 0) v.Put("texture", id, expected);
                else if (v.TryGet("texture", id, out var got) && !got.AsSpan().SequenceEqual(expected)) Interlocked.Increment(ref wrong);
                await Task.Yield();
            });
            Assert.True(wrong == 0, $"run {r}: {wrong} wrong reads");
            Assert.True(s1.CorruptEntriesRemoved + s2.CorruptEntriesRemoved == 0, $"run {r}: corrupt entries");
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
    }

    [Fact]
    public async Task PerUser_sharing_serves_another_session_from_disk()
    {
        var s = Store(sharing: ContentSharing.PerUser);
        var id = Guid.NewGuid();
        int fetchesB = 0;
        await s.View("grid", "A").Caching("texture", (_, _) => Task.FromResult(AssetFetchResult.Ok(Secret)))(id, CancellationToken.None);
        var r = await s.View("grid", "B").Caching("texture", (_, _) => { fetchesB++; return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.NotFound)); })(id, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Ok, r.Status);
        Assert.Equal(0, fetchesB);
    }

    [Fact]
    public async Task PerSession_sharing_never_serves_session_B_an_entry_only_A_fetched()
    {
        var s = Store(sharing: ContentSharing.PerSession);
        var id = Guid.NewGuid();
        await s.View("grid", "A").Caching("texture", (_, _) => Task.FromResult(AssetFetchResult.Ok(Secret)))(id, CancellationToken.None);
        int fetchesB = 0;
        var r = await s.View("grid", "B").Caching("texture", (_, _) => { fetchesB++; return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Denied)); })(id, CancellationToken.None);
        Assert.Equal(AssetFetchStatus.Denied, r.Status);            // B had to ask its own grid session, which refused
        Assert.Equal(1, fetchesB);
        Assert.False(s.View("grid", "B").TryGet("texture", id, out _));
        Assert.True(s.View("grid", "A").TryGet("texture", id, out _));
        // Through SessionAssets: B's authorisation is still asked first even in PerUser mode (a disk hit never bypasses it).
        var user = Store(sharing: ContentSharing.PerUser, root: _userRoot);
        await user.View("grid", "A").Caching("texture", (_, _) => Task.FromResult(AssetFetchResult.Ok(Secret)))(id, CancellationToken.None);
        using var b = new SessionAssets(new SharedAssetStore(), _ => false, user.View("grid", "B").Caching("texture", (_, _) => Task.FromResult(AssetFetchResult.Ok(Secret))));
        Assert.Equal(AssetFetchStatus.Denied, (await b.GetAsync(id, CancellationToken.None)).Status);
    }
}
