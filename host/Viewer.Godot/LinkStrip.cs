// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Linq;
using Godot;
using Viewer.Core.Inventory;
using Viewer.Shell.Chat;
using Viewer.Shell.Links;

namespace Viewer.Godot;

/// <summary>
/// The links a text holds (location, hop:// and web links), one per line under a text that is shown as typed (a group notice's body,
/// a parcel's description), so they can be clicked without the text itself being read as markup. A click goes through the one link
/// router every text place uses (<see cref="HostLinks"/>): a place on another grid asks first, nothing runs unclicked. Hidden when the
/// text holds no link. Main thread only.
/// </summary>
public partial class LinkStrip : RichTextLabel
{
    public LinkStrip(string name)
    {
        Name = name;
        BbcodeEnabled = true;
        FitContent = true;
        ScrollActive = false;
        Visible = false;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AutowrapMode = TextServer.AutowrapMode.WordSmart;
        MetaClicked += meta =>
        {
            if (LinkText.LinkOf(meta.AsString()) is { } raw)
                TooltipText = HostLinks.Click(raw) ?? "";
        };
    }

    /// <summary>The link targets drawn, in order (for checks).</summary>
    public string[] Targets => HostLinks.Targets(Text);

    /// <summary>Shows the links <paramref name="text"/> holds, each once, in the order they appear.</summary>
    public void ShowLinksOf(string? text)
    {
        var links = NotecardLinks.Split(text ?? "")
            .Where(s => s.Kind is NotecardSegmentKind.WebLink or NotecardSegmentKind.PlaceLink)
            .Select(s => s.Text).Distinct().ToList();
        TooltipText = string.Empty;   // the last click's note goes with the old links
        Text = string.Join("\n", links.Select(l => LinkText.Render(l, ChatText.Escape, HostLinks.Router)));
        Visible = links.Count > 0 && Text.Contains("[url=", System.StringComparison.Ordinal);
    }
}
