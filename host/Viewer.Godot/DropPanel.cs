// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Shell;
using Viewer.Shell.Reconnect;

namespace Viewer.Godot;

/// <summary>
/// The notice over the login screen when the grid ended the session unasked: why, in words, with Reconnect and Close. It only draws
/// <see cref="DropNotice"/> and <see cref="ReconnectModel"/> (tested without the engine) and hands clicks back; every text comes from
/// the string table and every control is named for assistive technology. Main thread only.
/// </summary>
public partial class DropPanel : CenterContainer
{
    private readonly StringTable _strings;
    private static ThemeSpec _theme => UiTheme.Spec;
    private readonly ReconnectModel _reconnect;
    private readonly Label _title, _body, _reason, _status;
    private readonly Button _reconnectButton, _closeButton, _cancelButton;
    private DropNotice? _notice;

    /// <summary>The notice that is showing, or null.</summary>
    public DropNotice? Notice => _notice;

    /// <summary>Reconnect was pressed.</summary>
    public event Action? ReconnectRequested;

    /// <summary>Close was pressed (the notice is gone; the login screen is under it).</summary>
    public event Action? Closed;

    public DropPanel(StringTable strings, ReconnectModel reconnect)
    {
        _strings = strings;
        _reconnect = reconnect;
        Name = "drop_notice";
        Visible = false;
        SetAnchorsPreset(LayoutPreset.FullRect);
        var panel = new PanelContainer { Name = "drop_panel" };
        var box = new VBoxContainer { Name = "drop_box", CustomMinimumSize = new Vector2(420, 0) };
        box.AddThemeConstantOverride("separation", 8);
        _title = ChatFeature.Note("drop_title", strings.Get("drop.title"), _theme);
        _body = ChatFeature.Note("drop_body", strings.Get("drop.body"), _theme);
        _reason = ChatFeature.Note("drop_reason", strings.Get("drop.reason.unknown"), _theme);
        _status = ChatFeature.Note("drop_status", strings.Get("drop.reconnect"), _theme);
        foreach (var l in new[] { _body, _reason, _status })
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var buttons = new HBoxContainer { Name = "drop_buttons" };
        _reconnectButton = new Button { Name = "reconnect", Text = strings.Get("drop.reconnect"), ThemeTypeVariation = UiTheme.PrimaryButton };
        _reconnectButton.AccessibilityName = strings.Get("drop.reconnect");
        _reconnectButton.Pressed += () => ReconnectRequested?.Invoke();
        _cancelButton = new Button { Name = "cancel_reconnect", Text = strings.Get("drop.cancel"), Visible = false };
        _cancelButton.AccessibilityName = strings.Get("drop.cancel");
        _cancelButton.Pressed += _reconnect.Cancel;
        _closeButton = new Button { Name = "close", Text = strings.Get("drop.close") };
        _closeButton.AccessibilityName = strings.Get("drop.close");
        _closeButton.Pressed += () => { Visible = false; _notice = null; Closed?.Invoke(); };
        buttons.AddChild(_reconnectButton);
        buttons.AddChild(_cancelButton);
        buttons.AddChild(_closeButton);
        box.AddChild(_title);
        box.AddChild(_body);
        box.AddChild(_reason);
        box.AddChild(_status);
        box.AddChild(buttons);
        panel.AddChild(box);
        ReverieRestyle.Apply(panel);   // not a floater: the reconnect notice takes the Reverie look itself
        AddChild(panel);
    }

    /// <summary>Shows a notice (replacing any showing).</summary>
    public void ShowNotice(DropNotice notice)
    {
        _notice = notice;
        _title.Text = notice.Title;
        _body.Text = notice.Body;
        _reason.Text = notice.Reason;
        _reason.AccessibilityName = notice.Reason;
        _reconnectButton.Visible = notice.CanReconnect;
        Visible = true;
    }

    /// <summary>Hides the notice (a session is back).</summary>
    public void Dismiss()
    {
        _notice = null;
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!Visible)
            return;
        _status.Text = _reconnect.Status;
        _status.Visible = _reconnect.Status.Length > 0;
        _status.AccessibilityName = _reconnect.Status;
        _reconnectButton.Disabled = _reconnect.State == ReconnectState.Trying;
        _cancelButton.Visible = _reconnect.State == ReconnectState.Trying;
    }
}
