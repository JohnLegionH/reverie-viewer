// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.Cryptography;
using Viewer.Core.Assets;
using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>The content store's key under DPAPI; legacy import; an unreadable key resets, never crashes. Sandbox directories only.</summary>
public class KeyProtectorTests : IDisposable
{
    private readonly List<string> _roots = new();
    private string NewRoot() { string r = TestTemp.NewFolderPath("key"); _roots.Add(r); return r; }
    public void Dispose() { foreach (var r in _roots) TestTemp.Delete(r); }   // only this instance's own folders
    private static readonly byte[] Data = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    private sealed class Refusing : IKeyProtector
    {
        public string Suffix => ".dpapi";
        public byte[] Protect(byte[] key) => [.. key, 0xEE];
        public byte[] Unprotect(byte[] blob) => throw new CryptographicException("cannot unprotect here");
    }

    [Fact]
    public void Windows_uses_dpapi_and_the_key_survives_a_restart()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        var id = Guid.NewGuid();
        new ContentStore(new() { Root = root }).View("g", "s").Put("texture", id, Data);
        Assert.True(File.Exists(Path.Combine(root, "store.key.dpapi")));
        Assert.False(File.Exists(Path.Combine(root, "store.key")));                     // no raw key on disk
        Assert.NotEqual(32, new FileInfo(Path.Combine(root, "store.key.dpapi")).Length); // a DPAPI blob, not the key
        Assert.True(new ContentStore(new() { Root = root }).View("g", "s").TryGet("texture", id, out var back));
        Assert.Equal(Data, back);
        Assert.IsType<DpapiKeyProtector>(KeyProtectors.ForThisPlatform());
    }

    [Fact]
    public void A_legacy_owner_only_key_is_imported_reprotected_and_its_file_removed()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        var id = Guid.NewGuid();
        new ContentStore(new() { Root = root, KeyProtector = new OwnerOnlyFileKeyProtector() }).View("g", "s").Put("texture", id, Data);   // layout
        Assert.True(File.Exists(Path.Combine(root, "store.key")));
        var sink = new ListSink();
        var s = new ContentStore(new() { Root = root, Log = new CoreLog(sink, "t") });
        Assert.True(s.View("g", "s").TryGet("texture", id, out var back));               // same key: old entries still read
        Assert.Equal(Data, back);
        Assert.False(File.Exists(Path.Combine(root, "store.key")));                      // the product removed its own legacy file
        Assert.True(File.Exists(Path.Combine(root, "store.key.dpapi")));
        Assert.Contains(sink.Entries, e => e.Event == "content-store.key-imported");
    }

    [Fact]
    public void An_unreadable_key_means_a_new_key_and_an_empty_cache_logged_never_a_crash()
    {
        string root = NewRoot();
        var id = Guid.NewGuid();
        new ContentStore(new() { Root = root, KeyProtector = new Refusing() }).View("g", "s").Put("texture", id, Data);   // key written
        File.WriteAllText(Path.Combine(root, "someone-elses.txt"), "not ours");
        var sink = new ListSink();
        var s = new ContentStore(new() { Root = root, KeyProtector = new Refusing(), Log = new CoreLog(sink, "t") });   // cannot unprotect it now
        Assert.Contains(sink.Entries, e => e.Event == "content-store.key-reset");
        Assert.Equal(0, s.EntryCount);                                                   // the unreadable entries went
        Assert.True(File.Exists(Path.Combine(root, "someone-elses.txt")));                // nothing else did
        Assert.False(s.View("g", "s").TryGet("texture", id, out _));
        s.View("g", "s").Put("texture", id, Data);
        Assert.True(s.View("g", "s").TryGet("texture", id, out _));
    }

    [Fact]
    public void A_damaged_dpapi_blob_resets_too()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = NewRoot();
        new ContentStore(new() { Root = root }).View("g", "s").Put("texture", Guid.NewGuid(), Data);
        var blob = File.ReadAllBytes(Path.Combine(root, "store.key.dpapi"));
        blob[blob.Length / 2] ^= 0x55;
        File.WriteAllBytes(Path.Combine(root, "store.key.dpapi"), blob);
        var sink = new ListSink();
        var s = new ContentStore(new() { Root = root, Log = new CoreLog(sink, "t") });
        Assert.Contains(sink.Entries, e => e.Event == "content-store.key-reset");
        Assert.Equal(0, s.EntryCount);
    }
}
