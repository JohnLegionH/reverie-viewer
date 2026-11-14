// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Viewer.Core.Platform;

namespace Viewer.Core.Assets;

/// <summary>Who may read an entry. The core also serves the multi-user web gateway, so this is a setting.</summary>
public enum ContentSharing
{
    /// <summary>Desktop default: every session of one OS user shares entries.</summary>
    PerUser,
    /// <summary>A session only reads entries it was itself authorised for (its own namespace on disk).</summary>
    PerSession,
}

public sealed record ContentStoreOptions
{
    /// <summary>Directory for entries and the key file; default: the per-user cache directory (ADR-019) + "Content".</summary>
    public string? Root { get; init; }
    public long CapBytes { get; init; } = 1L << 30;
    public ContentSharing Sharing { get; init; } = ContentSharing.PerUser;
    /// <summary>How the key is protected at rest; default: DPAPI on Windows, an owner-only file elsewhere.</summary>
    public IKeyProtector? KeyProtector { get; init; }
    /// <summary>Where key events (import, reset) are logged; none by default.</summary>
    public Viewer.Core.Diagnostics.CoreLog? Log { get; init; }
}

/// <summary>
/// The disk content store for fetched asset bytes. Per ADR-011 ("an encrypted local cache in a project format",
/// release-blocking) and ADR-019 (per-user local data directory):
///   - one file per entry, named by a SHA-256 over (format, source scope, kind, asset id[, session]) so entries are keyed
///     by where they came from and what they are, and a PerSession entry cannot even be addressed by another session;
///   - each file: "NVCS1" | 12-byte nonce | 16-byte tag | AES-256-GCM(SHA-256(bytes) | bytes), with the entry name as
///     associated data (a file renamed onto another entry fails authentication); nothing is plaintext at rest;
///   - the key: 32 random bytes, protected at rest by an <see cref="IKeyProtector"/> (Windows DPAPI, CurrentUser;
///     elsewhere an owner-only file until Stage 3);
///   - atomic writes (temp file + rename); a corrupt, truncated or foreign entry is a miss: it is removed and refetched;
///   - a size cap with least-recently-used eviction (a hit refreshes the entry's time); eviction and repair touch only
///     this store's own "*.nvc" files in its own directory;
///   - safe with concurrent sessions and processes: writes never expose a partial file, and readers tolerate files that
///     vanish (evicted by another store instance);
///   -: PARTIAL entries for a texture fetched in part: the bytes
///     so far and the file's total size when known, sealed like every entry, under their own name (the kind plus
///     "/partial"), so a partial entry can never be read as a complete one. A longer prefix replaces a shorter one; when
///     the file is complete it becomes an ordinary entry and the partial one is removed. The cap and eviction treat them
///     like any other entry.
/// </summary>
public sealed class ContentStore
{
    private const string Magic = "NVCS1";
    private const string Extension = ".nvc";
    private readonly byte[] _key;
    private readonly object _evictLock = new();
    private readonly object[] _partialLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();   // striped by entry path
    private long _corrupt;

    public ContentStore(ContentStoreOptions? options = null)
    {
        Options = options ?? new ContentStoreOptions();
        Root = Path.GetFullPath(Options.Root ?? Path.Combine(DataDirectories.ForCurrentUser().Cache, "Content"));
        Directory.CreateDirectory(Root);
        _key = LoadKey(Options.KeyProtector ?? KeyProtectors.ForThisPlatform());
    }

    public ContentStoreOptions Options { get; }
    public string Root { get; }
    public long CorruptEntriesRemoved => Interlocked.Read(ref _corrupt);

    /// <summary>A view for one session: its source scope (e.g. the grid) and, for PerSession sharing, its own key.</summary>
    public ContentView View(string sourceScope, string sessionKey) => new(this, sourceScope, Options.Sharing == ContentSharing.PerSession ? sessionKey : null);

