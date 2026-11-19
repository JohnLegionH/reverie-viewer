// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Security.AccessControl;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Viewer.Core.Diagnostics;
using Viewer.Core.Platform;

namespace Viewer.Automation;

/// <summary>
/// The ONLY way the automation token leaves the process (the test strategy 2.1): <c>automation-&lt;pid&gt;.json</c>
/// (<c>{"port":..,"token":..,"pid":..}</c>) in the per-user LOCAL data directory (<c>%LOCALAPPDATA%\&lt;product&gt;\Automation</c>
/// on Windows), readable by the current user only:
///   - Windows: the directory and the file get an explicit, protected DACL (no inherited entries) granting only the current
///     user's SID; the file is created with that DACL, so there is no window with a wider one;
///   - elsewhere: directory 0700, file created 0600.
/// The host deletes its own file when the server stops (product behaviour on its own file).
/// </summary>
public static class TokenFile
{
    public const string FolderName = "Automation";

    /// <summary>Where token files go for this user: beside the cache and logs, never beside the executable.</summary>
    public static string DefaultDirectory(DataDirectories dirs) => Path.Combine(Path.GetDirectoryName(dirs.Cache)!, FolderName);

    public static string PathFor(string directory, int pid) => Path.Combine(directory, $"automation-{pid}.json");

    internal static string Write(string directory, int port, string token, int pid)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("the token directory must be absolute", nameof(directory));
        string path = PathFor(directory, pid);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { port, token, pid });
        if (OperatingSystem.IsWindows())
        {
            var dirInfo = new DirectoryInfo(directory);
            if (!dirInfo.Exists)
                dirInfo.Create(DirectorySecurity());
            else
                dirInfo.SetAccessControl(DirectorySecurity());
            if (File.Exists(path))
                File.Delete(path);   // a stale file of a dead process with our pid: ours to replace
            using var fs = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.Read, 4096, FileOptions.None, FileSecurity());
            fs.Write(bytes);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var fs = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            fs.Write(bytes);
        }
        return path;
    }

    /// <summary>Removes this process's token file (its own file only).</summary>
    public static void Remove(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Who may access <paramref name="path"/>: (inheritance protected, identities with any allow rule).</summary>
    public static (bool Protected, IReadOnlyList<string> Allowed) Describe(string path)
    {
        if (!OperatingSystem.IsWindows())
            return (true, [File.GetUnixFileMode(path).ToString()]);
        return DescribeWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static (bool Protected, IReadOnlyList<string> Allowed) DescribeWindows(string path)
    {
        var sec = new FileInfo(path).GetAccessControl();
        var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference.Value).Distinct().ToList();
        return (sec.AreAccessRulesProtected, rules);
    }

    public static string CurrentUserSid() => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : Environment.UserName;

    [SupportedOSPlatform("windows")]
    private static FileSecurity FileSecurity()
    {
        var s = new FileSecurity();
        s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        s.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return s;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity DirectorySecurity()
    {
        var s = new DirectorySecurity();
        s.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        s.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }
}

/// <summary>
/// The session command's login reads its account from the test client's accounts file itself (the test strategy 2.2:
/// account-from-secrets-file): only a path and an alias cross the API, never a password.
/// The aliases it may use are the test-account pool file's, the same file the machine's claim script hands avatar accounts
/// out from (one alias per line, # comments, aliases only, no secrets), so a live check can log in with whichever account
/// its avatar claim was given. There is no list in the code. The grid owner's own account is refused whatever the file says.
/// </summary>
public static class AccountsFile
{
    /// <summary>Names the pool file directly.</summary>
    public const string PoolVariable = "NV_AVATAR_POOL";
    /// <summary>The claim check script (see the launchers' claim guard); without <see cref="PoolVariable"/> the pool file is the one beside it.</summary>
    public const string ClaimScriptVariable = "NV_CLAIM_CHECK_SCRIPT";
    public const string PoolFileName = "avatar-pool.txt";

    /// <summary>The grid owner's own account: a person's real account, never a test account. Matched as a whole alias, first or last name.</summary>
    private const string OwnerName = "legion";

    private const string NotApproved = "account alias not approved for automation logins";

    /// <summary>The pool file: <see cref="PoolVariable"/>, else <see cref="PoolFileName"/> beside the claim check script, else none.</summary>
    public static string? PoolPath(Func<string, string?> env)
    {
        if (env(PoolVariable) is { Length: > 0 } named)
            return named;
        if (env(ClaimScriptVariable) is { Length: > 0 } script && Path.GetDirectoryName(script) is { Length: > 0 } dir)
            return Path.Combine(dir, PoolFileName);
        return null;
    }

    /// <summary>The pool file's aliases as the claim script reads them: text after # dropped, trimmed, lower case, only
    /// [a-z0-9._-] aliases kept, each once, in file order.</summary>
    public static IReadOnlyList<string> ReadPool(string poolPath)
    {
        var aliases = new List<string>();
        foreach (string raw in File.ReadAllLines(poolPath))
        {
            int hash = raw.IndexOf('#');
            string line = (hash >= 0 ? raw[..hash] : raw).Trim().ToLowerInvariant();
            if (line.Length > 0 && line.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-') && !aliases.Contains(line))
                aliases.Add(line);
        }
        return aliases;
    }

    /// <summary>The hard check: true for the grid owner's account, by alias or by its first or last name.</summary>
    public static bool NeverAllowed(string alias, string? first = null, string? last = null) =>
        new[] { alias, first, last }.Any(n => n is not null && string.Equals(n.Trim(), OwnerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The account for <paramref name="alias"/>, allowed by the pool file the environment names.</summary>
    public static (string First, string Last, Secret Password) Load(string path, string alias) =>
        Load(path, alias, PoolPath(Environment.GetEnvironmentVariable));

    public static (string First, string Last, Secret Password) Load(string path, string alias, string? poolPath)
    {
        if (NeverAllowed(alias))
            throw new InvalidOperationException($"the account {alias} is never allowed for automation logins (it is the grid owner's own account, not a test account)");
        if (poolPath is null)
            throw new InvalidOperationException($"{NotApproved}: no test-account pool file is set ({PoolVariable}, or {PoolFileName} beside {ClaimScriptVariable})");
        if (!File.Exists(poolPath))
            throw new InvalidOperationException($"{NotApproved}: the test-account pool file {poolPath} does not exist");
        if (!ReadPool(poolPath).Contains(alias.Trim().ToLowerInvariant()))
            throw new InvalidOperationException($"{NotApproved}: {alias} is not in the test-account pool file {poolPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var a in doc.RootElement.GetProperty("accounts").EnumerateArray())
            if (a.GetProperty("alias").GetString() == alias)
            {
                string first = a.GetProperty("first").GetString()!, last = a.GetProperty("last").GetString()!;
                if (NeverAllowed(alias, first, last))
                    throw new InvalidOperationException($"the account {alias} is never allowed for automation logins (it is the grid owner's own account, not a test account)");
                return (first, last, new Secret(a.GetProperty("password").GetString()!, SensitiveKind.PasswordDigest));
            }
        throw new InvalidOperationException("account alias not in the accounts file");
    }
}
