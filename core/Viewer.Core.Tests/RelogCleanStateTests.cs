// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// A log out and a log in again in the same run (the home-refused dialog's "Log out and back in at home", or the login screen after a
/// logout) must start the world from the same clean state as a fresh start. The world's nodes hang under one stage node that is freed when
/// the session's presentation goes; a field that still points at a node freed with it makes the next session's frame call into a disposed
/// object, which throws every frame before the world is drawn (no avatar and no land). The host's Godot nodes cannot be made in this test
/// run, so these read the host's source, as <see cref="QuitSafetyTests"/> does: every field parented to the stage must be let go in
/// Unpresent (directly or in a method it calls), and the buttons that outlive a session are wired once, not once per session.
/// </summary>
public class RelogCleanStateTests
{
    private static string Host => Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot");

    private static Dictionary<string, string> MainFiles() =>
        Directory.EnumerateFiles(Host, "Main*.cs", SearchOption.TopDirectoryOnly)
            .ToDictionary(f => Path.GetFileName(f), f => File.ReadAllText(f).Replace("\r\n", "\n"));

    /// <summary>The body of a member, from its signature to the closing brace at the member's indent (four spaces).</summary>
    private static string? Body(string text, string signature)
    {
        int at = text.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0)
            return null;
        int end = text.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return end < 0 ? text[at..] : text[at..end];
    }

    /// <summary>Unpresent's body and the bodies of the Main methods it names (FreeBanWall, DetachNeighbours and the like).</summary>
    private static string TeardownText(Dictionary<string, string> files)
    {
        string unpresent = files.Values.Select(t => Body(t, "private void Unpresent()")).FirstOrDefault(b => b is not null)
            ?? throw new InvalidOperationException("Unpresent not found");
        var all = new System.Text.StringBuilder(unpresent);
        foreach (Match m in Regex.Matches(unpresent, @"\b([A-Z][A-Za-z0-9]+)\b"))
            foreach (var t in files.Values)
                if (Body(t, "void " + m.Groups[1].Value + "()") is { } called)
                    all.Append('\n').Append(called);
        return all.ToString();
    }

    [Fact]
    public void Every_node_the_world_stage_holds_is_let_go_when_the_stage_goes()
    {
        var files = MainFiles();
        var parented = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var t in files.Values)
            foreach (Match m in Regex.Matches(t, @"_stage[!?]?\.AddChild\((_[A-Za-z0-9]+)\)"))
                parented.Add(m.Groups[1].Value);
        Assert.Contains("_camera", parented);   // the scan sees the stage's children (the camera is one, let go with the stage)
        string teardown = TeardownText(files);
        var kept = parented.Where(f => !Regex.IsMatch(teardown, @"\b" + Regex.Escape(f) + @"\s*=\s*null\b")).ToList();
        Assert.True(kept.Count == 0, "parented to the world stage but still held after Unpresent (a freed node the next session would call into): " + string.Join(", ", kept));
    }

    [Fact]
    public void The_stand_and_stop_flying_buttons_are_wired_once_not_per_session()
    {
        // the buttons outlive a session; a second connection of the same handler is refused by the engine with an error at every login
        string text = File.ReadAllText(Path.Combine(Host, "Main.Interact.cs")).Replace("\r\n", "\n");
        int onceAt = text.IndexOf("if (_interactUi is null)", StringComparison.Ordinal);
        Assert.True(onceAt >= 0, "the once-only block of the interaction windows was not found");
        int onceEnd = text.IndexOf("\n                }\n", onceAt, StringComparison.Ordinal);   // the block's closing brace, at its own indent
        string once = onceEnd < 0 ? "" : text[onceAt..onceEnd];
        foreach (var wiring in new[] { "_interactUi.Stand.Pressed +=", "_interactUi.StopFlying.Pressed +=" })
        {
            int count = Regex.Matches(text, Regex.Escape(wiring)).Count;
            Assert.True(count == 1, wiring + " appears " + count + " times");
            Assert.True(once.Contains(wiring, StringComparison.Ordinal), wiring + " is outside the once-only block, so it runs again at every session");
        }
    }
}
