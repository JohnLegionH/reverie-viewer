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
/// An account is allowed when its alias is in the test-account pool file AND neither its alias nor its name is in the
/// protected-accounts file. The pool file is the one the machine's claim script hands avatar accounts out from (one alias
/// per line, # comments, aliases only, no secrets), so a live check can log in with whichever account its avatar claim was
/// given. The protected-accounts file lists, one per line, the accounts a machine never lets the automation use (a person's
/// real account, for example). Both files live outside the repository; the code holds no account names.
/// </summary>
public static class AccountsFile
{
    /// <summary>Names the pool file directly.</summary>
    public const string PoolVariable = "NV_AVATAR_POOL";
    /// <summary>Names the protected-accounts file directly.</summary>
    public const string ProtectedVariable = "NV_PROTECTED_ACCOUNTS";
    /// <summary>The claim check script (see the launchers' claim guard); without the variables above, each file is the one beside it.</summary>
    public const string ClaimScriptVariable = "NV_CLAIM_CHECK_SCRIPT";
    public const string PoolFileName = "avatar-pool.txt";
    public const string ProtectedFileName = "protected-accounts.txt";

    private const string NotApproved = "account alias not approved for automation logins";
    private const string OnProtectedList = NotApproved + ": the account is on the protected list";

    /// <summary>The pool file: <see cref="PoolVariable"/>, else <see cref="PoolFileName"/> beside the claim check script, else none.</summary>
    public static string? PoolPath(Func<string, string?> env) => NamedOrBesideClaimScript(env, PoolVariable, PoolFileName);

    /// <summary>The protected-accounts file: <see cref="ProtectedVariable"/>, else <see cref="ProtectedFileName"/> beside the claim
    /// check script, else none (then the pool alone decides).</summary>
    public static string? ProtectedPath(Func<string, string?> env) => NamedOrBesideClaimScript(env, ProtectedVariable, ProtectedFileName);

    private static string? NamedOrBesideClaimScript(Func<string, string?> env, string variable, string fileName)
    {
        if (env(variable) is { Length: > 0 } named)
            return named;
        if (env(ClaimScriptVariable) is { Length: > 0 } script && Path.GetDirectoryName(script) is { Length: > 0 } dir)
            return Path.Combine(dir, fileName);
        return null;
    }

    /// <summary>The pool file's aliases as the claim script reads them: text after # dropped, trimmed, lower case, only
    /// [a-z0-9._-] aliases kept, each once, in file order.</summary>
    public static IReadOnlyList<string> ReadPool(string poolPath)
    {
        var aliases = new List<string>();
        foreach (string raw in File.ReadAllLines(poolPath))
        {
            string line = Uncommented(raw);
            if (line.Length > 0 && line.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-') && !aliases.Contains(line))
                aliases.Add(line);
        }
        return aliases;
    }

    /// <summary>The protected-accounts file's names: an alias, a first or last name, or "first last"; text after # dropped,
    /// spacing collapsed, lower case, each once, in file order.</summary>
    public static IReadOnlyList<string> ReadProtected(string protectedPath)
    {
        var names = new List<string>();
        foreach (string raw in File.ReadAllLines(protectedPath))
        {
            string line = Normalised(Uncommented(raw));
            if (line.Length > 0 && !names.Contains(line))
                names.Add(line);
        }
        return names;
    }

    private static string Uncommented(string raw)
    {
        int comment = raw.IndexOf('#');
        return (comment >= 0 ? raw[..comment] : raw).Trim().ToLowerInvariant();
    }

    /// <summary>A name as the protected list compares it: lower case, runs of white space made one space, trimmed.</summary>
    private static string Normalised(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>The account for <paramref name="alias"/>, allowed by the pool and protected-accounts files the environment names.</summary>
    public static (string First, string Last, Secret Password) Load(string path, string alias) =>
        Load(path, alias, PoolPath(Environment.GetEnvironmentVariable), ProtectedPath(Environment.GetEnvironmentVariable));

    /// <summary>The account for <paramref name="alias"/>: refused unless the alias is in <paramref name="poolPath"/> and neither the
    /// alias nor the account's name is in <paramref name="protectedPath"/> (none: the pool alone decides). A protected-accounts file
    /// that is named but missing or unreadable refuses every account. A refusal for a protected account names no account.</summary>
    public static (string First, string Last, Secret Password) Load(string path, string alias, string? poolPath, string? protectedPath = null)
    {
        IReadOnlyList<string> protectedNames = [];
        if (protectedPath is not null)
        {
            if (!File.Exists(protectedPath))
                throw new InvalidOperationException($"{NotApproved}: the protected-accounts file {protectedPath} does not exist, so no account is allowed");
            try { protectedNames = ReadProtected(protectedPath); }
            catch (Exception e)
            {
                throw new InvalidOperationException($"{NotApproved}: the protected-accounts file {protectedPath} could not be read ({e.GetType().Name}), so no account is allowed");
            }
        }
        // the protected list is checked before the pool, by alias and by the account's names, so no refusal message ever spells
        // the alias of a protected account
        if (IsProtected(protectedNames, alias))
            throw new InvalidOperationException(OnProtectedList);
        if (protectedNames.Count > 0 && Find(path, alias) is { } named && IsProtected(protectedNames, named.First, named.Last, named.First + " " + named.Last))
            throw new InvalidOperationException(OnProtectedList);
        if (poolPath is null)
            throw new InvalidOperationException($"{NotApproved}: no test-account pool file is set ({PoolVariable}, or {PoolFileName} beside {ClaimScriptVariable})");
        if (!File.Exists(poolPath))
            throw new InvalidOperationException($"{NotApproved}: the test-account pool file {poolPath} does not exist");
        // exact: the accounts file below is searched with this same spelling, so no other spelling reaches an account the pool leaves out
        if (!ReadPool(poolPath).Contains(alias))
            throw new InvalidOperationException($"{NotApproved}: {alias} is not in the test-account pool file {poolPath}");
        return Find(path, alias) ?? throw new InvalidOperationException("account alias not in the accounts file");
    }

    /// <summary>The accounts file's entry for exactly <paramref name="alias"/>, or null.</summary>
    private static (string First, string Last, Secret Password)? Find(string path, string alias)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var a in doc.RootElement.GetProperty("accounts").EnumerateArray())
            if (a.GetProperty("alias").GetString() == alias)
                return (a.GetProperty("first").GetString()!, a.GetProperty("last").GetString()!,
                    new Secret(a.GetProperty("password").GetString()!, SensitiveKind.PasswordDigest));
        return null;
    }

    /// <summary>True when any of <paramref name="names"/>, compared as <see cref="Normalised"/>, is a whole entry of the protected list.</summary>
    private static bool IsProtected(IReadOnlyList<string> protectedNames, params string[] names) =>
        names.Any(n => protectedNames.Contains(Normalised(n)));
}
