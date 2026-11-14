// SPDX-License-Identifier: LGPL-2.1-only
/**
 * @file llface.cpp
 * @brief LLFace class implementation
 *
 * $LicenseInfo:firstyear=2001&license=viewerlgpl$
 * Second Life Viewer Source Code
 * Copyright (C) 2010, Linden Research, Inc.
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation;
 * version 2.1 of the License only.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public
 * License along with this library; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
 *
 * Linden Research, Inc., 945 Battery Street, San Francisco, CA  94111  USA
 * $/LicenseInfo$
 */

/* Adapted 2026 by Legion Builds: the texture size claim (pixel area, repeats and importance to the camera) is translated to C#
 * from calcPixelArea and calcImportanceToCamera in llface.cpp, with the changes noted in the comments below. */

using System.Diagnostics;
using System.Numerics;

namespace Viewer.Core.Residency;

/// <summary>Where a texture is.</summary>
public enum ResidencyState { NotLoaded, Loading, Resident, Evicted }

/// <summary>What uses a texture: an object's face, or a terrain detail layer.</summary>
public enum TextureUserKind { Face, Terrain }

/// <summary>One face (or terrain patch) of one object that draws a texture. <paramref name="Space"/> tells the regions apart: object ids are only
/// unique within a region, so two regions sharing one manager give their users different spaces (0 when there is only one).</summary>
public readonly record struct TextureUser(TextureUserKind Kind, uint Id, int Face, int Space = 0);

/// <summary>A bounding sphere in region coordinates (SL axes: x east, y north, z up).</summary>
public readonly record struct Bounds(Vector3 Centre, float Radius);

/// <summary>
/// The camera the priorities are measured from (SL axes). <see cref="Forward"/> and <see cref="Up"/> are unit vectors;
/// <see cref="FovY"/> is the vertical field of view in radians.
/// </summary>
public readonly record struct ResidencyCamera(Vector3 Position, Vector3 Forward, Vector3 Up, float FovY, float Aspect, float Near, float Far, int ViewportHeight)
{
    /// <summary>True when the sphere touches the view frustum (a symmetric perspective frustum; conservative at the corners).</summary>
    public bool Sees(Bounds b)
    {
        var d = b.Centre - Position;
        float z = Vector3.Dot(d, Forward);
        if (z < Near - b.Radius || z > Far + b.Radius) return false;
        var right = Vector3.Normalize(Vector3.Cross(Forward, Up));
        var up = Vector3.Cross(right, Forward);
        float ty = MathF.Tan(FovY / 2), tx = ty * Aspect;
        // distance from the side planes: |x| - z * tan <= r * sqrt(1 + tan^2)
        float x = Vector3.Dot(d, right), y = Vector3.Dot(d, up);
        return MathF.Abs(x) - z * tx <= b.Radius * MathF.Sqrt(1 + tx * tx) && MathF.Abs(y) - z * ty <= b.Radius * MathF.Sqrt(1 + ty * ty);
    }

    /// <summary>The sphere's on-screen diameter in pixels (0 when out of the frustum): the size its texels are seen at.</summary>
    public double ScreenDiameter(Bounds b)
    {
        if (!Sees(b)) return 0;
        float dist = Vector3.Distance(Position, b.Centre);
        return 2.0 * b.Radius * (ViewportHeight / 2.0) / (Math.Tan(FovY / 2) * Math.Max(dist, Math.Max(Near, 1e-3)));
    }

    /// <summary>
    /// The size claim the official viewer makes for a face (its "virtual size", in square pixels; behaviour from
    /// llviewertexturelist.cpp updateImageDecodePriority, llface.cpp calcPixelArea and calcImportanceToCamera): the
    /// area of the circle the sphere spans on screen, pi x (atan(r / d) x viewport height / vertical field of view)^2,
    /// d the distance to the sphere's surface (at least 0.001); divided by the face's repeats squared (the smaller of the
    /// two directions, clamped to 0.0095..25: a face showing part of its texture needs more texels, one repeating it
    /// needs fewer); boosted by 8 x the face's importance to the camera, never below 1 x. 0 when out of the frustum (an
    /// out-of-view texture wants its coarsest level here; the official viewer still claims its size).
    /// </summary>
    public double VirtualSize(Bounds b, float repeats)
    {
        if (!Sees(b)) return 0;
        var to = b.Centre - Position;
        double dist = to.Length();
        double d = Math.Max(dist - b.Radius, 0.001);
        double px = Math.Atan(b.Radius / d) * ViewportHeight / FovY;
        double area = px * px * 3.14159;
        area /= Math.Clamp((double)repeats * repeats, 0.0095, 25);
        double cos = dist > 0 ? Vector3.Dot(to / (float)dist, Forward) : 1;
        double importance = d < b.Radius ? 1 : Importance(cos, d);
        return area * Math.Max(importance * 8, 1);
    }

    /// <summary>The official viewer's importance of a face to the camera: 0 outside the view cone (half angle: half the
    /// vertical field of view times the aspect, at least 1) or beyond 96.1 m; else a distance weight (1, 0.5, 0.2, 0.05
    /// up to 16.1, 32.1, 48.1, 96.1 m) times an angle weight (1, 0.8, 0.64, 0.36 within 10, 20, 30 degrees of the view's
    /// centre and beyond). It drops the boost while the camera moves fast; here the boost stays (sharper, not blurrier).</summary>
    private double Importance(double cos, double dist)
    {
        if (cos <= Math.Cos(0.5 * FovY * Math.Max(1, Aspect)) || dist >= 96.1) return 0;
        double dw = dist <= 16.1 ? 1 : dist <= 32.1 ? 0.5 : dist <= 48.1 ? 0.2 : 0.05;
        double aw = cos >= 0.985 ? 1 : cos >= 0.94 ? 0.8 : cos >= 0.866 ? 0.64 : 0.36;
        return dw * aw;
    }

    /// <summary>Spec 3.2: in-frustum users score 1 + projected area (px^2) + 1/(1+distance); others 1/(1+distance) (always &lt; 1).</summary>
    public double Priority(Bounds b)
    {
        float dist = Vector3.Distance(Position, b.Centre);
        double closeness = 1.0 / (1.0 + dist);
        if (!Sees(b)) return closeness;
        double rPx = b.Radius * (ViewportHeight / 2.0) / (Math.Tan(FovY / 2) * Math.Max(dist, Math.Max(Near, 1e-3)));
        double area = Math.Min(Math.PI * rPx * rPx, 1e12);
        return 1.0 + area + closeness;
    }
}

