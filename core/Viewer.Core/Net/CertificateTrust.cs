// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;

namespace Viewer.Core.Net;

/// <summary>
/// The certificates a person chose to trust, each pinned to the host it was presented for and to its SHA-256 fingerprint:
/// a different certificate for the same host, or the same certificate for another host, is not trusted. Kept in memory and,
/// when a folder is given, in a JSON file there that later runs read. A file that cannot be read trusts nothing.
/// </summary>
public sealed class CertificateTrust
{
    public const string FileName = "trusted-certificates.json";

    internal sealed record Entry(string Host, string Sha256, string Subject, DateTimeOffset? NotAfter, DateTimeOffset TrustedAt);

    private readonly object _lock = new();
    private readonly List<Entry> _entries = [];

    public CertificateTrust(string? folder)
    {
        Folder = folder;
        if (folder is null) return;
        try
        {
            string path = Path.Combine(folder, FileName);
            if (File.Exists(path))
                _entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _entries.Clear();   // a file that cannot be read trusts nothing; the next choice replaces it
        }
    }

    public string? Folder { get; }

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>Whether this exact certificate (by fingerprint) is trusted for this host.</summary>
    public bool IsTrusted(string host, string sha256)
    {
        lock (_lock)
            return _entries.Any(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the host has a pinned certificate and this fingerprint is not it: the server's certificate changed since the
    /// person trusted it, which is refused rather than offered for trust again.</summary>
    public bool IsChanged(string host, string sha256) => false;

    /// <summary>Forgets every certificate pinned for the host (the grid was taken off the list), and saves the choice.</summary>
    public void Forget(string host)
    {
    }

    /// <summary>Trusts the certificate for the host from now on, and saves the choice.</summary>
    public void Trust(CertificateRefusal certificate)
    {
        lock (_lock)
        {
            if (_entries.Any(e => string.Equals(e.Host, certificate.Host, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Sha256, certificate.Sha256, StringComparison.OrdinalIgnoreCase)))
                return;
            _entries.Add(new Entry(certificate.Host, certificate.Sha256, certificate.Subject, certificate.NotAfter, DateTimeOffset.UtcNow));
            Save();
        }
    }

    private void Save()
    {
        if (Folder is null) return;
        try
        {
            Directory.CreateDirectory(Folder);
            string path = Path.Combine(Folder, FileName);
            string temp = path + ".new";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);   // the old file is replaced in one step, never half-written
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a folder that cannot be written keeps the choice for this run only
        }
    }

    /// <summary>The folder the choices are kept in: the folder named by NV_CERT_STORE when set (tests and unusual setups), else the settings folder.</summary>
    public static string? FolderFor(string? settingsFolder, Func<string, string?> environment) =>
        environment("NV_CERT_STORE") is { Length: > 0 } over ? over : settingsFolder;
}
