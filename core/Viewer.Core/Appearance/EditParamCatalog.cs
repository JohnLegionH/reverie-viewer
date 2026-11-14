// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llpaneleditwearable.cpp
 * @brief UI panel for editing of a particular wearable item.
 *
 * $LicenseInfo:firstyear=2009&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/**
 * @file llvisualparam.h
 * @brief Implementation of LLPolyMesh class.
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */


// Adapted 2026 by Legion Builds. The tabs of each wearable kind's panel and their order (the subpart tables of llpaneleditwearable.cpp), which
// parameters a tab lists and how they are ordered (getSortedParams in the same file), and which parameters are tweakable (isTweakable in
// llvisualparam.h) follow the files named above, written in C# for our own types. The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Globalization;
using System.Xml;
using Viewer.Core.Avatars;

using Viewer.Core.Inventory;

namespace Viewer.Core.Appearance;

/// <summary>One visual parameter as the wearable editor lists it: its labels, range, default, the sex it applies to, its place in its group,
/// and the parameters it drives.</summary>
public sealed record EditParam(int Id, string Name, string Label, string LabelMin, string LabelMax, float Min, float Max, float Default,
    ShapeSex Sex, int Order, string EditGroup, WearableType Wearable, bool Tweakable, IReadOnlyList<DrivenEntry> Driven)
{
    /// <summary>The same parameter in the shape definition's terms (range and default only), for the driver arithmetic.</summary>
    internal ShapeParam AsShapeParam() => new(Id, Name, Min, Max, Default, Sex, [], Driven);
}

/// <summary>One tab of an editor panel: an edit group and the sliders in it, in display order.</summary>
public sealed record EditSubpart(string EditGroup, ShapeSex Sex, IReadOnlyList<EditParam> Params);

/// <summary>
/// What the wearable editor offers, read from the character folder's avatar definition file at run time (the file is never
/// shipped). A panel's tabs are its type's edit groups; a tab lists the parameters of that group which are tweakable, fit the
/// avatar's sex and are ordered by the file's group order. Behaviour of the official viewer's wearable editor: the tab lists in
/// indra/newview/llpaneleditwearable.cpp (the subpart table), its getSortedParams, and isTweakable in
/// indra/llcharacter/llvisualparam.h; the data is the definition file's own attributes (indra/newview/character/avatar_lad.xml).
/// </summary>
public sealed class EditParamCatalog
{
    public const int MaxParams = 4096;

    // the tabs of each type's panel, in the order the official panel lists them
    private static readonly IReadOnlyDictionary<WearableType, (string Group, ShapeSex Sex)[]> Tabs = new Dictionary<WearableType, (string, ShapeSex)[]>
    {
        [WearableType.Shape] = [("shape_body", ShapeSex.Both), ("shape_head", ShapeSex.Both), ("shape_eyes", ShapeSex.Both), ("shape_ears", ShapeSex.Both), ("shape_nose", ShapeSex.Both),
            ("shape_mouth", ShapeSex.Both), ("shape_chin", ShapeSex.Both), ("shape_torso", ShapeSex.Both), ("shape_legs", ShapeSex.Both)],
        [WearableType.Skin] = [("skin_color", ShapeSex.Both), ("skin_facedetail", ShapeSex.Both), ("skin_makeup", ShapeSex.Both), ("skin_bodydetail", ShapeSex.Both)],
        [WearableType.Hair] = [("hair_color", ShapeSex.Both), ("hair_style", ShapeSex.Both), ("hair_eyebrows", ShapeSex.Both), ("hair_facial", ShapeSex.Male)],
        [WearableType.Eyes] = [("eyes", ShapeSex.Both)],
        [WearableType.Shirt] = [("shirt", ShapeSex.Both)],
        [WearableType.Pants] = [("pants", ShapeSex.Both)],
        [WearableType.Shoes] = [("shoes", ShapeSex.Both)],
        [WearableType.Socks] = [("socks", ShapeSex.Both)],
        [WearableType.Jacket] = [("jacket", ShapeSex.Both)],
        [WearableType.Gloves] = [("gloves", ShapeSex.Both)],
        [WearableType.Undershirt] = [("undershirt", ShapeSex.Both)],
        [WearableType.Underpants] = [("underpants", ShapeSex.Both)],
        [WearableType.Skirt] = [("skirt", ShapeSex.Both)],
        [WearableType.Physics] = [("physics_breasts_updown", ShapeSex.Female), ("physics_breasts_inout", ShapeSex.Female), ("physics_breasts_leftright", ShapeSex.Female),
            ("physics_belly_updown", ShapeSex.Both), ("physics_butt_updown", ShapeSex.Both), ("physics_butt_leftright", ShapeSex.Both), ("physics_advanced", ShapeSex.Both)],
    };

