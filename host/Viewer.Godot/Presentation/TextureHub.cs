// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System;
using System.Collections.Generic;
using Godot;
using Viewer.Core.Residency;
using Viewer.Shell;

namespace Viewer.Godot.Presentation;

/// <summary>
/// The texture state of a session, shared by every region's <see cref="PrimLayer"/>: one residency manager (one budget), one GPU texture
/// slot per asset, the discard level asked of the texture source for each texture in flight, and the queue of reloads. A texture two
/// regions use is asked for once, decoded and uploaded once and counted once; each layer keeps its own objects, materials and the
/// faces waiting for a texture.
///
/// A layer made without a hub makes its own, so a single layer behaves as it always did. Layers join with <see cref="Attach"/>, which
/// gives each its space (the residency tells regions apart by it: object ids are only unique within a region). The first layer to join
/// is the owner: it runs the manager's update each frame. When a layer leaves (<see cref="PrimLayer.Clear"/>), its users go; a texture
/// no other layer uses is evicted, and when it is the only layer left every slot is freed.
/// </summary>
public sealed class TextureHub
{
    public TextureSource? Source { get; }
    public TextureResidency? Residency { get; }

    /// <summary>One GPU texture per asset: materials bind it; an eviction swaps its data for a tiny placeholder under the same RID.</summary>
    public Dictionary<Guid, ImageTexture> Slots { get; } = new();
    public Dictionary<Guid, Viewer.Core.Imaging.AlphaClass> Alpha { get; } = new();
    /// <summary>The textures whose alpha is all or nothing enough to draw a face with them as a cut-out (<see cref="Viewer.Core.Imaging.AlphaMask"/>).</summary>
    public HashSet<Guid> AlphaMasks { get; } = new();
    /// <summary>The discard level asked of the texture source for each texture with a load or level change in flight.</summary>
    public Dictionary<Guid, int> Requested { get; } = new();
    public HashSet<Guid> Reloading { get; } = new();
    public Queue<Guid> Reupload { get; } = new();

    private readonly List<PrimLayer> _layers = new();
    private int _nextSpace;

    public TextureHub(TextureSource? source, long budgetBytes)
    {
        Source = source;
        if (source is not null)
            Residency = new TextureResidency(new ResidencyOptions { BudgetBytes = budgetBytes }) { FetchClasses = source.FetchClasses };
    }

    /// <summary>The layers sharing the hub, in the order they joined.</summary>
    public IReadOnlyList<PrimLayer> Layers => _layers;

    /// <summary>The draw distance in metres: objects beyond it are not presented (infinite: all are). The host sets it from the preference.</summary>
    public float DrawDistance { get; set; } = float.PositiveInfinity;

    /// <summary>The first layer to join: it runs the manager's update each frame.</summary>
    public PrimLayer? Owner => _layers.Count > 0 ? _layers[0] : null;

    /// <summary>Joins a layer; returns its space (0 for the first layer).</summary>
    public int Attach(PrimLayer layer)
    {
        _layers.Add(layer);
        return _nextSpace++;
    }

    public void Detach(PrimLayer layer) => _layers.Remove(layer);

    /// <summary>GPU texture slots held, once each however many regions use them.</summary>
    public int SlotCount => Slots.Count;

    /// <summary>Takes every arrival the texture source has and tells every layer (each re-presents its own waiting faces); a reload
    /// waiting for a texture goes to the shared queue once.</summary>
    public void DrainArrivals()
    {
        if (Source is null) return;
        while (Source.TryTakeArrival(out var arrived))
        {
            foreach (var layer in _layers) layer.TextureArrived(arrived);
            if (Reloading.Remove(arrived)) Reupload.Enqueue(arrived);
        }
    }

    /// <summary>Tells every layer a first load has been granted (or has data already) so each re-presents the faces it holds back.</summary>
    /// <summary>A texture's level changed and with it whether its alpha is a cut-out mask: every region's faces that draw with it are drawn again.</summary>
    public void AlphaMaskChanged(Guid id)
    {
        foreach (var layer in _layers.ToArray()) layer.MaskChanged(id);
    }

    public void Granted(Guid id, Viewer.Shell.MeshAvailability availability)
    {
        foreach (var layer in _layers.ToArray()) layer.GrantArrived(id, availability);
    }
}
