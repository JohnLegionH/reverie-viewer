// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>
    /// --tr-loginscreen=en|pseudo: the login screen as the viewer draws it, inside the shell with its theme, in a viewport of the
    /// usual test size, with a reserved example address in the grid field. The picture is compared with --tr-golden (the same
    /// tolerance as the other pictures). The pseudo-localised table is the negative control: its longer, accented strings must
    /// fail the same diff. Nothing is typed and no login is attempted.
    /// </summary>
    /// <summary>The login screen's picture is taller than the usual test picture, so the whole panel is in it.</summary>
    private const int LoginWidth = 640, LoginHeight = 720;
    private const string ExampleGridAddress = "https://grid.example.org:8002/";

    private async Task<int> LoginScreenRun(string table, string? golden)
    {
        var strings = table switch
        {
            "en" => StringTable.English(),
            "pseudo" => StringTable.Pseudo(),
            _ => throw new ArgumentException("--tr-loginscreen must be en or pseudo"),
        };
        var rig = MakeRig(new Vector2I(LoginWidth, LoginHeight));
        var shell = new ShellRoot(new ShellController(strings, () => { })) { FixedClockText = TestClockText };
        rig.Viewport.AddChild(shell);
        var login = new LoginScreen(strings, null);
        shell.MainView.AddChild(login);
        ((LineEdit)login.ById["grid"]).Text = ExampleGridAddress;   // reserved example address: test data, not interface text
        await Frames(8);
        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        _result["loginscreen"] = new { table, labels = login.Labels.Count, controls = login.ById.Count };
        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
                _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                var diff = g.GetWidth() != LoginWidth || g.GetHeight() != LoginHeight
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), LoginWidth, LoginHeight, 4, DiffTolerance.Terrain);
                _result["diff"] = new Dictionary<string, object?>
                {
                    ["golden"] = Path.GetFileName(golden), ["pass"] = diff.Pass, ["differing"] = diff.Differing,
                    ["differing_fraction"] = diff.DifferingFraction, ["mean_abs_error"] = Math.Round(diff.MeanAbsError, 4),
                    ["max_channel_error"] = diff.MaxChannelError, ["tolerance"] = DiffTolerance.Terrain, ["summary"] = diff.ToString(),
                };
                if (!diff.Pass)
                    _failures.Add("diff " + diff);
            }
        }
        shell.QueueFree();
        return _failures.Count == 0 ? 0 : 1;
    }
}
