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
    /// <summary>The test window's size, where the login card has to hold a long refusal and the disclosures have to fit.</summary>
    private const int CardWidth = 1152, CardHeight = 648;

    /// <summary>Second Life's refusal of a made-up name as its login service sent it (eleven lines with blank lines between them), after
    /// the viewer's "Login failed." line.</summary>
    private const string LongRefusal = "Login failed.\nSorry! We couldn't log you in.\n\nPlease check to make sure you entered the right\n\n"
        + "    * Username (like bobsmith12 or steller.sunshine)\n\n    * Password\n\n    * Second Factor Token (if enabled)\n\n"
        + "Also, please make sure your Caps Lock key is off. If you feel this is an error, please contact support@secondlife.com.";

    /// <summary>
    /// --tr-logincard=long|disclosure_first_run|disclosure_second_life: the login screen at the test window's size (1152x648) with the
    /// long refusal on its status line, or with one of the two disclosures over it. Checks: the card lies wholly inside the window and
    /// ends above the release line (long); the dialog's panel lies wholly inside the window (disclosures); then the picture against
    /// --tr-golden. --tr-alter=unbounded lets the status area grow with its text as before: its geometry check MUST fail.
    /// </summary>
    private async Task<int> LoginCardRun(string what, string? golden, bool unbounded)
    {
        var strings = StringTable.English();
        var rig = MakeRig(new Vector2I(CardWidth, CardHeight));
        var shell = new ShellRoot(new ShellController(strings, () => { })) { FixedClockText = TestClockText };
        rig.Viewport.AddChild(shell);
        var login = new LoginScreen(strings, null);
        shell.MainView.AddChild(login);
        login.ShowExampleGrid(ExampleGrid());
        var window = new Rect2(0, 0, CardWidth, CardHeight);
        if (what == "long")
            login.ShowStatusForTest(LongRefusal, unbounded);
        else if (LoginDialogSamples.For(strings, what) is { Kind: LoginDialogKind.Disclosure } dialog)
            login.Dialogs.Show(dialog);
        else
            throw new ArgumentException("--tr-logincard must be long, disclosure_first_run or disclosure_second_life");
        await Frames(8);

        if (what == "long")
        {
            var card = login.GetNode<Control>("card").GetGlobalRect();
            var release = login.GetNode<Control>("release").GetGlobalRect();
            var scroll = ((Control)login.FindChild("status_scroll", true, false)).GetGlobalRect();
            bool inside = window.Encloses(card), aboveRelease = card.End.Y <= release.Position.Y;
            _result["logincard"] = new { what, unbounded, card = new[] { card.Position.Y, card.End.Y }, release_top = release.Position.Y, status_height = scroll.Size.Y, inside, above_release = aboveRelease };
            if (!inside) _failures.Add($"the card ({card.Position.Y:F0} to {card.End.Y:F0}) is not wholly inside the {CardHeight} px window");
            if (!aboveRelease) _failures.Add($"the card ends at {card.End.Y:F0}, below the release line's top {release.Position.Y:F0}");
        }
        else
        {
            var panel = login.GetNode<Control>("login_dialog/login_dialog_panel").GetGlobalRect();
            bool inside = window.Encloses(panel);
            _result["logincard"] = new { what, panel = new[] { panel.Position.X, panel.Position.Y, panel.End.X, panel.End.Y }, inside };
            if (!inside) _failures.Add($"the {what} panel ({panel}) is not wholly inside the window");
        }

        var img = rig.Texture.GetImage();
        img.Convert(Image.Format.Rgba8);
        Directory.CreateDirectory(_out);
        img.SavePng(Path.Combine(_out, _tag + ".png"));
        if (golden is not null)
        {
            if (!Path.IsPathFullyQualified(golden) || !File.Exists(golden))
                _failures.Add("golden not found: " + Path.GetFileName(golden));
            else
            {
                var g = Image.LoadFromFile(golden);
                g.Convert(Image.Format.Rgba8);
                var diff = g.GetWidth() != CardWidth || g.GetHeight() != CardHeight
                    ? new DiffResult(false, g.GetWidth(), g.GetHeight(), 0, 0, 0, 0, "golden size differs")
                    : ImageDiff.Compare(img.GetData(), g.GetData(), CardWidth, CardHeight, 4, DiffTolerance.Terrain);
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
