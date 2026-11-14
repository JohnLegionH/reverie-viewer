// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The settings service's preview layer, which lets a Preferences page change a value so that everything listening restyles or
/// re-applies at once, while the file is written only when the person saves. Plain .NET; every member builds its own objects, so the
/// class runs in parallel.
/// </summary>
public class SettingsPreviewTests
{
    private sealed class MemoryStore(string? text = null) : ISettingsStore
    {
        public string? Text = text;
        public int Writes;
        public string? Read() => Text;
        public bool Write(string text) { Text = text; Writes++; return true; }
    }

    private static readonly Setting<int> Size = new("test_size", 10, v => Math.Clamp(v, 1, 20));
    private static readonly Setting<bool> Flag = new("test_flag", false, v => v);

    [Fact]
    public void A_preview_is_seen_at_once_and_announced_but_not_written()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        var heard = new List<string>();
        s.Changed += heard.Add;

        Assert.True(s.SetPreview(Size, 15));

        Assert.Equal(15, s.Get(Size));
        Assert.Equal(["test_size"], heard);
        Assert.Equal(0, store.Writes);
        Assert.Equal(["test_size"], s.PendingKeys);
    }

    [Fact]
    public void A_preview_goes_through_the_settings_own_rule()
    {
        var s = new SettingsService(new MemoryStore());
        Assert.True(s.SetPreview(Size, 99));
        Assert.Equal(20, s.Get(Size));
        var odd = new Setting<int>("test_odd", 1, v => v % 2 == 1 ? v : null);
        Assert.False(s.SetPreview(odd, 2));
        Assert.Equal(1, s.Get(odd));
        Assert.DoesNotContain("test_odd", s.PendingKeys);
    }

    [Fact]
    public void Commit_writes_the_previewed_keys_and_ends_their_preview()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        s.SetPreview(Size, 12);

        Assert.True(s.Commit(["test_size"]));

        Assert.Equal(1, store.Writes);
        Assert.Empty(s.PendingKeys);
        Assert.Equal(12, new SettingsService(new MemoryStore(store.Text)).Get(Size));
    }

    [Fact]
    public void Commit_writes_only_the_keys_named_and_leaves_the_other_previews_pending()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        s.SetPreview(Size, 12);
        s.SetPreview(Flag, true);

        s.Commit(["test_flag"]);

        var reread = new SettingsService(new MemoryStore(store.Text));
        Assert.True(reread.Get(Flag));
        Assert.Equal(10, reread.Get(Size));
        Assert.Equal(["test_size"], s.PendingKeys);
        Assert.Equal(12, s.Get(Size));
    }

    [Fact]
    public void Rollback_puts_the_saved_value_back_and_announces_it()
    {
        var s = new SettingsService(new MemoryStore("""{ "test_size": 7 }"""));
        s.SetPreview(Size, 15);
        var heard = new List<string>();
        s.Changed += heard.Add;

        s.Rollback(["test_size"]);

        Assert.Equal(7, s.Get(Size));
        Assert.Equal(["test_size"], heard);
        Assert.Empty(s.PendingKeys);
    }

    [Fact]
    public void Rollback_of_a_value_that_was_never_saved_goes_back_to_the_default_and_writes_nothing()
    {
        var store = new MemoryStore();
        var s = new SettingsService(store);
        s.SetPreview(Size, 15);
        Assert.Equal(15, s.Get(Size));

        s.Rollback(["test_size"]);

        Assert.Equal(10, s.Get(Size));
        Assert.Equal(0, store.Writes);
        Assert.DoesNotContain("test_size", store.Text ?? "");
    }

    [Fact]
    public void A_second_preview_keeps_the_first_saved_value_to_go_back_to()
    {
        var s = new SettingsService(new MemoryStore("""{ "test_size": 7 }"""));
        s.SetPreview(Size, 12);
        s.SetPreview(Size, 16);
        Assert.Equal(16, s.Get(Size));

        s.Rollback(["test_size"]);

        Assert.Equal(7, s.Get(Size));
    }

    [Fact]
    public void Text_values_preview_commit_and_roll_back_the_same_way()
    {
        var store = new MemoryStore("""{ "mode": "windowed" }""");
        var s = new SettingsService(store);
        var heard = new List<string>();
        s.Changed += heard.Add;

        Assert.True(s.SetTextPreview("mode", "fullscreen"));
        Assert.Equal("fullscreen", s.GetText("mode"));
        Assert.Equal(0, store.Writes);
        Assert.Equal(["mode"], heard);

        s.Rollback(["mode"]);
        Assert.Equal("windowed", s.GetText("mode"));

        s.SetTextPreview("mode", "borderless");
        Assert.True(s.Commit(["mode"]));
        Assert.Equal("borderless", new SettingsService(new MemoryStore(store.Text)).GetText("mode"));
    }

    [Fact]
    public void Other_keys_in_the_file_survive_a_commit()
    {
        var store = new MemoryStore("""{ "login_first": "Test", "test_size": 7 }""");
        var s = new SettingsService(store);
        s.SetPreview(Size, 9);
        s.Commit(["test_size"]);
        var reread = new SettingsService(new MemoryStore(store.Text));
        Assert.Equal(9, reread.Get(Size));
        Assert.Equal("Test", reread.GetText("login_first"));
    }
}
