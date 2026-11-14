// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Core.Social;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The block list on disk, one file per account, as the official viewer keeps its cached mute list (indra/newview/llmutelist.cpp,
/// requestFromServer, cache, loadFromFile, saveToFile): the file is read at login so blocks apply before the grid answers, the grid's
/// list replaces it when it arrives, and every change writes it. A fake transport and a unique temp folder per member; no grid entry is
/// ever added or removed. Every member builds its own objects and folder, so the class runs in parallel.
/// </summary>
public class MuteCacheTests
{
    private static readonly SimRef Here = new(1099511628032256UL, "Example Region");
    private static readonly Guid Self = Guid.Parse("11111111-2222-3333-4444-555555555555");   // the fake transport's login agent
    private static readonly Guid Pest = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000abcd");
    private static readonly Guid Thing = Guid.Parse("0b0b0b0b-0000-4000-8000-000000000001");
    private const string PestName = "Pest Resident";

    private static MessageBody Text(string t) => new(t, MessageChannel.LocalChat);

    private static (ViewerSession Session, FakeTransport Fake, ListSink Log) InWorld(string? directory, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var fake = new FakeTransport();
        var log = new ListSink();
        var opts = new SessionOptions { LoginUri = "http://grid.invalid/", MuteCacheDirectory = directory, MuteListDelay = delay ?? Task.Delay };
        var session = new ViewerSession(opts, fake, new CoreLog(log, "test"));
        Assert.True(session.LoginAsync(new LoginCredentials("Test", "User", new Secret("pw-not-real", SensitiveKind.PasswordDigest)), CancellationToken.None).Result.Ok);
        fake.DriveToInWorld(Here);
        return (session, fake, log);
    }

    private static void Say(FakeTransport fake, Guid from) =>
        fake.Raise(new ChatReceived(Here, from, from, PestName, ChatSource.Agent, ChatLineType.Say, ChatAudible.Fully, Vector3.Zero, Text("hi")));

    // ---------------- the file ----------------

    [Fact]
    public void The_file_is_named_for_the_account_with_the_official_cache_extension()
    {
        string dir = TestTemp.NewFolderPath("mute-name");
        Assert.Equal(Path.Combine(dir, Self.ToString() + ".cached_mute"), MuteCache.FileFor(dir, Self));
    }

