// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;
using Xunit.Abstractions;

namespace Viewer.Core.Tests;

/// <summary>
/// The committed hang case (a codestream whose packet data is all 0xFF) handed to the JPEG 2000 decoder itself with our
/// guard bypassed, in a child process with a deadline. The guard refuses this file before the decoder sees it
/// (<see cref="DecoderHangTests"/>); this test records whether the decoder version in use would also have returned. The
/// guard stays either way: this only says which of the two is the second line of defence.
/// </summary>
public class CoreJ2kHangTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    [Fact]
    public void The_decoder_alone_returns_or_refuses_the_committed_hang_case_inside_the_deadline()
    {
        var hang = Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "fuzz", "packet-data-ff.j2c");
        var control = Path.Combine(TestPaths.RepoRoot, "fixtures", "textures", "tex-gradient.j2c");
        Assert.True(File.Exists(TestPaths.DecodeProbeDll), "probe not built");

        // the control first: the decoder in the child works at all, so a missing answer for the hang case is the hang
        var (cCode, cOut) = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.DecodeProbeDll, "rawdecode", control], Deadline);
        Assert.True(cCode == 0 && cOut.Contains("RAW tex-gradient.j2c returned"), $"control: exit {cCode}: {cOut}");

        string outcome;
        try
        {
            var (code, text) = ChildProcess.Run(ChildProcess.DotnetHost, [TestPaths.DecodeProbeDll, "rawdecode", hang], Deadline);
            outcome = code == 0 ? text.Split('\n').FirstOrDefault(l => l.StartsWith("RAW "))?.Trim() ?? "no answer" : $"exit {code}";
        }
        catch (TimeoutException)
        {
            outcome = "hang: no answer inside " + Deadline.TotalSeconds + " s, child killed";
        }
        output.WriteLine("CoreJ2K alone on packet-data-ff.j2c: " + outcome);
        Assert.True(outcome.StartsWith("RAW packet-data-ff.j2c "), outcome);
    }
}