    internal string PathFor(string scope, string kind, Guid id, string? session)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{Magic}|{scope}|{kind}|{id:D}|{session}"));
        return Path.Combine(Root, Convert.ToHexString(h).ToLowerInvariant() + Extension);
    }

    internal bool TryGet(string path, out byte[] bytes)
    {
        bytes = [];
        byte[] file;
        try { file = ReadShared(path); }
        catch (FileNotFoundException) { return false; }
        catch (IOException) { return false; }   // being written or evicted by another instance: a miss
        // Windows refuses to open a file whose deletion is pending (another instance evicting or discarding it)
        catch (UnauthorizedAccessException) { return false; }
        if (!TryOpen(file, Path.GetFileName(path), out bytes))
        {
            Interlocked.Increment(ref _corrupt);
            TryDelete(path);
            return false;
        }
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }   // LRU: a hit is a use
        return true;
    }

    /// <summary>
    /// Entries are content-addressed (the path names the scope, kind and id), so two writers of one entry write the
    /// same content. An existing entry is therefore never replaced: the new one is written to a unique temp file and moved
    /// into place WITHOUT overwrite. If an entry is already there and verifies, it stands and the temp file (this store's
    /// own) is discarded; if the one there is corrupt, it is removed (own entry) and the move is tried once more. Readers
    /// only ever open complete entries (a move is atomic), never one being replaced.
    /// </summary>
    internal void Put(string path, ReadOnlySpan<byte> bytes)
    {
        var sealedBytes = Seal(bytes, Path.GetFileName(path));
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, sealedBytes);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: false);
                    tmp = null!;   // moved: nothing left to discard
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // there already (or being evicted by another instance): keep a valid one; replace only a corrupt one
                    if (ExistingIsValid(path)) break;
                    if (attempt == 0) TryDelete(path);
                }
            }
        }
        finally
        {
            if (tmp is not null) { try { File.Delete(tmp); } catch { } }   // this store's own temp file
        }
        Evict();
    }

    /// <summary>A partial entry: the bytes so far and the total size when known. A payload that does not hold
    /// together (no bytes, more bytes than the total) is removed as corrupt.</summary>
    internal bool TryGetPartial(string path, out byte[] bytes, out long? total)
    {
        bytes = [];
        total = null;
        if (!TryGet(path, out var payload))
            return false;
        long t = payload.Length >= 8 ? BinaryPrimitives.ReadInt64BigEndian(payload) : -2;
        if (payload.Length <= 8 || t < -1 || (t >= 0 && t < payload.Length - 8))
        {
            Interlocked.Increment(ref _corrupt);
            TryDelete(path);
            return false;
        }
        bytes = payload[8..];
        total = t < 0 ? null : t;
        return true;
    }

    /// <summary>Writes a partial entry (atomically, replacing a shorter one; a longer one already there stays).</summary>
    internal void PutPartial(string path, ReadOnlySpan<byte> bytes, long? total)
    {
        var payload = new byte[8 + bytes.Length];
        BinaryPrimitives.WriteInt64BigEndian(payload, total ?? -1);
        bytes.CopyTo(payload.AsSpan(8));
        var sealedBytes = Seal(payload, Path.GetFileName(path));
        // the check and the replace together, per entry, so two threads of this store never let a shorter prefix
        // replace a longer one (fetches now run on several pool threads at once). Sealing stays outside the lock.
        lock (_partialLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(path) % (uint)_partialLocks.Length])
        {
            if (TryGetPartial(path, out var existing, out _) && existing.Length >= bytes.Length)
                return;
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, sealedBytes);
            try { File.Move(tmp, path, overwrite: true); tmp = null!; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // another instance is replacing it
            finally { if (tmp is not null) { try { File.Delete(tmp); } catch { } } }   // this store's own temp file
        }
        Evict();
    }

    /// <summary>Removes one of this store's own entries (a partial entry once its file is complete).</summary>
    internal void Remove(string path) => TryDelete(path);

    private bool ExistingIsValid(string path)
    {
        try { return TryOpen(ReadShared(path), Path.GetFileName(path), out _); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private byte[] Seal(ReadOnlySpan<byte> bytes, string name)
    {
        var plain = new byte[32 + bytes.Length];
        SHA256.HashData(bytes, plain.AsSpan(0, 32));
        bytes.CopyTo(plain.AsSpan(32));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(_key, 16))
            gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
        CryptographicOperations.ZeroMemory(plain);
        return [.. Encoding.ASCII.GetBytes(Magic), .. nonce, .. tag, .. cipher];
    }

    private bool TryOpen(byte[] file, string name, out byte[] bytes)
    {
        bytes = [];
        int head = Magic.Length + 12 + 16;
        if (file.Length < head + 32 || Encoding.ASCII.GetString(file, 0, Magic.Length) != Magic)
            return false;
        var plain = new byte[file.Length - head];
        try
        {
            using var gcm = new AesGcm(_key, 16);
            gcm.Decrypt(file.AsSpan(Magic.Length, 12), file.AsSpan(head), file.AsSpan(Magic.Length + 12, 16), plain, Encoding.UTF8.GetBytes(name));
        }
        catch (CryptographicException) { return false; }
        var data = plain.AsSpan(32);
        if (!SHA256.HashData(data).AsSpan().SequenceEqual(plain.AsSpan(0, 32)))
            return false;
        bytes = data.ToArray();
        return true;
    }

    /// <summary>Evicts least-recently-used entries until the store is at or below 90 % of its cap. Own entries only.</summary>
    public void Evict()
    {
        lock (_evictLock)
        {
            var entries = new DirectoryInfo(Root).EnumerateFiles("*" + Extension).Select(f => { try { return (FullName: f.FullName, Length: f.Length, LastWriteTimeUtc: f.LastWriteTimeUtc); } catch { return (FullName: f.FullName, Length: 0L, LastWriteTimeUtc: DateTime.MinValue); } }).ToList();
            long total = entries.Sum(e => e.Length);
            if (total <= Options.CapBytes)
                return;
            long target = Options.CapBytes / 10 * 9;
            foreach (var e in entries.OrderBy(e => e.LastWriteTimeUtc))
            {
                if (total <= target) break;
                if (TryDelete(e.FullName)) total -= e.Length;
            }
        }
    }

    /// <summary>Removes every entry of this store (its own entry files in its own folder, nothing else) and returns how many.</summary>
    public int Clear()
    {
        lock (_evictLock)
        {
            int removed = 0;
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*" + Extension).ToList())
                if (TryDelete(file.FullName))
                    removed++;
            return removed;
        }
    }

    public long SizeBytes => new DirectoryInfo(Root).EnumerateFiles("*" + Extension).Sum(f => { try { return f.Length; } catch { return 0L; } });
    public int EntryCount => new DirectoryInfo(Root).EnumerateFiles("*" + Extension).Count();

    /// <summary>Reads while letting other instances replace or evict the file (share delete), so writers are not blocked.</summary>
    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[fs.Length];
        fs.ReadExactly(b);
        return b;
    }

    private bool TryDelete(string path)
    {
        // Only this store's own entry files, inside its own directory (never anything else).
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Root, StringComparison.OrdinalIgnoreCase))
            return false;
        try { File.Delete(path); return true; } catch { return false; }
    }

    /// <summary>
    /// The key, protected by <paramref name="protector"/> in "store.key" + its suffix. A legacy key
    /// ("store.key", raw, owner-only) is imported, re-protected, and this store removes its own legacy file (and nothing else). A key
    /// that cannot be unprotected (another user, another machine, damage) means a new key and an empty cache: the store's
    /// own entries, unreadable with the new key, are removed; logged, never a crash.
    /// </summary>
    private byte[] LoadKey(IKeyProtector protector)
    {
        string legacy = Path.Combine(Root, "store.key");
        string path = legacy + protector.Suffix;
        if (protector.Suffix.Length == 0)
            return LoadOrCreateKey(legacy);
        if (File.Exists(path))
        {
            try
            {
                var key = protector.Unprotect(File.ReadAllBytes(path));
                if (key.Length == 32) return key;
                throw new CryptographicException("unprotected key is not 32 bytes");
            }
            catch (CryptographicException)
            {
                Options.Log?.Warn("content-store.key-reset", Viewer.Core.Diagnostics.CoreLog.F("reason", "protected key could not be unprotected"));
                foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*" + Extension)) TryDelete(f.FullName);
                return WriteProtected(path, RandomNumberGenerator.GetBytes(32), protector);
            }
        }
        if (File.Exists(legacy))
        {
            var old = File.ReadAllBytes(legacy);
            if (old.Length == 32)
            {
                WriteProtected(path, old, protector);
                TryDeleteOwnLegacyKey(legacy);
                Options.Log?.Info("content-store.key-imported", Viewer.Core.Diagnostics.CoreLog.F("from", "legacy owner-only file"));
                return old;
            }
        }
        return WriteProtected(path, RandomNumberGenerator.GetBytes(32), protector);
    }

    private static byte[] WriteProtected(string path, byte[] key, IKeyProtector protector)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(key));
        File.Move(tmp, path, overwrite: true);
        return key;
    }

    private void TryDeleteOwnLegacyKey(string legacy)
    {
        // Only this store's own legacy key file, in its own directory, after the protected copy exists.
        if (Path.GetFileName(legacy) == "store.key" && string.Equals(Path.GetDirectoryName(legacy), Root, StringComparison.OrdinalIgnoreCase))
            try { File.Delete(legacy); } catch { }
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var k = File.ReadAllBytes(path);
                if (k.Length == 32) return k;
                throw new InvalidDataException("content store key file is not 32 bytes");
            }
            catch (FileNotFoundException) { }
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                using var fs = OperatingSystem.IsWindows()
                    ? CreateOwnerOnly(path)
                    : new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
                fs.Write(key);
                return key;
            }
            catch (IOException) when (File.Exists(path)) { }   // another instance created it first: read theirs
        }
        throw new IOException("could not create or read the content store key");
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateOwnerOnly(string path)
    {
        var s = new FileSecurity();
        s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        s.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize, FileShare.Read, 4096, FileOptions.None, s);
    }
}

