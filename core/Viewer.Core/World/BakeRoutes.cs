// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.World;

/// <summary>
/// Which avatar and bake slot each baked texture id belongs to, learned from every AvatarAppearance the session receives
/// (our own and everyone else's), so a texture fetch that is given only an id can ask the grid's appearance service for it.
///
/// The official viewer fetches every bake from the appearance service the login reply names (agent_appearance_service,
/// llstartup.cpp:3962) at service + "texture/" + avatar id + "/" + slot name + "/" + bake id (LLVOAvatar::getImageURL,
/// llvoavatar.cpp:5889-5908, used by getBakedTextureImage, llvoavatar.cpp:2598); the slot names are the bake dictionary's
/// (llavatarappearancedefines.cpp:81-91). Firestorm, which also serves grids without that service, uses the address only
/// when the region bakes avatars (RegionProtocols bit 0, LLViewerRegion's central bake version, llviewerregion.cpp:3316)
/// and the avatar's appearance says it is server-baked (appearance version above 0, setIsUsingServerBakes,
/// llvoavatar.cpp:11015; getImageURL, llvoavatar.cpp:6818); otherwise a bake is an ordinary texture fetched by id.
///
/// Bounded: the oldest routes go first past <see cref="Capacity"/>. A bake id names one bake, so its route never changes.
/// </summary>
public sealed class BakeRoutes
{
    /// <summary>The most bake ids held (eleven slots for a few hundred avatars).</summary>
    public const int Capacity = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, (Guid Agent, string Slot)> _routes = new();
    private readonly Queue<Guid> _order = new();

    /// <summary>Records the bakes an appearance names. An appearance that is not server-baked (version 0) records nothing.</summary>
    public void Note(AvatarAppearanceData appearance)
    {
        if (appearance.AppearanceVersion <= 0) return;
        var bakes = AppearanceSlots.BakesOf(appearance.FaceTextures);
        lock (_gate)
        {
            foreach (var (slot, id) in bakes)
            {
                if (_routes.ContainsKey(id)) continue;
                _routes[id] = (appearance.AgentId, slot);
                _order.Enqueue(id);
                while (_order.Count > Capacity) _routes.Remove(_order.Dequeue());
            }
        }
    }

    /// <summary>How many bake ids are held.</summary>
    public int Count { get { lock (_gate) return _routes.Count; } }

    /// <summary>
    /// The appearance-service address for a texture id, or null: no service URL from the login reply, a region that does
    /// not bake avatars, or an id that is not a server bake this session has seen. Null means the texture is fetched by id as before.
    /// </summary>
    public Uri? UriFor(Guid id, string? service, bool regionBakes)
    {
        if (!regionBakes || string.IsNullOrEmpty(service)) return null;
        if (regionBakes) return null; // RED STUB
        (Guid Agent, string Slot) route;
        lock (_gate)
            if (!_routes.TryGetValue(id, out route)) return null;
        return Uri.TryCreate(AppearanceSlots.BakeAddress(service, route.Agent, route.Slot, id), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
    }
}
