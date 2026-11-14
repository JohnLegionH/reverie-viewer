// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// tools/other-clients.ps1, the live scripts' guard against a test account used by two clients at once: a process
/// counts by its executable (or, under the dotnet host, the application it runs), never by other text on its command
/// line, so a shell or script that merely names the client does not block a login.
/// </summary>
public class OtherClientGuardTests
{
    private const string Pattern = @"Example\.Client|example-client";
    private readonly ITestOutputHelper _out;
    public OtherClientGuardTests(ITestOutputHelper output) { _out = output; }

    private static string Guard => Path.Combine(TestPaths.RepoRoot, "tools", "other-clients.ps1");

    private (int Count, string Output) Run(string pattern, params object[] processes)
    {
        string file = TestTemp.NewFilePath("other-clients", ".json");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(processes));
            var r = ChildProcess.Run("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Guard, "-Pattern", pattern, "-ProcessesJson", file],
                TimeSpan.FromMinutes(1));
            _out.WriteLine(r.Output);
            Assert.Equal(0, r.ExitCode);
            var line = r.Output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("OTHER CLIENTS ", StringComparison.Ordinal));
            return (int.Parse(line["OTHER CLIENTS ".Length..]), r.Output);
        }
        finally { TestTemp.Delete(file); }
    }

    private static object Proc(string name, string? path, string? commandLine) => new { Name = name, ExecutablePath = path, CommandLine = commandLine };

    private const string ClientExe = @"\\host\clients\example-client\example-client.exe";
    private const string DotnetExe = @"\\host\tools\dotnet\dotnet.exe";

    [Fact]
    public void The_client_running_blocks()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard reads Windows processes");
        var (count, output) = Run(Pattern, Proc("example-client.exe", ClientExe, ClientExe + " run moves.yaml"));
        Assert.Equal(1, count);
        Assert.Contains("example-client.exe", output);
    }

    [Fact]
    public void A_shell_whose_command_line_names_the_client_does_not_block()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard reads Windows processes");
        var (count, _) = Run(Pattern,
            Proc("bash.exe", @"\\host\tools\git\usr\bin\bash.exe", @"bash -c ""ls /clients/example-client/scenarios; grep Example.Client notes.txt"""),
            Proc("powershell.exe", @"\\host\system\powershell.exe", @"powershell -File \\host\clients\example-client\tools\check.ps1"),
            Proc("dotnet.exe", DotnetExe, @"dotnet build \\host\clients\example-client\Example.Client.sln"),
            Proc("System Idle Process", null, null));
        Assert.Equal(0, count);
    }

    [Fact]
    public void The_client_run_by_the_dotnet_host_blocks()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard reads Windows processes");
        var (count, _) = Run(Pattern, Proc("dotnet.exe", DotnetExe, "\"" + DotnetExe + @""" --roll-forward Major \\host\clients\agent\Example.Client.Host.dll mcp"));
        Assert.Equal(1, count);
    }

    [Fact]
    public void The_pattern_none_finds_nothing()
    {
        Assert.True(OperatingSystem.IsWindows(), "the guard reads Windows processes");
        Assert.Equal(0, Run("none", Proc("example-client.exe", ClientExe, "example-client.exe")).Count);
    }
}
