// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The own-avatar dump written at each login stays cheap and off the frame's path. The engine host cannot run in the unit tests, so the wiring is checked in the
/// source: the dump is taken from the frame loop, the character folder is hashed and the file written on another thread, and the file goes next to the session log.
/// </summary>
public class LoginDumpWiringTests
{
    private static string Host(string file) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", file));

    [Fact]
    public void The_frame_loop_takes_the_dump_once_per_frame_call_and_the_tick_throttles_itself()
    {
        string main = Host("Main.cs");
        var process = Regex.Match(main, @"public override void _Process\(double delta\)\s*\{(.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(process.Success, "_Process was not found");
        Assert.Contains("LoginDumpTick();", process.Groups[1].Value);
        string dump = Host("Main.LoginDump.cs");
        Assert.Matches(@"if \(now < _dumpNextAt\)\s*return;", dump);   // two seconds between readings, not one a frame
    }

    [Fact]
    public void The_folder_is_hashed_and_the_file_written_on_another_thread()
    {
        string dump = Host("Main.LoginDump.cs");
        Assert.Matches(@"Task\.Run\(\(\) => AvatarLoginDump\.Fingerprint\(", dump);
        var write = Regex.Match(dump, @"Task\.Run\(\(\) =>\s*\{(.*?)\}\);", RegexOptions.Singleline);
        Assert.True(write.Success, "the file write is not inside a Task.Run");
        Assert.Contains("File.WriteAllText(path, json)", write.Groups[1].Value);
        Assert.DoesNotContain("File.WriteAllText", dump.Replace(write.Value, ""));   // no write outside it
    }

    [Fact]
    public void The_file_goes_next_to_the_session_log_and_takes_no_picture()
    {
        string dump = Host("Main.LoginDump.cs");
        Assert.Contains("LoginDumpName = \"viewer.avatar.json\"", dump);
        Assert.Contains("Path.Combine(Path.GetDirectoryName(logPath)", dump);
        Assert.DoesNotContain("GetImage", dump);
        Assert.DoesNotContain("SavePng", dump);
    }
}
