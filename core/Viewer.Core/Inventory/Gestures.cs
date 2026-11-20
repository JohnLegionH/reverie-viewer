// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Text;

namespace Viewer.Core.Inventory;

/// <summary>The kinds of gesture step, numbered as the official gesture file numbers them (llmultigesture.h EStepType).</summary>
public enum GestureStepKind { Animation = 0, Sound = 1, Chat = 2, Wait = 3 }

/// <summary>
/// One step of a gesture. An animation step starts its animation, or stops it with <see cref="StopFlag"/>; a sound step plays its sound;
/// a chat step says its text; a wait step waits <see cref="Seconds"/> (<see cref="WaitTimeFlag"/>) or until the animations it started have
/// finished (<see cref="WaitAnimationsFlag"/>). The flags are the official ones (llmultigesture.h ANIM_FLAG_STOP, WAIT_FLAG_TIME, WAIT_FLAG_ALL_ANIM).
/// </summary>
public sealed record GestureStep(GestureStepKind Kind, string Name, Guid AssetId, string Text, float Seconds, uint Flags)
{
    public const uint StopFlag = 0x01, WaitTimeFlag = 0x01, WaitAnimationsFlag = 0x02;

    public static GestureStep Animation(string name, Guid asset, bool stop) => new(GestureStepKind.Animation, name, asset, "", 0, stop ? StopFlag : 0);
    public static GestureStep Sound(string name, Guid asset) => new(GestureStepKind.Sound, name, asset, "", 0, 0);
    public static GestureStep Chat(string text) => new(GestureStepKind.Chat, "", Guid.Empty, text, 0, 0);
    public static GestureStep WaitTime(float seconds) => new(GestureStepKind.Wait, "", Guid.Empty, "", seconds, WaitTimeFlag);
    public static GestureStep WaitAnimations() => new(GestureStepKind.Wait, "", Guid.Empty, "", 0, WaitAnimationsFlag);

    public bool Stops => Kind == GestureStepKind.Animation && (Flags & StopFlag) != 0;
    public bool WaitsForTime => Kind == GestureStepKind.Wait && (Flags & WaitTimeFlag) != 0;
    public bool WaitsForAnimations => Kind == GestureStepKind.Wait && (Flags & WaitAnimationsFlag) != 0;
}

/// <summary>A gesture: its shortcut key and modifier, its chat trigger and replacement text, and its steps in order.</summary>
public sealed class GestureDocument
{
    public byte Key { get; set; }
    public uint Mask { get; set; }
    public string Trigger { get; set; } = "";
    public string Replace { get; set; } = "";
    public List<GestureStep> Steps { get; } = new();

    public bool MoveUp(int index)
    {
        if (index <= 0 || index >= Steps.Count)
            return false;
        (Steps[index - 1], Steps[index]) = (Steps[index], Steps[index - 1]);
        return true;
    }

    public bool MoveDown(int index)
    {
        if (index < 0 || index >= Steps.Count - 1)
            return false;
        (Steps[index + 1], Steps[index]) = (Steps[index], Steps[index + 1]);
        return true;
    }

    public bool Remove(int index)
    {
        if (index < 0 || index >= Steps.Count)
            return false;
        Steps.RemoveAt(index);
        return true;
    }
}

/// <summary>
/// The shortcut keys the gesture editor offers: F2 to F12, alone or with Shift or Ctrl, Ctrl+F10 excepted (llpreviewgesture.cpp lines 508
/// and 941-949). Key and modifier numbers are the official ones (indra_constants.h KEY_F1 = 0xA1, MASK_CONTROL 1, MASK_SHIFT 4).
/// </summary>
public static class GestureKeys
{
    public const byte None = 0, F2 = 0xA2, F3 = 0xA3, F9 = 0xA9, F10 = 0xAA, F12 = 0xAC;
    public const uint MaskNone = 0, MaskControl = 0x0001, MaskShift = 0x0004;

