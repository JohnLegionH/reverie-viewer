// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Media.Packs;

/// <summary>What is kept between visits: the packs the person said no to.</summary>
public interface IPackAnswers
{
    bool IsRefused(string id);
    void SetRefused(string id, bool refused);
}

/// <summary>Asks the person whether to download a pack.</summary>
public interface IPackAsker
{
    void Ask(PackOffer offer, Action<bool> answered);
    /// <summary>Take the question away without an answer.</summary>
    void Cancel();
}

public enum PackWant { Unavailable, Ready, Refused, Asking, Downloading }

/// <summary>
/// Offers packs. Nothing is downloaded unless the person says yes to the question; it is asked once, and a no is remembered
/// until the person asks for the pack again (<see cref="AskAgain"/>). One question or download is handled at a time. It works
/// on one thread: the download runs by itself and its outcome is collected with <see cref="Poll"/>.
/// </summary>
public sealed class PackOffers(PackManifest manifest, PackStore store, IPackAnswers answers, IPackAsker asker,
    Func<PackOffer, CancellationToken, Task<PackInstallResult>>? install = null)
{
    private readonly Func<PackOffer, CancellationToken, Task<PackInstallResult>> _install = install ?? store.InstallAsync;
    private PackOffer? _asking;
    private int _question;
    private bool _removing;
    private (PackOffer Offer, CancellationTokenSource Cancel, Task<PackInstallResult> Task)? _download;

    /// <summary>Something needs the pack <paramref name="id"/>: says how it stands, and asks the person if that is the next step.</summary>
    public PackWant Want(string id)
    {
        var offer = manifest.Offer(id);
        var state = store.State(id, manifest);
        if (state == PackState.Ready)
            return PackWant.Ready;
        if (_download is { } running)
            return running.Offer.Id == id ? PackWant.Downloading : PackWant.Unavailable;
        if (offer is null)
            return PackWant.Unavailable;
        if (answers.IsRefused(id))
            return PackWant.Refused;
        if (_asking is not null)
            return _asking.Id == id ? PackWant.Asking : PackWant.Unavailable;
        _asking = offer;
        int question = ++_question;
        asker.Ask(offer, yes => Answered(question, yes));
        return PackWant.Asking;
    }

    /// <summary>The person asks for the pack themselves: an earlier no is forgotten.</summary>
    public PackWant AskAgain(string id)
    {
        // while nothing could be asked (no such pack, or another is being handled) the no stays
        if (manifest.Offer(id) is not null && (_asking is null || _asking.Id == id) && _download is null)
            answers.SetRefused(id, false);
        return Want(id);
    }

    /// <summary>The question is not wanted any more: it is taken away, and no answer is kept.</summary>
    public void CancelQuestion()
    {
        if (_asking is null)
            return;
        _asking = null;
        _question++;
        asker.Cancel();
    }

    /// <summary>The outcome of a download that has finished, once; null otherwise.</summary>
    public (PackOffer Offer, PackInstallResult Result)? Poll()
    {
        if (_download is not { Task.IsCompleted: true } done)
            return null;
        _download = null;
        done.Cancel.Dispose();
        if (_removing)
        {
            // the download may have got past its last look at the cancel: what it put in place is removed too
            _removing = false;
            store.Remove(done.Offer.Id);
            return (done.Offer, PackInstallResult.Cancelled);
        }
        var result = done.Task.IsCompletedSuccessfully ? done.Task.Result : PackInstallResult.DownloadFailed;
        return (done.Offer, result);
    }

    /// <summary>Removes a pack; a download of it that runs is stopped (it leaves nothing behind).</summary>
    public bool Remove(string id)
    {
        if (_asking?.Id == id)
            CancelQuestion();
        if (_download is { } running && running.Offer.Id == id)
        {
            _removing = true;
            running.Cancel.Cancel();
        }
        return store.Remove(id);
    }

    private void Answered(int question, bool yes)
    {
        if (question != _question || _asking is not { } offer)
            return;   // an answer to a question that was taken away counts for nothing
        _asking = null;
        _question++;
        if (!yes)
        {
            answers.SetRefused(offer.Id, true);
            return;
        }
        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        _download = (offer, cancel, Task.Run(() => _install(offer, token)));
    }
}
