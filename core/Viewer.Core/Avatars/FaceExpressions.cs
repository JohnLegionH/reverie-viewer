// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llemote.cpp
 * @brief Implementation of LLEmote class
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
 * @file llmotioncontroller.cpp
 * @brief Implementation of LLMotionController class.
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
 * @file llkeyframemotion.cpp
 * @brief Implementation of LLKeyframeMotion class.
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

// Adapted 2026 by Legion Builds. The facial expression motion (LLEmote: its fade-in, hold and fade-out times, the weight it gives its expression
// parameter and the cross-fade against Express_Closed_Mouth), the way the motion controller weighs and orders motions that share the face slot
// (LLMotionController startMotion, activateMotionInstance and updateMotionsByType), the start of an animation's emote when it activates
// (LLKeyframeMotion::onActivate and the emote name read in deserialize) and the names and ids of the expression animations (llanimationstates.cpp)
// follow the files named above, rewritten in C# for our own types (release 26.4).
// The rest of this file is our own code.
// Copyright (c) 2026 Legion Builds

using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Viewer.Core.Avatars;

/// <summary>The built-in facial expression animations: their ids, the expression parameter each one drives, and the names an animation file may give as its emote.</summary>
public static class Emotes
{
    /// <summary>The parameter every expression cross-fades against: the closed mouth, at its maximum while no expression plays.</summary>
    public const string ClosedMouth = "Express_Closed_Mouth";

    /// <summary>Seconds an expression takes to fade in, to hold and to fade out (llemote.h).</summary>
    public const double FadeIn = 0.3, Hold = 1.1, FadeOut = 1.4;

    // The expression animations (ANIM_AGENT_EXPRESS_*), each with the name the animation table gives it (llanimationstates.cpp). The official
    // expression motion looks its parameter up by that name, ignoring case, so each name is the parameter's.
    private static readonly ImmutableArray<(string Id, string Name)> Express =
    [
        ("aa2df84d-cf8f-7218-527b-424a52de766e", "Express_Afraid_Emote"),
        ("1a03b575-9634-b62a-5767-3a679e81f4de", "Express_Anger_Emote"),
        ("214aa6c1-ba6a-4578-f27c-ce7688f61d0d", "Express_Bored_Emote"),
        ("d535471b-85bf-3b4d-a542-93bea4f59d33", "Express_Cry_Emote"),
        ("d4416ff1-09d3-300f-4183-1b68a19b9fc1", "Express_Disdain"),
        ("0b8c8211-d78c-33e8-fa28-c51a9594e424", "Express_Embarrassed_Emote"),
        ("fee3df48-fa3d-1015-1e26-a205810e3001", "Express_Frown"),
        ("1e8d90cc-a84e-e135-884c-7c82c8b03a14", "Express_Kiss"),
        ("62570842-0950-96f8-341c-809e65110823", "Express_Laugh_Emote"),
        ("d63bc1f9-fc81-9625-a0c6-007176d82eb7", "Express_Open_Mouth"),
        ("f76cda94-41d4-a229-2872-e0296e58afe1", "Express_Repulsed_Emote"),
        ("eb6ebfb2-a4b3-a19c-d388-4dd5c03823f7", "Express_Sad_Emote"),
        ("a351b1bc-cc94-aac2-7bea-a7e6ebad15ef", "Express_Shrug_Emote"),
        ("b7c7c833-e3d3-c4e3-9fc0-131237446312", "Express_Smile"),
        ("728646d9-cc79-08b2-32d6-937f0a835c24", "Express_Surprise_Emote"),
        ("835965c6-7f2f-bda2-5deb-2478737f91bf", "Express_Tongue_Out"),
        ("b92ec1a5-e7ce-a76b-2b05-bcdb9311417e", "Express_Toothsmile"),
        ("da020525-4d94-59d6-23d7-81fdebf33148", "Express_Wink_Emote"),
        ("9c05e5c7-6f07-6ca4-ed5a-b230390c3950", "Express_Worry_Emote"),
    ];

