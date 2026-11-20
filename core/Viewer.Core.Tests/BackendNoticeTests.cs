// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>A command line the viewer's own text gives for the backend has to be one the viewer accepts: the engine hands the viewer
/// only the arguments after a bare "--", and BackendChoice reads "--backend=" from those.</summary>
public sealed class BackendNoticeTests
{
    private static List<(string Key, string Text)> Strings()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "host", "Viewer.Shell", "Strings", "en.json");
            if (File.Exists(p))
                return JsonDocument.Parse(File.ReadAllText(p)).RootElement.EnumerateObject()
                    .Where(e => e.Value.ValueKind == JsonValueKind.String).Select(e => (e.Name, e.Value.GetString()!)).ToList();
        }
        throw new FileNotFoundException("en.json");
    }

    [Fact]
    public void Every_backend_flag_a_string_gives_follows_a_bare_double_dash_and_is_accepted()
    {
        foreach (var (key, text) in Strings())
            foreach (Match m in Regex.Matches(text, @"(?<sep>--\s+)?(?<flag>--backend=[a-z0-9]+)"))
            {
                Assert.True(m.Groups["sep"].Success, $"{key}: --backend= must follow a bare -- (the engine passes only what follows it to the viewer)");
                // the viewer, set to another backend, started with exactly that argument, takes the choice from it
                string value = m.Groups["flag"].Value["--backend=".Length..];
                string saved = value == BackendChoice.Vulkan ? BackendChoice.D3D12 : BackendChoice.Vulkan;
                Assert.Equal(value, BackendChoice.Requested(saved, new[] { m.Groups["flag"].Value }));
            }
    }

    [Fact]
    public void The_check_catches_a_flag_without_the_double_dash()
    {
        var m = Regex.Match("start the viewer with --backend=auto.", @"(?<sep>--\s+)?(?<flag>--backend=[a-z0-9]+)");
        Assert.True(m.Success);
        Assert.False(m.Groups["sep"].Success);
    }

    [Fact]
    public void The_backend_help_text_exists()
    {
        Assert.Contains(Strings(), s => s.Key == "prefs.backend.notice");
    }
}