/// <summary>The camera's per-frame constants worked out once, so scoring many users a frame repeats none of the camera maths. Gives the same
/// answers as <see cref="ResidencyCamera.Priority"/> and <see cref="ResidencyCamera.VirtualSize"/>.</summary>
internal readonly struct CameraScorer
{
    private readonly Vector3 _pos, _fwd, _right, _up;
    private readonly float _near, _far, _tx, _ty, _sx, _sy, _fovY;
    private readonly double _tanHalf, _cone, _aspect;
    private readonly int _height;

    public CameraScorer(ResidencyCamera c)
    {
        _pos = c.Position; _fwd = c.Forward; _near = c.Near; _far = c.Far; _fovY = c.FovY; _height = c.ViewportHeight; _aspect = c.Aspect;
        _right = Vector3.Normalize(Vector3.Cross(c.Forward, c.Up));
        _up = Vector3.Cross(_right, c.Forward);
        _ty = MathF.Tan(c.FovY / 2);
        _tx = _ty * c.Aspect;
        _sx = MathF.Sqrt(1 + _tx * _tx);
        _sy = MathF.Sqrt(1 + _ty * _ty);
        _tanHalf = Math.Tan(c.FovY / 2);
        _cone = Math.Cos(0.5 * c.FovY * Math.Max(1, c.Aspect));
    }

    private bool Sees(Bounds b)
    {
        var d = b.Centre - _pos;
        float z = Vector3.Dot(d, _fwd);
        if (z < _near - b.Radius || z > _far + b.Radius) return false;
        float x = Vector3.Dot(d, _right), y = Vector3.Dot(d, _up);
        return MathF.Abs(x) - z * _tx <= b.Radius * _sx && MathF.Abs(y) - z * _ty <= b.Radius * _sy;
    }

    public double Priority(Bounds b)
    {
        float dist = Vector3.Distance(_pos, b.Centre);
        double closeness = 1.0 / (1.0 + dist);
        if (!Sees(b)) return closeness;
        double rPx = b.Radius * (_height / 2.0) / (_tanHalf * Math.Max(dist, Math.Max(_near, 1e-3)));
        double area = Math.Min(Math.PI * rPx * rPx, 1e12);
        return 1.0 + area + closeness;
    }

    public double VirtualSize(Bounds b, float repeats)
    {
        if (!Sees(b)) return 0;
        var to = b.Centre - _pos;
        double dist = to.Length();
        double d = Math.Max(dist - b.Radius, 0.001);
        double px = Math.Atan(b.Radius / d) * _height / _fovY;
        double area = px * px * 3.14159;
        area /= Math.Clamp((double)repeats * repeats, 0.0095, 25);
        double cos = dist > 0 ? Vector3.Dot(to / (float)dist, _fwd) : 1;
        double importance = d < b.Radius ? 1 : Importance(cos, d);
        return area * Math.Max(importance * 8, 1);
    }

    private double Importance(double cos, double dist)
    {
        if (cos <= _cone || dist >= 96.1) return 0;
        double dw = dist <= 16.1 ? 1 : dist <= 32.1 ? 0.5 : dist <= 48.1 ? 0.2 : 0.05;
        double aw = cos >= 0.985 ? 1 : cos >= 0.94 ? 0.8 : cos >= 0.866 ? 0.64 : 0.36;
        return dw * aw;
    }
}

/// <summary>Load: fetch and upload a texture that has no image yet (at <see cref="ResidencyAction.Level"/>, coarse first);
/// Evict: swap it to its placeholder; Level: swap a resident texture to another discard level.</summary>
public enum ResidencyActionKind { Load, Evict, Level }

/// <summary>What the host must do this frame. <see cref="Level"/> is the discard level to decode and upload (Load and
/// Level): 0 is full resolution; <see cref="TextureResidency.Coarsest"/> means the coarsest the codestream has.</summary>
public readonly record struct ResidencyAction(ResidencyActionKind Kind, Guid Texture, int Level = 0);

