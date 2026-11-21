// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

/// <summary>
/// The confirmation dialogs: one modal question each, as notifications of the Alert kind, drawn by the one shared dialog. The words and
/// buttons are the official viewer's for the same question (its notifications.xml: ReturnToOwner, ConfirmObjectDelete*, AttachmentDrop,
/// GrantModifyRights); the strings are in the string table. The first button (index 0) is the one Enter presses, the second (index 1)
/// is the way out that Esc takes. Answering with the first runs <c>confirm</c>; the second, or closing, runs nothing.
/// </summary>
public static class Confirmations
{
    public const int Yes = 0, No = 1;

    /// <summary>Return to owner: OK or Cancel, with the warning about no-transfer objects on its own line.</summary>
    public static Notification Return(Action confirm) => Ask("confirm.return.title", "confirm.return.body", null, "button.ok", "button.cancel", confirm);

    /// <summary>Delete an object that is locked, cannot be copied or is not the agent's own; <paramref name="variant"/> is the official
    /// notification's name (<see cref="Viewer.Core.Building.BuildRules.DeleteConfirmation"/>).</summary>
    public static Notification DeleteObjects(string variant, Action confirm) => Ask("confirm.delete.title", "confirm.delete." + variant, null, "button.ok", "button.cancel", confirm);

    /// <summary>Drop a worn attachment on the ground: Yes or No.</summary>
    public static Notification Drop(Action confirm) => Ask("confirm.drop.title", "confirm.drop.body", null, "button.yes", "button.no", confirm);

    /// <summary>Give a friend the right to edit the agent's objects: Yes or No.</summary>
    public static Notification GrantModifyRights(string friendName, Action confirm) => Ask("confirm.grant_modify.title", "confirm.grant_modify.body", friendName, "button.yes", "button.no", confirm);

    /// <summary>Open a web page in the person's own browser: it names the page's host, and only the first button opens it.</summary>
    public static Notification OpenLink(Uri page, Action open) =>
        Ask("link.open.title", "link.open.body", Grids.GridLinks.HostText(page), "link.open.button", "button.cancel", open);

    private static Notification Ask(string titleKey, string bodyKey, string? arg, string yesKey, string noKey, Action confirm) => new()
    {
        Kind = NotificationKind.Alert,
        Confirmation = true,
        Title = NoticeText.Of(titleKey),
        Body = NoticeText.Of(bodyKey, arg),
        Buttons = [new NoticeButton(NoticeText.Of(yesKey), Yes), new NoticeButton(NoticeText.Of(noKey), No)],
        DefaultButton = Yes,
        CancelButton = No,
        Responder = a => { if (a.Button == Yes) confirm(); },
    };
}