    // The body animations whose table names start with "express_" and are not expression motions: an emote name may name one of them, which
    // the official viewer then plays as an animation; listed so the lookup answers as the official table does.
    private static readonly ImmutableArray<(string Id, string Name)> ExpressBody = [
        ("6b61c8e8-4747-0d75-12d7-e49ff207a4ca", "express_afraid"),
        ("5747a48e-073e-c331-f6f3-7c2149613d3e", "express_anger"),
        ("18b3a4b5-b463-bd48-e4b6-71eaac76c515", "express_laugh"),
        ("b906c4ba-703b-1940-32a3-0c7f7d791510", "express_bored"),
        ("92624d3e-1068-f1aa-a5ec-8244585193ed", "express_cry"),
        ("514af488-9051-044a-b3fc-d4dbf76377c6", "express_embarrased"),
        ("36f81a92-f076-5893-dc4b-7c3795e487cf", "express_repulsed"),
        ("0eb702e2-cc5a-9a88-56a5-661a55c0676a", "express_sad"),
        ("70ea714f-3a97-d742-1b01-590a8fcd1db5", "express_shrug"),
        ("313b9881-4302-73c0-c7d0-0e7a36b6c224", "express_surprise"),
        ("869ecdad-a44b-671e-3266-56aef2e3ac2e", "express_wink"),
        ("9f496bd2-589a-709f-16cc-69bf7df1d36c", "express_worry"),
    ];

    /// <summary>The expression animations the official viewer plays as expressions, by id, with the parameter each drives.</summary>
    public static IReadOnlyDictionary<Guid, string> Params { get; } = Express.ToFrozenDictionary(e => Guid.Parse(e.Id), e => e.Name);

    private static readonly FrozenDictionary<string, Guid> ByName = Express.Concat(ExpressBody)
        .ToFrozenDictionary(e => e.Name, e => Guid.Parse(e.Id), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="id"/> is one of the expression animations.</summary>
    public static bool IsEmote(Guid id) => Params.ContainsKey(id);

    /// <summary>The animation an animation file's emote name names (the official animation name table, any case); null for none, for an unknown name
    /// and for "Closed_Mouth", which the official viewer ignores.</summary>
    public static Guid? FromEmoteName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == "Closed_Mouth") return null;
        return ByName.TryGetValue(name, out var id) ? id : null;
    }
}

/// <summary>
/// The facial expressions playing on one avatar, as the official viewer's expression motions play them: each starts at no weight, fades in, holds,
/// stops on its own and fades out; its expression parameter follows its weight and the closed mouth its complement. Weights are worked out at the
/// caller's clock in seconds, the newest expression first. Not thread-safe; one per avatar.
/// </summary>
public sealed class FaceExpressions
{
    private sealed class Instance(Guid id, string param, double now)
    {
        public readonly Guid Id = id;
        public readonly string Param = param;
        // an expression does not loop and lasts fade-in + hold + fade-out: it stops itself that long less its fade-out after it starts (activateMotionInstance)
        public MotionTiming Timing = new(now, Emotes.FadeIn, Emotes.FadeOut, now + Emotes.FadeIn + Emotes.Hold, 1.0);
        public bool Deprecated;
        public bool Stopped(double t) => Timing.Stop is { } s && s <= t;
    }

    private readonly List<Instance> _live = new();                  // the newest first, as the official active list (push_front)
    private readonly Dictionary<string, float> _weights = new();
    private readonly Dictionary<Guid, int> _signalled = new();

    /// <summary>The expression parameters' weights as last worked out, by parameter name (the closed mouth included); empty while nothing has played.</summary>
    public IReadOnlyDictionary<string, float> Weights => _weights;

    /// <summary>The clock time the latest expression started at (null: none has).</summary>
    public double? LastStart { get; private set; }

    /// <summary>Whether any expression plays (eases in, holds or eases out).</summary>
    public bool Active => _live.Count > 0;

