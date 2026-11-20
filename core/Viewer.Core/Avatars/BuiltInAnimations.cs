// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llanimationstates.cpp
 * @brief Implementation of animation state related functions.
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


// Adapted 2026 by Legion Builds from indra/llcharacter/llanimationstates.cpp (gUserAnimStates: the animation names and their asset ids, in the
// official order) and the labels from indra/newview/skins/default/xui/en/strings.xml (the anim_<name> strings), written out in C#. The rest
// of this file is our own code.
// Copyright (c) 2026 Legion Builds

namespace Viewer.Core.Avatars;

/// <summary>One of the viewer's built-in animations a gesture may play: its internal name, the label the interface shows, and its asset id
/// (the same on every grid).</summary>
public sealed record BuiltInAnimation(string Name, string Label, Guid Id);

/// <summary>
/// The built-in animations the official viewer's gesture editor offers before the agent's own (LLPreviewGesture::addAnimations, which
/// lists gUserAnimStates with the readable label of each, then the animations in the inventory). The list and its order are the
/// official table's.
/// </summary>
public static class BuiltInAnimations
{
    public static IReadOnlyList<BuiltInAnimation> All { get; } =
    [
        new("express_afraid", "Afraid", Guid.Parse("6b61c8e8-4747-0d75-12d7-e49ff207a4ca")),
        new("express_anger", "Angry", Guid.Parse("5747a48e-073e-c331-f6f3-7c2149613d3e")),
        new("away", "Away", Guid.Parse("fd037134-85d4-f241-72c6-4f42164fedee")),
        new("backflip", "Backflip", Guid.Parse("c4ca6188-9127-4f31-0158-23c4e2f93304")),
        new("express_laugh", "Belly Laugh", Guid.Parse("18b3a4b5-b463-bd48-e4b6-71eaac76c515")),
        new("express_toothsmile", "BigSmile", Guid.Parse("b92ec1a5-e7ce-a76b-2b05-bcdb9311417e")),
        new("blowkiss", "Blow Kiss", Guid.Parse("db84829b-462c-ee83-1e27-9bbee66bd624")),
        new("express_bored", "Bored", Guid.Parse("b906c4ba-703b-1940-32a3-0c7f7d791510")),
        new("bow", "Bow", Guid.Parse("82e99230-c906-1403-4d9c-3889dd98daba")),
        new("clap", "Clap", Guid.Parse("9b0c1c4e-8ac7-7969-1494-28c874c4f668")),
        new("courtbow", "Court Bow", Guid.Parse("9ba1c942-08be-e43a-fb29-16ad440efc50")),
        new("express_cry", "Cry", Guid.Parse("92624d3e-1068-f1aa-a5ec-8244585193ed")),
        new("dance1", "Dance 1", Guid.Parse("b68a3d7c-de9e-fc87-eec8-543d787e5b0d")),
        new("dance2", "Dance 2", Guid.Parse("928cae18-e31d-76fd-9cc9-2f55160ff818")),
        new("dance3", "Dance 3", Guid.Parse("30047778-10ea-1af7-6881-4db7a3a5a114")),
        new("dance4", "Dance 4", Guid.Parse("951469f4-c7b2-c818-9dee-ad7eea8c30b7")),
        new("dance5", "Dance 5", Guid.Parse("4bd69a1d-1114-a0b4-625f-84e0a5237155")),
        new("dance6", "Dance 6", Guid.Parse("cd28b69b-9c95-bb78-3f94-8d605ff1bb12")),
        new("dance7", "Dance 7", Guid.Parse("a54d8ee2-28bb-80a9-7f0c-7afbbe24a5d6")),
        new("dance8", "Dance 8", Guid.Parse("b0dc417c-1f11-af36-2e80-7e7489fa7cdc")),
        new("express_disdain", "Disdain", Guid.Parse("d4416ff1-09d3-300f-4183-1b68a19b9fc1")),
        new("drink", "Drink", Guid.Parse("0f86e355-dd31-a61c-fdb0-3a96b9aad05f")),
        new("express_embarrased", "Embarrassed", Guid.Parse("514af488-9051-044a-b3fc-d4dbf76377c6")),
        new("angry_fingerwag", "Finger Wag", Guid.Parse("c1bc7f36-3ba0-d844-f93c-93be945d644f")),
        new("fist_pump", "Fist Pump", Guid.Parse("7db00ccd-f380-f3ee-439d-61968ec69c8a")),
        new("yoga_float", "Floating Yoga", Guid.Parse("42ecd00b-9947-a97c-400a-bbc9174c7aeb")),
        new("express_frown", "Frown", Guid.Parse("fee3df48-fa3d-1015-1e26-a205810e3001")),
        new("impatient", "Impatient", Guid.Parse("5ea3991f-c293-392e-6860-91dfa01278a3")),
        new("jumpforjoy", "Jump For Joy", Guid.Parse("709ea28e-1573-c023-8bf8-520c8bc637fa")),
        new("kissmybutt", "Kiss My Butt", Guid.Parse("19999406-3a3a-d58c-a2ac-d72e555dcf51")),
        new("express_kiss", "Kiss", Guid.Parse("1e8d90cc-a84e-e135-884c-7c82c8b03a14")),
        new("laugh_short", "Laugh", Guid.Parse("ca5b3f14-3194-7a2b-c894-aa699b718d1f")),
        new("musclebeach", "Muscle Beach", Guid.Parse("315c3a41-a5f3-0ba4-27da-f893f769e69b")),
        new("no_unhappy", "No (Unhappy)", Guid.Parse("d83fa0e5-97ed-7eb2-e798-7bd006215cb4")),
        new("no_head", "No", Guid.Parse("5a977ed9-7f72-44e9-4c4c-6e913df8ae74")),
        new("nyanya", "Nya-nya-nya", Guid.Parse("f061723d-0a18-754f-66ee-29a44795a32f")),
        new("punch_onetwo", "One-Two Punch", Guid.Parse("eefc79be-daae-a239-8c04-890f5d23654a")),
        new("express_open_mouth", "Open Mouth", Guid.Parse("d63bc1f9-fc81-9625-a0c6-007176d82eb7")),
        new("peace", "Peace", Guid.Parse("b312b10e-65ab-a0a4-8b3c-1326ea8e3ed9")),
        new("point_you", "Point at Other", Guid.Parse("ec952cca-61ef-aa3b-2789-4d1344f016de")),
        new("point_me", "Point at Self", Guid.Parse("17c024cc-eef2-f6a0-3527-9869876d7752")),
        new("punch_l", "Punch Left", Guid.Parse("f3300ad9-3462-1d07-2044-0fef80062da0")),
        new("punch_r", "Punch Right", Guid.Parse("c8e42d32-7310-6906-c903-cab5d4a34656")),
        new("rps_countdown", "RPS count", Guid.Parse("35db4f7e-28c2-6679-cea9-3ee108f7fc7f")),
        new("rps_paper", "RPS paper", Guid.Parse("0836b67f-7f7b-f37b-c00a-460dc1521f5a")),
        new("rps_rock", "RPS rock", Guid.Parse("42dd95d5-0bc6-6392-f650-777304946c0f")),
        new("rps_scissors", "RPS scissors", Guid.Parse("16803a9f-5140-e042-4d7b-d28ba247c325")),
        new("express_repulsed", "Repulsed", Guid.Parse("f76cda94-41d4-a229-2872-e0296e58afe1")),
        new("kick_roundhouse_r", "Roundhouse Kick", Guid.Parse("49aea43b-5ac3-8a44-b595-96100af0beda")),
        new("express_sad", "Sad", Guid.Parse("0eb702e2-cc5a-9a88-56a5-661a55c0676a")),
        new("salute", "Salute", Guid.Parse("cd7668a6-7011-d7e2-ead8-fc69eff1a104")),
        new("shout", "Shout", Guid.Parse("6bd01860-4ebd-127a-bb3d-d1427e8e0c42")),
        new("express_shrug", "Shrug", Guid.Parse("70ea714f-3a97-d742-1b01-590a8fcd1db5")),
        new("express_smile", "Smile", Guid.Parse("b7c7c833-e3d3-c4e3-9fc0-131237446312")),
        new("smoke_idle", "Smoke Idle", Guid.Parse("d2f2ee58-8ad1-06c9-d8d3-3827ba31567a")),
        new("smoke_inhale", "Smoke Inhale", Guid.Parse("6802d553-49da-0778-9f85-1599a2266526")),
        new("smoke_throw_down", "Smoke Throw Down", Guid.Parse("0a9fb970-8b44-9114-d3a9-bf69cfe804d6")),
        new("express_surprise", "Surprise", Guid.Parse("313b9881-4302-73c0-c7d0-0e7a36b6c224")),
        new("sword_strike_r", "Sword Strike", Guid.Parse("85428680-6bf9-3e64-b489-6f81087c24bd")),
        new("angry_tantrum", "Tantrum", Guid.Parse("11000694-3f41-adc2-606b-eee1d66f3724")),
        new("express_tongue_out", "TongueOut", Guid.Parse("835965c6-7f2f-bda2-5deb-2478737f91bf")),
        new("hello", "Wave", Guid.Parse("9b29cd61-c45b-5689-ded2-91756b8d76a9")),
        new("whisper", "Whisper", Guid.Parse("7693f268-06c7-ea71-fa21-2b30d6533f8f")),
        new("whistle", "Whistle", Guid.Parse("b1ed7982-c68e-a982-7561-52a88a5298c0")),
        new("express_wink", "Wink", Guid.Parse("869ecdad-a44b-671e-3266-56aef2e3ac2e")),
        new("wink_hollywood", "Wink (Hollywood)", Guid.Parse("c0c4030f-c02b-49de-24ba-2331f43fe41c")),
        new("express_worry", "Worry", Guid.Parse("9c05e5c7-6f07-6ca4-ed5a-b230390c3950")),
        new("yes_happy", "Yes (Happy)", Guid.Parse("b8c8b2a3-9008-1771-3bfc-90924955ab2d")),
        new("yes_head", "Yes", Guid.Parse("15dd911d-be82-2856-26db-27659b142875")),
    ];

    private static readonly IReadOnlyDictionary<Guid, string> Labels = All.ToDictionary(a => a.Id, a => a.Label);

    /// <summary>The label of a built-in animation by its asset id; null for any other animation.</summary>
    public static string? LabelOf(Guid id) => Labels.GetValueOrDefault(id);

    /// <summary>What the gesture editor's animation list offers: every built-in animation under its label in the official order, then the
    /// given inventory animations sorted by name (case-insensitive, as the official list sorts them).</summary>
    public static IReadOnlyList<(Guid Id, string Name)> Choices(IEnumerable<(Guid Id, string Name)> inventory)
    {
        var list = All.Select(a => (a.Id, Name: a.Label)).ToList();
        list.AddRange(inventory.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase));
        return list;
    }
}
