// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Music;

namespace Viewer.Core.Media;

/// <summary>What a site is allowed for. An answer for one kind says nothing about another.</summary>
public enum MediaKind { Sound, Video, Web }

/// <summary>The answers kept for each site and kind, and the sites the person blocked.</summary>
public interface IMediaSiteStore
{
    SiteAnswer Get(MediaKind kind, string site);
    void Set(MediaKind kind, string site, SiteAnswer answer);
    bool IsBlocked(string site);
}

/// <summary>Asks the person about one site for one kind.</summary>
public interface IMediaAsker
{
    void Ask(MediaKind kind, string site, Uri url, Action<bool> answered);
    /// <summary>The question for this kind is not wanted any more: take it away without an answer.</summary>
    void Cancel(MediaKind kind);
}

public enum MediaDecision { Allowed, Refused, Waiting }

/// <summary>
/// May an address be loaded? Answers are kept per site and per kind, a blocked site wins over every answer, only http and https can ever be
/// loaded, and a site without an answer is asked about once. An answer covers one site: a further site that a redirect or a playlist entry
/// leads to is named in <c>alsoSites</c> and asked about in turn. It works on one thread.
/// </summary>
public sealed class MediaConsent(IMediaSiteStore store, IMediaAsker asker)
{
    private readonly Dictionary<MediaKind, string> _asking = [];

    public SiteAnswer AnswerFor(MediaKind kind, string site)
    {
        site = site.ToLowerInvariant();
        return store.IsBlocked(site) ? SiteAnswer.DontPlay : store.Get(kind, site);
    }

    /// <summary>True when the site of <paramref name="address"/> is allowed for this kind. Nothing is asked.</summary>
    public bool MayLoad(MediaKind kind, string? address) => RadioUrl.TryParse(address, out var uri) && AnswerFor(kind, RadioUrl.Site(uri)) == SiteAnswer.Play;

    /// <summary>Whether media may start without a click (a face's auto play, a script's play): only an allowed site, with auto play on.</summary>
    public bool MayAutoPlay(MediaKind kind, string? address, bool autoPlayOn) => autoPlayOn && MayLoad(kind, address);

    /// <summary>The decision for loading <paramref name="url"/>. While a site has no answer it is asked about (once), and
    /// <paramref name="changed"/> is called when the answer comes.</summary>
    public MediaDecision Want(MediaKind kind, Uri url, IEnumerable<string> alsoSites, Action changed)
    {
        string? unanswered = null;
        foreach (var site in alsoSites.Prepend(RadioUrl.Site(url)))
        {
            var answer = AnswerFor(kind, site);
            if (answer == SiteAnswer.DontPlay)
            {
                Release(kind);
                return MediaDecision.Refused;
            }
            if (answer == SiteAnswer.Ask)
                unanswered ??= site.ToLowerInvariant();
        }
        if (unanswered is null)
        {
            Release(kind);
            return MediaDecision.Allowed;
        }
        if (_asking.GetValueOrDefault(kind) == unanswered)
            return MediaDecision.Waiting;
        Release(kind);
        string asked = unanswered;
        _asking[kind] = asked;
        asker.Ask(kind, asked, url, ok =>
        {
            if (_asking.GetValueOrDefault(kind) == asked)
                _asking.Remove(kind);
            store.Set(kind, asked, ok ? SiteAnswer.Play : SiteAnswer.DontPlay);
            changed();
        });
        return MediaDecision.Waiting;
    }

    /// <summary>The media is not wanted any more: an open question is taken away without an answer.</summary>
    public void Release(MediaKind kind)
    {
        if (_asking.Remove(kind))
            asker.Cancel(kind);
    }

    /// <summary>Forgets earlier refusals for these sites, so they are asked about again. A blocked site stays blocked.</summary>
    public void AskAgain(MediaKind kind, IEnumerable<string> sites)
    {
        foreach (var each in sites)
        {
            string site = each.ToLowerInvariant();
            if (!store.IsBlocked(site) && store.Get(kind, site) == SiteAnswer.DontPlay)
                store.Set(kind, site, SiteAnswer.Ask);
        }
    }
}
