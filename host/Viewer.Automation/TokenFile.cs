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
/// account-from-secrets-file): only a path and an alias cross the API, never a password. Only the approved aliases.
/// </summary>
public static class AccountsFile
{
    public static readonly string[] Approved = ["tester1", "viewer1", "truly", "stocktester1", "stocktester2"];

    public static (string First, string Last, Secret Password) Load(string path, string alias)
    {
        if (!Approved.Contains(alias))
            throw new InvalidOperationException("account alias not approved for automation logins");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var a in doc.RootElement.GetProperty("accounts").EnumerateArray())
            if (a.GetProperty("alias").GetString() == alias)
                return (a.GetProperty("first").GetString()!, a.GetProperty("last").GetString()!, new Secret(a.GetProperty("password").GetString()!, SensitiveKind.PasswordDigest));
        throw new InvalidOperationException("account alias not in the accounts file");
    }
}
