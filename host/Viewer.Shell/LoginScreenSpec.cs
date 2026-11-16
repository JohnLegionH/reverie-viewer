// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell;

public enum LoginControlKind { Text, Secret, Button, Status, Note, Choice, Check }

/// <summary>One control of the login screen: its id, kind, the string-table key of its visible label and of its accessible name.</summary>
public sealed record LoginControl(string Id, LoginControlKind Kind, string LabelKey, string AccessibleNameKey, string? HintKey = null)
{
    /// <summary>Whether Enter in this field logs in.</summary>
    public bool EnterLogsIn => false;
}

/// <summary>
/// The login screen as data (ADR-020): Viewer.Godot builds its controls from this list, so the tests can check that
/// every control has a label and an accessible name from the string table without running the engine.
/// </summary>
public static class LoginScreenSpec
{
    public const string TitleKey = "login.title";

    public static IReadOnlyList<LoginControl> Controls { get; } =
    [
        new("grid", LoginControlKind.Text, "login.grid.label", "login.grid.label", "login.grid.hint"),
        new("resolved", LoginControlKind.Note, "login.resolved.none", "login.resolved.label"),
        new("username", LoginControlKind.Text, "login.username.label", "login.username.label", "login.username.hint"),
        new("password", LoginControlKind.Secret, "login.password.label", "login.password.label"),
        new("start", LoginControlKind.Text, "login.start.label", "login.start.label", "login.start.hint"),
        new("login", LoginControlKind.Button, "login.button.login", "login.button.login"),
        new("cancel", LoginControlKind.Button, "login.button.cancel", "login.button.cancel"),
        new("status", LoginControlKind.Status, "login.status.ready", "login.status.label"),
    ];
}
