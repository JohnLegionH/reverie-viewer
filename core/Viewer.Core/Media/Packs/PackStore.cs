// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Viewer.Core.Media.Packs;

public enum PackState { NotInstalled, Ready, Withdrawn }

public enum PackInstallResult { Installed, DownloadFailed, WrongSize, WrongHash, BadArchive, WriteFailed, Cancelled }

/// <summary>A pack found in the packs folder.</summary>
public sealed record InstalledPack(string Id, string Version, string Folder);

/// <summary>
/// The packs folder: <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/</c> holds a pack's files and the record written last, so a folder
/// without the record is not a pack. A download goes to a file of its own, must have exactly the stated size and hash, and
/// is unpacked (a zip archive) beside the packs before it is moved into place; anything that fails is removed. An archive
/// with an entry that could be written outside its own folder is refused whole. Nothing else is done with the files:
/// nothing is run, registered or installed outside this folder. Nothing is made on disk before a download starts.
/// </summary>
public sealed class PackStore(string root, bool allowLoopbackHttp = false, TimeSpan? idleTimeout = null)
{
    private const string RecordName = "installed.json";
    private const string DownloadFolder = ".download", StagingFolder = ".staging";
    private const int MaxRedirects = 5, MaxEntries = 20_000;
    private const long MaxUnpackedBytes = 4L << 30;

    private readonly TimeSpan _idle = idleTimeout ?? TimeSpan.FromSeconds(60);

