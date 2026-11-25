// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Xunit;

namespace Viewer.Core.Tests;

public sealed class ChatBarCaretTests
{
    [Fact]
    public void The_chat_bar_text_cursor_blinks()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "ChromeBars.cs"));
        int start = source.IndexOf("Input = new TextEdit", StringComparison.Ordinal);
        Assert.Contains("CaretBlink = true", source[start..source.IndexOf("};", start, StringComparison.Ordinal)]);
    }
}
