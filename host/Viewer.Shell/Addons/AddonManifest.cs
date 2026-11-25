// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using Viewer.Addons;

namespace Viewer.Shell.Addons;

/// <summary>
/// An add-on's manifest, add-on.json in the add-on's folder: who made it, what it is, the interface version it needs, the job
/// it fills (if any), the permissions it asks for and the library to load. An add-on that asks to change the inventory names
/// the one folder it may change in "inventory_folder", as folder names from the inventory's top joined by '/'.
/// </summary>
public sealed record AddonManifest(string Id, string Name, string Version, string Author, string Licence, string Interface,
    string? Job, IReadOnlyList<string> Permissions, string Library, string? Entry = null, string? InventoryFolder = null,
    bool? DefaultOn = null, bool SwitchAtNextLogin = false)
{
    /// <summary>The grids the manifest's "grids" declares; null when it declares none.</summary>
    public IReadOnlyList<Viewer.Core.Session.GridKind>? Grids { get; init; }

    public const string FileName = "add-on.json";

    private static readonly Regex IdShape = new(@"^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>Reads a manifest; null with the problems when it cannot be used.</summary>
    public static AddonManifest? Parse(string json, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        problems = found;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            found.Add("not JSON");
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            found.Add("not a JSON object");
            return null;
        }

        string? Text(string name, bool required)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString()!.Trim();
            if (required)
                found.Add($"'{name}' is missing");
            return null;
        }

        string? id = Text("id", true), name = Text("name", true), version = Text("version", true), author = Text("author", true),
            licence = Text("licence", true), iface = Text("interface", true), library = Text("library", true);
        string? job = Text("job", false), entry = Text("entry", false), inventoryFolder = Text("inventory_folder", false);
        bool? defaultOn = null;
        if (root.TryGetProperty("default_on", out var d))
        {
            if (d.ValueKind is JsonValueKind.True or JsonValueKind.False)
                defaultOn = d.GetBoolean();
            else
                found.Add("'default_on' must be true or false");
        }
        string? switchAt = Text("switch_at", false);
        if (switchAt is not null and not ("now" or "next-login"))
            found.Add("'switch_at' must be \"now\" or \"next-login\"");
        if (id is not null && !IdShape.IsMatch(id))
            found.Add("'id' must be lower-case letters and digits, with . _ or - between them");
        if (job is not null && !IdShape.IsMatch(job))
            found.Add("'job' must be lower-case letters and digits, with . _ or - between them");
        if (iface is not null && !Supports(iface))
            found.Add($"needs interface {iface}; this viewer has {AddonInterface.Version}");
        if (library is not null && (library.Contains('/') || library.Contains('\\') || !library.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            found.Add("'library' must be a .dll file name in the add-on's folder");

        var permissions = new List<string>();
        if (root.TryGetProperty("permissions", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array)
                found.Add("'permissions' must be a list");
            else
                foreach (var p in list.EnumerateArray())
                {
                    string? s = p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                    if (s is null || !AddonPermissions.All.Contains(s))
                        found.Add($"unknown permission '{s ?? p.ToString()}'");
                    else if (!permissions.Contains(s))
                        permissions.Add(s);
                }
        }
        List<Viewer.Core.Session.GridKind>? grids = null;
        // "grids" came with interface 0.6: in an older add-on's manifest the key means nothing, as before
        if (iface is not null && Minor(iface) >= 6 && root.TryGetProperty("grids", out var named))
        {
            grids = [];
            foreach (var g in named.ValueKind == JsonValueKind.Array ? named.EnumerateArray().ToArray() : [])
            {
                Viewer.Core.Session.GridKind? kind = (g.ValueKind == JsonValueKind.String ? g.GetString() : null) switch
                {
                    "opensim" => Viewer.Core.Session.GridKind.OpenSim,
                    "secondlife" => Viewer.Core.Session.GridKind.SecondLife,
                    _ => null,
                };
                if (kind is not { } known)
                {
                    grids.Clear();
                    break;
                }
                if (!grids.Contains(known))
                    grids.Add(known);
            }
            if (grids.Count == 0)
                found.Add("'grids' must be a list naming \"opensim\", \"secondlife\" or both");
        }
        if (permissions.Contains(AddonPermissions.InventoryWrite) && FolderPath(inventoryFolder) is null)
            found.Add("'inventory.write' needs 'inventory_folder': the folder it may change, as names from the inventory's top joined by '/'");
        if (found.Count > 0)
            return null;
        return new AddonManifest(id!, name!, version!, author!, licence!, iface!, job, permissions, library!, entry, inventoryFolder,
            defaultOn, switchAt == "next-login") { Grids = grids };
    }

    private static int Minor(string version) => version.Split('.') is [_, var b] && int.TryParse(b, out int minor) ? minor : 0;

    /// <summary>The folder names of an "inventory_folder" path, top first; null when it names none.</summary>
    public static IReadOnlyList<string>? FolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        string[] parts = path.Split('/').Select(p => p.Trim()).ToArray();
        return parts.Any(p => p.Length == 0) ? null : parts;
    }

    /// <summary>Whether this viewer can load an add-on built for the interface version given: the same major version, and a
    /// minor version no newer than the viewer's.</summary>
    public static bool Supports(string version)
    {
        static (int, int)? Read(string v) => v.Split('.') is [var a, var b] && int.TryParse(a, out int x) && int.TryParse(b, out int y) ? (x, y) : null;
        return Read(version) is var (major, minor) && Read(AddonInterface.Version) is var (hostMajor, hostMinor) && major == hostMajor && minor <= hostMinor;
    }
}