/// <summary>The manager's settings (spec 3). Timers are in time: the host passes a monotonic clock
/// to <see cref="TextureResidency.Update"/>; per-frame work limits stay per frame.</summary>
public sealed record ResidencyOptions
{
    public required long BudgetBytes { get; init; }
    /// <summary>Evict down to this fraction of the budget (hysteresis).</summary>
    public double LowWater { get; init; } = 0.9;
    /// <summary>A texture uploaded less than this long ago is not evicted or stepped down (spec 3.7). 60 frames.</summary>
    public TimeSpan MinResidence { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>A reload (or step up) this soon after the same texture's eviction (or step down) is thrash (spec 3.8). 120 frames.</summary>
    public TimeSpan ThrashWindow { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Hysteresis on rank: a load displaces a resident only when it outranks it by this factor.</summary>
    public double DisplaceMargin { get; init; } = 1.25;
    /// <summary>At most this many users are re-scored per frame (bounded per-frame cost).</summary>
    public int UsersPerFrame { get; init; } = 512;
    /// <summary>At most this many loads are started per frame (the host's upload work stays within its frame budget).</summary>
    public int MaxLoadsPerFrame { get; init; } = 8;
    /// <summary>Reserved for a load whose size is not known yet: 512 x 512 RGBA8 with mipmaps.</summary>
    public long UnknownSizeEstimate { get; init; } = TextureFootprint.Bytes(512, 512, true);
    /// <summary>The texels a level must have per pixel across (along a side) of its best user's size claim.</summary>
    public double TexelsPerPixel { get; init; } = 1.0;
    /// <summary>The clock the frame's time budget is read from, in <see cref="Stopwatch"/> ticks (null: the system's). Tests pass their own.</summary>
    public Func<long>? Clock { get; init; }
}

/// <summary>GPU bytes as uploaded (spec 3.10).</summary>
public static class TextureFootprint
{
    /// <summary>RGBA8: w * h * 4, and a full mip chain adds a third.</summary>
    public static long Bytes(int width, int height, bool mipmaps)
    {
        long b = (long)width * height * 4;
        return mipmaps ? b * 4 / 3 : b;
    }
}

/// <summary>Placeholder colours (spec 2): the texture's own average, never black and never magenta.</summary>
public static class PlaceholderColour
{
    /// <summary>The mean RGBA of an RGBA8 image (sRGB values averaged as stored), packed 0xRRGGBBAA.</summary>
    public static uint Average(ReadOnlySpan<byte> rgba)
    {
        int n = rgba.Length / 4;
        if (n == 0) return Of(128, 128, 128, 255);
        long r = 0, g = 0, b = 0, a = 0;
        for (int i = 0; i < n * 4; i += 4) { r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; a += rgba[i + 3]; }
        return Of((byte)((r + n / 2) / n), (byte)((g + n / 2) / n), (byte)((b + n / 2) / n), (byte)((a + n / 2) / n));
    }

    /// <summary>The colour a placeholder is drawn in: the average, lifted off black (every channel at least 24 when the
    /// brightest is below 24) and moved off magenta (green raised when red and blue are high and green low).</summary>
    public static uint ForPlaceholder(uint average)
    {
        byte r = (byte)(average >> 24), g = (byte)(average >> 16), b = (byte)(average >> 8), a = (byte)average;
        if (Math.Max(r, Math.Max(g, b)) < 24) { r = Math.Max(r, (byte)24); g = Math.Max(g, (byte)24); b = Math.Max(b, (byte)24); }
        if (r > 160 && b > 160 && g < 96) g = 96;
        return Of(r, g, b, a);
    }

    public static bool IsBlack(uint c) => Math.Max((byte)(c >> 24), Math.Max((byte)(c >> 16), (byte)(c >> 8))) < 24;
    public static bool IsMagenta(uint c) => (byte)(c >> 24) > 160 && (byte)(c >> 8) > 160 && (byte)(c >> 16) < 96;

    public static uint Of(byte r, byte g, byte b, byte a) => ((uint)r << 24) | ((uint)g << 16) | ((uint)b << 8) | a;
}

/// <summary>
/// The texture residency manager. Engine-free and deterministic: the host registers
/// users with their bounds, reports uploads, and once per frame calls <see cref="Update"/> with the camera and its monotonic
/// clock; the answer is the loads to start and the evictions to apply, in that frame, within the budget. Main thread
/// only (not thread-safe).
/// </summary>
public sealed class TextureResidency
{
    private sealed class Tex
    {
        public Guid Id;
        public bool Removed, Touched, Scored;   // Scored: its priority has been worked out at least once
        public Key? WantedKey, RefineKey, ResidentKey;   // where the texture stands in the ordered indexes (null: not in it)
        public readonly HashSet<TextureUser> Users = new();
        public int BaseUsers;        // users that are faces' own textures
        public int PbrBaseUsers;     // users that are PBR base colours
        public bool DetailOnly => Users.Count > 0 && BaseUsers == 0 && PbrBaseUsers == 0;
        // the earliest class among the users (a texture without users counts as a base colour)
        public Net.TextureFetchClass Class => BaseUsers > 0 || Users.Count == 0 ? Net.TextureFetchClass.BaseColour
            : PbrBaseUsers > 0 ? Net.TextureFetchClass.PbrBaseColour : Net.TextureFetchClass.Detail;
        public ResidencyState State;
        public long Bytes;           // as uploaded (0 until the first upload)
        public long ReservedBytes;   // while loading
        public uint? Average;
        public double Priority;
        public bool Visible;
        public TimeSpan? UploadedAt, EvictedAt, SteppedDownAt;   // times on the host's monotonic clock; null: never
        public bool PriorityDirty;
        public bool Pinned, Failed;
        // discard levels. Levels = 0 until the first upload says; 1 = no lower levels. Pending: the level asked for.
        public int Level = -1, Pending = -1, Levels, FullW, FullH, Desired;
        public double NeedArea;      // the best user's size claim in square pixels (VirtualSize)
        public bool LevelsLocked;
        public int Effective => Pending >= 0 ? Pending : Level;
        public int Coarsest => Math.Max(0, (LevelsLocked ? 1 : Levels) - 1);
        public long BytesAt(int level) => TextureFootprint.Bytes(Imaging.TextureLevels.Size(FullW, level), Imaging.TextureLevels.Size(FullH, level), true);
    }

    private sealed class User
    {
        public required Guid Texture;
        public Bounds Bounds;
        public double Priority;
        public bool Visible;
        public double NeedArea;
        public float Repeats = 1;
        public int Slot;   // position in the round-robin order
        public TextureUser Key;
        public Tex? Tex;   // the texture it draws
        public Net.TextureFetchClass Class;
        public bool Detail => Class == Net.TextureFetchClass.Detail;
    }

    /// <summary>A Load or Level action's level meaning "the coarsest level the codestream has" (sizes not known yet).</summary>
    public const int Coarsest = int.MaxValue;

    /// <summary>A texture's place in an ordered index: its fetch class, its priority and its id.</summary>
    private readonly record struct Key(int Class, double Priority, Guid Id);

    /// <summary>Fetch class first, then the highest priority, then the id: the order loads and refinements are taken in.</summary>
    private sealed class WantedOrder : IComparer<Key>
    {
        public int Compare(Key a, Key b)
        {
            int c = a.Class.CompareTo(b.Class);
            if (c != 0) return c;
            c = b.Priority.CompareTo(a.Priority);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }
    }

    /// <summary>The lowest priority first, then the id: the order residents give way in.</summary>
    private sealed class LowFirstOrder : IComparer<Key>
    {
        public int Compare(Key a, Key b)
        {
            int c = a.Priority.CompareTo(b.Priority);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }
    }

    private readonly Dictionary<Guid, Tex> _tex = new();
    private readonly Dictionary<TextureUser, User> _users = new();
    private readonly List<User> _order = new();   // round-robin order of users
    private int _cursor;
    // The ordered indexes the decisions take their candidates from, kept up to date as textures change, so a frame never walks or sorts
    // every texture: the textures waiting for a first load, the resident ones that want a finer level, and the resident ones by priority.
    private readonly SortedSet<Key> _wanted = new(new WantedOrder()), _refine = new(new WantedOrder()), _residents = new(new LowFirstOrder());
    private readonly List<Tex> _pending = new();       // textures that are to enter an index, when the frame's time allows
    private readonly Queue<Tex> _dirty = new();        // priority to be recomputed; worked off within the frame's time, the rest next frame

    public TextureResidency(ResidencyOptions options) => Options = options;

    private int _maxResolution = 2048;

    /// <summary>The largest texture level kept, in texels across (the Preferences setting). A close texture is never wanted at a finer
    /// level than this allows; 2,048 is the rule the viewer has always had. A new value re-decides every texture's wanted level at once;
    /// a value that cannot be a size is ignored (2,048).</summary>
    public int MaxTextureResolution
    {
        get => _maxResolution;
        set
        {
            int size = value >= 1 ? Math.Min(value, 16384) : 2048;
            if (size == _maxResolution)
                return;
            _maxResolution = size;
            foreach (var t in _tex.Values)
            {
                t.Desired = DesiredLevel(t);
                Touch(t);
            }
        }
    }

    /// <summary>The time one <see cref="Update"/> may spend. The official viewer gives its texture work 0.05 x the frame interval, never below
    /// 2 ms or above 5 ms (llviewerdisplay.cpp); the host sets it each frame. The default is no limit.</summary>
    public TimeSpan FrameBudget { get; set; } = Timeout.InfiniteTimeSpan;

    public ResidencyOptions Options { get; private set; }
    public long BudgetBytes => Options.BudgetBytes;
    public long ResidentBytes { get; private set; }
    public long LoadingBytes { get; private set; }
    public long Evictions { get; private set; }

    /// <summary>Times the resident textures were put in priority order (for the tests and the report).</summary>
    public long ResidentSorts { get; private set; }
    public long Reloads { get; private set; }
    public long Thrash { get; private set; }
    public long Loads { get; private set; }
    /// <summary>Level changes decided (steps down under pressure, steps up toward the desired level).</summary>
    public long StepsDown { get; private set; }
    public long StepsUp { get; private set; }
    /// <summary>The last <see cref="Update"/>'s priority-update cost (ms) and how many users it re-scored.</summary>
    public double LastPriorityMs { get; private set; }
    public int LastUsersScored { get; private set; }
    public int Textures => _tex.Count;
    public int Users => _users.Count;
    public int OnPlaceholders => _tex.Values.Count(t => t.State == ResidencyState.Evicted);
    public int Loading => _tex.Values.Count(t => t.State == ResidencyState.Loading);
    public int Resident => _tex.Values.Count(t => t.State == ResidencyState.Resident);
    /// <summary>Resident textures with a level change in flight.</summary>
    public int Refining => _tex.Values.Count(t => t.State == ResidencyState.Resident && t.Pending >= 0);

    /// <summary>A texture that counts against the budget but is never evicted or loaded by the manager (the terrain's
    /// detail layers: always in view, always full screen).</summary>
    public void Pin(Guid texture, long bytes)
    {
        if (!_tex.TryGetValue(texture, out var t)) t = NewTex(texture);
        if (t.State == ResidencyState.Resident) ResidentBytes -= t.Bytes;
        t.Pinned = true;
        t.State = ResidencyState.Resident;
        t.Bytes = bytes;
        ResidentBytes += bytes;
        Touch(t);
    }

    public void Unpin(Guid texture)
    {
        if (!_tex.Remove(texture, out var t)) return;
        Forget(t);
        if (!t.Pinned) return;
        if (t.State == ResidencyState.Resident) ResidentBytes -= t.Bytes;
    }

    public void SetBudget(long bytes) => Options = Options with { BudgetBytes = bytes };

    /// <summary>How many users one update may score at most (the time budget limits it too).</summary>
    public void SetUsersPerFrame(int users) => Options = Options with { UsersPerFrame = Math.Max(1, users) };

    public ResidencyState StateOf(Guid texture) => _tex.TryGetValue(texture, out var t) ? t.State : ResidencyState.NotLoaded;
    public bool HasFailed(Guid texture) => _tex.TryGetValue(texture, out var t) && t.Failed;
    public double PriorityOf(Guid texture) => _tex.TryGetValue(texture, out var t) ? t.Priority : 0;
    public bool IsVisible(Guid texture) => _tex.TryGetValue(texture, out var t) && t.Visible;
    public uint? AverageOf(Guid texture) => _tex.TryGetValue(texture, out var t) ? t.Average : null;
    public long BytesOf(Guid texture) => _tex.TryGetValue(texture, out var t) ? t.Bytes : 0;
    /// <summary>The level on the GPU (-1 when none), the level asked for and not yet uploaded (-1 when none), the
    /// level its size on screen needs (spec 4.2), and how many levels it has (0: not known yet).</summary>
    public int LevelOf(Guid texture) => _tex.TryGetValue(texture, out var t) && t.State == ResidencyState.Resident ? t.Level : -1;
    public int PendingLevelOf(Guid texture) => _tex.TryGetValue(texture, out var t) ? t.Pending : -1;
    public int DesiredLevelOf(Guid texture) => _tex.TryGetValue(texture, out var t) ? t.Desired : 0;
    public int LevelsOf(Guid texture) => _tex.TryGetValue(texture, out var t) ? t.Levels : 0;
    /// <summary>The footprint <paramref name="texture"/> would have at <paramref name="level"/> (0 when its size is not known).</summary>
    public long BytesAtLevel(Guid texture, int level) => _tex.TryGetValue(texture, out var t) && t.FullW > 0 ? t.BytesAt(level) : 0;
    /// <summary>Resident at (or finer than) its desired level with nothing in flight, or failed.</summary>
    public bool AtDesired(Guid texture) => _tex.TryGetValue(texture, out var t) && (t.Failed || (t.State == ResidencyState.Resident && t.Pending < 0 && t.Level <= t.Desired));
    public IEnumerable<Guid> All => _tex.Keys;

    /// <summary>Where each texture's fetch class is published for the fetches (null: not published).</summary>
    public Net.TextureFetchClasses? FetchClasses { get; set; }

    /// <summary>Textures with at least one detail-slot user.</summary>
    public IEnumerable<Guid> DetailMaps => _tex.Where(kv => kv.Value.Users.Count > kv.Value.BaseUsers + kv.Value.PbrBaseUsers).Select(kv => kv.Key);

    /// <summary>True when every user of <paramref name="texture"/> is a detail slot.</summary>
    public bool IsDetailOnly(Guid texture) => _tex.TryGetValue(texture, out var t) && t.DetailOnly;

    /// <summary>Registers (or re-points) a user of <paramref name="texture"/> with its bounds. <paramref name="use"/> is what
    /// the user draws with it: a face's own texture, a PBR material's base colour, or one of its detail slots (normal,
    /// metallic-roughness, emissive). A texture is fetched in the earliest class among its users. <paramref name="repeats"/>
    /// is how many times the face repeats its texture (the smaller of the two directions; below 1 when it shows part of it).</summary>
    public void SetUser(TextureUser user, Guid texture, Bounds bounds, float repeats = 1, Net.TextureFetchClass use = Net.TextureFetchClass.BaseColour)
    {
        repeats = Math.Abs(repeats);
        if (_users.TryGetValue(user, out var u))
        {
            if (u.Repeats != repeats) { u.Repeats = repeats; u.NeedArea = -1; }
            if (u.Texture != texture || u.Class != use)
            {
                Detach(user, u);
                u.Texture = texture;
                u.Class = use;
                Attach(user, u);
            }
            u.Bounds = bounds;
            return;
        }
        _users[user] = u = new User { Key = user, Texture = texture, Bounds = bounds, Repeats = repeats, Class = use, Slot = _order.Count };
        _order.Add(u);
        Attach(user, u);
    }

    /// <summary>Removes a user; a texture left without users keeps its state and falls to priority 0.</summary>
    public void RemoveUser(TextureUser user)
    {
        if (!_users.Remove(user, out var u)) return;
        Detach(user, u);
        // the last user takes the removed one's place: no walk over every user to find it
        int i = u.Slot, last = _order.Count - 1;
        if (i < 0 || i > last || _order[i] != u) return;
        if (i != last)
        {
            _order[i] = _order[last];
            _order[i].Slot = i;
        }
        _order.RemoveAt(last);
        if (_cursor > _order.Count) _cursor = 0;
    }

    private void Attach(TextureUser user, User u)
    {
        if (!_tex.TryGetValue(u.Texture, out var t)) t = NewTex(u.Texture);
        u.Tex = t;
        if (t.Users.Add(user)) Count(t, u, +1);
        MarkDirty(t);
        Touch(t);
        Publish(u.Texture, t);
    }

    private void Detach(TextureUser user, User u)
    {
        if (!_tex.TryGetValue(u.Texture, out var t)) return;
        if (t.Users.Remove(user)) Count(t, u, -1);
        MarkDirty(t);
        Touch(t);
        Publish(u.Texture, t);
    }

    private Tex NewTex(Guid id)
    {
        var t = new Tex { Id = id };
        _tex[id] = t;
        MarkDirty(t);
        return t;
    }

    private void MarkDirty(Tex t)
    {
        if (t.PriorityDirty) return;
        t.PriorityDirty = true;
        _dirty.Enqueue(t);
    }

    /// <summary>Notes that a texture changed in a way the ordered indexes depend on. It leaves or moves within an index at once, so no decision
    /// ever uses a stale entry; entering an index for the first time waits for <see cref="FlushIndex"/>, which stops when the frame's time is used up.</summary>
    private void Touch(Tex t)
    {
        if (!Reconcile(t, insertNew: false) || t.Touched) return;
        t.Touched = true;
        _pending.Add(t);
    }

    /// <summary>Takes a texture out of every index (it is leaving the manager).</summary>
    private void Forget(Tex t)
    {
        t.Removed = true;
        Place(_wanted, ref t.WantedKey, null);
        Place(_refine, ref t.RefineKey, null);
        Place(_residents, ref t.ResidentKey, null);
    }

    private static void Place(SortedSet<Key> set, ref Key? at, Key? want)
    {
        if (at == want) return;
        if (at is { } old) set.Remove(old);
        if (want is { } key) set.Add(key);
        at = want;
    }

    /// <summary>Puts a texture's entries in the three indexes right for its state. A texture already in an index is moved at once; one not yet in
    /// it is added only when <paramref name="insertNew"/> says so. Returns true when an addition is still to be made.</summary>
    private bool Reconcile(Tex t, bool insertNew)
    {
        if (t.Removed) return false;
        var key = new Key((int)t.Class, t.Priority, t.Id);
        bool resident = t.State == ResidencyState.Resident && !t.Pinned;
        Key? w = t.Scored && t.State is ResidencyState.NotLoaded or ResidencyState.Evicted && t.Users.Count > 0 && !t.Failed ? key : null;
        Key? r = resident && t.Pending < 0 && !t.LevelsLocked && t.FullW > 0 && t.Desired < t.Level ? key : null;
        Key? s = resident ? new Key(0, t.Priority, t.Id) : null;
        bool more = Move(_wanted, ref t.WantedKey, w, insertNew);
        more |= Move(_refine, ref t.RefineKey, r, insertNew);
        more |= Move(_residents, ref t.ResidentKey, s, insertNew);
        return more;
    }

    private static bool Move(SortedSet<Key> set, ref Key? at, Key? want, bool insertNew)
    {
        if (at == want) return false;
        bool was = at is not null;
        if (at is { } old) set.Remove(old);
        at = null;
        if (want is not { } key) return false;
        if (!was && !insertNew) return true;
        set.Add(key);
        at = key;
        return false;
    }

    /// <summary>Adds the textures waiting to enter an index, oldest first, until the frame's time is used up (at least one is added); the
    /// rest enter on a later frame.</summary>
    private void FlushIndex()
    {
        int done = 0;
        for (; done < _pending.Count; done++)
        {
            if (done > 0 && _updateTimed && Ticks() >= _updateEnd) break;
            var t = _pending[done];
            t.Touched = false;
            Reconcile(t, insertNew: true);
        }
        _pending.RemoveRange(0, done);
    }

    private static void Count(Tex t, User u, int by)
    {
        if (u.Class == Net.TextureFetchClass.BaseColour) t.BaseUsers += by;
        else if (u.Class == Net.TextureFetchClass.PbrBaseColour) t.PbrBaseUsers += by;
    }

    private void Publish(Guid texture, Tex t) => FetchClasses?.Set(texture, t.Class);

    /// <summary>The host uploaded <paramref name="texture"/> (first time or a reload): its real size and average colour.
    /// A texture reported this way has one level (behaviour).</summary>
    public void Uploaded(Guid texture, long bytes, uint averageRgba, TimeSpan now) => Uploaded(texture, 0, 1, 0, 0, bytes, averageRgba, now);

    /// <summary>The host uploaded <paramref name="level"/> of a texture with <paramref name="levels"/> levels and the
    /// given full-resolution size (a first load, a reload, or a level change).</summary>
    public void Uploaded(Guid texture, int level, int levels, int fullWidth, int fullHeight, long bytes, uint averageRgba, TimeSpan now)
    {
        if (!_tex.TryGetValue(texture, out var t)) t = NewTex(texture);
        LoadingBytes -= t.ReservedBytes;
        if (t.State == ResidencyState.Resident) ResidentBytes -= t.Bytes;
        t.ReservedBytes = 0;
        t.Bytes = bytes;
        t.Level = level;
        t.Pending = -1;
        t.Levels = Math.Max(1, levels);
        t.FullW = fullWidth;
        t.FullH = fullHeight;
        t.Average ??= averageRgba;   // recorded at first decode (spec 2)
        t.State = ResidencyState.Resident;
        t.UploadedAt = now;
        ResidentBytes += bytes;
        t.Desired = DesiredLevel(t);
        Touch(t);
    }

    /// <summary>The host could not load it (missing, refused): it stays on its placeholder / not loaded.</summary>
    public void LoadFailed(Guid texture)
    {
        if (_tex.TryGetValue(texture, out var r) && r.State == ResidencyState.Resident && r.Pending >= 0)
        {
            // a level change failed: the level on the GPU stays, and this texture changes level no more
            LoadingBytes -= r.ReservedBytes;
            r.ReservedBytes = 0;
            r.Pending = -1;
            r.LevelsLocked = true;
            ResidentBytes -= r.Bytes;
            if (r.FullW > 0) r.Bytes = r.BytesAt(r.Level);
            ResidentBytes += r.Bytes;
            Touch(r);
            return;
        }
        if (!_tex.TryGetValue(texture, out var t) || t.State != ResidencyState.Loading) return;
        t.Pending = -1;
        LoadingBytes -= t.ReservedBytes;
        t.ReservedBytes = 0;
        t.Failed = true;   // not asked for again (a region change clears it)
        t.State = t.Average is null ? ResidencyState.NotLoaded : ResidencyState.Evicted;
        Touch(t);
    }

    /// <summary>Hands <paramref name="faces"/> every face user in view (as last scored) that draws the face's own image
    /// (not a detail slot: a detail map only sharpens what the face shows), its texture and whether that texture is
    /// resident (an image on screen), at <paramref name="ms"/> from the path's start (measurement). <paramref name="fallback"/>
    /// names the texture a face user draws instead while its own has no image (a PBR face's legacy texture), if any; it
    /// counts as the face's image when that texture is resident.</summary>
    public void ObserveFaceImages(FaceFirstImages faces, double ms, Func<TextureUser, Guid>? fallback = null)
    {
        foreach (var (key, u) in _users)
        {
            if (key.Kind != TextureUserKind.Face || !u.Visible || u.Detail) continue;
            bool shown = IsResident(u.Texture);
            var instead = !shown && fallback?.Invoke(key) is { } g && g != Guid.Empty && IsResident(g) ? g : Guid.Empty;
            faces.Observe(key, u.Texture, shown, ms, instead);
        }
    }

    private bool IsResident(Guid texture) => _tex.TryGetValue(texture, out var t) && t.State == ResidencyState.Resident;

    /// <summary>Whether a user other than those <paramref name="own"/> picks out wants <paramref name="texture"/> (so it is
    /// fetched on that user's account).</summary>
    public bool HasUserOtherThan(Guid texture, Func<TextureUser, bool> own) =>
        _tex.TryGetValue(texture, out var t) && t.Users.Any(u => !own(u));

    /// <summary>A region leaves a manager that other regions share: removes every user of <paramref name="space"/>. A texture left with no
    /// user at all is evicted (an Evict action for the host, its bytes freed) and forgotten; a texture another space still uses is untouched.</summary>
    public IReadOnlyList<ResidencyAction> DropSpace(int space, TimeSpan now)
    {
        var actions = new List<ResidencyAction>();
        var touched = new HashSet<Guid>();
        foreach (var key in _users.Keys.Where(k => k.Space == space).ToList())
        {
            touched.Add(_users[key].Texture);
            RemoveUser(key);
        }
        foreach (var id in touched)
        {
            if (!_tex.TryGetValue(id, out var t) || t.Users.Count > 0 || t.Pinned) continue;
            if (t.State is ResidencyState.Resident or ResidencyState.Loading)
            {
                LoadingBytes -= t.ReservedBytes;
                ResidentBytes -= t.Bytes;
                Evictions++;
                actions.Add(new ResidencyAction(ResidencyActionKind.Evict, id));
            }
            _tex.Remove(id);
            Forget(t);
        }
        return actions;
    }

    /// <summary>Forgets everything (region change): no state outlives its region.</summary>
    public void Clear()
    {
        foreach (var t in _tex.Values) t.Removed = true;
        _tex.Clear(); _users.Clear(); _order.Clear(); _cursor = 0;
        _wanted.Clear(); _refine.Clear(); _residents.Clear(); _pending.Clear(); _dirty.Clear();
        FetchClasses?.Clear();
        ResidentBytes = LoadingBytes = 0;
    }

    /// <summary>
    /// One frame: re-score up to <see cref="ResidencyOptions.UsersPerFrame"/> users, refresh the touched textures'
    /// priorities, then decide loads and evictions (spec 3.5-3.8). The host applies the returned actions. <paramref name="now"/>
    /// is the host's monotonic clock; tests pass explicit times, so the same inputs give the same decisions.
    /// </summary>
    public IReadOnlyList<ResidencyAction> Update(ResidencyCamera camera, TimeSpan now)
    {
        // The frame's time budget (the official viewer limits its per-frame texture work the same way and carries the rest over): scoring users
        // may use up to half of it, recomputing textures' priorities up to 85 percent, the decisions the rest. Each phase does at least one unit of
        // work, and what it leaves undone waits for the next frame.
        bool timed = FrameBudget > TimeSpan.Zero && FrameBudget != Timeout.InfiniteTimeSpan;
        long start = Ticks(), budget = timed ? FrameBudget.Ticks : 0;
        long scoreEnd = start + budget / 2, refreshEnd = start + budget * 85 / 100;
        _updateEnd = start + budget;
        _updateTimed = timed;

        var scorer = new CameraScorer(camera);
        int n = Math.Min(Options.UsersPerFrame, _order.Count), k = 0;
        for (; k < n; k++)
        {
            if (k > 0 && timed && Ticks() >= scoreEnd) break;
            if (_cursor >= _order.Count) _cursor = 0;
            var u = _order[_cursor++];
            var key = u.Key;
            double p = key.Kind == TextureUserKind.Terrain ? 1.0 + (double)camera.ViewportHeight * camera.ViewportHeight * camera.Aspect : scorer.Priority(u.Bounds);
            bool vis = key.Kind == TextureUserKind.Terrain || p >= 1.0;
            double need = key.Kind == TextureUserKind.Terrain ? double.MaxValue : scorer.VirtualSize(u.Bounds, u.Repeats);
            if (p != u.Priority || vis != u.Visible || need != u.NeedArea)
            {
                u.Priority = p;
                u.Visible = vis;
                u.NeedArea = need;
                MarkDirty(u.Tex!);
            }
        }
        LastUsersScored = k;
        for (int done = 0; _dirty.Count > 0; done++)
        {
            if (done > 0 && timed && Ticks() >= refreshEnd) break;
            var t = _dirty.Dequeue();
            t.PriorityDirty = false;
            if (t.Removed) continue;
            double best = 0, need = 0;
            bool vis = false;
            foreach (var key in t.Users)
            {
                var u = _users[key];
                if (u.Priority > best) best = u.Priority;
                if (u.NeedArea > need) need = u.NeedArea;
                vis |= u.Visible;
            }
            t.Priority = best;
            t.Visible = vis;
            t.NeedArea = need;
            t.Desired = DesiredLevel(t);
            t.Scored = true;
            Touch(t);
        }
        LastPriorityMs = (Ticks() - start) / (double)TimeSpan.TicksPerMillisecond;
        var actions = Decide(now);
        LastUpdateMs = (Ticks() - start) / (double)TimeSpan.TicksPerMillisecond;
        return actions;
    }

    private long _updateEnd;
    private bool _updateTimed;

    /// <summary>The time of the last <see cref="Update"/> in milliseconds, scoring and decisions together.</summary>
    public double LastUpdateMs { get; private set; }

    private long Ticks() => Options.Clock is { } c ? c() : (long)(Stopwatch.GetTimestamp() * (TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency));

    /// <summary>The level the texture's best user wants, as the official viewer chooses its discard level
    /// (llviewertexture.cpp): floor(log4(full texels / size claim)), so the coarsest level with at least as many texels as
    /// the claim; at least 1 for a texture over 2048 texels across; a claim of 10 square pixels or less (out of view
    /// included) wants the coarsest.</summary>
    private int DesiredLevel(Tex t)
    {
        if (t.Pinned || t.Levels <= 1 || t.FullW <= 0) return 0;
        return LevelForClaim(t.FullW, t.FullH, t.LevelsLocked ? 1 : t.Levels, t.NeedArea, Options.TexelsPerPixel, _maxResolution);
    }

    /// <summary>The discard level a size claim (square pixels) wants of a texture <paramref name="fullWidth"/> by <paramref name="fullHeight"/> with
    /// <paramref name="levels"/> levels, by the rule <see cref="DesiredLevel"/> describes. Shared by the residency manager and by the avatar bakes, which
    /// the official viewer sizes the same way (their claim is the avatar's pixel area).</summary>
    public static int LevelForClaim(int fullWidth, int fullHeight, int levels, double claimArea, double texelsPerPixel = 1.0, int maxResolution = 2048)
    {
        if (levels <= 1 || fullWidth <= 0 || fullHeight <= 0) return 0;
        int coarsest = levels - 1;
        double need = claimArea * texelsPerPixel * texelsPerPixel;
        if (need <= 10) return coarsest;
        double level = Math.Floor(Math.Log((double)fullWidth * fullHeight / need) / Math.Log(4));
        int finest = fullWidth > 2048 || fullHeight > 2048 ? 1 : 0;
        finest = Math.Max(finest, CapFloor(fullWidth, fullHeight, maxResolution, coarsest));
        return (int)Math.Clamp(level, Math.Min(finest, coarsest), coarsest);
    }

    /// <summary>The finest level the Preferences cap allows: the first whose longer side is within the cap (at most the coarsest level).
    /// A cap of 2,048 or more adds nothing to the viewer's own rule.</summary>
    private static int CapFloor(int fullWidth, int fullHeight, int maxResolution, int coarsest)
    {
        if (maxResolution >= 2048)
            return 0;
        int side = Math.Max(fullWidth, fullHeight), within = 0;
        while ((side >> within) > maxResolution && within < coarsest)
            within++;
        return within;
    }

    private long Used => ResidentBytes + LoadingBytes;

    private IReadOnlyList<ResidencyAction> Decide(TimeSpan now)
    {
        var actions = new List<ResidencyAction>();
        long budget = Options.BudgetBytes;
        long lowWater = (long)(budget * Options.LowWater);
        // whether what is in view alone is over the budget: worked out the first time it matters, and at once when a step before it changes sizes
        bool? visibleOver = null;
        bool VisibleOverBudget() => visibleOver ??= _tex.Values.Where(t => t.Visible && t.State == ResidencyState.Resident).Sum(t => t.Bytes) > budget;
        if (_maxResolution < 2048) _ = VisibleOverBudget();
        FlushIndex();
        // the residents from the lowest priority up, put in order the first time something has to be freed: a frame that frees nothing (most of them)
        // does not sort every texture. Nothing that Decide does before the first free changes a resident's state or priority, so the order is the same
        // as if it had been made here.
        List<KeyValuePair<Guid, Tex>>? orderedResidents = null;
        List<KeyValuePair<Guid, Tex>> Residents()
        {
            if (orderedResidents is null)
            {
                FlushIndex();
                orderedResidents = _residents.Select(k => new KeyValuePair<Guid, Tex>(k.Id, _tex[k.Id])).ToList();
                ResidentSorts++;
            }
            return orderedResidents;
        }
        bool Young(Tex t) => t.UploadedAt is { } at && now - at < Options.MinResidence && !VisibleOverBudget();

        void Evict(Guid id, Tex t)
        {
            LoadingBytes -= t.ReservedBytes;
            t.ReservedBytes = 0;
            t.Pending = -1;
            t.State = ResidencyState.Evicted;
            t.EvictedAt = now;
            ResidentBytes -= t.Bytes;
            Evictions++;
            Touch(t);
            actions.Add(new ResidencyAction(ResidencyActionKind.Evict, id));
        }

        // a step down is accounted at once (the smaller level replaces the larger when its decode arrives)
        bool CanStepDown(Tex t) => t.FullW > 0 && !t.LevelsLocked && t.Levels > 1 && t.Effective < t.Coarsest && !(t.Pending >= 0 && t.Pending < t.Level);
        void StepDown(Guid id, Tex t)
        {
            int to = t.Effective + 1;
            long smaller = t.BytesAt(to);
            ResidentBytes -= t.Bytes - smaller;
            t.Bytes = smaller;
            t.Pending = to;
            t.SteppedDownAt = now;
            StepsDown++;
            Touch(t);
            actions.RemoveAll(a => a.Texture == id && a.Kind == ResidencyActionKind.Level);   // one level change per texture per frame: the last
            actions.Add(new ResidencyAction(ResidencyActionKind.Level, id, to));
        }

        // the Preferences cap: a resident texture finer than the cap allows steps down to it, a level a frame
        // (the resident order is made only when some texture is finer than the cap, so a frame that has nothing to do here still sorts nothing)
        bool AboveCap(Tex t) => t.State == ResidencyState.Resident && !t.Pinned && t.FullW > 0 && CanStepDown(t)
            && t.Effective < CapFloor(t.FullW, t.FullH, _maxResolution, t.Coarsest);
        if (_maxResolution < 2048 && _tex.Values.Any(AboveCap))
            foreach (var (id, t) in Residents())
                if (AboveCap(t)) StepDown(id, t);

        // Frees memory until usage is at or below target (spec 4.3): first steps down textures finer than they need, then
        // every texture a level at a time in repeated passes, lowest priority first; only when nothing can step further, whole
        // textures go to their placeholders, lowest priority first. With a requester, only textures it outranks by the
        // displacement margin give way. A refinement (evict: false) only steps others down: it never sends one to its placeholder.
        void Free(long target, bool respectYoung, Guid? requester, double requesterPriority, bool evict = true)
        {
            bool May(Guid id, Tex t) => t.State == ResidencyState.Resident && (!respectYoung || !Young(t)) && id != requester
                && (requester is null || t.Priority * Options.DisplaceMargin < requesterPriority);
            foreach (var (id, t) in Residents())
            {
                if (Used <= target) return;
                while (Used > target && May(id, t) && CanStepDown(t) && t.Effective < t.Desired) StepDown(id, t);
            }
            bool stepped = true;
            while (Used > target && stepped)
            {
                stepped = false;
                foreach (var (id, t) in Residents())
                {
                    if (Used <= target) return;
                    if (May(id, t) && CanStepDown(t)) { StepDown(id, t); stepped = true; }
                }
            }
            if (!evict) return;
            foreach (var (id, t) in Residents())
            {
                if (Used <= target) return;
                if (May(id, t)) Evict(id, t);
            }
        }

        // 1. over budget: free down to the low-water mark; the budget itself wins over the minimum residence
        if (Used > budget)
        {
            Free(lowWater, respectYoung: true, null, 0);
            if (Used > budget) Free(budget, respectYoung: false, null, 0);
        }

        // 2. loads, faces' own textures first, then PBR base colours, then detail maps (the textures' fetch classes), then
        // highest priority first, coarse first (spec 4.4);
        // beyond the budget only by displacing what they outrank
        FlushIndex();
        var wanted = _wanted.Take(Options.MaxLoadsPerFrame + 1).Select(k => new KeyValuePair<Guid, Tex>(k.Id, _tex[k.Id])).ToList();
        int started = 0;
        foreach (var (id, t) in wanted)
        {
            if (started >= Options.MaxLoadsPerFrame) break;
            // a size not known yet reserves the estimate, capped at what is free (a small budget still streams small
            // textures; a load that comes out larger is corrected by the next pass's budget restore)
            bool known = t.FullW > 0 && t.Levels > 0;
            int level = known ? t.Coarsest : Coarsest;
            long need = known ? t.BytesAt(level) : t.Bytes > 0 ? t.Bytes : Math.Min(Options.UnknownSizeEstimate, Math.Max(1, budget - Used));
            if (Used + need > budget) Free(budget - need, respectYoung: true, id, t.Priority);
            if (Used + need > budget) break;   // nothing lower to displace: the rest wait
            if (t.State == ResidencyState.Evicted)
            {
                Reloads++;
                if (t.EvictedAt is { } ev && now - ev <= Options.ThrashWindow) Thrash++;
            }
            t.State = ResidencyState.Loading;
            t.ReservedBytes = need;
            t.Pending = level;
            Touch(t);
            LoadingBytes += need;
            Loads++;
            started++;
            actions.Add(new ResidencyAction(ResidencyActionKind.Load, id, level));
        }

        // 3. refinement: residents coarser than they need step up in the same class order as the loads, then
        // highest priority first, as far toward the desired level as the budget allows (displacing only what they outrank);
        // not held back by the minimum residence
        FlushIndex();
        var refine = _refine.Take(1024).Select(k => new KeyValuePair<Guid, Tex>(k.Id, _tex[k.Id])).ToList();
        int tried = 0;
        foreach (var (id, t) in refine)
        {
            if (started >= Options.MaxLoadsPerFrame) break;
            if (tried++ > 0 && _updateTimed && Ticks() >= _updateEnd) break;   // out of time: the rest are looked at next frame
            if (t.State != ResidencyState.Resident || t.Pending >= 0) continue;   // stepped down or evicted for another this frame
            int to = -1;
            for (int l = t.Desired; l < t.Level; l++)
            {
                long extra = t.BytesAt(l) - t.Bytes;
                if (Used + extra > budget) Free(budget - extra, respectYoung: true, id, t.Priority, evict: false);
                if (Used + extra <= budget) { to = l; break; }
            }
            if (to < 0) continue;
            long delta = t.BytesAt(to) - t.Bytes;
            t.ReservedBytes = delta;
            LoadingBytes += delta;
            t.Pending = to;
            Touch(t);
            StepsUp++;
            if (t.SteppedDownAt is { } sd && now - sd <= Options.ThrashWindow) Thrash++;   // never stepped down: not thrash
            started++;
            actions.Add(new ResidencyAction(ResidencyActionKind.Level, id, to));
        }
        return actions;
    }
}
