// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llwearabletype.cpp
 * @brief LLWearableType class implementation
 *
 * $LicenseInfo:firstyear=2002&license=viewerlgpl$
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
 * @file llavatarappearancedefines.cpp
 * @brief Implementation of LLAvatarAppearanceDefines::LLAvatarAppearanceDictionary
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

/**
 * @File llavatarappearance.cpp
 * @brief Implementation of LLAvatarAppearance class
 *
 * $LicenseInfo:firstyear=2012&license=viewerlgpl$
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


// Adapted 2026 by Legion Builds. The wearable kinds' names, numbers and asset types (llwearabletype.cpp), the texture slot numbers
// (llavatarappearancedefines.cpp), the colour parameters of a slot (teToColorParams in llavatarappearance.cpp) and which slots a panel offers and
// whether one may be left empty (llpaneleditwearable.cpp) are the files' tables, written out in C#. The rest of this file is our own code. The slots' default textures follow
// llviewerwearable.cpp (getDefaultTextureImageID) and the UIImgDefault*UUID values of app_settings/settings.xml.
// Copyright (c) 2026 Legion Builds

using Viewer.Core.Inventory;

namespace Viewer.Core.Appearance;

/// <summary>A texture slot of a wearable: its number in the avatar's texture table, a short name for the interface,
/// and whether it may be left empty.</summary>
public sealed record TextureSlot(int Index, string Name, bool AllowNone);

/// <summary>A colour swatch: the texture slot it tints and the three visual parameters (red, green, blue weights, 0 to 1) that hold the colour.</summary>
public sealed record ColourSwatch(int Slot, int Red, int Green, int Blue);

/// <summary>
/// What the wearable types are: asset kind, texture slots and colour parameters. Pure tables. The numbers follow the official
/// viewer's tables (indra/llappearance/llwearabletype.cpp, llavatarappearancedefines.cpp, llavatarappearance.cpp
/// <c>teToColorParams</c>, and the picker lists in indra/newview/llpaneleditwearable.cpp); the code is our own.
/// </summary>
public static class WearableKinds
{
    public const int AssetTypeClothing = 5, AssetTypeBodypart = 13;

    private static readonly System.Collections.Immutable.ImmutableArray<string> Names =
        ["shape", "skin", "hair", "eyes", "shirt", "pants", "shoes", "socks", "jacket", "gloves", "undershirt", "underpants", "skirt", "alpha", "tattoo", "physics", "universal"];

    public static bool IsBodyPart(WearableType t) => t is WearableType.Shape or WearableType.Skin or WearableType.Hair or WearableType.Eyes;

    public static int AssetType(WearableType t) => IsBodyPart(t) ? AssetTypeBodypart : AssetTypeClothing;

    /// <summary>The type's lower-case name as the avatar definition file writes it ("" for <see cref="WearableType.None"/>).</summary>
    public static string Name(WearableType t) => (int)t >= 0 && (int)t < Names.Length ? Names[(int)t] : "";

    public static bool TryFromNumber(int number, out WearableType type)
    {
        type = WearableType.Shape;
        if (number < 0 || number >= Names.Length)
            return false;
        type = (WearableType)number;
        return true;
    }

    private static TextureSlot Slot(int index, string name, bool none = false) => new(index, name, none);

    private static readonly IReadOnlyList<TextureSlot> NoSlots = [];

    private static readonly IReadOnlyDictionary<WearableType, IReadOnlyList<TextureSlot>> SlotTable = new Dictionary<WearableType, IReadOnlyList<TextureSlot>>
    {
        [WearableType.Skin] = [Slot(0, "head", true), Slot(5, "upper", true), Slot(6, "lower", true)],
        [WearableType.Hair] = [Slot(4, "texture")],
        [WearableType.Eyes] = [Slot(3, "iris")],
        [WearableType.Shirt] = [Slot(1, "fabric")],
        [WearableType.Pants] = [Slot(2, "fabric")],
        [WearableType.Shoes] = [Slot(7, "fabric")],
        [WearableType.Socks] = [Slot(12, "fabric")],
        [WearableType.Jacket] = [Slot(13, "upper_fabric"), Slot(14, "lower_fabric")],
        [WearableType.Gloves] = [Slot(15, "fabric")],
        [WearableType.Undershirt] = [Slot(16, "fabric")],
        [WearableType.Underpants] = [Slot(17, "fabric")],
        [WearableType.Skirt] = [Slot(18, "fabric")],
        [WearableType.Alpha] = [Slot(21, "lower_alpha", true), Slot(22, "upper_alpha", true), Slot(23, "head_alpha", true), Slot(24, "eyes_alpha", true), Slot(25, "hair_alpha", true)],
        [WearableType.Tattoo] = [Slot(28, "lower_tattoo", true), Slot(27, "upper_tattoo", true), Slot(26, "head_tattoo", true)],
        [WearableType.Universal] = [Slot(29, "head_universal", true), Slot(30, "upper_universal", true), Slot(31, "lower_universal", true), Slot(32, "skirt_tattoo", true),
            Slot(33, "hair_tattoo", true), Slot(34, "eyes_tattoo", true), Slot(35, "leftarm_tattoo", true), Slot(36, "leftleg_tattoo", true),
            Slot(37, "aux1_tattoo", true), Slot(38, "aux2_tattoo", true), Slot(39, "aux3_tattoo", true)],
    };

    public static IReadOnlyList<TextureSlot> Slots(WearableType t) => SlotTable.GetValueOrDefault(t) ?? NoSlots;

    private static readonly Guid ImgDefaultAvatar = Guid.Parse("c228d1cf-4b5d-4ba8-84f4-899a0796aa97");
    private static readonly Guid ImgWhite = Guid.Parse("5748decc-f629-461c-9a36-a35a221fe21f");
    private static readonly Guid ImgEyes = Guid.Parse("6522e74d-1660-4e7f-b601-6f48c1659a77");
    private static readonly Guid ImgHair = Guid.Parse("7ca39b4c-bd19-4699-aff7-f93fd03d3e7b");

    /// <summary>
    /// The texture a slot holds when it is reset, and the one a new wearable's file writes for it (LLViewerWearable::getDefaultTextureImageID
    /// and setTexturesToDefaults): the type's default image setting where the avatar definition names one (eyes and hair their own pictures,
    /// clothing and alpha the plain white texture; app_settings/settings.xml UIImgDefault*UUID), else the default avatar texture (skin, tattoo
    /// and universal slots). Empty for a slot the type does not have.
    /// </summary>
    public static Guid DefaultTexture(WearableType t, int slot)
    {
        if (!Slots(t).Any(s => s.Index == slot))
            return Guid.Empty;
        return t switch
        {
            WearableType.Eyes => ImgEyes,
            WearableType.Hair => ImgHair,
            WearableType.Skin or WearableType.Tattoo or WearableType.Universal => ImgDefaultAvatar,
            _ => ImgWhite,
        };
    }

    private static readonly IReadOnlyList<ColourSwatch> NoSwatches = [];

    private static readonly IReadOnlyDictionary<WearableType, IReadOnlyList<ColourSwatch>> SwatchTable = new Dictionary<WearableType, IReadOnlyList<ColourSwatch>>
    {
        [WearableType.Shirt] = [new(1, 803, 804, 805)],
        [WearableType.Pants] = [new(2, 806, 807, 808)],
        [WearableType.Shoes] = [new(7, 812, 813, 817)],
        [WearableType.Socks] = [new(12, 818, 819, 820)],
        [WearableType.Jacket] = [new(13, 834, 835, 836)],
        [WearableType.Gloves] = [new(15, 827, 829, 830)],
        [WearableType.Undershirt] = [new(16, 821, 822, 823)],
        [WearableType.Underpants] = [new(17, 824, 825, 826)],
        [WearableType.Skirt] = [new(18, 921, 922, 923)],
        [WearableType.Tattoo] = [new(26, 1071, 1072, 1073)],
        [WearableType.Universal] = [new(29, 1238, 1239, 1240)],
    };

    public static IReadOnlyList<ColourSwatch> Swatches(WearableType t) => SwatchTable.GetValueOrDefault(t) ?? NoSwatches;
}
