// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Viewer.Core.Assets;

/// <summary>Protects the content store's key at rest. Unprotect throws <see cref="CryptographicException"/> when it cannot.</summary>
public interface IKeyProtector
{
    /// <summary>File name suffix of the protected key ("" = the key is stored as-is in an owner-only file).</summary>
    string Suffix { get; }
    byte[] Protect(byte[] key);
    byte[] Unprotect(byte[] blob);
}

/// <summary>
/// Windows DPAPI, CurrentUser scope, with fixed entropy (System.Security.Cryptography.ProtectedData, pinned 10.0.12):
/// the key file is only usable by the same Windows user on the same machine. Mac/Linux keychains are Stage 3.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    // A constant string, turned into bytes per call: a static byte[] would be mutable static state (ADR-006 guard).
    private const string EntropyText = "NativeViewer.ContentStore.key.v1";
    private static byte[] Entropy() => Encoding.ASCII.GetBytes(EntropyText);
    public string Suffix => ".dpapi";
    public byte[] Protect(byte[] key) => ProtectedData.Protect(key, Entropy(), DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] blob) => ProtectedData.Unprotect(blob, Entropy(), DataProtectionScope.CurrentUser);
}

/// <summary>Until Stage 3 on non-Windows platforms: the raw key in an owner-only file (the behaviour).</summary>
public sealed class OwnerOnlyFileKeyProtector : IKeyProtector
{
    public string Suffix => "";
    public byte[] Protect(byte[] key) => (byte[])key.Clone();
    public byte[] Unprotect(byte[] blob) => blob.Length == 32 ? (byte[])blob.Clone() : throw new CryptographicException("key file is not 32 bytes");
}

public static class KeyProtectors
{
    public static IKeyProtector ForThisPlatform() => OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new OwnerOnlyFileKeyProtector();
}
