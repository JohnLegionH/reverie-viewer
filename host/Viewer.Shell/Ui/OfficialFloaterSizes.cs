// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;

namespace Viewer.Shell;

/// <summary>
/// The default sizes of the official viewer's windows that ours stand for: the width and height on the root element of each window's
/// layout file (indra/newview/skins/default/xui/en/floater_*.xml), raised to that file's minimum where the minimum is the larger.
/// A window opens the first time at least this large (<see cref="FloaterManager.FirstOpenSize"/>). Windows with no official
/// counterpart are listed with no size, so the table says which of ours were looked up.
/// </summary>
public static class OfficialFloaterSizes
{
    public static IReadOnlyDictionary<string, (string File, UiSize? Size)> Table { get; } = new Dictionary<string, (string, UiSize?)>(StringComparer.Ordinal)
    {
        ["inventory"] = ("floater_my_inventory.xml", new UiSize(418, 570)),
        ["appearance"] = ("floater_my_appearance.xml", new UiSize(333, 588)),
        ["outfits"] = ("floater_my_appearance.xml", new UiSize(333, 588)),
        ["upload_image"] = ("floater_image_preview.xml", new UiSize(300, 495)),
        ["upload_bulk"] = ("floater_bulk_upload.xml", new UiSize(430, 207)),
        ["upload_model"] = ("floater_model_preview.xml", new UiSize(980, 625)),
        ["nearby_chat"] = ("floater_im_session.xml", new UiSize(394, 355)),
        ["conversations"] = ("floater_im_container.xml", new UiSize(450, 210)),
        ["inspect"] = ("floater_inspect.xml", new UiSize(400, 300)),
        ["about_land"] = ("floater_about_land.xml", new UiSize(634, 420)),
        ["object_contents"] = ("floater_openobject.xml", new UiSize(300, 350)),
        ["object_buy"] = ("floater_buy_object.xml", new UiSize(280, 310)),
        ["object_pay"] = ("floater_pay.xml", new UiSize(261, 208)),
        ["texture_preview"] = ("floater_preview_texture.xml", new UiSize(370, 350)),
        ["notifications"] = ("floater_notifications_tabbed.xml", new UiSize(435, 550)),
        ["people"] = ("floater_people.xml", new UiSize(370, 570)),
        ["personal_lighting"] = ("floater_adjust_environment.xml", new UiSize(845, 280)),
        ["preferences"] = ("floater_preferences.xml", new UiSize(658, 530)),
        ["world_map"] = ("floater_world_map.xml", new UiSize(650, 600)),
        ["minimap"] = ("floater_map.xml", new UiSize(200, 200)),
        ["about"] = ("floater_about.xml", new UiSize(500, 440)),
        ["sound_tuner"] = ("floater_sound_devices.xml", new UiSize(490, 164)),
        ["gestures"] = ("floater_gesture.xml", new UiSize(313, 465)),
        ["notecard"] = ("floater_preview_notecard.xml", new UiSize(400, 361)),
        // ours with no official window of their own
        ["upload_bulk_models"] = ("", null),
        ["object_info"] = ("", null),
        ["teleport"] = ("", null),
        ["licences"] = ("", null),
        ["addons"] = ("", null),
    };

    /// <summary>The official size for one of our windows; null when it has no official counterpart. A numbered window (a notecard
    /// opened in its second slot, "notecard.2") takes its kind's size.</summary>
    public static UiSize? Of(string id)
    {
        if (Table.TryGetValue(id, out var row))
            return row.Size;
        int dot = id.IndexOf('.');
        return dot > 0 && Table.TryGetValue(id[..dot], out var kind) ? kind.Size : null;
    }
}
