// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Overrider;

/// <summary>One state line of a ZHAO-II notecard: the state and its steps; a step of several names plays them together.</summary>
public sealed record ZhaoLine(AoStateId State, IReadOnlyList<IReadOnlyList<string>> Steps);

/// <summary>What a ZHAO-II notecard holds, and the lines that could not be read.</summary>
public sealed record ZhaoCard(IReadOnlyList<ZhaoLine> Lines, IReadOnlyList<string> UnknownStates, IReadOnlyList<int> BadLines);

/// <summary>
/// Reads a ZHAO-II style notecard: lines of the form <c>[ State ]anim|anim|anim</c>, where a step may name several
/// animations separated by commas to play them together. Lines starting with # are comments. The state names are the ones
/// <see cref="AoSet.GetStateByName"/> knows (Firestorm's names and the ZHAO names). The format is the one the Firestorm
/// viewer imports (aoengine.cpp, parseNotecard); this is our own reader of it.
/// </summary>
public static class ZhaoNotecard
{
    public static ZhaoCard Parse(string text)
    {
        var lines = new List<ZhaoLine>();
        var unknown = new List<string>();
        var bad = new List<int>();
        var names = new AoSet(Guid.Empty);
        string[] all = text.Replace("\r\n", "\n").Split('\n');
        for (int index = 0; index < all.Length; index++)
        {
            string line = all[index].Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            int end = line.IndexOf(']');
            if (line[0] != '[' || end < 0)
            {
                bad.Add(index + 1);
                continue;
            }
            string stateName = line[1..end].Trim();
            if (names.GetStateByName(stateName) is not { } state)
            {
                unknown.Add(stateName);
                continue;
            }
            var steps = new List<IReadOnlyList<string>>();
            foreach (string step in line[(end + 1)..].Split('|'))
            {
                var members = step.Split(',').Select(m => m.Trim()).Where(m => m.Length > 0).ToArray();
                if (members.Length > 0)
                    steps.Add(members);
            }
            if (steps.Count > 0)
                lines.Add(new ZhaoLine(state.Num, steps));
        }
        return new ZhaoCard(lines, unknown, bad);
    }
}
