// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Threading.Tasks;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>Binds the Appearance window to example content and sets up the scene (see <see cref="AppearanceExample"/>).</summary>
    private static Task ExampleAppearance(AppearancePanel panel, string scene) => AppearanceExample.Show(panel, scene);
}
