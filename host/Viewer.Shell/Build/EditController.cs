// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Numerics;
using Viewer.Core.Actions;
using Viewer.Core.Building;
using Viewer.Core.Interaction;
using Viewer.Core.Meshing;
using Viewer.Core.Session;
using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>The build tools as the official viewer names them (lltoolmgr.cpp, menu_viewer.xml: Ctrl+1 to Ctrl+5).</summary>
public enum BuildTool { Focus, Move, Edit, Create, Land }

/// <summary>
/// Build mode, its tools and the edit selection, and every edit sent from it: names, descriptions, permissions, Locked, place, rotation
/// and size by number, flags, shape, Link, Unlink and the Shift-drag copy. Behaviour follows the official viewer's LLToolMgr,
/// LLSelectMgr and LLPanelObject (read as behaviour only). No engine types: the Godot side draws the window and the handles from it.
/// </summary>
public sealed class EditController
{
    private readonly WorldState _world;
    private readonly Func<ActionIntent, ActionResult> _submit;
    private readonly Func<EditLimits> _limits;

    public EditController(WorldState world, Func<ActionIntent, ActionResult> submit, Func<EditLimits>? limits = null)
    {
        _world = world;
        _submit = submit;
        _limits = limits ?? (() => EditLimits.Official);
    }

    public event Action? Changed;

    public bool BuildMode { get; private set; }
    public BuildTool Tool { get; private set; } = BuildTool.Create;
    /// <summary>"Edit linked": a click selects one part of a linkset instead of the whole (EditLinkedParts).</summary>
    public bool EditLinked { get; set; }
    /// <summary>Snap to grid (SnapEnabled, on by default; G toggles it).</summary>
    public bool SnapEnabled { get; set; } = true;
    /// <summary>The grid unit in metres (GridResolution).</summary>
    public float GridUnit { get; set; } = Snap.DefaultGridUnit;
    /// <summary>"Stretch Both Sides" (ScaleUniform, off by default).</summary>
    public bool StretchBothSides { get; set; }
    /// <summary>"Stretch Textures" (ScaleStretchTextures, on by default).</summary>
    public bool StretchTextures { get; set; } = true;

    /// <summary>The selection in the order it was made, the latest last.</summary>
    public IReadOnlyList<uint> Selection => [];

    /// <summary>The object the window shows: the one selected last.</summary>
    public uint? Primary => null;

    /// <summary>Ctrl+B: opens build mode with the Create tool, or closes it.</summary>
    public void ToggleBuild() { }

    /// <summary>Esc or closing the window: build mode ends, Edit linked goes off, nothing stays selected.</summary>
    public void CloseBuild() { }

    /// <summary>Choose a tool (Ctrl+1 to Ctrl+5); build mode opens with it.</summary>
    public void SelectTool(BuildTool tool) { }

    /// <summary>A click with the Edit or Move tool: selects the object's root (or the part, with Edit linked); Shift adds or removes; a click on
    /// nothing clears. True when the click was the edit tools'.</summary>
    public bool Click(PickResult pick, bool shift) => false;

    /// <summary>A drag-select: the objects inside the box (their roots, or the parts with Edit linked); Shift adds to the selection.</summary>
    public void SelectMany(IEnumerable<uint> locals, bool shift) { }

    public void Deselect() => Changed?.Invoke();

    /// <summary>What may be done with a selected object.</summary>
    public EditAccess Access(uint local) => new(false, false, false, false);

    /// <summary>The object's properties, once the region has sent them for the selection.</summary>
    public ObjectPropertiesReceived? PropertiesOf(uint local) => null;

    public void Receive(TransportEvent e) { }

    public bool SetName(string name) => false;
    public bool SetDescription(string description) => false;
    public bool SetPosition(Vector3 position) => false;
    public bool SetRotation(Vector3 degrees) => false;
    public bool SetSize(Vector3 size) => false;
    public bool SetFlags(bool physical, bool temporary, bool phantom) => false;
    public bool SetLocked(bool locked) => false;
    public bool SetPermission(byte field, uint mask, bool on) => false;
    public bool SetShape(PrimShape shape) => false;

    /// <summary>Ctrl+L: links the selection, the last selected becoming the root. Null when sent, else why not.</summary>
    public string? Link() => "stub";

    /// <summary>Ctrl+Shift+L: unlinks every prim of the selected linksets.</summary>
    public bool Unlink() => false;

    /// <summary>The start of a Shift-drag with the Move tool: the selection is copied in place and the copy stays behind while the
    /// originals move on with the drag.</summary>
    public bool CopyInPlace() => false;
}
