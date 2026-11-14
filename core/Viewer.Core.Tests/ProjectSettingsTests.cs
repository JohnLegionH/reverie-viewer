// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Engine project settings the drawing relies on. The face shaders declare per-instance shader parameters (the object's scale, the water cut,
/// the skin row); the engine keeps every instance's values in one shared buffer whose size is a project setting, and an instance that finds no
/// room draws wrongly and makes the engine log errors every frame.
/// </summary>
public class ProjectSettingsTests
{
    private const string Setting = "rendering/limits/global_shader_variables/buffer_size";

    /// <summary>Slots the engine reserves for one instance's parameters (the engine's MAX_INSTANCE_UNIFORM_INDICES).</summary>
    private const int SlotsPerInstance = 16;

    /// <summary>Instances the buffer must hold: more than a full region of objects (a region may hold tens of thousands of prims) each with its own instance.</summary>
    private const int SupportedInstances = 65536;

    private static int BufferSize()
    {
        string project = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "project.godot"));
        var section = Regex.Match(project, @"^\[rendering\]\r?\n(?<body>.*?)(?=^\[|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(section.Success, "project.godot has no [rendering] section");
        var line = Regex.Match(section.Groups["body"].Value, @"^" + Regex.Escape(Setting["rendering/".Length..]) + @"=(?<n>\d+)\s*$", RegexOptions.Multiline);
        Assert.True(line.Success, $"project.godot [rendering] sets no {Setting}: the engine's default holds only 4,096 instances with instance parameters");
        return int.Parse(line.Groups["n"].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void The_shader_parameter_buffer_holds_every_instance_of_the_largest_supported_view()
    {
        Assert.True(BufferSize() >= SupportedInstances * SlotsPerInstance,
            $"{Setting} is {BufferSize()}, below {SupportedInstances * SlotsPerInstance} ({SupportedInstances} instances of {SlotsPerInstance} slots)");
    }
}
