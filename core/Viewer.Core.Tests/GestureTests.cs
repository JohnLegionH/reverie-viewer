// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;
using Viewer.Core.Inventory;
using Xunit;

namespace Viewer.Core.Tests;

/// <summary>
/// Gestures: the official viewer's gesture file (version 2: key, modifier, trigger, replacement and the steps in order), the
/// editor's step list (add, up, down, remove), the shortcut keys it offers, and how a line of chat or a key plays an active
/// gesture. Pure, runs in parallel.
/// </summary>
public class GestureTests
{
    private static readonly Guid Wave = Guid.Parse("c541c47f-e0c0-058b-ad1a-d6ae3a4584d9");
    private static readonly Guid Chime = Guid.Parse("b1b2b3b4-0000-4000-8000-000000000001");

    private static GestureDocument Sample() => new()
    {
        Key = GestureKeys.F2, Mask = GestureKeys.MaskShift, Trigger = "/wave", Replace = "Hello!",
        Steps =
        {
            GestureStep.Animation("wave", Wave, stop: false),
            GestureStep.Sound("chime", Chime),
            GestureStep.Chat("waves"),
            GestureStep.WaitTime(1.5f),
            GestureStep.WaitAnimations(),
            GestureStep.Animation("wave", Wave, stop: true),
        },
    };

    [Fact]
    public void The_file_is_written_line_by_line_as_the_official_viewer_writes_it()
    {
        string file = Encoding.UTF8.GetString(GestureFormat.Write(Sample()));
        string expected = string.Join("\n",
            "2", "162", "4", "/wave", "Hello!", "6",
            "0", "wave", Wave.ToString("D"), "0",
            "1", "chime", Chime.ToString("D"), "0",
            "2", "waves", "0",
            "3", "1.500000", "1",
            "3", "0.000000", "2",
            "0", "wave", Wave.ToString("D"), "1") + "\n";
        Assert.Equal(expected, file);
    }

    [Fact]
    public void A_written_gesture_reads_back_the_same()
    {
        var read = GestureFormat.Parse(GestureFormat.Write(Sample()));
        Assert.NotNull(read);
        Assert.Equal(GestureKeys.F2, read!.Key);
        Assert.Equal(GestureKeys.MaskShift, read.Mask);
        Assert.Equal("/wave", read.Trigger);
        Assert.Equal("Hello!", read.Replace);
        Assert.Equal(Sample().Steps, read.Steps);
    }

    [Fact]
    public void A_file_of_another_version_or_cut_short_is_not_read()
    {
        Assert.Null(GestureFormat.Parse("1\n0\n0\n\n\n0\n"u8));
        Assert.Null(GestureFormat.Parse("2\n0\n0\n/x\n"u8));
        Assert.Null(GestureFormat.Parse([]));
    }

    [Fact]
    public void An_empty_file_from_a_new_gesture_reads_as_an_empty_gesture()
    {
        var read = GestureFormat.Parse("2\n0\n0\n\n\n0\n"u8);
        Assert.NotNull(read);
        Assert.Empty(read!.Steps);
        Assert.Equal("", read.Trigger);
    }

    [Fact]
    public void Steps_move_up_and_down_and_are_removed_in_place()
    {
        var g = Sample();
        Assert.True(g.MoveUp(2));
        Assert.Equal(GestureStepKind.Chat, g.Steps[1].Kind);
        Assert.False(g.MoveUp(0));
        Assert.True(g.MoveDown(1));
        Assert.Equal(GestureStepKind.Chat, g.Steps[2].Kind);
        Assert.False(g.MoveDown(g.Steps.Count - 1));
        Assert.True(g.Remove(0));
        Assert.Equal(5, g.Steps.Count);
        Assert.False(g.Remove(9));
    }

    [Fact]
    public void The_shortcut_keys_are_F2_to_F12_with_none_shift_or_ctrl_and_never_Ctrl_F10()
    {
        Assert.Equal(11, GestureKeys.Offered.Count);
        Assert.Equal("F2", GestureKeys.Label(GestureKeys.Offered[0]));
        Assert.Equal("F12", GestureKeys.Label(GestureKeys.Offered[^1]));
        Assert.True(GestureKeys.Allowed(GestureKeys.F10, GestureKeys.MaskShift));
        Assert.False(GestureKeys.Allowed(GestureKeys.F10, GestureKeys.MaskControl));
        Assert.True(GestureKeys.Allowed(GestureKeys.F9, GestureKeys.MaskControl));
    }

    [Fact]
    public void A_trigger_word_in_chat_plays_the_gesture_and_is_replaced_by_its_text()
    {
        var active = new[] { Sample() };
        var hit = GestureTriggers.Match("/WAVE to you", active, pick: _ => 0);
        Assert.NotNull(hit);
        Assert.Same(active[0], hit!.Value.Gesture);
        Assert.Equal("Hello! to you", hit.Value.Revised);
    }

    [Fact]
    public void A_trigger_with_no_replacement_is_taken_out_of_the_line()
    {
        var g = Sample();
        g.Replace = "";
        var hit = GestureTriggers.Match("hi /wave there", [g], pick: _ => 0);
        Assert.Equal("hi there", hit!.Value.Revised);
        var alone = GestureTriggers.Match("/wave", [g], pick: _ => 0);
        Assert.Equal("", alone!.Value.Revised);
    }

    [Fact]
    public void Only_the_first_trigger_in_a_line_plays_and_a_line_without_one_plays_nothing()
    {
        var a = Sample();
        var b = new GestureDocument { Trigger = "/bow", Replace = "bows" };
        var hit = GestureTriggers.Match("/bow /wave", [a, b], pick: _ => 0);
        Assert.Same(b, hit!.Value.Gesture);
        Assert.Equal("bows /wave", hit.Value.Revised);
        Assert.Null(GestureTriggers.Match("wave hello", [a, b], pick: _ => 0));
        Assert.Null(GestureTriggers.Match("anything", [new GestureDocument()], pick: _ => 0));   // no trigger set
    }

    [Fact]
    public void A_key_with_its_modifier_plays_the_gesture_bound_to_it()
    {
        var g = Sample();
        Assert.Same(g, GestureTriggers.MatchKey(GestureKeys.F2, GestureKeys.MaskShift, [g], pick: _ => 0));
        Assert.Null(GestureTriggers.MatchKey(GestureKeys.F2, 0, [g], pick: _ => 0));
        Assert.Null(GestureTriggers.MatchKey(GestureKeys.F3, GestureKeys.MaskShift, [g], pick: _ => 0));
        Assert.Null(GestureTriggers.MatchKey(0, 0, [new GestureDocument()], pick: _ => 0));   // no key set
    }
}
