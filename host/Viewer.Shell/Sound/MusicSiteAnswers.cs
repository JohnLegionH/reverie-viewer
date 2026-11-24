// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Music;

namespace Viewer.Shell.Sound;

/// <summary>
/// The answers to "play music from this site?", kept as text in the settings (<see cref="AudioLevels.MusicSitesKey"/>): one line a site,
/// <c>host=play</c> or <c>host=dont</c>. A site not in the text has not been asked about. The text is read as it is found: a line that
/// is not of that form is left out.
/// </summary>
public static class MusicSiteAnswers
{
    /// <summary>The most sites kept; the oldest answers go first when a person has answered for more.</summary>
    public const int MaxSites = 500;

    public static SiteAnswer Get(string? text, string site)
    {
        site = site.Trim().ToLowerInvariant();
        foreach (var (host, answer) in Parse(text))
            if (host == site)
                return answer;
        return SiteAnswer.Ask;
    }

    /// <summary>The text with <paramref name="site"/> set to <paramref name="answer"/> (Ask forgets the site).</summary>
    public static string Set(string? text, string site, SiteAnswer answer)
    {
        site = site.Trim().ToLowerInvariant();
        var kept = Parse(text).Where(p => p.Site != site).ToList();
        if (answer != SiteAnswer.Ask && site.Length > 0)
            kept.Add((site, answer));
        if (kept.Count > MaxSites)
            kept = kept.Skip(kept.Count - MaxSites).ToList();
        return string.Join("\n", kept.Select(p => p.Site + "=" + (p.Answer == SiteAnswer.Play ? "play" : "dont")));
    }

    private static IEnumerable<(string Site, SiteAnswer Answer)> Parse(string? text)
    {
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.LastIndexOf('=');
            if (eq <= 0)
                continue;
            string host = line[..eq].Trim().ToLowerInvariant(), value = line[(eq + 1)..].Trim();
            if (value == "play")
                yield return (host, SiteAnswer.Play);
            else if (value == "dont")
                yield return (host, SiteAnswer.DontPlay);
        }
    }
}
