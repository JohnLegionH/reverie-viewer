// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

namespace Viewer.Core.Assets;

/// <summary>A transport that can fetch texture asset bytes (sculpt maps, stored raw; decoding is).</summary>
public interface ITextureTransport
{
    Task<AssetFetchResult> FetchTextureAsync(Guid id, CancellationToken ct);
}

/// <summary>A transport that can fetch material asset bytes (a PBR material: glTF JSON in an LLSD wrapper, stored raw).</summary>
public interface IMaterialTransport
{
    Task<AssetFetchResult> FetchMaterialAsync(Guid id, CancellationToken ct);
}

/// <summary>A transport that can fetch animation asset bytes (the binary keyframe layout, stored raw).</summary>
public interface IAnimationTransport
{
    Task<AssetFetchResult> FetchAnimationAsync(Guid id, CancellationToken ct);
}

/// <summary>A transport that can fetch sound asset bytes (Ogg Vorbis, stored raw).</summary>
public interface ISoundTransport
{
    Task<AssetFetchResult> FetchSoundAsync(Guid id, CancellationToken ct);
}

/// <summary>What one sound request through the region's asset capability answered, for checks: the HTTP status (null when no answer
/// came), the Content-Type sent, the body's size, its first bytes in hex, and why no body was kept (null when one was).</summary>
public sealed record SoundProbe(int? Status, string? ContentType, long Size, string FirstBytes, string? Why);

/// <summary>A transport that can say exactly what the grid answered for a sound (development checks only).</summary>
public interface ISoundProbe
{
    Task<SoundProbe> ProbeSoundAsync(Guid id, CancellationToken ct);
}

/// <summary>A transport that can fetch settings asset bytes (a sky, water or day cycle: LLSD text).</summary>
public interface ISettingsTransport
{
    Task<AssetFetchResult> FetchSettingsAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// A local-only asset cache (ADR-011: grid content, never committed), one file per asset keyed by its id:
/// <c>&lt;id&gt;.mesh</c> for mesh assets, <c>&lt;id&gt;.j2k</c> for textures (sculpt maps), <c>&lt;id&gt;.material</c> for PBR materials, <c>&lt;id&gt;.anim</c> for animations, <c>&lt;id&gt;.settings</c> for settings assets (skies). It is also the replay asset
/// source: <see cref="FetchMeshAsync"/> / <see cref="FetchTextureAsync"/> serve from it, and a miss is NotFound.
/// Writes only inside its own directory, only files it names itself.
/// </summary>
public sealed class LocalAssetCache(string directory) : IMeshTransport, ITextureTransport, IMaterialTransport, IAnimationTransport, ISettingsTransport
{
    public string Directory { get; } = Path.GetFullPath(directory);

    public string MeshPath(Guid id) => Path.Combine(Directory, id.ToString("D") + ".mesh");
    public string TexturePath(Guid id) => Path.Combine(Directory, id.ToString("D") + ".j2k");
    public string MaterialPath(Guid id) => Path.Combine(Directory, id.ToString("D") + ".material");
    public string AnimationPath(Guid id) => Path.Combine(Directory, id.ToString("D") + ".anim");
    public string SettingsPath(Guid id) => Path.Combine(Directory, id.ToString("D") + ".settings");

    public bool HasMesh(Guid id) => File.Exists(MeshPath(id));
    public bool HasTexture(Guid id) => File.Exists(TexturePath(id));
    public bool HasMaterial(Guid id) => File.Exists(MaterialPath(id));
    public bool HasAnimation(Guid id) => File.Exists(AnimationPath(id));

    public void PutMesh(Guid id, ReadOnlySpan<byte> bytes) => Put(MeshPath(id), bytes);
    public void PutTexture(Guid id, ReadOnlySpan<byte> bytes) => Put(TexturePath(id), bytes);
    public void PutMaterial(Guid id, ReadOnlySpan<byte> bytes) => Put(MaterialPath(id), bytes);
    public void PutAnimation(Guid id, ReadOnlySpan<byte> bytes) => Put(AnimationPath(id), bytes);
    public void PutSettings(Guid id, ReadOnlySpan<byte> bytes) => Put(SettingsPath(id), bytes);

    private void Put(string path, ReadOnlySpan<byte> bytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes.ToArray());
        File.Move(tmp, path, overwrite: true);   // same content for the same id: replacing is idempotent
    }

    public Task<AssetFetchResult> FetchMeshAsync(Guid id, CancellationToken ct) => Read(MeshPath(id), ct);
    public Task<AssetFetchResult> FetchTextureAsync(Guid id, CancellationToken ct) => Read(TexturePath(id), ct);
    public Task<AssetFetchResult> FetchMaterialAsync(Guid id, CancellationToken ct) => Read(MaterialPath(id), ct);
    public Task<AssetFetchResult> FetchAnimationAsync(Guid id, CancellationToken ct) => Read(AnimationPath(id), ct);
    public Task<AssetFetchResult> FetchSettingsAsync(Guid id, CancellationToken ct) => Read(SettingsPath(id), ct);

    private static Task<AssetFetchResult> Read(string path, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(AssetFetchResult.Of(AssetFetchStatus.Cancelled));
        return Task.FromResult(File.Exists(path) ? AssetFetchResult.Ok(File.ReadAllBytes(path)) : AssetFetchResult.Of(AssetFetchStatus.NotFound));
    }
}
