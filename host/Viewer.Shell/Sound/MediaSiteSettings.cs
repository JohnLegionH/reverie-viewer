// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Media;
using Viewer.Core.Music;

namespace Viewer.Shell.Sound;

/// <summary>The site answers and the blocked list, kept as settings text.</summary>
public sealed class MediaSiteSettings(Func<string, string?> read, Action<string, string> write) : IMediaSiteStore
{
    public const string VideoSitesKey = "media_video_sites";
    public const string WebSitesKey = "media_web_sites";
    public const string BlockedSitesKey = "media_blocked_sites";

    private static string Key(MediaKind kind) => kind switch
    {
        MediaKind.Sound => AudioLevels.MusicSitesKey,   // the answers given for parcel music are the sound answers
        MediaKind.Video => VideoSitesKey,
        _ => WebSitesKey,
    };

    public SiteAnswer Get(MediaKind kind, string site) => MusicSiteAnswers.Get(read(Key(kind)), site);

    public void Set(MediaKind kind, string site, SiteAnswer answer) => write(Key(kind), MusicSiteAnswers.Set(read(Key(kind)), site, answer));

    public bool IsBlocked(string site) => Blocked(read(BlockedSitesKey)).Contains(site.Trim().ToLowerInvariant());

    public void SetBlocked(string site, bool blocked)
    {
        var sites = Blocked(read(BlockedSitesKey));
        string host = site.Trim().ToLowerInvariant();
        sites.Remove(host);
        if (blocked && host.Length > 0)
            sites.Add(host);
        write(BlockedSitesKey, string.Join("\n", sites));
    }

    private static List<string> Blocked(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.ToLowerInvariant()).ToList();

    /// <summary>A test of "may sound be opened from this site", fixed at this moment, safe to call from any thread.</summary>
    public Func<string, bool> SoundSnapshot()
    {
        string? answers = read(AudioLevels.MusicSitesKey);
        var blocked = Blocked(read(BlockedSitesKey)).ToHashSet();
        return site => !blocked.Contains(site) && MusicSiteAnswers.Get(answers, site) == SiteAnswer.Play;
    }
}
