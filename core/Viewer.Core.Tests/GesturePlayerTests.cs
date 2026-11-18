// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Inventory;
using Viewer.Shell.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Playing a gesture as the official gesture manager steps one (llgesturemgr.cpp stepGesture and runStep): every step runs at once until a
/// wait; a timed wait waits its seconds; "until animations are done" waits until the animations the gesture started have stopped; at the
/// end the gesture waits for its animations, then stops any still playing. And the active gestures: a line of chat plays the gesture its
/// trigger names and is revised, a key plays the gesture bound to it. A fake clock and fake effects; pure, runs in parallel.
/// </summary>
public class GesturePlayerTests
{
    private static readonly Guid Wave = Guid.Parse("c541c47f-e0c0-058b-ad1a-d6ae3a4584d9");
    private static readonly Guid Bow = Guid.Parse("82e99230-c906-1403-4d9c-3889dd98daba");
    private static readonly Guid Chime = Guid.Parse("b1b2b3b4-0000-4000-8000-000000000001");

    private sealed class Fx : IGestureEffects
    {
        public readonly List<string> Log = new();
        public readonly HashSet<Guid> Playing = new();
        public int PlayTicks = 2;   // an animation plays for this many polls after it starts
        private readonly Dictionary<Guid, int> _left = new();

        public void Animate(Guid animation, bool start)
        {
            Log.Add((start ? "start " : "stop ") + Name(animation));
            if (start) { Playing.Add(animation); _left[animation] = PlayTicks; }
            else Playing.Remove(animation);
        }
        public void Sound(Guid sound) => Log.Add("sound " + Name(sound));
        public void Say(string text) => Log.Add("say " + text);
        public bool IsPlaying(Guid animation) => Playing.Contains(animation);

        public Task Delay(TimeSpan span, CancellationToken ct)
        {
            Log.Add("wait " + span.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var a in Playing.ToArray())
                if (--_left[a] <= 0)
                    Playing.Remove(a);
            return Task.CompletedTask;
        }

        private static string Name(Guid g) => g == Wave ? "wave" : g == Bow ? "bow" : g == Chime ? "chime" : "?";
    }

    [Fact]
    public async Task Steps_run_in_order_and_a_timed_wait_waits_its_seconds()
    {
        var g = new GestureDocument { Steps = { GestureStep.Chat("hello"), GestureStep.Sound("chime", Chime), GestureStep.WaitTime(1.5f), GestureStep.Chat("there") } };
        var fx = new Fx();
        await GesturePlayer.PlayAsync(g, fx, fx.Delay, CancellationToken.None);
        Assert.Equal(new[] { "say hello", "sound chime", "wait 1.5", "say there" }, fx.Log);
    }

    [Fact]
    public async Task Waiting_for_animations_waits_until_those_started_have_stopped()
    {
        var g = new GestureDocument { Steps = { GestureStep.Animation("wave", Wave, stop: false), GestureStep.WaitAnimations(), GestureStep.Chat("done") } };
        var fx = new Fx { PlayTicks = 3 };
        await GesturePlayer.PlayAsync(g, fx, fx.Delay, CancellationToken.None);
        Assert.NotEmpty(fx.Log);
        Assert.Equal("start wave", fx.Log[0]);
        Assert.Equal("say done", fx.Log[^1]);
        Assert.Equal(3, fx.Log.Count(l => l.StartsWith("wait ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_stop_step_stops_its_animation_and_the_end_waits_for_the_rest_without_stopping_them()
    {
        // llgesturemgr.cpp stepGesture: at the end the gesture waits, with no time limit, until its animations have stopped of themselves
        var g = new GestureDocument { Steps = { GestureStep.Animation("wave", Wave, stop: false), GestureStep.Animation("bow", Bow, stop: false), GestureStep.Animation("wave", Wave, stop: true) } };
        var fx = new Fx { PlayTicks = 400 };   // 40 seconds of polls: longer than the 30-second limit of a wait step
        await GesturePlayer.PlayAsync(g, fx, fx.Delay, CancellationToken.None);
        var acts = fx.Log.Where(l => !l.StartsWith("wait ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "start wave", "start bow", "stop wave" }, acts);
        Assert.Equal(400, fx.Log.Count(l => l.StartsWith("wait ", StringComparison.Ordinal)));
        Assert.Empty(fx.Playing);
    }

    [Fact]
    public async Task A_wait_for_animations_step_gives_up_after_30_seconds()
    {
        // MAX_WAIT_ANIM_SECS = 30 (llgesturemgr.cpp), for the step only
        var g = new GestureDocument { Steps = { GestureStep.Animation("wave", Wave, stop: false), GestureStep.WaitAnimations(), GestureStep.Chat("on"), GestureStep.Animation("wave", Wave, stop: true) } };
        var fx = new Fx { PlayTicks = 100000 };
        await GesturePlayer.PlayAsync(g, fx, fx.Delay, CancellationToken.None);
        int said = fx.Log.IndexOf("say on");
        Assert.Equal(300, fx.Log.Take(said).Count(l => l.StartsWith("wait ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_chat_line_with_an_active_trigger_is_revised_and_plays_the_gesture()
    {
        var played = new List<GestureDocument>();
        var set = new ActiveGestures(g => played.Add(g), pick: _ => 0);
        var wave = new GestureDocument { Trigger = "/wave", Replace = "Hello!" };
        set.Set(Guid.NewGuid(), wave);
        Assert.Equal("Hello! all", set.Revise("/wave all"));
        Assert.Same(wave, Assert.Single(played));
        Assert.Null(set.Revise("no trigger here"));
        Assert.Single(played);
    }

    [Fact]
    public void A_key_plays_the_gesture_bound_to_it_and_an_inactive_gesture_plays_nothing()
    {
        var played = new List<GestureDocument>();
        var set = new ActiveGestures(g => played.Add(g), pick: _ => 0);
        var item = Guid.NewGuid();
        var g = new GestureDocument { Key = GestureKeys.F3, Mask = GestureKeys.MaskNone };
        set.Set(item, g);
        Assert.True(set.Key(GestureKeys.F3, GestureKeys.MaskNone));
        Assert.False(set.Key(GestureKeys.F2, GestureKeys.MaskNone));
        set.Remove(item);
        Assert.False(set.Key(GestureKeys.F3, GestureKeys.MaskNone));
        Assert.Single(played);
    }
}
