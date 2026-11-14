// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using LibreMetaverse;
using Viewer.Core.World;

namespace Viewer.Core.Protocol;

/// <summary>
/// LibreMetaverse -> core material values: the texture entry's per-face material references, the RenderMaterials
/// capability's legacy materials, and the GLTF override message. The only place these conversions live.
/// </summary>
public static class LmvMaterials
{
    /// <summary>
    /// The texture entry's per-face bump, shininess, legacy material id and PBR material id; null when no face has any
    /// (so a prim without materials carries nothing new).
    /// </summary>
    internal static FaceMaterials? ToMaterials(Primitive.TextureEntry? te)
    {
        if (te?.DefaultTexture is not { } def)
            return null;
        var faces = new FaceMaterial?[te.FaceTextures.Length];
        int last = -1;
        bool any = !M(def).IsNone;
        for (int i = 0; i < faces.Length; i++)
            if (te.FaceTextures[i] is { } f)
            {
                faces[i] = M(f);
                last = i;
                any |= !faces[i]!.Value.IsNone;
            }
        return any ? new FaceMaterials(M(def), faces.Take(last + 1)) : null;
    }

    // The shininess byte keeps its level in the top two bits (none, low, medium, high).
    private static FaceMaterial M(Primitive.TextureEntryFace f) =>
        new((byte)f.Bump, (byte)((byte)f.Shiny >> 6), f.MaterialID.Guid, f.RenderMaterialID.Guid);

    internal static LegacyMaterial ToLegacyMaterial(LibreMetaverse.Materials.LegacyMaterial m) => new(
        m.ID.Guid,
        new MaterialMap(m.NormalMap.Guid, (float)m.NormalMapOffsetX, (float)m.NormalMapOffsetY, (float)m.NormalMapRepeatX, (float)m.NormalMapRepeatY, (float)m.NormalMapRotation),
        new MaterialMap(m.SpecularMap.Guid, (float)m.SpecularMapOffsetX, (float)m.SpecularMapOffsetY, (float)m.SpecularMapRepeatX, (float)m.SpecularMapRepeatY, (float)m.SpecularMapRotation),
        new Rgba(m.SpecularColor.R, m.SpecularColor.G, m.SpecularColor.B, m.SpecularColor.A),
        m.SpecularExponent, m.EnvironmentIntensity, ToAlphaMode(m.DiffuseAlphaMode), m.AlphaMaskCutoff);

    private static LegacyAlphaMode ToAlphaMode(LibreMetaverse.Materials.LegacyMaterialAlphaMode a) => a switch
    {
        LibreMetaverse.Materials.LegacyMaterialAlphaMode.Blend => LegacyAlphaMode.Blend,
        LibreMetaverse.Materials.LegacyMaterialAlphaMode.Mask => LegacyAlphaMode.Mask,
        LibreMetaverse.Materials.LegacyMaterialAlphaMode.Emissive => LegacyAlphaMode.Emissive,
        _ => LegacyAlphaMode.None,
    };

    /// <summary>The largest material asset accepted (a glTF material is a few kilobytes of JSON).</summary>
    public const int MaterialCapBytes = 256 * 1024;

