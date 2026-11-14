// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Linq;
using Godot;
using Viewer.Core.Actions;
using Viewer.Core.Diagnostics;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Notifications;

namespace Viewer.Godot;

public partial class TestRender
{
    /// <summary>The script dialog scene (--tr-dialog=N): a dialog of N buttons (up to twelve) drawn as the viewer draws it over the terrain
    /// fixture, with the buttons in rows of three, the first bottom-left, and Ignore below. A dialog of four buttons is the negative control.</summary>
    private int _dialogButtons;

    private void ParseDialog(string[] args)
    {
        if (Arg(args, "--tr-dialog=") is { } n && int.TryParse(n, out int count) && count is > 0 and <= DialogLayout.MaxButtons)
            _dialogButtons = count;
    }

    private void AddDialogPanel(Rig rig)
    {
        if (_dialogButtons == 0)
            return;
        var model = new NotificationModel(() => 0);
        var sent = new System.Collections.Generic.List<ActionIntent>();
        var prompts = new ScriptPrompts(model, i => { sent.Add(i); return new ActionResult(ActionStatus.Sent, 1); }, () => 0, () => "Test User");
        var buttons = Enumerable.Range(1, _dialogButtons).Select(i => "Option " + i).ToArray();
        prompts.Receive(new ScriptDialogReceived(null, Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000a1"), Guid.Empty, "Example Vendor", "Example Owner", false,
            new MessageBody("Choose one of the options below.", MessageChannel.LocalChat), 7, buttons));
        var strings = StringTable.English();
        var theme = ThemeSpec.Default;
        var panel = NotificationView.Build(model.List[0], strings, theme, (_, _, _) => { }, id => { });
        panel.Theme = ShellTheme.Build(UiTheme.Tokens);
        panel.Position = new Vector2(40, 40);
        var layer = new CanvasLayer { Name = "dialog_layer" };
        var host = new Control { Name = "dialog_host", MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(host);
        host.AddChild(panel);
        rig.Root.AddChild(layer);
        var found = panel.GetChildren().Count;
        _result["dialog"] = new { buttons = _dialogButtons, rows = DialogLayout.For(_dialogButtons).Rows.Select(r => r.ToArray()).ToArray(), ignore = true, children = found };
    }
}
