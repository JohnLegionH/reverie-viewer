// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Viewer.Core.Diagnostics;

namespace Viewer.Shell;

/// <summary>The login hash the official viewer sends in place of the password: "$1$" and the MD5 of the password as 32 lower-case hex digits.</summary>
public static class LoginHash
{
    /// <summary>The hash of <paramref name="password"/>.</summary>
    public static string Of(string password) =>
        "$1$" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

    /// <summary>Whether <paramref name="text"/> contains a login hash ("$1$" followed by 32 hex digits).</summary>
    public static bool Contains(string text) => HashPattern.IsMatch(text);

    /// <summary>Whether <paramref name="text"/> is exactly a login hash (a saved one is sent as it is, not hashed again).</summary>
    public static bool Is(string text) => text.Length == 35 && HashPattern.IsMatch(text);

    // a plain expression, not a generated one: the generated kind names its types per build, and the shipping and development
    // exports must hold the same shell assembly
    private static readonly Regex HashPattern = new(@"\$1\$[0-9a-fA-F]{32}", RegexOptions.CultureInvariant);
}

/// <summary>Where remembered login hashes are kept for the current user. Nothing in it is ever logged or shown.</summary>
public interface ILoginVault
{
    /// <summary>Whether this computer can keep a remembered password safely; false hides the tick.</summary>
    bool Available { get; }
    /// <summary>The string-table key of the one line that says why the tick is not offered (null when <see cref="Available"/>).</summary>
    string? UnavailableKey { get; }
    void Save(string grid, string name, Secret hash);
    Secret? Load(string grid, string name);
    /// <summary>Forgets the entry for this grid and name. True when there was one.</summary>
    bool Forget(string grid, string name);
}

/// <summary>
/// Windows: each hash protected with DPAPI for the current user, with entropy tied to this product and the entry (so a blob read
/// under another entry or by another program's scope is refused), in one file in the viewer's per-user settings folder. The file
/// holds only SHA-256 digests of the entry keys and the protected blobs: no name, address or hash in readable form.
/// </summary>
public sealed class DpapiLoginVault(string folder) : ILoginVault
{
    /// <summary>The saved-login file's name. The package script and the repository scan refuse any file of this name.</summary>
    public const string FileName = "remembered-logins.dat";

    private const string EntropyPrefix = "NativeViewer remembered login v1|";

    public string FilePath => Path.Combine(folder, FileName);
    public bool Available => OperatingSystem.IsWindows();
    public string? UnavailableKey => Available ? null : "login.remember_password.unavailable";

    public void Save(string grid, string name, Secret hash)
    {
        if (!OperatingSystem.IsWindows() || !LoginHash.Is(hash.Reveal()))
            return;
        string key = RememberPassword.EntryKey(grid, name);
        var entries = Read();
        entries[Digest(key)] = Convert.ToBase64String(Protect(hash.Reveal(), key));
        Write(entries);
    }

    public Secret? Load(string grid, string name)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        string key = RememberPassword.EntryKey(grid, name);
        if (!Read().TryGetValue(Digest(key), out var blob))
            return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(blob); }
        catch (FormatException) { return null; }
        return Unprotect(bytes, key) is { } hash && LoginHash.Is(hash) ? new Secret(hash, SensitiveKind.PasswordDigest) : null;
    }

    public bool Forget(string grid, string name)
    {
        var entries = Read();
        if (!entries.Remove(Digest(RememberPassword.EntryKey(grid, name))))
            return false;
        Write(entries);
        return true;
    }

    /// <summary>Protects <paramref name="hash"/> for the current user under <paramref name="scope"/> (test seam).</summary>
    public static byte[] Protect(string hash, string scope)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        return ProtectWindows(Encoding.UTF8.GetBytes(hash), Entropy(scope));
    }

    /// <summary>Reads a blob back under <paramref name="scope"/>; null when it cannot be read under that scope.</summary>
    public static string? Unprotect(byte[] blob, string scope)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try { return Encoding.UTF8.GetString(UnprotectWindows(blob, Entropy(scope))); }
        catch (CryptographicException) { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data, byte[] entropy) => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] blob, byte[] entropy) => ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);

    private static byte[] Entropy(string scope) => SHA256.HashData(Encoding.UTF8.GetBytes(EntropyPrefix + scope));

    private static string Digest(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private Dictionary<string, string> Read()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) is { } d
                ? new(d, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(StringComparer.Ordinal); }
    }

    private void Write(Dictionary<string, string> entries)
    {
        Directory.CreateDirectory(folder);
        // written beside the file and moved over it, so a half-written file is never read
        string next = FilePath + ".next";
        File.WriteAllText(next, JsonSerializer.Serialize(entries));
        File.Move(next, FilePath, overwrite: true);
    }
}

