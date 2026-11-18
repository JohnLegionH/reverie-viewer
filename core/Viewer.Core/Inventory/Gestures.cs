// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Inventory;

public enum GestureStepKind { Animation = 0, Sound = 1, Chat = 2, Wait = 3 }

public sealed record GestureStep(GestureStepKind Kind, string Name, Guid AssetId, string Text, float Seconds, uint Flags)
{
    public static GestureStep Animation(string name, Guid asset, bool stop) => new(GestureStepKind.Animation, "", Guid.Empty, "", 0, 0);
    public static GestureStep Sound(string name, Guid asset) => new(GestureStepKind.Sound, "", Guid.Empty, "", 0, 0);
    public static GestureStep Chat(string text) => new(GestureStepKind.Chat, "", Guid.Empty, "", 0, 0);
    public static GestureStep WaitTime(float seconds) => new(GestureStepKind.Wait, "", Guid.Empty, "", 0, 0);
    public static GestureStep WaitAnimations() => new(GestureStepKind.Wait, "", Guid.Empty, "", 0, 0);
}

public sealed class GestureDocument
{
    public byte Key { get; set; }
    public uint Mask { get; set; }
    public string Trigger { get; set; } = "";
    public string Replace { get; set; } = "";
    public List<GestureStep> Steps { get; } = new();
    public bool MoveUp(int index) => false;
    public bool MoveDown(int index) => false;
    public bool Remove(int index) => false;
}

public static class GestureKeys
{
    public const byte F2 = 0, F3 = 0, F9 = 0, F10 = 0;
    public const uint MaskControl = 0, MaskShift = 0;
    public static IReadOnlyList<byte> Offered { get; } = [];
    public static string Label(byte key) => "";
    public static bool Allowed(byte key, uint mask) => false;
}

public static class GestureFormat
{
    public static byte[] Write(GestureDocument gesture) => [];
    public static GestureDocument? Parse(ReadOnlySpan<byte> data) => null;
}

public static class GestureTriggers
{
    public static (GestureDocument Gesture, string Revised)? Match(string line, IReadOnlyList<GestureDocument> active, Func<int, int> pick) => (new GestureDocument(), "x");
    public static GestureDocument? MatchKey(byte key, uint mask, IReadOnlyList<GestureDocument> active, Func<int, int> pick) => null;
}
