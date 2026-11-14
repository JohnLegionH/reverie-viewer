// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Avatars;

/// <summary>
/// Which avatars must work out again what their worn meshes hide of the body. The official viewer does it in LLVOAvatar::updateMeshVisibility when a
/// piece is attached or detached, when an appearance message is applied, when the overall appearance changes (llvoavatar.cpp), and when a worn
/// object's face texture changes to or from a baked texture id (llviewerobject.cpp updateAvatarMeshVisibility); a change between two ordinary
/// textures cannot change the result and is left out. Main thread only.
/// </summary>
public sealed class WornRecheck
{
    private readonly HashSet<uint> _avatars = new();
    private bool _everyone;

    /// <summary>A piece was attached to <paramref name="wearer"/>.</summary>
    public void Attached(uint wearer) => _avatars.Add(wearer);

    /// <summary>A piece was detached; <paramref name="wearer"/> is null when it is not known who wore it (then everyone is marked).</summary>
    public void Detached(uint? wearer)
    {
        if (wearer is { } w) _avatars.Add(w);
        else _everyone = true;
    }

    /// <summary>An appearance message was applied to <paramref name="avatar"/>.</summary>
    public void AppearanceApplied(uint avatar) => _avatars.Add(avatar);

    /// <summary>The overall appearance of <paramref name="avatar"/> changed.</summary>
    public void OverallAppearanceChanged(uint avatar) => _avatars.Add(avatar);

    /// <summary>A worn object's faces went from the textures <paramref name="before"/> to <paramref name="after"/>: marks <paramref name="wearer"/> when a baked texture id came or went.</summary>
    public void FacesChanged(uint wearer, IReadOnlyCollection<Guid> before, IReadOnlyCollection<Guid> after)
    {
        foreach (var id in before)
            if (BakeChannels.IsBakeId(id) && !after.Contains(id)) { _avatars.Add(wearer); return; }
        foreach (var id in after)
            if (BakeChannels.IsBakeId(id) && !before.Contains(id)) { _avatars.Add(wearer); return; }
    }

    /// <summary>Marks <paramref name="avatar"/> again: its recheck could not be done this frame.</summary>
    public void Retry(uint avatar) => _avatars.Add(avatar);

    /// <summary>The marks so far (everyone, or these avatars), cleared.</summary>
    public (bool Everyone, IReadOnlyList<uint> Avatars) Take()
    {
        var result = (_everyone, (IReadOnlyList<uint>)_avatars.ToArray());
        _everyone = false;
        _avatars.Clear();
        return result;
    }
}
