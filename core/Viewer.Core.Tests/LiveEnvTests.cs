// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools/live-env.ps1: the live scripts take their settings from the environment and, for a setting that is not set,
/// from an untracked local file of NAME=value lines. A variable that is set always wins. Tested with a fixture file.
/// </summary>
public class LiveEnvTests
{
    private readonly ITestOutputHelper _out;
    public LiveEnvTests(ITestOutputHelper output) { _out = output; }

    private static string Script => Path.Combine(TestPaths.RepoRoot, "tools", "live-env.ps1");

    private string Value(string name, string fileText, string? alreadySet = null)
    {
        string file = TestTemp.NewFilePath("live-env", ".env");
        try
        {
            File.WriteAllText(file, fileText);
            var env = new Dictionary<string, string?> { ["NV_LIVE_ENV_FILE"] = file, [name] = alreadySet };
            var r = ChildProcess.Run("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Name", name],
                TimeSpan.FromMinutes(1), env);
            _out.WriteLine(r.Output);
            Assert.Equal(0, r.ExitCode);
            return r.Output.Trim();
        }
        finally { TestTemp.Delete(file); }
    }

    private const string Fixture =
        "# a comment line\n\nNV_TEST_ACCOUNTS=C:\\example\\accounts.json\nNV_LIVE_START=uri:Example Region&10&20&30\n" +
        "NV_GRID_INFO_URL=http://example.org:8002/get_grid_info\nNV_OTHER_CLIENT_PATTERN=Example\\.Agent|example-agent\n";

    [Fact]
    public void An_unset_setting_is_read_from_the_file()
    {
        Assert.Equal("http://example.org:8002/get_grid_info", Value("NV_GRID_INFO_URL", Fixture));
    }

    [Fact]
    public void A_value_keeps_its_ampersands_pipes_and_backslashes()
    {
        Assert.Equal("uri:Example Region&10&20&30", Value("NV_LIVE_START", Fixture));
        Assert.Equal(@"Example\.Agent|example-agent", Value("NV_OTHER_CLIENT_PATTERN", Fixture));
    }

    [Fact]
    public void A_variable_that_is_set_wins_over_the_file()
    {
        Assert.Equal("uri:Set Region&1&2&3", Value("NV_LIVE_START", Fixture, "uri:Set Region&1&2&3"));
    }

    [Fact]
    public void A_name_the_file_does_not_hold_gives_nothing()
    {
        Assert.Equal("", Value("NV_LIVE_START", "# nothing here\nNV_GRID_INFO_URL=http://example.org/x\n"));
    }
}
