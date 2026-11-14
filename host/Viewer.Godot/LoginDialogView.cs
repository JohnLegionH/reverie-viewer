// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>
/// One login-flow dialog (<see cref="LoginDialog"/>) drawn over the login screen: a dimmed backdrop that takes the clicks, and a
/// centred panel with the title, the text, the listed lines (a certificate's facts), a read-only text box, a link that opens in the
/// system browser, a one-line input, an agreement box, and the buttons. The main button waits for the agreement box when there is one.
/// The answer is handed to <c>done</c> once. Every visible text comes from the dialog data; every control has an accessible name.
/// </summary>
public partial class LoginDialogView : Control
{
    private readonly LoginDialog _dialog;
    private readonly Action<LoginDialogResult> _done;
    private readonly LineEdit? _input;
    private readonly CheckBox? _agree;
    private readonly Button _confirm;
    private bool _answered;

    public LoginDialog Dialog => _dialog;

    public LoginDialogView(LoginDialog dialog, Action<LoginDialogResult> done)
    {
        _dialog = dialog;
        _done = done;
        Name = "login_dialog";
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var scrim = new ColorRect { Name = "login_dialog_scrim", MouseFilter = MouseFilterEnum.Stop, Color = ShellTheme.C(UiTheme.Tokens.Scrim) };
        scrim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(scrim);

        var panel = new PanelContainer { Name = "login_dialog_panel" };
        panel.AnchorLeft = 0.5f; panel.AnchorRight = 0.5f; panel.AnchorTop = 0.5f; panel.AnchorBottom = 0.5f;
        panel.GrowHorizontal = GrowDirection.Both; panel.GrowVertical = GrowDirection.Both;
        AddChild(panel);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);

        var title = new Label { Name = "title", Text = dialog.Title, ThemeTypeVariation = UiTheme.TitleLabel };
        title.AccessibilityName = dialog.Title;
        box.AddChild(title);

        if (dialog.Body.Length > 0)
        {
            var body = new Label { Name = "body", Text = dialog.Body, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(540, 0) };
            body.AccessibilityName = dialog.Body;
            box.AddChild(body);
        }

        if (dialog.Fields.Count > 0)
        {
            var grid = new GridContainer { Name = "fields", Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 12);
            foreach (var f in dialog.Fields)
            {
                var label = new Label { Text = f.Label, ThemeTypeVariation = UiTheme.CaptionLabel };
                label.AccessibilityName = f.Label;
                var value = new Label { Text = f.Value, AutowrapMode = TextServer.AutowrapMode.Arbitrary, CustomMinimumSize = new Vector2(380, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
                value.AccessibilityName = f.Label + ": " + f.Value;
                grid.AddChild(label);
                grid.AddChild(value);
            }
            box.AddChild(grid);
        }

        if (dialog.ReadOnlyText.Length > 0)
        {
            var text = new TextEdit { Name = "text", Text = dialog.ReadOnlyText, Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(540, 160) };
            text.AccessibilityName = dialog.ReadOnlyText;
            box.AddChild(text);
        }

        if (dialog.Link is { } link)
        {
            var button = new LinkButton { Name = "link", Text = link, Uri = link };
            button.AccessibilityName = link;
            box.AddChild(button);
        }

        if (dialog.InputLabel is { } inputLabel)
        {
            var label = new Label { Text = inputLabel, ThemeTypeVariation = UiTheme.CaptionLabel };
            label.AccessibilityName = inputLabel;
            box.AddChild(label);
            _input = new LineEdit { Name = "input" };
            _input.AccessibilityName = inputLabel;
            box.AddChild(_input);
        }

        if (dialog.AgreeLabel is { } agreeLabel)
        {
            _agree = new CheckBox { Name = "agree", Text = agreeLabel };
            _agree.AccessibilityName = agreeLabel;
            box.AddChild(_agree);
        }

        var buttons = new HBoxContainer { Name = "buttons", Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", 8);
        if (dialog.CancelLabel is { } cancelLabel)
        {
            var cancel = new Button { Name = "cancel", Text = cancelLabel };
            cancel.AccessibilityName = cancelLabel;
            cancel.Pressed += () => Answer(new LoginDialogResult(false, _input?.Text, _agree?.ButtonPressed ?? false));
            buttons.AddChild(cancel);
        }
        _confirm = new Button { Name = "confirm", Text = dialog.ConfirmLabel, ThemeTypeVariation = UiTheme.PrimaryButton };
        _confirm.AccessibilityName = dialog.ConfirmLabel;
        _confirm.Disabled = _agree is not null;   // the agreement box turns it on
        _confirm.Pressed += () => Answer(new LoginDialogResult(true, _input?.Text, _agree?.ButtonPressed ?? false));
        buttons.AddChild(_confirm);
        box.AddChild(buttons);

        if (_agree is not null)
            _agree.Toggled += on => _confirm.Disabled = !on;
        if (_input is not null)
            _input.TextSubmitted += _ => _confirm.EmitSignal(BaseButton.SignalName.Pressed);
    }

    public override void _Ready()
    {
        if (_input is not null) _input.GrabFocus();
        else _confirm.GrabFocus();
    }

    /// <summary>Ends the dialog with this answer (once); the view removes itself.</summary>
    public void Answer(LoginDialogResult result)
    {
        if (_answered) return;
        _answered = true;
        QueueFree();
        _done(result);
    }
}

/// <summary>Shows <see cref="LoginDialog"/>s on a login screen and returns the answers. Safe to call from any thread.</summary>
public sealed class LoginDialogHost(Control screen) : ILoginPrompts
{
    private LoginDialogView? _open;

    /// <summary>Raised on the main thread when a dialog appears (the interface sounds play its sound).</summary>
    public event Action<LoginDialog>? Shown;

    /// <summary>The dialog on screen now, if any.</summary>
    public LoginDialog? Current => _open is { } v && GodotObject.IsInstanceValid(v) ? v.Dialog : null;

    public Task<LoginDialogResult> AskAsync(LoginDialog dialog, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<LoginDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Callable.From(() =>
        {
            Close();
            var view = new LoginDialogView(dialog, r => { _open = null; tcs.TrySetResult(r); });
            _open = view;
            screen.AddChild(view);
            Shown?.Invoke(dialog);
        }).CallDeferred();
        ct.Register(() => Callable.From(() =>
        {
            if (_open is { } v && GodotObject.IsInstanceValid(v)) v.Answer(LoginDialogResult.Cancelled);
            tcs.TrySetCanceled();
        }).CallDeferred());
        return tcs.Task;
    }

    /// <summary>Shows a dialog without waiting for an answer (the automation's pictures); an answer, if one comes, is dropped.</summary>
    public void Show(LoginDialog dialog)
    {
        Close();
        var view = new LoginDialogView(dialog, _ => _open = null);
        _open = view;
        screen.AddChild(view);
        Shown?.Invoke(dialog);
    }

    /// <summary>Removes the dialog on screen, answering it as cancelled.</summary>
    public void Close()
    {
        if (_open is { } v && GodotObject.IsInstanceValid(v)) v.Answer(LoginDialogResult.Cancelled);
        _open = null;
    }
}
