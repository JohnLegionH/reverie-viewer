// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Avatars;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Facial expressions as the official viewer plays them: the expression animations and the parameter each drives (llvoavatar.cpp, llanimationstates.cpp),
/// an animation's emote name (llkeyframemotion.cpp), the expression motion's curve (llemote.cpp with the motion controller's ease in and out), and how
/// several at once share the face (llmotioncontroller.cpp updateMotionsByType: the newest first, one at full weight per frame takes the face slot).
/// Expected weights are worked out here from those rules, independently of the code under test.
/// </summary>
public sealed class FaceExpressionTests
{
    private static readonly Guid Smile = Guid.Parse("b7c7c833-e3d3-c4e3-9fc0-131237446312");
    private static readonly Guid Frown = Guid.Parse("fee3df48-fa3d-1015-1e26-a205810e3001");
    private static readonly Guid Worry = Guid.Parse("9c05e5c7-6f07-6ca4-ed5a-b230390c3950");
    private static readonly Guid Embarrassed = Guid.Parse("0b8c8211-d78c-33e8-fa28-c51a9594e424");
    private static readonly Guid LaughEmote = Guid.Parse("62570842-0950-96f8-341c-809e65110823");
    private static readonly Guid AfraidBody = Guid.Parse("6b61c8e8-4747-0d75-12d7-e49ff207a4ca");   // express_afraid: a body animation, not an expression motion