    /// <summary>The installed pack with this id; null when there is none.</summary>
    public InstalledPack? Find(string id)
    {
        if (!PackManifest.IsId(id))
            return null;
        try
        {
            string home = Path.Combine(root, id);
            if (!Directory.Exists(home))
                return null;
            return Directory.GetDirectories(home)
                .Where(folder => PackManifest.IsVersion(Path.GetFileName(folder)) && File.Exists(Path.Combine(folder, RecordName)))
                .OrderByDescending(folder => File.GetLastWriteTimeUtc(Path.Combine(folder, RecordName)))
                .Select(folder => new InstalledPack(id, Path.GetFileName(folder), folder))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the installed pack may be used. A version the manifest has withdrawn may not.</summary>
    public PackState State(string id, PackManifest manifest) =>
        Find(id) is not { } pack ? PackState.NotInstalled
        : manifest.IsWithdrawn(id, pack.Version) ? PackState.Withdrawn
        : PackState.Ready;

    /// <summary>Downloads, checks and unpacks a pack. Call it only after the person said yes.</summary>
    public async Task<PackInstallResult> InstallAsync(PackOffer offer, CancellationToken cancel = default)
    {
        if (!PackManifest.IsId(offer.Id) || !PackManifest.IsVersion(offer.Version) || !PackManifest.UrlAllowed(offer.Url, allowLoopbackHttp))
            return PackInstallResult.DownloadFailed;
        string work = Guid.NewGuid().ToString("N");
        string part = Path.Combine(root, DownloadFolder, work + ".part");
        string staging = Path.Combine(root, StagingFolder, work);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            var fetched = await Download(offer, part, cancel).ConfigureAwait(false);
            if (fetched != PackInstallResult.Installed)
                return fetched;
            return await Task.Run(() => Unpack(offer, part, staging, cancel), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the caller's cancel, or the idle limit: no bytes arrived in time
            return cancel.IsCancellationRequested ? PackInstallResult.Cancelled : PackInstallResult.DownloadFailed;
        }
        catch (Exception e) when (e is HttpRequestException or HttpIOException or System.Net.Sockets.SocketException)
        {
            return PackInstallResult.DownloadFailed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return PackInstallResult.WriteFailed;
        }
        finally
        {
            TryDelete(part);
            TryDeleteFolder(staging);
            TryDeleteEmpty(Path.Combine(root, DownloadFolder));
            TryDeleteEmpty(Path.Combine(root, StagingFolder));
        }
    }

    /// <summary>Removes every version of a pack. False when there was nothing to remove or it could not be removed.</summary>
    public bool Remove(string id)
    {
        if (!PackManifest.IsId(id))
            return false;
        string home = Path.Combine(root, id);
        return Directory.Exists(home) && TryDeleteFolder(home);
    }

    private async Task<PackInstallResult> Download(PackOffer offer, string part, CancellationToken cancel)
    {
        // redirects are followed here, so that each address is checked before it is opened; no cookies and no sign-in are sent
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var url = offer.Url;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            idle.CancelAfter(_idle);
            using var reply = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if ((int)reply.StatusCode is 301 or 302 or 303 or 307 or 308 && reply.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                // a download that began over https never goes on over plain http
                if (!PackManifest.UrlAllowed(next, allowLoopbackHttp) || url.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                    return PackInstallResult.DownloadFailed;
                url = next;
                continue;
            }
            if (reply.StatusCode != HttpStatusCode.OK)
                return PackInstallResult.DownloadFailed;
            if (reply.Content.Headers.ContentLength is { } stated && stated != offer.Bytes)
                return PackInstallResult.WrongSize;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            using (var from = await reply.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
            using (var to = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    idle.CancelAfter(_idle);
                    int got = await from.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (got == 0)
                        break;
                    total += got;
                    if (total > offer.Bytes)
                        return PackInstallResult.WrongSize;
                    hash.AppendData(buffer, 0, got);
                    await to.WriteAsync(buffer.AsMemory(0, got), idle.Token).ConfigureAwait(false);
                }
            }
            if (total != offer.Bytes)
                return PackInstallResult.WrongSize;
            return Convert.ToHexString(hash.GetHashAndReset()).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase)
                ? PackInstallResult.Installed : PackInstallResult.WrongHash;
        }
        return PackInstallResult.DownloadFailed;
    }

    private PackInstallResult Unpack(PackOffer offer, string part, string staging, CancellationToken cancel)
    {
        try
        {
            using var zip = ZipFile.OpenRead(part);
            if (zip.Entries.Count == 0 || zip.Entries.Count > MaxEntries || zip.Entries.Any(e => !SafeEntry(e.FullName)))
                return PackInstallResult.BadArchive;
            Directory.CreateDirectory(staging);
            string inside = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            long left = MaxUnpackedBytes;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!target.StartsWith(inside, StringComparison.Ordinal))
                    return PackInstallResult.BadArchive;
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var from = entry.Open();
                using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                int got;
                while ((got = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    if ((left -= got) < 0)
                        return PackInstallResult.BadArchive;   // counted as written: an archive can state a smaller size than it holds
                    to.Write(buffer, 0, got);
                }
            }
        }
        catch (InvalidDataException)
        {
            return PackInstallResult.BadArchive;
        }
        cancel.ThrowIfCancellationRequested();
        File.WriteAllText(Path.Combine(staging, RecordName), JsonSerializer.Serialize(new { id = offer.Id, version = offer.Version, sha256 = offer.Sha256 }));
        string home = Path.Combine(root, offer.Id), folder = Path.Combine(home, offer.Version);
        Directory.CreateDirectory(home);
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
        Directory.Move(staging, folder);
        foreach (var old in Directory.GetDirectories(home))
            if (!old.Equals(folder, StringComparison.OrdinalIgnoreCase))
                TryDeleteFolder(old);
        return PackInstallResult.Installed;
    }

    /// <summary>A relative path with forward slashes whose every part is a plain name on every platform.</summary>
    private static bool SafeEntry(string name)
    {
        if (name.Length == 0 || name.Equals(RecordName, StringComparison.OrdinalIgnoreCase) || name.Any(c => c < ' ' || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
            return false;
        var parts = (name.EndsWith('/') ? name[..^1] : name).Split('/');
        // a part ending in a dot or a space is another name on Windows ("..", ".. " and "name." among them)
        return parts.All(p => p.Length > 0 && p[^1] is not ('.' or ' '));
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static bool TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteEmpty(string folder)
    {
        try { Directory.Delete(folder, false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
