// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using Godot;
using Viewer.Core.Diagnostics;
using Viewer.Core.Media.Packs;
using Viewer.Core.Platform;
using Viewer.Core.Session;
using Viewer.Shell;
using Viewer.Shell.Sound;

namespace Viewer.Godot;

/// <summary>
/// Optional media packs in the running viewer: the question before a download and the word on how it ended. This version lists no
/// pack, so nothing here asks, downloads or makes a folder, and a frame costs one test of a field. A development build may be given a
/// list to try it with: NV_MEDIA_PACK_MANIFEST names a manifest file (its addresses may then be plain http on this computer), and
/// NV_MEDIA_PACK_OFFER names a pack to ask about once the agent is logged in. A refusal given to such a list is kept apart.
/// </summary>
public partial class Main
{
    private PackOffers? _packs;
    private PackStore? _packStore;
    private ViewerSession? _packSession;
    private bool _packsLooked;
    private string? _packOfferAtLogin;
    private ConfirmationDialog? _packAsk;
    private AcceptDialog? _packDone;
    private (PackOffer Offer, Action<bool> Answered)? _packWaitingAsk;
    private (PackOffer Offer, PackInstallResult Result)? _packWaitingWord;
    private Action<bool>? _packAnswer;

    /// <summary>The width the pack boxes' words wrap to, in interface units.</summary>
    private const int PackBoxWidth = 460;

    private sealed class PackAsker(Main main) : IPackAsker
    {
        public void Ask(PackOffer offer, Action<bool> answered) => main._packWaitingAsk = (offer, answered);
        public void Cancel() => main.CancelPackAsk();
    }

    /// <summary>A question or a pack box is on screen: another must wait, since only one can hold the window.</summary>
    private bool PackBoxUp => _packAsk is { Visible: true } || _packDone is { Visible: true };

    private void LookForPacks()
    {
        _packsLooked = true;
#if NV_AUTOMATION
        if (System.Environment.GetEnvironmentVariable("NV_MEDIA_PACK_MANIFEST") is not { Length: > 0 } path || !System.IO.Path.IsPathFullyQualified(path))
            return;
        string json;
        string folder;
        try
        {
            json = System.IO.File.ReadAllText(path);
            folder = DataDirectories.PacksFolder(DataDirectories.Current, System.Environment.GetEnvironmentVariable, Viewer.Core.Identity.ViewerIdentity.DataFolderName);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info("media.pack.manifest-unread", CoreLog.F("reason", e.GetType().Name));
            return;
        }
        var manifest = PackManifest.Parse(json, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, allowLoopbackHttp: true);
        var settings = UserSettings(null);
        var answers = new MediaPackSettings(key => settings.GetText(key + "_test"), (key, text) => settings.SetText(key + "_test", text));
        _packStore = new PackStore(folder, allowLoopbackHttp: true);
        _packs = new PackOffers(manifest, _packStore, answers, new PackAsker(this));
        _packOfferAtLogin = System.Environment.GetEnvironmentVariable("NV_MEDIA_PACK_OFFER");
#endif
    }

    /// <summary>Every frame.</summary>
    private void UpdateMediaPacks(ViewerSession? session)
    {
        if (!_packsLooked)
            LookForPacks();
        if (_packs is not { } packs || _strings is not { } strings)
            return;
        if (!ReferenceEquals(_packSession, session))
        {
            if (_packSession is not null)
                packs.CancelQuestion();   // leaving takes an open question away; it is not an answer
            _packSession = session;
        }
        if (_packOfferAtLogin is { Length: > 0 } id && session is { IsEnded: false } && session.AgentId != Guid.Empty)
        {
            _packOfferAtLogin = null;
            WantPack(id);
        }
        if (packs.Poll() is { } done)
        {
            Log.Info("media.pack.finished", CoreLog.F("pack", done.Offer.Id), CoreLog.F("version", done.Offer.Version), CoreLog.F("result", done.Result.ToString()));
            if (done.Result != PackInstallResult.Cancelled)
                _packWaitingWord = done;
        }
        if (_musicAsk is { Visible: true } || PackBoxUp)
            return;
        if (_packWaitingAsk is { } ask)
        {
            _packWaitingAsk = null;
            ShowPackAsk(strings, ask.Offer, ask.Answered);
        }
        else if (_packWaitingWord is { } word)
        {
            _packWaitingWord = null;
            if (_packDone is null)
            {
                _packDone = BuildPackDone(strings);
                AddChild(_packDone);
            }
            ShowPackDone(_packDone, strings, word.Offer.Id, word.Result == PackInstallResult.Installed);
        }
    }