/// <summary>
/// Linux: the desktop's Secret Service keyring, through its own secret-tool command (libsecret's tools), keyed by an SHA-256 digest of
/// the entry; nothing is written to disk by the viewer. Offered only when secret-tool is installed and a session bus is there.
/// </summary>
public sealed class SecretServiceLoginVault : ILoginVault
{
    private const string Attribute = "nativeviewer-login";
    private readonly string _tool;

    private SecretServiceLoginVault(string tool) => _tool = tool;

    /// <summary>The vault when the Secret Service can be reached through secret-tool, else null.</summary>
    public static SecretServiceLoginVault? Find(Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(env("DBUS_SESSION_BUS_ADDRESS")))
            return null;
        foreach (var dir in (env("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, "secret-tool");
            if (File.Exists(candidate))
                return new SecretServiceLoginVault(candidate);
        }
        return null;
    }

    public bool Available => true;
    public string? UnavailableKey => null;

    public void Save(string grid, string name, Secret hash)
    {
        if (LoginHash.Is(hash.Reveal()))
            Run(["store", "--label=Reverie Viewer login", Attribute, Key(grid, name)], hash.Reveal(), out _);
    }

    public Secret? Load(string grid, string name) =>
        Run(["lookup", Attribute, Key(grid, name)], null, out string found) && LoginHash.Is(found.Trim()) ? new Secret(found.Trim(), SensitiveKind.PasswordDigest) : null;

    public bool Forget(string grid, string name)
    {
        bool had = Load(grid, name) is not null;
        Run(["clear", Attribute, Key(grid, name)], null, out _);
        return had;
    }

    private static string Key(string grid, string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RememberPassword.EntryKey(grid, name)))).ToLowerInvariant();

    private bool Run(string[] args, string? input, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo(_tool) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (input is not null) p.StandardInput.Write(input);
            p.StandardInput.Close();
            output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(10_000)) { p.Kill(); return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }
}

/// <summary>No safe place on this computer: the tick is not offered, and the reason is said in one line.</summary>
public sealed class NoLoginVault(string reasonKey) : ILoginVault
{
    public bool Available => false;
    public string? UnavailableKey => reasonKey;
    public void Save(string grid, string name, Secret hash) { }
    public Secret? Load(string grid, string name) => null;
    public bool Forget(string grid, string name) => false;
}

/// <summary>The remember-password rules of the login screen, kept apart from the view so they can be tested.</summary>
public static class RememberPassword
{
    /// <summary>The vault for this computer: DPAPI on Windows (in <paramref name="settingsDir"/>), the Secret Service on Linux when it can be
    /// reached, otherwise none (the tick is not offered, and the line says why).</summary>
    public static ILoginVault ForThisComputer(string? settingsDir, Func<string, string?> env)
    {
        if (OperatingSystem.IsWindows())
            return settingsDir is null ? new NoLoginVault("login.remember_password.no_folder") : new DpapiLoginVault(settingsDir);
        if (OperatingSystem.IsLinux())
            return SecretServiceLoginVault.Find(env) is { } keyring ? keyring : new NoLoginVault("login.remember_password.no_keyring");
        return new NoLoginVault("login.remember_password.unavailable");
    }

    /// <summary>The key an entry is saved under: the tidy grid address and the lower-case name.</summary>
    public static string EntryKey(string grid, string name)
    {
        // the address without its scheme or a closing slash, so "https://grid.example.org:8002/" and "grid.example.org:8002" are one grid
        string typed = grid.Trim();
        var uri = GridAddress.Tidy(typed).Uri ?? GridAddress.Tidy("https://" + typed).Uri;
        string address = uri is null ? typed : uri.Host + ":" + uri.Port + uri.AbsolutePath.TrimEnd('/');
        string person = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return (address + "|" + person).ToLowerInvariant();
    }

    /// <summary>Whether a failed attempt made with the saved hash should forget it: only a refusal from the grid itself (not a cancel,
    /// an unreachable grid or a connection failure, which say nothing about the hash).</summary>
    public static bool ForgetAfterFailure(string? failureCode) => failureCode is not null && failureCode is not
        ("cancelled" or "unreachable" or "connect" or "exception" or "certificate" or "invalid" or "outboundDenied" or "tos" or "critical" or "update" or "mfa_challenge");
}