    private static double Cubic(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    private static float W(FaceExpressions f, string name) => f.Weights.TryGetValue(name, out float w) ? w : name == Emotes.ClosedMouth ? 1f : 0f;

    private static void Near(double expected, float actual, string what) =>
        Assert.True(Math.Abs(expected - actual) < 1e-5, $"{what}: expected {expected:F6}, got {actual:F6}");

    [Fact]
    public void EveryOfficialExpressionAnimationDrivesItsParameter()
    {
        Assert.Equal(19, Emotes.Params.Count);
        Assert.Equal("Express_Smile", Emotes.Params[Smile]);
        Assert.Equal("Express_Frown", Emotes.Params[Frown]);
        Assert.Equal("Express_Worry_Emote", Emotes.Params[Worry]);
        Assert.Equal("Express_Embarrassed_Emote", Emotes.Params[Embarrassed]);
        Assert.Equal("Express_Laugh_Emote", Emotes.Params[LaughEmote]);
        Assert.True(Emotes.IsEmote(Smile));
        Assert.False(Emotes.IsEmote(AfraidBody));
    }

    [Fact]
    public void AnEmoteNameIsLookedUpAsTheOfficialTableDoes()
    {
        Assert.Equal(Smile, Emotes.FromEmoteName("express_smile"));
        Assert.Equal(Smile, Emotes.FromEmoteName("EXPRESS_SMILE"));
        Assert.Equal(LaughEmote, Emotes.FromEmoteName("express_laugh_emote"));
        Assert.Equal(Embarrassed, Emotes.FromEmoteName("express_embarrassed_emote"));
        Assert.Equal(AfraidBody, Emotes.FromEmoteName("express_afraid"));
        Assert.Null(Emotes.FromEmoteName("Closed_Mouth"));
        Assert.Null(Emotes.FromEmoteName(""));
        Assert.Null(Emotes.FromEmoteName("not_an_animation"));
    }

    [Fact]
    public void AnExpressionFollowsTheOfficialCurve()
    {
        var f = new FaceExpressions();
        Assert.Null(f.LastStart);
        Assert.True(f.Start(Smile, 0));
        Assert.Equal(0.0, f.LastStart);
        // fade in by cubic_step over 0.3 s, hold until the send-stop time (2.8 - 1.4 = 1.4 s), fade out by cubic_step over 1.4 s, then gone
        foreach (double t in new[] { 0.0, 0.075, 0.15, 0.3, 1.0, 1.4, 1.75, 2.1, 2.79 })
        {
            double w = t <= 0.3 ? Cubic(t / 0.3) : t <= 1.4 ? 1.0 : Cubic(1 - (t - 1.4) / 1.4);
            f.Update(t);
            Near(w, W(f, "Express_Smile"), $"smile at {t}");
            Near(1 - w, W(f, Emotes.ClosedMouth), $"closed mouth at {t}");
            Assert.True(f.Active, $"active at {t}");
        }
        f.Update(2.81);
        Near(0, W(f, "Express_Smile"), "smile once done");
        Near(1, W(f, Emotes.ClosedMouth), "closed mouth once done");
        Assert.False(f.Active);
    }

    [Fact]
    public void AnExpressionTheRegionStopsFadesOutFromWhereItIs()
    {
        var f = new FaceExpressions();
        f.Signal([(Smile, 1)], 0);
        f.Update(0.15);
        Near(0.5, W(f, "Express_Smile"), "half way in");
        f.Signal([], 0.15);
        f.Update(0.85);
        Near(0.5 * Cubic(1 - 0.7 / 1.4), W(f, "Express_Smile"), "fading out from half");
        f.Update(1.6);
        Near(0, W(f, "Express_Smile"), "done");
        Assert.False(f.Active);
    }

    [Fact]
    public void TheSameSequenceAgainDoesNotRestartAndANewOneDoes()
    {
        var f = new FaceExpressions();
        f.Signal([(Smile, 1)], 0);
        f.Update(0.5);
        f.Signal([(Smile, 1)], 0.5);
        f.Update(1.0);
        Near(1, W(f, "Express_Smile"), "still held");
        // fading out at 2.0, a new sequence number: the old one fades on, a new one starts from nothing; the old one is updated last, so its weight shows
        f.Signal([(Smile, 2)], 2.0);
        f.Update(2.15);
        double old = Cubic(1 - 0.75 / 1.4);
        Near(old, W(f, "Express_Smile"), "the older instance updates last");
        Near(1 - old, W(f, Emotes.ClosedMouth), "closed mouth from the older instance");
        f.Update(2.85);   // the old one is done and resets the parameter after the new one set it
        f.Update(2.9);
        Near(1, W(f, "Express_Smile"), "the new instance holds");
        Near(0, W(f, Emotes.ClosedMouth), "closed mouth under the new instance");
    }

    [Fact]
    public void TwoExpressionsShareTheFaceAsTheOfficialMotionLoopDoes()
    {
        var f = new FaceExpressions();
        f.Start(Smile, 0);
        f.Start(Frown, 0.5);
        f.Update(0.6);   // frown fading in (newest, updated first), smile at full takes the face slot and sets the closed mouth last
        Near(Cubic(0.1 / 0.3), W(f, "Express_Frown"), "frown fading in");
        Near(1, W(f, "Express_Smile"), "smile");
        Near(0, W(f, Emotes.ClosedMouth), "closed mouth");
        f.Update(1.0);   // both at full: the frown takes the face slot, the smile is left as it was
        Near(1, W(f, "Express_Frown"), "frown");
        Near(1, W(f, "Express_Smile"), "smile");
        Near(0, W(f, Emotes.ClosedMouth), "closed mouth");
        f.Update(2.1);   // both fading out (frown stopped at 1.9, smile at 1.4): each sets its own, the smile sets the closed mouth last
        double frown = Cubic(1 - 0.2 / 1.4), smile = Cubic(1 - 0.7 / 1.4);
        Near(frown, W(f, "Express_Frown"), "frown fading out");
        Near(smile, W(f, "Express_Smile"), "smile fading out");
        Near(1 - smile, W(f, Emotes.ClosedMouth), "closed mouth");
    }

    private static AnimationClip Clip(string emote) => new()
    {
        Duration = 2f, Loop = true, LoopIn = 0, LoopOut = 2f, Priority = 3, EaseIn = 0.5f, EaseOut = 0.5f, Emote = emote, Tracks = [],
    };

    [Fact]
    public void AnAnimationsEmoteStartsItsExpressionOnce()
    {
        var mc = new MotionController();
        var stand = Guid.NewGuid();
        var signalled = new List<SignalledMotion> { new(stand, Clip("express_smile"), 1) };
        mc.Update(0, signalled);
        mc.Expressions.Update(0.5);
        Near(1, W(mc.Expressions, "Express_Smile"), "smile while the animation starts");
        mc.Update(3.0, signalled);   // the looping animation plays on; its emote was played once
        mc.Expressions.Update(3.0);
        Near(0, W(mc.Expressions, "Express_Smile"), "the emote played once");
        Assert.False(mc.Expressions.Active);
    }

    [Fact]
    public void AnEmoteAlreadyPlayingIsNotRestartedAndClosedMouthIsIgnored()
    {
        var mc = new MotionController();
        Assert.True(mc.Expressions.Start(Smile, 0));
        mc.Update(1.0, [new SignalledMotion(Guid.NewGuid(), Clip("express_smile"), 1), new SignalledMotion(Guid.NewGuid(), Clip("Closed_Mouth"), 2)]);
        mc.Expressions.Update(1.0);
        Near(1, W(mc.Expressions, "Express_Smile"), "kept, not restarted");
        Assert.Single(mc.Expressions.Weights, kv => kv.Key.StartsWith("Express_", StringComparison.Ordinal) && kv.Key != Emotes.ClosedMouth && kv.Value != 0);
    }

    [CharacterFact]
    public void AnExpressionReshapesOnlyTheHeadAndEyelashes()
    {
        var body = AvatarBody.Load(CharacterFactAttribute.Folder!);
        var def = body.Shape!;
        var types = BodyMorph.MeshTypesOf(def, ["Express_Smile", Emotes.ClosedMouth]);
        Assert.Equal(new[] { "eyelashMesh", "headMesh" }, types.OrderBy(t => t, StringComparer.Ordinal).ToArray());
        var shape = AvatarShape.Neutral(new AvatarRig(body.Skeleton));
        var named = new Dictionary<string, float> { ["Express_Smile"] = 1f, [Emotes.ClosedMouth] = 0f };
        var full = BodyMorph.Compute(body, def, shape, named, onlyNamed: false)!;
        var neutral = BodyMorph.Compute(body, def, shape)!;
        var some = BodyMorph.ComputeParts(body, def, shape, named, onlyNamed: false, types);
        Assert.NotNull(some);
        int head = 0;
        for (int i = 0; i < body.Parts.Count; i++)
        {
            if (!types.Contains(body.Parts[i].Type)) { Assert.Null(some![i]); continue; }
            Assert.Equal(full[i].Positions, some![i]!.Positions);
            Assert.Equal(full[i].Normals, some[i]!.Normals);
            if (body.Parts[i].Type == "headMesh" && !full[i].Positions.SequenceEqual(neutral[i].Positions)) head++;
        }
        Assert.Equal(1, head);   // the smile moves the head
    }
}
