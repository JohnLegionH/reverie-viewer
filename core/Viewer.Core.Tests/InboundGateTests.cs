// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Diagnostics;
using Viewer.Core.Protocol;
using Viewer.Core.Session;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The circuit gate the development build's fault command uses: the session's door to it, the adapter's answer without a circuit, and a
/// scan of the host's sources that every use of it sits inside the development-only automation code (so a shipping build has no caller). The
/// shipping export itself is scanned by tools/s1.ps1 (ExportScanTests). No grid. Every member builds its own objects, so the class runs in parallel.
/// </summary>
public class InboundGateTests
{
    [Fact]
    public void The_session_hands_the_request_to_a_transport_that_can_close_its_inbound_side_and_says_what_it_answered()
    {
        var fake = new FakeTransport();
        var session = new ViewerSession(new SessionOptions { LoginUri = "http://grid.invalid/" }, fake, new CoreLog(new ListSink(), "test"));
        Assert.True(session.CloseInboundCircuit());
        Assert.Equal(1, fake.DropCalls);
        fake.DropResult = false;
        Assert.False(session.CloseInboundCircuit());
        Assert.Equal(2, fake.DropCalls);
    }

    [Fact]
    public void The_adapter_with_no_circuit_has_nothing_to_close_and_does_not_throw()
    {
        using var t = new LmvTransport(new Viewer.Core.Net.OutboundPolicy(new Viewer.Core.Net.OutboundOptions()), new CoreLog(new ListSink(), "test"));
        Assert.False(((IInboundGate)t).CloseInbound());
    }

    // ---------------- the source scan ----------------

    private static readonly string[] Marks = ["CloseInboundCircuit", "drop_inbound", "IInboundGate"];

    /// <summary>The lines that mention the gate outside every development-only conditional block.</summary>
    internal static List<int> Unguarded(string source)
    {
        var found = new List<int>();
        var stack = new Stack<bool>();
        int guarded = 0;
        var lines = source.Replace("\r\n", "\n").Split((char)10);
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("#if ", StringComparison.Ordinal))
            {
                bool g = t.Contains("NV_AUTOMATION", StringComparison.Ordinal);
                stack.Push(g);
                if (g) guarded++;
                continue;
            }
            if (t.StartsWith("#endif", StringComparison.Ordinal))
            {
                if (stack.Count > 0 && stack.Pop()) guarded--;
                continue;
            }
            if (guarded == 0 && Marks.Any(m => t.Contains(m, StringComparison.Ordinal)))
                found.Add(i + 1);
        }
        return found;
    }

    private static string Lines(params string[] lines) => string.Join((char)10, lines) + (char)10;

    [Fact]
    public void The_scan_sees_an_unguarded_mention_and_passes_a_guarded_one()
    {
        Assert.Equal([2], Unguarded(Lines("class A {", "  bool x = s.CloseInboundCircuit();", "}")));
        Assert.Empty(Unguarded(Lines("#if NV_AUTOMATION", "class A { bool x = s.CloseInboundCircuit(); }", "#endif")));
        Assert.Empty(Unguarded(Lines("#if NV_AUTOMATION", "class A { string s = \"drop_inbound\"; }", "#endif")));
        Assert.Equal([5], Unguarded(Lines("#if NV_AUTOMATION", "#if DEBUG", "#endif", "#endif", "var x = s.CloseInboundCircuit();")));
        Assert.Equal([2], Unguarded(Lines("#if SOMETHING_ELSE", "var x = s.CloseInboundCircuit();", "#endif")));
    }

    [Fact]
    public void Every_use_of_the_gate_in_the_host_sits_inside_the_development_only_automation_code()
    {
        string root = TestPaths.RepoRoot;
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "host"), "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace((char)92, '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.StartsWith("host/Viewer.Automation/", StringComparison.Ordinal)
                || rel.StartsWith("host/Viewer.Godot/Automation/", StringComparison.Ordinal))
                continue;   // the automation sources are compiled into development builds only (Viewer.Godot.csproj)
            foreach (int line in Unguarded(File.ReadAllText(file)))
                bad.Add($"{rel}:{line}");
        }
        Assert.Empty(bad);
    }
}
