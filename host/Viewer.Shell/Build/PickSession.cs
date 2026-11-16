// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using Viewer.Core.World;

namespace Viewer.Shell.Build;

/// <summary>One face as a picker found it when it opened: its texture, its colour and its PBR material (empty for none).</summary>
public sealed record FacePick(uint Local, int Face, Guid Texture, Rgba Colour, Guid Material);

/// <summary>The faces a picker's choices go to, as they were when it opened (the official saveSelectedObjectTextures and the
/// colours the selection keeps for selectionRevertColors).</summary>
public sealed record FacePicks(IReadOnlyList<FacePick> Faces);

/// <summary>
/// One opening of a picker that stays open (the Build window's texture, material and colour pickers). A choice is applied as it is made
/// (the official viewer's Apply now, on by default: TextureLivePreview and ApplyColorImmediately in its settings.xml); OK keeps it;
/// Cancel, or the picker closing any other way, puts back what was there when it opened (the official LLFloaterTexturePicker::onBtnCancel
/// and LLFloaterColorPicker::onClickCancel). Once ended, nothing more is applied or put back. Main thread only.
/// </summary>
public sealed class PickSession<T>
{
    private readonly Action<T> _apply;
    private readonly Action _revert;

    public PickSession(Action<T> apply, Action revert)
    {
        _apply = apply;
        _revert = revert;
    }

    /// <summary>True once OK, Cancel or a close has ended the session.</summary>
    public bool Ended { get; private set; }

    /// <summary>True when OK ended it: the choice stays.</summary>
    public bool Kept { get; private set; }

    /// <summary>A choice: applied at once.</summary>
    public void Choose(T value)
    {
    }

    /// <summary>OK: the choice stays.</summary>
    public void Ok()
    {
    }

    /// <summary>Cancel: what was there is put back.</summary>
    public void Cancel()
    {
    }

    /// <summary>The picker has closed (its close button, or anything else that closed it): without OK, what was there is put back.</summary>
    public void Closed()
    {
    }
}
