// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

public enum LoginControlKind { Text, Secret, Button, Status, Note, Choice, Check }

/// <summary>One control of the login screen: its id, kind, the string-table key of its visible label and of its accessible name.</summary>
public sealed record LoginControl(string Id, LoginControlKind Kind, string LabelKey, string AccessibleNameKey, string? HintKey = null)
{
    /// <summary>Whether Enter in this field logs in.</summary>
    public bool EnterLogsIn => Kind is LoginControlKind.Text or LoginControlKind.Secret;
}

/// <summary>
/// The login screen as data (ADR-020): Viewer.Godot builds its controls from this list, so the tests can check that
/// every control has a label and an accessible name from the string table without running the engine.
/// </summary>
public static class LoginScreenSpec
{
    public const string TitleKey = "login.title";

    /// <summary>The login card, top to bottom: the grid chooser, the name, the password, the two remember ticks on one line, Start at
    /// (last location, home or a region, with the region's field), Log in (Cancel while an attempt runs) and the status line.</summary>
    public static IReadOnlyList<LoginControl> Controls { get; } =
    [
        new("grid", LoginControlKind.Choice, "login.grid.label", "login.grid.label"),
        new("username", LoginControlKind.Text, "login.username.label", "login.username.label", "login.username.hint"),
        new("password", LoginControlKind.Secret, "login.password.label", "login.password.label"),
        new("remember", LoginControlKind.Check, "login.remember", "login.remember"),
        new("remember_password", LoginControlKind.Check, "login.remember_password", "login.remember_password"),
        new("start", LoginControlKind.Choice, "login.start.label", "login.start.label"),
        new("start_region", LoginControlKind.Text, "login.start.region.label", "login.start.region.label", "login.start.region.hint"),
        new("login", LoginControlKind.Button, "login.button.login", "login.button.login"),
        new("cancel", LoginControlKind.Button, "login.button.cancel", "login.button.cancel"),
        new("status", LoginControlKind.Status, "login.status.ready", "login.status.label"),
    ];

    /// <summary>The password field's show/hide button: a 16-unit eye icon with 6 units of padding each side, set this far in from
    /// the field's right edge.</summary>
    public const int RevealButtonWidth = 28, RevealButtonInset = 4;

    /// <summary>The space the password field keeps on its right for its text: the button, its inset and the field's own padding, so
    /// typed text (or its dots) stops before the button and scrolls, and the button never covers it.</summary>
    public static int PasswordTextRightMargin(int fieldPadding) => fieldPadding;
}