/// <summary>One session's view of the <see cref="ContentStore"/> (scope + sharing policy applied).</summary>
public sealed class ContentView
{
    private readonly ContentStore _store;
    private readonly string _scope;
    private readonly string? _session;

    internal ContentView(ContentStore store, string scope, string? session)
    {
        _store = store;
        _scope = scope;
        _session = session;
    }

    public bool TryGet(string kind, Guid id, out byte[] bytes) => _store.TryGet(_store.PathFor(_scope, kind, id, _session), out bytes);

    public void Put(string kind, Guid id, ReadOnlySpan<byte> bytes) => _store.Put(_store.PathFor(_scope, kind, id, _session), bytes);

    /// <summary>A partial entry's bytes so far and the file's total size when known (never a complete entry).</summary>
    public bool TryGetPartial(string kind, Guid id, out byte[] bytes, out long? total) => _store.TryGetPartial(PartialPath(kind, id), out bytes, out total);

    public void PutPartial(string kind, Guid id, ReadOnlySpan<byte> bytes, long? total) => _store.PutPartial(PartialPath(kind, id), bytes, total);

    /// <summary>The whole file is here: it becomes an ordinary entry and the partial entry goes.</summary>
    public void Complete(string kind, Guid id, ReadOnlySpan<byte> bytes)
    {
        Put(kind, id, bytes);
        _store.Remove(PartialPath(kind, id));
    }

    private string PartialPath(string kind, Guid id) => _store.PathFor(_scope, kind + "/partial", id, _session);

    /// <summary>A SessionAssets fetcher that reads this view first and writes what <paramref name="inner"/> fetches.
    /// SessionAssets authorises before calling it, so a disk hit never bypasses the session's authorisation (ADR-006).</summary>
    public Func<Guid, CancellationToken, Task<AssetFetchResult>> Caching(string kind, Func<Guid, CancellationToken, Task<AssetFetchResult>> inner) => async (id, ct) =>
    {
        if (TryGet(kind, id, out var hit))
            return new AssetFetchResult(AssetFetchStatus.Ok, hit) { Source = "content" };   // counted as the store's
        var r = await inner(id, ct).ConfigureAwait(false);
        if (r.Status == AssetFetchStatus.Ok)
            Put(kind, id, r.Bytes.Span);
        return r;
    };
}
