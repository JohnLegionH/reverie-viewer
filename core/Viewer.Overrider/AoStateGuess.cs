// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text;

namespace Viewer.Overrider;

/// <summary>
/// Guesses the state an animation belongs to from its name, for the review list shown when animations are dropped on the
/// overrider. The whole table is in this file. A name that matches nothing, or matches only a word that cannot say which
/// state is meant (a bare "turn"), gets no guess: it goes to Unsorted, never to a state that may be wrong.
/// </summary>
public static class AoStateGuess
{
    /// <summary>
    /// The keyword table, most specific first: the first row with a match wins. A keyword with a space is a phrase: its
    /// words must follow each other in the name, or stand joined in one word ("gsit", "standup"). A single word matches a
    /// word of the name, or the start of one followed only by an ending such as s, ing or ed ("stands", "walking").
    /// The ZHAO-II state names (Stand.1, Walk.N, Sit.G and the rest) are matched first, exactly, by <see cref="AoSet"/>.
    /// </summary>
    public static readonly IReadOnlyList<(AoStateId State, string[] Keywords)> Table =
    [
        (AoStateId.PreJumping, ["pre jump", "prejumping", "pjump"]),
        (AoStateId.SittingOnGround, ["ground sit", "sit ground", "g sit", "sit g", "groundsit", "sitting ground"]),
        (AoStateId.CrouchWalking, ["crouch walk", "walk crouch", "crouchwalk", "c walk", "sneak"]),
        (AoStateId.SoftLanding, ["soft land", "soft landing"]),
        (AoStateId.StandingUp, ["stand up", "standing up", "get up"]),
        (AoStateId.FlyingSlow, ["fly slow", "flying slow", "slow fly", "slow flying"]),
        (AoStateId.SwimmingUp, ["swim up", "swimming up"]),
        (AoStateId.SwimmingDown, ["swim down", "swimming down"]),
        (AoStateId.FlyingUp, ["fly up", "flying up", "hover up", "up"]),
        (AoStateId.FlyingDown, ["fly down", "flying down", "hover down", "down"]),
        (AoStateId.TurningLeft, ["turn left", "turning left", "turn l", "left turn"]),
        (AoStateId.TurningRight, ["turn right", "turning right", "turn r", "right turn"]),
        (AoStateId.Floating, ["float", "tread", "swim hover"]),
        (AoStateId.SwimmingForward, ["swim"]),
        (AoStateId.Typing, ["typing", "type", "typer"]),
        (AoStateId.Crouching, ["crouch"]),
        (AoStateId.Hovering, ["hover", "hvr"]),
        (AoStateId.Flying, ["fly", "flying", "flight"]),
        (AoStateId.Jumping, ["jump"]),
        (AoStateId.Landing, ["land", "landing"]),
        (AoStateId.Falling, ["fall", "falling"]),
        (AoStateId.Running, ["run", "running", "jog", "sprint"]),
        (AoStateId.Walking, ["walk", "wlk"]),
        (AoStateId.Sitting, ["sit", "seated", "chair"]),
        (AoStateId.Standing, ["stand", "std", "stnd", "idle", "pose"]),
    ];

    // endings a word may have after its keyword ("stands", "walking", "jumped"); nothing else may follow
    private static readonly string[] Endings = ["", "s", "es", "ing", "ning", "ed", "er"];

    /// <summary>The state the name suggests, or null for Unsorted.</summary>
    public static AoStateId? Guess(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string trimmed = name.Trim();
        // a ZHAO-II or Firestorm state name on its own ("Stand.1", "Sit.G", "Standing")
        if (new AoSet(Guid.Empty).GetStateByName(trimmed) is { } exact && exact.Num != AoStateId.AlwaysPlay)
            return exact.Num;

        var words = Words(trimmed);
        if (words.Count == 0)
            return null;
        foreach (var (state, keywords) in Table)
        {
            foreach (string keyword in keywords)
            {
                if (Matches(words, keyword))
                    return state;
            }
        }
        return null;
    }

    /// <summary>The name's words, lower case: split at anything that is not a letter, and where a capital follows a small letter.</summary>
    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char previous = '\0';
        foreach (char c in name)
        {
            bool split = !char.IsLetter(c) || (char.IsUpper(c) && char.IsLower(previous));
            if (split && current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
            if (char.IsLetter(c))
                current.Append(char.ToLowerInvariant(c));
            previous = c;
        }
        if (current.Length > 0)
            words.Add(current.ToString());
        return words;
    }

    private static bool Matches(List<string> words, string keyword)
    {
        string[] parts = keyword.Split(' ');
        if (parts.Length == 1)
            return words.Any(w => WordMatches(w, parts[0]));
        // the words in a row, each allowed an ending ("flying slow", "turning left")
        for (int i = 0; i + parts.Length <= words.Count; i++)
        {
            bool all = true;
            for (int k = 0; k < parts.Length && all; k++)
                all = WordMatches(words[i + k], parts[k]);
            if (all)
                return true;
        }
        // or joined in one word ("gsit", "standup", "prejump")
        string joined = string.Concat(parts);
        return words.Any(w => WordMatches(w, joined));
    }

    private static bool WordMatches(string word, string keyword) =>
        word.StartsWith(keyword, StringComparison.Ordinal) && Endings.Contains(word[keyword.Length..]);
}
