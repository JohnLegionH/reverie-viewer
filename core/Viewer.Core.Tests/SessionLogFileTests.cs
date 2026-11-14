// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Viewer.Core.Diagnostics;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The session log file, kept the way the official viewer keeps its log: one file for this session, the one before it kept beside it as
/// the old log, and nothing else touched. Entries are the redacted lines the console shows. A folder that cannot be used costs the viewer
/// nothing: logging never throws.
/// </summary>
public class SessionLogFileTests
{
    private static LogEntry Entry(string evt, LogLevel level = LogLevel.Info) =>
        new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), level, "viewer", evt, [new("n", "1")]);

    private static string ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [Fact]
    public void A_session_writes_its_lines_to_the_log_file_which_can_be_read_while_it_is_open()
    {
        string folder = TestTemp.NewFolderPath("logfile");
        using var log = SessionLogFile.Open(folder, continueCurrent: false);
        Assert.Equal(Path.Combine(folder, SessionLogFile.CurrentName), log.Path);
        log.Write(Entry("session.start"));
        log.Write(Entry("session.stage"));
        string text = ReadShared(log.Path!);
        Assert.Equal(Entry("session.start").Format() + Environment.NewLine + Entry("session.stage").Format() + Environment.NewLine, text);
    }

    [Fact]
    public void The_next_session_keeps_the_one_before_it_as_the_old_log_and_only_that_one()
    {
        string folder = TestTemp.NewFolderPath("logfile");
        using (var first = SessionLogFile.Open(folder, false)) first.Write(Entry("first"));
        using (var second = SessionLogFile.Open(folder, false)) second.Write(Entry("second"));
        Assert.Contains("first", ReadShared(Path.Combine(folder, SessionLogFile.OldName)));
        Assert.DoesNotContain("second", ReadShared(Path.Combine(folder, SessionLogFile.OldName)));
        using (var third = SessionLogFile.Open(folder, false)) third.Write(Entry("third"));
        string old = ReadShared(Path.Combine(folder, SessionLogFile.OldName));
        Assert.Contains("second", old);
        Assert.DoesNotContain("first", old);
        string current = ReadShared(Path.Combine(folder, SessionLogFile.CurrentName));
        Assert.Contains("third", current);
        Assert.DoesNotContain("second", current);
        Assert.Equal([SessionLogFile.CurrentName, SessionLogFile.OldName], Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void A_restart_of_the_same_run_goes_on_in_the_same_file_and_does_not_push_the_old_log_out()
    {
        string folder = TestTemp.NewFolderPath("logfile");
        using (var first = SessionLogFile.Open(folder, false)) first.Write(Entry("before"));
        using (var again = SessionLogFile.Open(folder, true)) again.Write(Entry("after"));
        string current = ReadShared(Path.Combine(folder, SessionLogFile.CurrentName));
        Assert.Contains("before", current);
        Assert.Contains("after", current);
        Assert.False(File.Exists(Path.Combine(folder, SessionLogFile.OldName)));
    }

    [Fact]
    public void Other_files_in_the_folder_are_never_touched()
    {
        string folder = TestTemp.NewFolder("logfile");
        string other = Path.Combine(folder, "notes.txt");
        File.WriteAllText(other, "keep");
        using (var a = SessionLogFile.Open(folder, false)) a.Write(Entry("a"));
        using (var b = SessionLogFile.Open(folder, false)) b.Write(Entry("b"));
        Assert.Equal("keep", File.ReadAllText(other));
    }

    [Fact]
    public void A_folder_that_cannot_be_used_gives_a_log_that_writes_nothing_and_never_throws()
    {
        string blocker = TestTemp.NewFilePath("logfile", ".txt");
        File.WriteAllText(blocker, "a file where the folder should be");
        using var log = SessionLogFile.Open(Path.Combine(blocker, "Logs"), false);
        Assert.Null(log.Path);
        log.Write(Entry("nowhere"));
    }

    [Fact]
    public void Entries_below_the_minimum_level_are_left_out_when_the_file_sits_behind_a_level_filter()
    {
        string folder = TestTemp.NewFolderPath("logfile");
        using var file = SessionLogFile.Open(folder, false);
        var filtered = new LevelFilterSink(file, LogLevel.Warn);
        filtered.Write(Entry("quiet", LogLevel.Info));
        filtered.Write(Entry("loud", LogLevel.Warn));
        string text = ReadShared(file.Path!);
        Assert.DoesNotContain("quiet", text);
        Assert.Contains("loud", text);
    }

    private sealed class ListSink : ILogSink
    {
        public readonly List<LogEntry> Entries = [];
        public void Write(LogEntry entry) => Entries.Add(entry);
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(LogEntry entry) => throw new InvalidOperationException("broken sink");
    }

    [Fact]
    public void The_tee_gives_every_entry_to_every_sink_even_when_one_fails()
    {
        var a = new ListSink();
        var b = new ListSink();
        var tee = new TeeLogSink(a, new ThrowingSink(), b);
        tee.Write(Entry("x"));
        Assert.Single(a.Entries);
        Assert.Single(b.Entries);
    }
}