    public static IReadOnlyList<byte> Offered { get; } = Enumerable.Range(F2, F12 - F2 + 1).Select(k => (byte)k).ToArray();

    public static string Label(byte key) => key is >= 0xA1 and <= F12 ? "F" + (key - 0xA0).ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>The key number of function key F<paramref name="number"/> (2 to 12), or <see cref="None"/> for any other number.</summary>
    public static byte FromFunctionKey(int number) => number is >= 2 and <= 12 ? (byte)(0xA0 + number) : None;

    public static bool Allowed(byte key, uint mask) => !(key == F10 && mask == MaskControl) && mask is MaskNone or MaskControl or MaskShift;
}

/// <summary>
/// The official gesture file, version 2, written the way its ASCII data packer writes it (llmultigesture.cpp serialize; lldatapacker.cpp
/// LLDataPackerAsciiBuffer, names off): one value per line: version, key, modifier, trigger, replacement, step count, then for each step its
/// type and its fields (animation and sound: name, asset id, flags; chat: text, flags; wait: seconds as "%f", flags). Our own code.
/// </summary>
public static class GestureFormat
{
    public const int Version = 2, MaxSteps = 1000, MaxBytes = 64 * 1024;

    /// <summary>The largest gesture file the official viewer saves (llpreviewgesture.cpp saveIfNeeded: GestureSaveFailedTooManySteps).</summary>
    public const int MaxSavedBytes = 1000;

    public static bool FitsSave(byte[] file) => file.Length <= MaxSavedBytes;

    public static byte[] Write(GestureDocument g)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal)).Append('\n');
        string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        Line(N(Version));
        Line(N(g.Key));
        Line(N(g.Mask));
        Line(g.Trigger);
        Line(g.Replace);
        Line(N(g.Steps.Count));
        foreach (var s in g.Steps)
        {
            Line(N((int)s.Kind));
            switch (s.Kind)
            {
                case GestureStepKind.Animation or GestureStepKind.Sound:
                    Line(s.Name);
                    Line(s.AssetId.ToString("D"));
                    break;
                case GestureStepKind.Chat:
                    Line(s.Text);
                    break;
                case GestureStepKind.Wait:
                    Line(s.Seconds.ToString("F6", CultureInfo.InvariantCulture));
                    break;
            }
            Line(N(s.Flags));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Reads a gesture file; null when it is not version 2, is cut short, or holds a value that does not read.</summary>
    public static GestureDocument? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > MaxBytes)
            return null;
        string text = Encoding.UTF8.GetString(data).TrimEnd('\0');
        var lines = text.Split('\n');
        int at = 0;
        string? Next() => at < lines.Length ? lines[at++].TrimEnd('\r') : null;
        bool Int(out long v) { v = 0; return Next() is { } s && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v); }

        if (!Int(out long version) || version != Version)
            return null;
        if (!Int(out long key) || key is < 0 or > 255 || !Int(out long mask) || mask is < 0 or > uint.MaxValue)
            return null;
        if (Next() is not { } trigger || Next() is not { } replace)
            return null;
        if (!Int(out long count) || count is < 0 or > MaxSteps)
            return null;
        var g = new GestureDocument { Key = (byte)key, Mask = (uint)mask, Trigger = trigger, Replace = replace };
        for (int i = 0; i < count; i++)
        {
            if (!Int(out long type))
                return null;
            GestureStep? step = null;
            switch (type)
            {
                case 0 or 1:
                {
                    if (Next() is not { } name || Next() is not { } id || !Guid.TryParse(id.Trim(), out var asset) || !Int(out long flags))
                        return null;
                    step = new GestureStep((GestureStepKind)type, name, asset, "", 0, (uint)flags);
                    break;
                }
                case 2:
                {
                    if (Next() is not { } chat || !Int(out long flags))
                        return null;
                    step = new GestureStep(GestureStepKind.Chat, "", Guid.Empty, chat, 0, (uint)flags);
                    break;
                }
                case 3:
                {
                    if (Next() is not { } secs || !float.TryParse(secs.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) || !Int(out long flags))
                        return null;
                    step = new GestureStep(GestureStepKind.Wait, "", Guid.Empty, "", seconds, (uint)flags);
                    break;
                }
                default:
                    return null;
            }
            g.Steps.Add(step);
        }
        return g;
    }
}