    private PackWant WantPack(string id, bool again = false)
    {
        // only the packs the interface has words for
        var want = id is "video" or "web" && _packs is { } packs ? again ? packs.AskAgain(id) : packs.Want(id) : PackWant.Unavailable;
        Log.Info("media.pack.want", CoreLog.F("pack", id), CoreLog.F("state", want.ToString()));
        return want;
    }

    private void ShowPackAsk(StringTable strings, PackOffer offer, Action<bool> answered)
    {
        if (_packAsk is null)
        {
            _packAsk = BuildPackAsk(strings);
            _packAsk.Confirmed += () => AnswerPack(true);
            _packAsk.Canceled += () => AnswerPack(false);
            AddChild(_packAsk);
        }
        _packAnswer = answered;
        ShowPackQuestion(_packAsk, strings, offer.Id, offer.Bytes, offer.Url.Host);
        Log.Info("media.pack.ask", CoreLog.F("pack", offer.Id), CoreLog.F("bytes", offer.Bytes), CoreLog.F("site", offer.Url.Host));
    }

    private void CancelPackAsk()
    {
        _packWaitingAsk = null;
        _packAnswer = null;
        if (_packAsk is { Visible: true })
            _packAsk.Hide();
    }

    private void AnswerPack(bool yes)
    {
        var answer = _packAnswer;
        _packAnswer = null;
        if (answer is not null)
            Log.Info("media.pack.answer", CoreLog.F("download", yes));
        answer?.Invoke(yes);
    }

    internal static ConfirmationDialog BuildPackAsk(StringTable strings) => new()
    {
        Name = "media_pack_ask", OkButtonText = strings.Get("media.pack.ask.yes"), CancelButtonText = strings.Get("media.pack.ask.no"), DialogAutowrap = true,
    };

    internal static void ShowPackQuestion(ConfirmationDialog dialog, StringTable strings, string id, long bytes, string site)
    {
        dialog.Title = strings.Get($"media.pack.title.{id}");
        dialog.DialogText = strings.Get($"media.pack.ask.text.{id}", MediaPackSettings.SizeText(bytes), site);
        PopupQuestion(dialog, PackBoxWidth);
        // a box that appears while the person types must not start a download: Enter presses the refusal until they choose otherwise
        dialog.GetCancelButton().GrabFocus();
    }

    internal static AcceptDialog BuildPackDone(StringTable strings) => new()
    {
        Name = "media_pack_done", OkButtonText = strings.Get("media.pack.done.ok"), DialogAutowrap = true,
    };

    internal static void ShowPackDone(AcceptDialog dialog, StringTable strings, string id, bool installed)
    {
        dialog.Title = strings.Get($"media.pack.title.{id}");
        dialog.DialogText = strings.Get($"media.pack.done.{(installed ? "added" : "failed")}.{id}");
        PopupQuestion(dialog, PackBoxWidth);
    }

#if NV_AUTOMATION
    /// <summary>The media_pack command (development builds only): how a pack stands, a want for it as the viewer would raise one, and removal.</summary>
    public object MediaPackCall(string action, string id)
    {
        if (!_packsLooked)
            LookForPacks();
        string? want = action switch { "want" => WantPack(id).ToString(), "ask_again" => WantPack(id, again: true).ToString(), _ => null };
        bool? removed = action == "remove" ? _packs?.Remove(id) ?? false : null;
        if (action == "remove" && _packWaitingWord?.Offer.Id == id)
            _packWaitingWord = null;   // no word that a pack arrived after it was removed
        var installed = _packStore?.Find(id);
        return new
        {
            listed = _packs is not null, want, removed, installed_version = installed?.Version, folder = installed?.Folder,
            question_shown = _packAsk is { Visible: true }, word_shown = _packDone is { Visible: true } ? _packDone.DialogText : null,
        };
    }
#endif
}
