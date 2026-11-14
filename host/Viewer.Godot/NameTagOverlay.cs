// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Shell;
using Viewer.Shell.NameTags;

namespace Viewer.Godot;

/// <summary>
/// Draws the name tags <see cref="NameTagModel"/> plans: one small panel per avatar, white text on a black background, the same
/// pixel size at any distance, its bottom edge 25 pixels above the point the plan names and kept wholly inside the view. A screen
/// overlay on top of the 3D view (so, unlike the official viewer's tags, it is not hidden by the world in front of an avatar). Panels
/// are reused; one for an avatar that is gone is hidden, and everything is freed with the overlay. Main thread only.
/// </summary>
public partial class NameTagOverlay : Control
{
    private sealed class Tag
    {
        public required PanelContainer Panel;
        public required VBoxContainer Box;
        public string Key = "";
    }

    private readonly Dictionary<Guid, Tag> _tags = new();
    private readonly float _backgroundOpacity;

    /// <summary>How many tags were drawn by the last <see cref="Show"/>.</summary>
    public int Shown { get; private set; }

    /// <summary>The avatars whose tags the last <see cref="Show"/> drew (a planned tag whose avatar is off screen is not among them).</summary>
    public IReadOnlyList<Guid> DrawnAgents { get; private set; } = [];

    public int NormalSize { get; set; } = 14;
    public int SmallSize { get; set; } = 11;

    /// <param name="backgroundOpacity">The black background's opacity (the official chat bubble opacity, 0.5).</param>
    public NameTagOverlay(float backgroundOpacity = 0.5f)
    {
        Name = "name_tags";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _backgroundOpacity = backgroundOpacity;
    }

    /// <summary>Draws these plans seen through <paramref name="camera"/>; every tag not in the plans is hidden.</summary>
    public void Show(IReadOnlyList<NameTagPlan> plans, Camera3D camera, Vector2 view)
    {
        var live = new HashSet<Guid>();
        var drawn = new List<Guid>();
        int shown = 0;
        foreach (var plan in plans.OrderByDescending(p => p.Distance))   // far to near, so the nearer tag is drawn over
        {
            var world = new Vector3(plan.Anchor.X, plan.Anchor.Z, -plan.Anchor.Y);   // region axes to the engine's
            if (camera.IsPositionBehind(world))
                continue;
            if (!_tags.TryGetValue(plan.AgentId, out var tag))
                _tags[plan.AgentId] = tag = Make();
            Fill(tag, plan);
            var size = tag.Panel.GetCombinedMinimumSize();
            tag.Panel.Size = size;
            var at = camera.UnprojectPosition(world);
            if (at.X < -50 || at.X > view.X + 50 || at.Y < -50 || at.Y > view.Y + 50 + size.Y)
                continue;   // its avatar is not in view
            float x = Math.Clamp(at.X - size.X / 2f, 0f, Math.Max(0f, view.X - size.X));
            float y = Math.Clamp(at.Y - NameTagModel.LiftPixels - size.Y, 0f, Math.Max(0f, view.Y - size.Y));
            tag.Panel.Position = new Vector2((float)Math.Round(x), (float)Math.Round(y));
            var faded = tag.Panel.Modulate;
            faded.A = plan.Alpha;
            tag.Panel.Modulate = faded;
            tag.Panel.Visible = true;
            MoveChild(tag.Panel, GetChildCount() - 1);
            live.Add(plan.AgentId);
            drawn.Add(plan.AgentId);
            shown++;
        }
        foreach (var (id, tag) in _tags)
            if (!live.Contains(id))
                tag.Panel.Visible = false;
        Shown = shown;
        DrawnAgents = drawn;
    }

    /// <summary>Hides every tag (no session is presented).</summary>
    public void HideAll()
    {
        foreach (var tag in _tags.Values)
            tag.Panel.Visible = false;
        Shown = 0;
        DrawnAgents = [];
    }

    private Tag Make()
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        var style = new StyleBoxFlat { BgColor = ShellTheme.C(UiTheme.Tokens.Scrim with { A = _backgroundOpacity }) };
        style.SetCornerRadiusAll(UiTheme.Tokens.Radius);
        style.SetContentMarginAll(4);
        panel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 1);
        panel.AddChild(box);
        AddChild(panel);
        return new Tag { Panel = panel, Box = box };
    }

    private void Fill(Tag tag, NameTagPlan plan)
    {
        string key = string.Join("\n", plan.Lines.Select(l => $"{l.Size}|{l.Colour.Hex}|{l.Text}"));
        if (key == tag.Key)
            return;
        tag.Key = key;
        while (tag.Box.GetChildCount() > plan.Lines.Count)
        {
            var extra = tag.Box.GetChild(tag.Box.GetChildCount() - 1);
            tag.Box.RemoveChild(extra);
            extra.Free();
        }
        while (tag.Box.GetChildCount() < plan.Lines.Count)
            tag.Box.AddChild(new Label { MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center });
        for (int i = 0; i < plan.Lines.Count; i++)
        {
            var line = plan.Lines[i];
            var label = (Label)tag.Box.GetChild(i);
            int size = line.Size == NameTagSize.Normal ? NormalSize : SmallSize;
            label.AddThemeFontSizeOverride("font_size", size);
            label.AddThemeColorOverride("font_color", ShellTheme.C(line.Colour));
            label.Text = line.Text;
            var font = label.GetThemeFont("font");
            float width = font.GetStringSize(line.Text, HorizontalAlignment.Left, -1, size).X;
            label.CustomMinimumSize = new Vector2(Math.Min(width, NameTagModel.MaxWidthPixels), 0);
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }
        tag.Panel.ResetSize();
    }
}
