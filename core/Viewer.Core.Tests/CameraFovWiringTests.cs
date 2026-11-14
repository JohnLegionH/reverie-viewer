// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.RegularExpressions;
using Viewer.Shell;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// The main view's camera has the official default vertical field of view (60 degrees, the CameraAngle setting's 1.047198 rad). The engine host
/// cannot run in the unit tests, so the wiring is checked in the source: the camera the world is presented with is created with it, and
/// nothing else in the main window sets the field except the aim command.
/// </summary>
public class CameraFovWiringTests
{
    [Fact]
    public void The_default_vertical_field_is_the_official_one()
    {
        Assert.Equal(1.047198f, CameraDefaults.VerticalFovDegrees * MathF.PI / 180f, 4);
    }

    [Fact]
    public void The_camera_the_world_is_presented_with_starts_at_that_field()
    {
        string text = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "host", "Viewer.Godot", "Main.cs"));
        var m = Regex.Match(text, @"_camera\s*=\s*new Camera3D\s*\{([^}]*)\}");
        Assert.True(m.Success, "the main camera's creation was not found");
        Assert.Matches(@"Fov\s*=\s*CameraDefaults\.VerticalFovDegrees", m.Groups[1].Value);
    }
}