    /// <summary>Starts expression <paramref name="id"/> at <paramref name="now"/> (llmotioncontroller.cpp startMotion): false when it is not an
    /// expression, or when it already plays at full weight and has not stopped.</summary>
    public bool Start(Guid id, double now)
    {
        if (!Emotes.Params.TryGetValue(id, out var param)) return false;
        var current = _live.FirstOrDefault(i => i.Id == id && !i.Deprecated);
        if (current is not null)
        {
            // one that is blending or stopping is deprecated (it finishes fading out) and a new one starts; one at full weight keeps playing
            bool blending = current.Timing.Weight(now) < 1.0;
            if (!blending && !current.Stopped(now)) return false;
            current.Deprecated = true;
            if (!current.Stopped(now)) current.Timing = current.Timing.StoppedAt(now);
        }
        _live.Insert(0, new Instance(id, param, now));
        LastStart = now;
        // onActivate: the closed mouth at its maximum, the expression's own parameter at nothing
        _weights[Emotes.ClosedMouth] = 1f;
        _weights[param] = 0f;
        return true;
    }

    /// <summary>Starts expression <paramref name="id"/> unless it is already playing (an animation's emote, llkeyframemotion.cpp onActivate).</summary>
    public bool StartIfInactive(Guid id, double now) => !_live.Any(i => i.Id == id) && Start(id, now);

    /// <summary>Stops expression <paramref name="id"/> at <paramref name="now"/>: it fades out from the weight it has.</summary>
    public bool Stop(Guid id, double now)
    {
        bool any = false;
        foreach (var i in _live.Where(i => i.Id == id && !i.Stopped(now))) { i.Timing = i.Timing.StoppedAt(now); any = true; }
        return any;
    }

    /// <summary>What the region says plays, as the official viewer applies it to expressions (llvoavatar.cpp processAnimationStateChanges): an
    /// expression newly named, or named with a new sequence number, starts; one no longer named stops. Other animations are ignored.</summary>
    public void Signal(IEnumerable<(Guid Id, int Sequence)> playing, double now)
    {
        if (_signalled.Count == 0 && !playing.Any(p => Emotes.IsEmote(p.Id))) return;   // the usual frame: no expression named before or now
        var wanted = new Dictionary<Guid, int>();
        foreach (var (id, seq) in playing)
            if (Emotes.IsEmote(id)) wanted[id] = wanted.TryGetValue(id, out int s) ? Math.Max(s, seq) : seq;
        foreach (var id in _signalled.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            _signalled.Remove(id);
            Stop(id, now);
        }
        foreach (var (id, seq) in wanted)
        {
            if (_signalled.TryGetValue(id, out int had) && had == seq) continue;
            _signalled[id] = seq;
            Start(id, now);
        }
    }

    /// <summary>Works out the weights at <paramref name="now"/>; true when any weight changed.</summary>
    public bool Update(double now)
    {
        if (_live.Count == 0) return false;
        bool changed = false;
        bool faceTaken = false;   // the face slot of the joint signature: the first expression at full weight takes it, later ones at full weight are left as they are
        for (int k = 0; k < _live.Count; k++)
        {
            var i = _live[k];
            if (i.Timing.Finished(now))
            {
                // onDeactivate: the parameter back to its default, the closed mouth to its maximum
                changed |= Set(i.Param, 0f) | Set(Emotes.ClosedMouth, 1f);
                _live.RemoveAt(k--);
                continue;
            }
            double w = i.Timing.Weight(now);
            if (w >= 1.0)
            {
                if (faceTaken) continue;
                faceTaken = true;
            }
            // onUpdate: the parameter by the motion's weight (between its minimum 0 and maximum 1), the closed mouth by its complement
            changed |= Set(i.Param, (float)w) | Set(Emotes.ClosedMouth, (float)(1.0 - w));
        }
        return changed;
    }

    private bool Set(string name, float w)
    {
        if (_weights.TryGetValue(name, out float had) && had == w) return false;
        _weights[name] = w;
        return true;
    }
}