    private readonly Dictionary<int, EditParam> _byId;
    private readonly Dictionary<WearableType, List<EditParam>> _byType;

    private EditParamCatalog(Dictionary<int, EditParam> byId)
    {
        _byId = byId;
        _byType = byId.Values.GroupBy(p => p.Wearable).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToList());
    }

    /// <summary>A catalog with nothing in it (no definition file): no sliders, and every value reads as 0.</summary>
    public static EditParamCatalog Empty { get; } = new(new Dictionary<int, EditParam>());

    public static EditParamCatalog Parse(byte[] ladXml)
    {
        var def = ShapeDefinition.Parse(ladXml);   // ranges, defaults, sex and the driver tables, with the same limits
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(ladXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var map = new Dictionary<int, EditParam>();
        foreach (var e in doc.GetElementsByTagName("param").OfType<XmlElement>())
        {
            if (!int.TryParse(e.GetAttribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0 || map.ContainsKey(id))
                continue;
            if (!TypeOf(e.GetAttribute("wearable"), out var wearable) || !def.TryGet(id, out var sp))
                continue;
            if (map.Count >= MaxParams)
                throw new InvalidDataException($"avatar definition file declares more than {MaxParams} wearable parameters");
            int group = Int(e, "group", 0);
            string name = e.GetAttribute("name");
            string label = e.GetAttribute("label") is { Length: > 0 } l ? l : name;
            map[id] = new EditParam(id, name, label, e.GetAttribute("label_min"), e.GetAttribute("label_max"), sp.Min, sp.Max, sp.Default, sp.Sex,
                Int(e, "edit_group_order", 0), e.GetAttribute("edit_group"), wearable, group is 0 or 2, sp.Driven);
        }
        return new EditParamCatalog(map);
    }

    private static bool TypeOf(string name, out WearableType type)
    {
        for (int i = 0; WearableKinds.TryFromNumber(i, out type); i++)
            if (WearableKinds.Name(type) == name)
                return true;
        type = WearableType.Shape;
        return false;
    }

    private static int Int(XmlElement e, string name, int fallback) =>
        e.HasAttribute(name) && int.TryParse(e.GetAttribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    /// <summary>The tabs of a wearable type's panel, for a male or female avatar, in the official order; tabs with no sliders are left out.</summary>
    public IReadOnlyList<EditSubpart> Subparts(WearableType type, bool male)
    {
        var sex = male ? ShapeSex.Male : ShapeSex.Female;
        var result = new List<EditSubpart>();
        if (!Tabs.TryGetValue(type, out var tabs) || !_byType.TryGetValue(type, out var all))
            return result;
        foreach (var (group, tabSex) in tabs)
        {
            if ((tabSex & sex) == 0)
                continue;
            var sliders = all.Where(p => p.Tweakable && p.EditGroup == group && (p.Sex & sex) != 0).OrderBy(p => p.Order).ThenBy(p => p.Id).ToList();
            if (sliders.Count > 0)
                result.Add(new EditSubpart(group, tabSex, sliders));
        }
        return result;
    }

    /// <summary>Every parameter the definition names for a wearable type (all groups), in id order.</summary>
    public IReadOnlyList<EditParam> ParamsOf(WearableType type) => _byType.TryGetValue(type, out var l) ? l : [];

    public bool TryGet(int id, out EditParam param) => _byId.TryGetValue(id, out param!);
}
