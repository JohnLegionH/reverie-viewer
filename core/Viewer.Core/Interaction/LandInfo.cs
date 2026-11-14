// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Session;

namespace Viewer.Core.Interaction;

/// <summary>One line of the About Land window: a label key and the value to show.</summary>
public sealed record LandRow(string LabelKey, string Value);

/// <summary>About Land: a parcel's properties as rows of text (read-only). Values that are words are string keys (land.on, land.off,
/// land.group_owned) the interface translates; numbers and names are shown as they are.</summary>
public static class LandInfo
{
    private static string OnOff(bool b) => b ? "land.on" : "land.off";

    public static IReadOnlyList<LandRow> Rows(ParcelDetailsReceived p, Func<Guid, string?> nameOf)
    {
        var rows = new List<LandRow>
        {
            new("land.name", p.Name ?? ""),
            new("land.description", p.Description ?? ""),
            new("land.owner", p.GroupOwned ? "land.group_owned" : nameOf(p.OwnerId) is { Length: > 0 } n ? n : p.OwnerId.ToString()),
            new("land.area", p.Area.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("land.access_list", OnOff(p.UseAccessList)),
            new("land.ban_list", OnOff(p.UseBanList)),
            new("land.group_only", OnOff(p.GroupAccessOnly)),
            new("land.payment_required", OnOff(p.DenyAnonymous)),
            new("land.age_required", OnOff(p.DenyAgeUnverified)),
            new("land.pass_list", OnOff(p.UsePassList)),
        };
        if (p.UsePassList)
        {
            rows.Add(new("land.pass_price", p.PassPrice.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            rows.Add(new("land.pass_hours", p.PassHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
        }
        return rows;
    }
}
