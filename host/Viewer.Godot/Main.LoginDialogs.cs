// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Shell;

namespace Viewer.Godot;

/// <summary>The login-flow dialogs for the development automation: show one with example content, say which is open, close it.</summary>
public partial class Main
{
    /// <summary>{action: show|state|close, kind?}: shows an example of a login dialog on the login screen (kinds: <see cref="LoginDialogSamples.Kinds"/>),
    /// reports the dialog open, or closes it. Null when the login screen is not there.</summary>
    public object? LoginDialogCall(string action, string? kind)
    {
        if (_loginScreen is not { } screen || _strings is null)
            return null;
        switch (action)
        {
            case "show":
            {
                if (kind is null || LoginDialogSamples.For(_strings, kind) is not { } dialog)
                    return new { error = "kind must be one of " + string.Join(", ", LoginDialogSamples.Kinds) };
                screen.Visible = true;
                screen.Dialogs.Show(dialog);
                return Describe(dialog);
            }
            case "close":
                screen.Dialogs.Close();
                return new { open = false };
            default:
                return screen.Dialogs.Current is { } open ? Describe(open) : new { open = false };
        }

        static object Describe(LoginDialog d) => new
        {
            open = true, kind = d.Kind.ToString(), title = d.Title, confirm = d.ConfirmLabel, cancel = d.CancelLabel,
            fields = d.Fields.Count, input = d.InputLabel is not null, agree = d.AgreeLabel is not null, link = d.Link is not null,
        };
    }
}