    [Fact]
    public void Entries_written_are_read_back_exactly_in_the_official_line_shape()
    {
        string dir = TestTemp.NewFolder("mute-roundtrip");
        try
        {
            string path = MuteCache.FileFor(dir, Self);
            var entries = new[]
            {
                new MuteEntry(MuteKind.Resident, Pest, PestName, 0),
                new MuteEntry(MuteKind.Object, Thing, "Noisy Box", MuteFlag.Particles),
                new MuteEntry(MuteKind.ByName, Guid.Empty, "Spam Bot", MuteFlag.TextChat),
            };
            Assert.True(MuteCache.Write(path, entries));
            string[] lines = File.ReadAllLines(path);
            Assert.Equal($"1 {Pest} {PestName}|0", lines[0]);                  // type, id, name, '|', flags
            Assert.Equal($"2 {Thing} Noisy Box|4", lines[1]);
            Assert.Equal($"0 {Guid.Empty} Spam Bot|1", lines[2]);
            var back = MuteCache.Read(path, out int skipped);
            Assert.NotNull(back);
            Assert.Equal(0, skipped);
            Assert.Equal(entries, back);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));                    // written by temp file and rename, none left over
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void A_file_with_a_bad_line_keeps_the_good_lines_and_counts_the_bad_one()
    {
        string dir = TestTemp.NewFolder("mute-badline");
        try
        {
            string path = MuteCache.FileFor(dir, Self);
            File.WriteAllText(path, $"1 {Pest} {PestName}|0\r\nthis is not an entry\r\n2 not-a-guid Box|0\r\n2 {Thing} Noisy Box|0\r\n");
            var back = MuteCache.Read(path, out int skipped);
            Assert.NotNull(back);
            Assert.Equal(2, skipped);
            Assert.Equal([new MuteEntry(MuteKind.Resident, Pest, PestName, 0), new MuteEntry(MuteKind.Object, Thing, "Noisy Box", 0)], back);
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void A_missing_file_reads_as_nothing_not_as_an_empty_list()
    {
        string dir = TestTemp.NewFolderPath("mute-missing");
        Assert.Null(MuteCache.Read(MuteCache.FileFor(dir, Self), out int skipped));
        Assert.Equal(0, skipped);
    }

    // ---------------- the session ----------------

    [Fact]
    public void The_cached_list_applies_before_the_grid_answers()
    {
        string dir = TestTemp.NewFolder("mute-before");
        try
        {
            MuteCache.Write(MuteCache.FileFor(dir, Self), [new MuteEntry(MuteKind.Resident, Pest, PestName, 0)]);
            var (session, fake, _) = InWorld(dir);
            Assert.NotEqual(MuteListState.Loaded, session.Mutes.State);   // the grid has not answered
            Assert.True(session.Mutes.IsMuted(Pest, null));
            Say(fake, Pest);
            Assert.Equal(1, session.MutedDrops);
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void The_grids_list_replaces_the_cached_one_and_is_written_to_the_file()
    {
        string dir = TestTemp.NewFolder("mute-replace");
        try
        {
            string path = MuteCache.FileFor(dir, Self);
            MuteCache.Write(path, [new MuteEntry(MuteKind.Resident, Pest, PestName, 0)]);
            var (session, fake, _) = InWorld(dir);
            fake.Raise(new MuteListReceived([new MuteEntry(MuteKind.Object, Thing, "Noisy Box", 0)]));
            Assert.Equal(MuteListState.Loaded, session.Mutes.State);
            Assert.False(session.Mutes.IsMuted(Pest, null));
            Assert.True(session.Mutes.IsMuted(Thing, null));
            Assert.Equal([new MuteEntry(MuteKind.Object, Thing, "Noisy Box", 0)], MuteCache.Read(path, out _));
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void An_empty_list_from_the_grid_empties_the_file()
    {
        string dir = TestTemp.NewFolder("mute-empty");
        try
        {
            string path = MuteCache.FileFor(dir, Self);
            MuteCache.Write(path, [new MuteEntry(MuteKind.Resident, Pest, PestName, 0)]);
            var (session, fake, _) = InWorld(dir);
            fake.Raise(new MuteListReceived([]));
            Assert.Equal(0, session.Mutes.Count);
            Assert.Empty(MuteCache.Read(path, out _) ?? throw new Xunit.Sdk.XunitException("the file was removed instead of emptied"));
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void A_block_and_an_unblock_each_write_the_file_and_a_new_session_starts_from_it()
    {
        string dir = TestTemp.NewFolder("mute-restart");
        try
        {
            string path = MuteCache.FileFor(dir, Self);
            var (first, _, _) = InWorld(dir);
            Assert.True(first.Block(MuteKind.Resident, Pest, PestName).Done);
            Assert.Equal([new MuteEntry(MuteKind.Resident, Pest, PestName, 0)], MuteCache.Read(path, out _));
            first.Dispose();

            var (second, fake, _) = InWorld(dir);              // a restart: the grid has not answered yet
            Assert.True(second.Mutes.IsMuted(Pest, null));
            Say(fake, Pest);
            Assert.Equal(1, second.MutedDrops);
            Assert.True(second.Unblock(Pest, PestName));
            Assert.Empty(MuteCache.Read(path, out _) ?? throw new Xunit.Sdk.XunitException("the file was removed instead of emptied"));
            second.Dispose();

            var (third, _, _) = InWorld(dir);
            Assert.False(third.Mutes.IsMuted(Pest, null));
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void A_grid_that_never_answers_leaves_the_cached_entries_in_force()
    {
        string dir = TestTemp.NewFolder("mute-unavailable");
        try
        {
            MuteCache.Write(MuteCache.FileFor(dir, Self), [new MuteEntry(MuteKind.Resident, Pest, PestName, 0)]);
            var manual = new ManualDelay();
            var (session, _, _) = InWorld(dir, manual.Delay);
            manual.ElapseLast();
            SpinWait.SpinUntil(() => session.Mutes.State == MuteListState.Unavailable, TimeSpan.FromSeconds(60));
            Assert.Equal(MuteListState.Unavailable, session.Mutes.State);
            Assert.True(session.Mutes.IsMuted(Pest, null));
            Assert.Single(MuteCache.Read(MuteCache.FileFor(dir, Self), out _)!);   // the wait running out does not wipe the file
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void A_broken_file_is_ignored_with_one_log_line_and_the_list_still_works()
    {
        string dir = TestTemp.NewFolder("mute-broken");
        try
        {
            string path = MuteCache.FileFor(dir, Self);
            File.WriteAllBytes(path, [0xFF, 0xFE, 0x00, 0x01, 0x02, 0x03, 0x00, 0x00, 0x7F]);
            var (session, fake, log) = InWorld(dir);
            Assert.Equal(0, session.Mutes.Count);
            Assert.Contains("mute.cache-unreadable", log.Text);
            Assert.DoesNotContain(PestName, log.Text);
            Assert.True(session.Block(MuteKind.Resident, Pest, PestName).Done);   // blocking still works, and rewrites the file whole
            Assert.Equal([new MuteEntry(MuteKind.Resident, Pest, PestName, 0)], MuteCache.Read(path, out _));
            fake.Raise(new MuteListReceived([]));
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void Names_never_reach_the_log_when_the_cache_is_read_or_written()
    {
        string dir = TestTemp.NewFolder("mute-log");
        try
        {
            MuteCache.Write(MuteCache.FileFor(dir, Self), [new MuteEntry(MuteKind.Resident, Pest, PestName, 0)]);
            var (session, fake, log) = InWorld(dir);
            fake.Raise(new MuteListReceived([new MuteEntry(MuteKind.Object, Thing, "Noisy Box", 0)]));
            session.Block(MuteKind.Resident, Pest, PestName);
            Assert.DoesNotContain(PestName, log.Text);
            Assert.DoesNotContain("Noisy Box", log.Text);
        }
        finally { TestTemp.Delete(dir); }
    }

    [Fact]
    public void With_no_cache_folder_nothing_is_read_or_written()
    {
        var (session, _, _) = InWorld(null);
        Assert.True(session.Block(MuteKind.Resident, Pest, PestName).Done);
        Assert.Equal(1, session.Mutes.Count);   // memory only, as before
        Assert.Empty(Directory.GetFiles(Directory.GetCurrentDirectory(), "*" + MuteCache.Extension));   // and no file in the working folder either
    }
}
