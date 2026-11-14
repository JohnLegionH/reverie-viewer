// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Assets;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Clear cache: removes the store's own entry files from its own folder and nothing else. A file the store did not make, and a
/// folder inside the store's folder, are left exactly as they were, and the store works afterwards. Every member uses its own unique
/// folder, so the class runs in parallel.
/// </summary>
public class ContentStoreClearTests : IDisposable
{
    private readonly string _root = TestTemp.NewFolderPath("store-clear");

    public void Dispose() => TestTemp.Delete(_root);   // only the unique folder this instance was handed, through the helper

    private ContentStore Store() => new(new ContentStoreOptions { Root = _root });

    [Fact]
    public void Clear_removes_every_entry_and_says_how_many()
    {
        var store = Store();
        var view = store.View("grid-a", "s1");
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            view.Put("texture", id, new byte[] { 1, 2, 3, 4 });
        Assert.Equal(3, store.EntryCount);

        Assert.Equal(3, store.Clear());

        Assert.Equal(0, store.EntryCount);
        foreach (var id in ids)
            Assert.False(view.TryGet("texture", id, out _));
    }

    [Fact]
    public void Clear_leaves_a_file_it_did_not_make_and_a_folder_inside_its_own_alone()
    {
        var store = Store();
        var view = store.View("grid-a", "s1");
        view.Put("texture", Guid.NewGuid(), new byte[] { 9 });
        string note = Path.Combine(_root, "notes.txt");
        string sub = Path.Combine(_root, "other");
        string inner = Path.Combine(sub, "inner.nvc");
        File.WriteAllText(note, "mine");
        Directory.CreateDirectory(sub);
        File.WriteAllText(inner, "not an entry of this store");

        Assert.Equal(1, store.Clear());

        Assert.True(File.Exists(note));
        Assert.Equal("mine", File.ReadAllText(note));
        Assert.True(File.Exists(inner));
        Assert.True(Directory.Exists(sub));
    }

    [Fact]
    public void The_store_still_works_after_it_is_cleared()
    {
        var store = Store();
        var view = store.View("grid-a", "s1");
        view.Put("texture", Guid.NewGuid(), new byte[] { 1 });
        store.Clear();

        var id = Guid.NewGuid();
        view.Put("mesh", id, new byte[] { 5, 6, 7 });

        Assert.True(view.TryGet("mesh", id, out var back));
        Assert.Equal(new byte[] { 5, 6, 7 }, back);
        Assert.Equal(1, store.EntryCount);
    }

    [Fact]
    public void Clearing_an_empty_store_removes_nothing()
    {
        var store = Store();
        Assert.Equal(0, store.Clear());
        store.View("grid-a", "s1").Put("texture", Guid.NewGuid(), new byte[] { 1 });
        Assert.Equal(1, store.Clear());   // the control: the same call does remove an entry that is there
    }
}
