// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using Viewer.Addons;

namespace Viewer.Addon.Rlv;

/// <summary>
/// RLV as an add-on: the protocol lives here, the enforcement in the viewer. Owner-say lines of the user's own objects that
/// start with '@' go to the engine; what the engine holds is published to the viewer through the restraints interface (built-in
/// add-ons only), which enforces it. The Restrictions window lists every object holding restrictions, read only: there is no
/// way to lift a restriction from it (RLVa has none either; switching RLV off and logging in again is the way out).
/// </summary>
public sealed class RlvAddon : IAddon, IAddonDiagnostics, IRlvSink
{
    private IAddonContext? _context;
    private IAddonRestraints? _restraints;
    private RlvEngine? _engine;
    private AddonWindow? _window;

    public void Start(IAddonContext context)
    {
        _context = context;
        _restraints = context.Restraints ?? throw new AddonPermissionException(AddonPermissions.Restraints);
        var version = typeof(RlvAddon).Assembly.GetName().Version ?? new Version(1, 0, 0);
        _engine = new RlvEngine(this, _restraints.ViewerName, version);
        context.ChatReceived += Heard;
        context.Tick += () => _engine?.Collect(context.Now);
        context.LoggedOut += () => _engine?.ClearAll();
        _window = context.AddWindow("restrictions", "RLV Restrictions");
        Refresh();
        context.AddMenuItem("RLV > Restrictions…", () =>
        {
            Refresh();
            _window.Open();
        });
    }

    public void Stop()
    {
        _engine?.ClearAll();
        _engine = null;
    }

    private void Heard(ChatLine line)
    {
        if (line.Kind == ChatKind.OwnerSay && _engine is { } engine && _context is { } context)
            engine.Process(line.FromId, line.FromName, line.Text, context.Now);
    }

    // ---------------- the engine's sink ----------------

    public void Reply(int channel, string text) => _restraints?.Reply(channel, text);

    public bool ForceSit(Guid issuer, Guid objectId) => _restraints?.ForceSit(issuer, objectId) ?? false;

    public bool ForceStand(Guid issuer) => _restraints?.ForceStand(issuer) ?? false;

    public bool ForceTeleport(Guid issuer, double x, double y, double z) => _restraints?.ForceTeleport(issuer, x, y, z) ?? false;

    public bool ObjectPresent(Guid objectId) => _restraints?.ObjectPresent(objectId) ?? false;

    public void Changed()
    {
        if (_engine is { } engine)
            _restraints?.Publish(engine.Restrictions.Select(r => new AddonRestriction(r.ObjectId, r.ObjectName, r.Behaviour, r.Option, r.Enforced, r.SetAt)).ToArray());
        Refresh();
    }

    // ---------------- the Restrictions window ----------------

    /// <summary>The window's lines: a plain line at the top, then each object by name with each behaviour it holds, whether it
    /// is enforced and when it was set; the way out in one line at the end.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        var held = _engine?.Restrictions ?? [];
        double now = _context?.Now ?? 0;
        lines.Add(held.Count == 0 ? "RLV is on. No object is restricting you." : "RLV is on. These objects are restricting you.");
        foreach (var group in held.GroupBy(r => r.ObjectId))
        {
            lines.Add(group.First().ObjectName is { Length: > 0 } name ? name : group.Key.ToString());
            foreach (var r in group)
            {
                string when = DateTime.Now.AddSeconds(r.SetAt - now).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                string what = r.Option.Length > 0 ? r.Behaviour + ":" + r.Option : r.Behaviour;
                lines.Add($"    {what} — {(r.Enforced ? "enforced" : "not enforced yet")} — set at {when}");
            }
        }
        if (_engine?.NotBuilt is { Count: > 0 } notBuilt)
            lines.Add("Not built yet, ignored: " + string.Join(", ", notBuilt));
        lines.Add("Emergency release: switch RLV off in Me > Add-ons and log in again.");
        return lines;
    }

    private void Refresh()
    {
        if (_window is null)
            return;
        var lines = Lines();
        _window.SetControls(lines.Select((l, i) => (AddonControl)new AddonLabel("line" + i.ToString(CultureInfo.InvariantCulture)) { Text = l }).ToArray());
    }

    // ---------------- diagnostics (development builds, tests) ----------------

    public object? Call(string action, string? text) => action switch
    {
        "restrictions" => (_engine?.Restrictions ?? []).Select(r => new
        {
            @object = r.ObjectId,
            name = r.ObjectName,
            behaviour = r.Behaviour,
            option = r.Option,
            enforced = r.Enforced,
        }).ToArray(),
        "lines" => Lines(),
        _ => null,
    };
}
