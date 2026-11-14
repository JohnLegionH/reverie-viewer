// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Shell.Notifications;

/// <summary>Where an upload's items went, as the notice after an upload names it.</summary>
public enum UploadFolder { Textures, Objects }

/// <summary>
/// The notice after an upload: what was uploaded and to which folder ("Uploaded to Textures: name", or a count for a bulk upload),
/// with a Show in inventory button that opens the inventory on the uploaded item (the first one, for several).
/// </summary>
public static class UploadNotice
{
    /// <summary>The index of the Show in inventory button.</summary>
    public const int ShowButton = 0;

    /// <summary>The notice for <paramref name="uploaded"/> (name and new inventory item of each upload that worked), or null when none did.
    /// <paramref name="show"/> is called with the item to select when the button is pressed.</summary>
    public static Notification? For(IReadOnlyList<(string Name, Guid ItemId)> uploaded, UploadFolder folder, Action<Guid> show)
    {
        if (uploaded.Count == 0)
            return null;
        string where = folder == UploadFolder.Textures ? "textures" : "objects";
        Guid first = uploaded[0].ItemId;
        return new Notification
        {
            Kind = NotificationKind.Notify,
            Body = uploaded.Count == 1
                ? NoticeText.Of("upload.notice.one." + where, uploaded[0].Name)
                : NoticeText.Of("upload.notice.many." + where, uploaded.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Buttons = [new NoticeButton(NoticeText.Of("upload.notice.show"), ShowButton)],
            Responder = answer => { if (answer.Button == ShowButton && first != Guid.Empty) show(first); },
        };
    }
}
