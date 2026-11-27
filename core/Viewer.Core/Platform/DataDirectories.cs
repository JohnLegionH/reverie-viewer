// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.Identity;

namespace Viewer.Core.Platform;

public enum DataPlatform { Windows, Linux, MacOS }

/// <summary>
/// Per-user data directories (ADR-019), defined per platform. Settings roam; cache and logs stay local. Nothing is
/// ever beside the executable: a platform whose variables are missing or relative is an error, not a fallback to the
/// application directory. This only computes paths; creating them is the host's job at startup.
/// <list type="bullet">
/// <item>Windows: settings <c>%APPDATA%\&lt;product&gt;</c>; cache <c>%LOCALAPPDATA%\&lt;product&gt;\Cache</c>; logs <c>%LOCALAPPDATA%\&lt;product&gt;\Logs</c>.</item>
/// <item>Linux (XDG): settings <c>$XDG_CONFIG_HOME/&lt;product&gt;</c>; cache <c>$XDG_CACHE_HOME/&lt;product&gt;</c>; logs <c>$XDG_STATE_HOME/&lt;product&gt;/logs</c>
/// (defaults <c>~/.config</c>, <c>~/.cache</c>, <c>~/.local/state</c>).</item>
/// <item>macOS: <c>~/Library/Application Support/&lt;product&gt;</c>, <c>~/Library/Caches/&lt;product&gt;</c>, <c>~/Library/Logs/&lt;product&gt;</c>.</item>
/// </list>
/// </summary>
public sealed record DataDirectories(string Settings, string Cache, string Logs)
{
    /// <summary>The directories for this machine and product (default: <see cref="ViewerIdentity.DataFolderName"/>).</summary>
    public static DataDirectories ForCurrentUser(string product = ViewerIdentity.DataFolderName)
    {
        var dirs = For(Current, Environment.GetEnvironmentVariable, product);
        dirs.EnsureNotUnder(AppContext.BaseDirectory);
        return dirs;
    }

    public static DataPlatform Current =>
        OperatingSystem.IsWindows() ? DataPlatform.Windows
        : OperatingSystem.IsMacOS() ? DataPlatform.MacOS
        : DataPlatform.Linux;

    /// <summary>Pure: the directories for <paramref name="platform"/> given its environment variables.</summary>
    public static DataDirectories For(DataPlatform platform, Func<string, string?> env, string product)
    {
        if (string.IsNullOrWhiteSpace(product) || product.IndexOfAny(['/', '\\', ':']) >= 0 || product is "." or "..")
            throw new ArgumentException("product must be a plain folder name", nameof(product));

        switch (platform)
        {
            case DataPlatform.Windows:
            {
                string roaming = Required(env, "APPDATA", '\\');
                string local = Required(env, "LOCALAPPDATA", '\\');
                string root = Join('\\', local, product);
                return new(Join('\\', roaming, product), Join('\\', root, "Cache"), Join('\\', root, "Logs"));
            }
            case DataPlatform.Linux:
            {
                string config = Xdg(env, "XDG_CONFIG_HOME", ".config");
                string cache = Xdg(env, "XDG_CACHE_HOME", ".cache");
                string state = Xdg(env, "XDG_STATE_HOME", ".local/state");
                return new(Join('/', config, product), Join('/', cache, product), Join('/', Join('/', state, product), "logs"));
            }
            case DataPlatform.MacOS:
            {
                string home = Required(env, "HOME", '/');
                return new(Join('/', home, "Library/Application Support", product), Join('/', home, "Library/Caches", product), Join('/', home, "Library/Logs", product));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(platform));
        }
    }

    /// <summary>The person's folder for optional downloaded packs. It is local to the machine (packs are large and made for one platform), and
    /// outside the cache, which may be cleared at any time. Windows <c>%LOCALAPPDATA%\&lt;product&gt;\Packs</c>; Linux
    /// <c>$XDG_DATA_HOME/&lt;product&gt;/packs</c> (default <c>~/.local/share</c>); macOS <c>~/Library/Application Support/&lt;product&gt;/Packs</c>.</summary>
    public static string PacksFolder(DataPlatform platform, Func<string, string?> env, string product)
    {
        var dirs = For(platform, env, product);   // checks the product name and the variables
        return platform switch
        {
            DataPlatform.Windows => Join('\\', Required(env, "LOCALAPPDATA", '\\'), product, "Packs"),
            DataPlatform.Linux => Join('/', Xdg(env, "XDG_DATA_HOME", ".local/share"), product, "packs"),
            _ => Join('/', dirs.Settings, "Packs"),
        };
    }

    /// <summary>The folder the session log goes to: <c>NV_LOG_DIR</c> when it names an absolute folder (test and automation runs set it so
    /// they never replace the log of a session someone is using), else <paramref name="usual"/>.</summary>
    public static string LogsFolder(Func<string, string?> env, string usual)
    {
        string? configured = env("NV_LOG_DIR");
        return !string.IsNullOrWhiteSpace(configured) && IsAbsolute(configured, '\\') || !string.IsNullOrWhiteSpace(configured) && IsAbsolute(configured, '/')
            ? configured.Trim()
            : usual;
    }

    /// <summary>Throws if any directory is the application directory or inside it (ADR-019: nothing beside the exe).</summary>
    public void EnsureNotUnder(string applicationDirectory)
    {
        string app = Normalise(applicationDirectory);
        foreach (var d in new[] { Settings, Cache, Logs })
        {
            string n = Normalise(d);
            if (n.Equals(app, StringComparison.OrdinalIgnoreCase) || n.StartsWith(app + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("a per-user data directory resolves inside the application directory; refusing to write beside the executable");
        }
    }

    private static string Normalise(string p) => p.Replace('\\', '/').TrimEnd('/');

    private static string Required(Func<string, string?> env, string name, char sep)
    {
        string? v = env(name);
        if (string.IsNullOrWhiteSpace(v) || !IsAbsolute(v, sep))
            throw new InvalidOperationException($"{name} is not set to an absolute path; per-user data directories cannot be placed (never beside the executable)");
        return v.TrimEnd(sep);
    }

    private static string Xdg(Func<string, string?> env, string name, string homeDefault)
    {
        // XDG Base Directory spec: a relative value is invalid and must be ignored.
        string? v = env(name);
        if (!string.IsNullOrWhiteSpace(v) && IsAbsolute(v, '/'))
            return v.TrimEnd('/');
        return Join('/', Required(env, "HOME", '/'), homeDefault);
    }

    private static bool IsAbsolute(string p, char sep) =>
        sep == '/' ? p.StartsWith('/') : (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/')) || p.StartsWith(@"\\", StringComparison.Ordinal);

    private static string Join(char sep, params string[] parts) => string.Join(sep, parts);
}
