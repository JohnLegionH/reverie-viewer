// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Reflection;

namespace Viewer.Core.Identity;

/// <summary>
/// What this viewer tells a grid it is (ADR-019: honest identity). Our own name, channel and version, never another
/// viewer's: no "Second Life Release", no Firestorm channel, and not the web gateway's "web-viewer-dev" either.
/// </summary>
/// <remarks>The viewer's name lives only here: the string table's brand entry, the export and package names and the data
/// folders all follow it (tests hold them to it).</remarks>
public sealed record ViewerIdentity(string Name, string Channel, string Version)
{
    /// <summary>The product's full name, told to a grid at login and shown on the login screen and in About.</summary>
    public const string ProductName = "Reverie Viewer";

    /// <summary>The short name shown in the window title, the menu bar and the add-on host (the string table's brand entry).</summary>
    public const string ShortName = "Reverie";

    /// <summary>The per-user data folders' name (<see cref="Platform.DataDirectories"/>): the product name with no space.</summary>
    public const string DataFolderName = "ReverieViewer";

    /// <summary>The data folders' name before the viewer had its name; their settings are carried over once
    /// (<see cref="Platform.SettingsCarryOver"/>).</summary>
    public const string PreviousDataFolderName = "NativeViewer";

    /// <summary>The release packages' name: reverie-viewer-&lt;version&gt;-&lt;platform&gt;.</summary>
    public const string PackageName = "reverie-viewer";

    /// <summary>The program file's name, without its platform extension (Reverie.exe, Reverie.x86_64, Reverie.arm64).</summary>
    public const string ExecutableName = "Reverie";

    /// <summary>What this release is, shown after the version on the login screen.</summary>
    public const string ReleaseLabel = "early preview";

    /// <summary>The identity of this build: name and channel "Reverie Viewer", version = the core's three-part version and the
    /// build number (major.minor.patch.build, a different version for every commit; Viewer.Core.csproj, ViewerBuildNumber).</summary>
    public static ViewerIdentity Current { get; } =
        new(ProductName, ProductName, BuildVersion(typeof(ViewerIdentity).Assembly));

    /// <summary>This identity with the version's build number left off (major.minor.patch), for the test pictures, which must not
    /// change with every commit. Never sent to a grid.</summary>
    public ViewerIdentity WithoutBuildNumber() => this;

    /// <summary>HTTP user agent for the login and capability requests: a product token, so no spaces.</summary>
    public string UserAgent => Name.Replace(" ", "") + "/" + Version;

    /// <summary>AssemblyInformationalVersion without its "+sha" build-metadata suffix, else Version as major.minor.build.</summary>
    internal static string BuildVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