    /// <summary>
    /// A PBR material asset from the region's ViewerAsset capability ("material_id"), bounded before it is held, through
    /// the session's HTTP client (the outbound policy applies). Not through the library's asset request: in 3.1.6 it builds
    /// no usable query for this asset type, and every such request came back not found.
    /// </summary>
    internal static async Task<Viewer.Core.Assets.AssetFetchResult> FetchAsync(GridClient client, Guid id, CancellationToken ct)
    {
        Uri? cap;
        try { cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        var r = await Viewer.Core.Net.BoundedHttp.GetBoundedAsync(client.HttpCapsClient, MaterialUri(cap, id), MaterialCapBytes, ct).ConfigureAwait(false);
        if (r.Body is { Length: > 0 } body)
            return Viewer.Core.Assets.AssetFetchResult.Ok(body);
        return Viewer.Core.Assets.AssetFetchResult.Of(r.Status == 404 ? Viewer.Core.Assets.AssetFetchStatus.NotFound : Viewer.Core.Assets.AssetFetchStatus.Failed);
    }

    /// <summary>
    /// An animation asset's bytes through the region's ViewerAsset capability (a plain bounded GET, as for materials);
    /// Failed when the region has no such capability.
    /// </summary>
    internal static async Task<Viewer.Core.Assets.AssetFetchResult> FetchAnimationAsync(GridClient client, Guid id, CancellationToken ct)
    {
        Uri? cap;
        try { cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        var r = await Viewer.Core.Net.BoundedHttp.GetBoundedAsync(client.HttpCapsClient, AnimationUri(cap, id), Viewer.Core.Avatars.AnimationClip.MaxBytes, ct).ConfigureAwait(false);
        if (r.Body is { Length: > 0 } body)
            return Viewer.Core.Assets.AssetFetchResult.Ok(body);
        return Viewer.Core.Assets.AssetFetchResult.Of(r.Status == 404 ? Viewer.Core.Assets.AssetFetchStatus.NotFound : Viewer.Core.Assets.AssetFetchStatus.Failed);
    }

    /// <summary>The ViewerAsset request for an animation asset.</summary>
    internal static Uri AnimationUri(Uri cap, Guid id) => new(cap + (cap.Query.Length > 0 ? "&" : "?") + "animatn_id=" + id.ToString("D"));

    /// <summary>The ViewerAsset request for a sound asset (Ogg Vorbis).</summary>
    internal static Uri SoundUri(Uri cap, Guid id) => new(cap + (cap.Query.Length > 0 ? "&" : "?") + "sound_id=" + id.ToString("D"));

    /// <summary>A sound asset's bytes through the region's ViewerAsset capability (a plain bounded GET, as for animations);
    /// Failed when the region has no such capability.</summary>
    internal static async Task<Viewer.Core.Assets.AssetFetchResult> FetchSoundAsync(GridClient client, Guid id, CancellationToken ct)
    {
        Uri? cap;
        try { cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        var r = await Viewer.Core.Net.BoundedHttp.GetBoundedAsync(client.HttpCapsClient, SoundUri(cap, id), SoundCapBytes, ct).ConfigureAwait(false);
        if (r.Body is { Length: > 0 } body)
            return Viewer.Core.Assets.AssetFetchResult.Ok(body);
        return Viewer.Core.Assets.AssetFetchResult.Of(r.Status == 404 ? Viewer.Core.Assets.AssetFetchStatus.NotFound : Viewer.Core.Assets.AssetFetchStatus.Failed);
    }

    /// <summary>The largest sound asset accepted (a sound asset is at most ten seconds of Ogg Vorbis, far under this).</summary>
    public const int SoundCapBytes = 2 * 1024 * 1024;

    /// <summary>The same request as <see cref="FetchSoundAsync"/>, reporting what came back (status, Content-Type, size, first bytes)
    /// instead of keeping the bytes.</summary>
    internal static async Task<Viewer.Core.Assets.SoundProbe> ProbeSoundAsync(GridClient client, Guid id, CancellationToken ct)
    {
        Uri? cap;
        try { cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return new(null, null, 0, "", "the region has no ViewerAsset capability");
        try
        {
            var r = await Viewer.Core.Net.BoundedHttp.GetBoundedAsync(client.HttpCapsClient, SoundUri(cap, id), SoundCapBytes, ct).ConfigureAwait(false);
            var body = r.Body ?? [];
            return new(r.Status, r.ContentType, r.Body is null ? r.BytesRead : body.Length, Convert.ToHexString(body.AsSpan(0, Math.Min(16, body.Length))), r.Why);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(null, null, 0, "", ex.GetType().Name);
        }
    }

    /// <summary>The ViewerAsset request for a settings asset (a sky, water or day cycle).</summary>
    internal static Uri SettingsUri(Uri cap, Guid id) => new(cap + (cap.Query.Length > 0 ? "&" : "?") + "settings_id=" + id.ToString("D"));

    /// <summary>The largest settings asset accepted (a sky or water is a few kilobytes of LLSD).</summary>
    public const int SettingsCapBytes = World.SettingsSky.MaxBytes;

    /// <summary>A settings asset (a sky, water or day cycle) from the region's ViewerAsset capability ("settings_id"), bounded, through the session's HTTP client.</summary>
    internal static async Task<Viewer.Core.Assets.AssetFetchResult> FetchSettingsAsync(GridClient client, Guid id, CancellationToken ct)
    {
        Uri? cap;
        try { cap = client.Network.CurrentSim?.Caps?.CapabilityURI("ViewerAsset"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { cap = null; }
        if (cap is null)
            return Viewer.Core.Assets.AssetFetchResult.Of(Viewer.Core.Assets.AssetFetchStatus.Failed);
        var r = await Viewer.Core.Net.BoundedHttp.GetBoundedAsync(client.HttpCapsClient, SettingsUri(cap, id), SettingsCapBytes, ct).ConfigureAwait(false);
        if (r.Body is { Length: > 0 } body)
            return Viewer.Core.Assets.AssetFetchResult.Ok(body);
        return Viewer.Core.Assets.AssetFetchResult.Of(r.Status == 404 ? Viewer.Core.Assets.AssetFetchStatus.NotFound : Viewer.Core.Assets.AssetFetchStatus.Failed);
    }

    /// <summary>The ViewerAsset request for a material asset.</summary>
    internal static Uri MaterialUri(Uri cap, Guid id) => new(cap + (cap.Query.Length > 0 ? "&" : "?") + "material_id=" + id.ToString("D"));

    /// <summary>
    /// The glTF JSON inside a PBR material asset: the asset is LLSD (XML, binary or notation) holding a map with a
    /// "version" of 1.0 or 1.1, a "type" of "GLTF 2.0" and the glTF document as a string under "data", as the official
    /// viewer reads it. Null when the bytes are over <see cref="MaterialCapBytes"/> or not that shape. Binary and XML LLSD
    /// are checked against <see cref="LlsdGuard"/>'s limits before the library reads them; data outside them gives null and
    /// one call to <paramref name="refused"/> with the reason.
    /// </summary>
    public static string? GltfOfMaterialAsset(byte[] bytes, Action<string>? refused = null)
    {
        if (bytes.Length == 0 || bytes.Length > MaterialCapBytes)
            return null;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            string? version, type, data;
            if (text.StartsWith('<'))
            {
                bool binary = IsBinaryHeader(text);
                int body = binary ? Array.IndexOf(bytes, (byte)'\n') + 1 : 0;
                var why = binary
                    ? body > 0 ? LlsdGuard.CheckBinary(bytes.AsSpan(body)) : "binary header without a body"
                    : LlsdGuard.CheckXml(text);
                if (why is not null)
                {
                    refused?.Invoke(why);
                    return null;
                }
                var osd = binary
                    ? LibreMetaverse.StructuredData.OSDParser.DeserializeLLSDBinary(bytes)
                    : LibreMetaverse.StructuredData.OSDParser.DeserializeLLSDXml(text);
                if (osd is not LibreMetaverse.StructuredData.OSDMap m)
                    return null;
                version = m.ContainsKey("version") ? m["version"].AsString() : null;
                type = m.ContainsKey("type") ? m["type"].AsString() : null;
                data = m.ContainsKey("data") && m["data"].Type == LibreMetaverse.StructuredData.OSDType.String ? m["data"].AsString() : null;
            }
            else
            {
                if (LlsdNotation.Parse(text) is not System.Text.Json.Nodes.JsonObject m)
                    return null;
                version = m["version"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? vs) ? vs : null;
                type = m["type"] is System.Text.Json.Nodes.JsonValue t && t.TryGetValue(out string? ts) ? ts : null;
                data = m["data"] is System.Text.Json.Nodes.JsonValue d && d.TryGetValue(out string? ds) ? ds : null;
            }
            return version is "1.0" or "1.1" && type == "GLTF 2.0" && data is not null ? data : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>The binary LLSD header line, written "&lt;? LLSD/Binary ?&gt;" or "&lt;?llsd/binary?&gt;" (case and spaces vary).</summary>
    private static bool IsBinaryHeader(string text)
    {
        int end = text.IndexOf('\n');
        var head = (end < 0 ? text : text[..end]).Replace(" ", "", StringComparison.Ordinal).TrimEnd('\r');
        return head.Equals("<?llsd/binary?>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The texture ids a PBR material asset names (its glTF images' uris; empty and unreadable ones left out), or null
    /// when the asset is not a material (<see cref="GltfOfMaterialAsset"/>) or its glTF is not a JSON object.
    /// </summary>
    public static IReadOnlyList<Guid>? TexturesOfMaterialAsset(Guid id, byte[] bytes)
    {
        if (GltfOfMaterialAsset(bytes) is not { } gltf)
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(gltf);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;
            var ids = new List<Guid>();
            if (doc.RootElement.TryGetProperty("images", out var images) && images.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var img in images.EnumerateArray())
                    if (img.ValueKind == System.Text.Json.JsonValueKind.Object && img.TryGetProperty("uri", out var uri)
                        && uri.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(uri.GetString(), out var g) && g != Guid.Empty)
                        ids.Add(g);
            return ids.Distinct().ToList();
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// The GLTF override message (LLSD notation): a map with the object's local id ("id"), the faces it names ("te") and,
    /// in the same order, each face's override map ("od"). Every named face is returned with its override as JSON; a
    /// face whose override is not a map is returned with an empty object (its override cleared). An empty face list
    /// clears the object's overrides. Null for a message that is not that shape. Read with <see cref="LlsdNotation"/>,
    /// not the library's parser (see there).
    /// </summary>
    internal static IReadOnlyList<FaceMaterialOverride>? ParseOverrideMessage(string notation, out uint localId)
    {
        localId = 0;
        System.Text.Json.Nodes.JsonNode? root;
        try { root = LlsdNotation.Parse(notation); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { return null; }
        if (root is not System.Text.Json.Nodes.JsonObject map || map["id"] is not System.Text.Json.Nodes.JsonValue id || !id.TryGetValue(out long local)
            || local < 0 || local > uint.MaxValue)
            return null;
        localId = (uint)local;
        var faces = new List<FaceMaterialOverride>();
        if (map["te"] is not System.Text.Json.Nodes.JsonArray tes)
            return faces;
        var ods = map["od"] as System.Text.Json.Nodes.JsonArray;
        for (int i = 0; i < tes.Count; i++)
        {
            if (tes[i] is not System.Text.Json.Nodes.JsonValue f || !f.TryGetValue(out long face) || face < 0 || face > 255)
                return null;
            string json = ods is not null && i < ods.Count && ods[i] is System.Text.Json.Nodes.JsonObject o ? o.ToJsonString() : "{}";
            faces.Add(new FaceMaterialOverride((int)face, json));
        }
        return faces;
    }
}
