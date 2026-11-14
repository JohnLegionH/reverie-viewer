// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Collections.Generic;
using System.Linq;
using Godot;
using Viewer.Core.Interaction;
using Viewer.Core.World;

namespace Viewer.Godot.Presentation;

/// <summary>One object's floating text: where it hangs (region coordinates), its words and its colour.</summary>
public readonly record struct FloatingText(uint LocalId, System.Numerics.Vector3 Anchor, PrimInteraction Interaction);

/// <summary>
/// Floating text over objects (what scripts set): each is a label that always faces the camera, in the colour and opacity the script
/// chose, faded and hidden with distance by <see cref="HoverTextRules"/>, hidden by solid objects in front of it (it is depth tested)
/// and drawn with a dark outline for contrast. Text behind the camera is not drawn.
/// </summary>
public partial class HoverTextLayer : Node3D
{
    private readonly Dictionary<uint, Label3D> _labels = new();

    /// <summary>How many labels are shown now.</summary>
    public int Shown { get; private set; }

    public void Update(IReadOnlyList<FloatingText> texts, Vector3 cameraPosition, Vector3 cameraForward, float maxDistance = HoverTextRules.DefaultMaxDistance)
    {
        var keep = new HashSet<uint>();
        int shown = 0;
        foreach (var t in texts)
        {
            var at = new Vector3(t.Anchor.X, t.Anchor.Z, -t.Anchor.Y);
            var toText = at - cameraPosition;
            float distance = toText.Length();
            var style = HoverTextRules.Style(t.Interaction, distance, maxDistance);
            if (!style.Visible || toText.Dot(cameraForward) <= 0f)
                continue;
            keep.Add(t.LocalId);
            if (!_labels.TryGetValue(t.LocalId, out var label))
            {
                label = new Label3D
                {
                    Name = "text_" + t.LocalId, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, DoubleSided = true,
                    FontSize = 56, PixelSize = 0.0055f, OutlineSize = 12, OutlineModulate = new Color(0f, 0f, 0f, 0.8f), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom, AutowrapMode = TextServer.AutowrapMode.WordSmart, Width = 1000f, AlphaCut = Label3D.AlphaCutMode.Disabled,
                };
                AddChild(label);
                _labels[t.LocalId] = label;
            }
            string words = t.Interaction.HoverText!;
            if (label.Text != words) label.Text = words;
            label.Position = at;
            label.Modulate = new Color(style.R, style.G, style.B, style.Alpha);
            label.OutlineModulate = new Color(0f, 0f, 0f, 0.8f * style.Alpha);
            shown++;
        }
        foreach (var id in _labels.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            RemoveChild(_labels[id]);
            _labels[id].QueueFree();
            _labels.Remove(id);
        }
        Shown = shown;
    }
}