/// <summary>
/// How chat and keys play active gestures, as the official gesture manager does (llgesturemgr.cpp triggerAndReviseString and
/// triggerGesture): a line is cut at spaces; the first word that equals an active gesture's trigger, ignoring case, plays that gesture
/// (one of them at random when several share it) and is replaced by its replacement text, or left out when it has none; later
/// triggers in the line stay as they are. A key with its modifier plays a gesture bound to them. <paramref name="pick"/> chooses
/// among several (given the count, returns an index).
/// </summary>
public static class GestureTriggers
{
    public static (GestureDocument Gesture, string Revised)? Match(string line, IReadOnlyList<GestureDocument> active, Func<int, int> pick)
    {
        GestureDocument? played = null;
        var revised = new StringBuilder();
        bool first = true;
        foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            GestureDocument? gesture = null;
            if (played is null)
            {
                var matching = active.Where(g => g.Trigger.Length > 0 && string.Equals(g.Trigger, token, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count > 0)
                {
                    gesture = played = matching[Math.Clamp(pick(matching.Count), 0, matching.Count - 1)];
                    if (gesture.Replace.Length > 0)
                    {
                        if (!first)
                            revised.Append(' ');
                        revised.Append(string.Equals(token, gesture.Replace, StringComparison.OrdinalIgnoreCase) ? token : gesture.Replace);
                    }
                }
            }
            if (gesture is null)
            {
                if (!first)
                    revised.Append(' ');
                revised.Append(token);
            }
            first = false;
        }
        return played is null ? null : (played, revised.ToString());
    }

    public static GestureDocument? MatchKey(byte key, uint mask, IReadOnlyList<GestureDocument> active, Func<int, int> pick)
    {
        if (key == GestureKeys.None)
            return null;
        var matching = active.Where(g => g.Key == key && g.Mask == mask).ToList();
        return matching.Count == 0 ? null : matching[Math.Clamp(pick(matching.Count), 0, matching.Count - 1)];
    }
}

/// <summary>Gestures on the grid. Implemented by the protocol adapter.</summary>
public interface IGestureTransport
{
    /// <summary>The gestures the grid has active for the agent (item id and asset id), as the login and later activations give them.</summary>
    IReadOnlyList<(Guid ItemId, Guid AssetId)> ActiveGestures();

    /// <summary>Marks a gesture active or inactive (ActivateGestures and DeactivateGestures, as llgesturemgr.cpp sends them).</summary>
    bool SetGestureActive(Guid itemId, Guid assetId, bool active);

    /// <summary>The asset id an inventory gesture item points at; empty when unknown.</summary>
    Guid GestureAssetOf(Guid itemId);

    /// <summary>Fetches a gesture's file by its asset id (the region's ViewerAsset capability, "gesture_id").</summary>
    Task<Viewer.Core.Assets.AssetFetchResult> FetchGestureAsync(Guid assetId, CancellationToken ct);

    /// <summary>Saves a gesture's file over its inventory item (UpdateGestureAgentInventory, then the uploader it names; llpreviewgesture.cpp saveIfNeeded).</summary>
    Task<NotecardSaveResult> SaveGestureAsync(Guid itemId, byte[] file, CancellationToken ct);

    /// <summary>Plays a gesture on the agent by its asset id: its animations, sounds, chat and waits in order.</summary>
    Task PlayGestureAsync(Guid assetId, CancellationToken ct);

    /// <summary>Plays a sound at the agent for everyone near (the SoundTrigger message, as a gesture's sound step sends it). False when not sent.</summary>
    bool TriggerSound(Guid sound) => false;
}
