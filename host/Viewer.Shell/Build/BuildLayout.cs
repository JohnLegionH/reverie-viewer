// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

using System.Globalization;
using System.Numerics;

namespace Viewer.Shell.Build;

/// <summary>The Build window's collapsible sections, top to bottom. They replace the official floater's tabs (General, Object, Features,
/// Texture, Content; floater_tools.xml): Placement and Shape hold the Object tab, Look the Texture tab, Object the General tab, Behaviour
/// the Features tab and Contents the Content tab. Contents comes first: it was last, below every other section, and had to be scrolled to.</summary>
public enum BuildSection { Contents, Placement, Shape, Look, Object, Behaviour }

/// <summary>One row of the Build window: the control's name, the section it sits in (null: the tool rows at the top), the string key of
/// its label, and other names people search for it by (the official viewer's names where they differ from ours).</summary>
public sealed record BuildRow(string Id, BuildSection? Section, string LabelKey, IReadOnlyList<string> Aliases);

/// <summary>What the closed sections summarise about the selection, read from the edit controller.</summary>
public sealed record BuildFacts(
    Vector3? Position = null, Vector3? Size = null, Vector3? RotationDegrees = null,
    string? ShapeName = null, double Hollow = 0, double TwistDegrees = 0,
    int FacesPicked = 0, bool SelectFaces = false, string? Texture = null,
    string? Name = null, bool Locked = false, bool Physical = false, bool Temporary = false, bool Phantom = false,
    bool Flexible = false, bool Light = false, string? PhysicsShape = null,
    int? ContentCount = null);

/// <summary>
/// The Build window's layout as data, so it is tested without the engine: the tool rows and every section's rows with the names they are
/// found by, each closed section's one-line summary, the hint line under the tools, and the settings search. The tool and mode names
/// and their keys follow the official build floater (floater_tools.xml; menu_viewer.xml "Move Tool" is our Grab), read as behaviour.
/// </summary>
public static class BuildLayout
{
    /// <summary>The sections in the order the window shows them.</summary>
    public static IReadOnlyList<BuildSection> Sections { get; } = Enum.GetValues<BuildSection>();

    /// <summary>The sections open the first time the window is shown (the ones a builder reaches for most).</summary>
    public static IReadOnlyList<BuildSection> OpenByDefault { get; } = [BuildSection.Placement, BuildSection.Look, BuildSection.Object];

    /// <summary>Whether the Contents section opens by itself for a selected object: when the object holds something (the count is null while
    /// its contents are not known yet). The person's own open or closed choice for the section is kept apart from this.</summary>
    public static bool ContentsOpenFor(int? contentCount) => contentCount is > 0;

    /// <summary>The string key of a section's title.</summary>
    public static string TitleKey(BuildSection s) => "edit.section." + s.ToString().ToLowerInvariant();

    /// <summary>The control name of a section (its header is "section_&lt;name&gt;").</summary>
    public static string NameOf(BuildSection s) => s.ToString().ToLowerInvariant();

    /// <summary>The section the official tab's content went to, for a tab name the live checks still use ("tab:texture").</summary>
    public static BuildSection? SectionOfTab(string tab) => tab switch
    {
        "general" => BuildSection.Object,
        "object" => BuildSection.Placement,
        "texture" => BuildSection.Look,
        "features" => BuildSection.Behaviour,
        "content" => BuildSection.Contents,
        _ => Enum.TryParse<BuildSection>(tab, true, out var s) ? s : null,
    };

    private static BuildRow R(string id, BuildSection? s, string key, params string[] aliases) => new(id, s, key, aliases);

    /// <summary>Every row of the window. The tool rows come first (Section null).</summary>
    public static IReadOnlyList<BuildRow> Rows { get; } =
    [
        // tools and modes (the official names they were known by first)
        R("tool_focus", null, "edit.tool.focus", "Focus Tool", "camera", "zoom", "orbit", "pan"),
        R("tool_move", null, "edit.tool.move", "Move Tool", "grab", "lift", "spin"),
        R("tool_edit", null, "edit.tool.edit", "Edit Tool", "select"),
        R("tool_create", null, "edit.tool.create", "Create Tool", "rez", "new prim", "make a box"),
        R("tool_land", null, "edit.tool.land", "Land Tool", "terraform", "flatten", "raise", "lower", "smooth"),
        R("manip_move", null, "edit.manip.move", "Position", "arrows"),
        R("manip_rotate", null, "edit.manip.rotate", "rings", "turn"),
        R("manip_scale", null, "edit.manip.scale", "scale", "resize", "size handles"),
        R("manip_face", null, "edit.manip.face", "Select Face", "Select Texture", "face"),
        R("undo", null, "edit.undo", "undo"),
        R("redo", null, "edit.redo", "redo"),
        R("keep_tool", null, "edit.keep_tool", "Keep Tool selected", "sticky"),

        // Placement (the official Object tab's place, size and rotation)
        R("position", BuildSection.Placement, "edit.position", "Position (meters)", "location", "coordinates"),
        R("size", BuildSection.Placement, "edit.size", "Size (meters)", "scale", "dimensions"),
        R("rotation", BuildSection.Placement, "edit.rotation", "Rotation (degrees)", "angle"),
        R("object_clipboard", BuildSection.Placement, "edit.copy_all", "copy", "paste", "clipboard"),
        R("snap", BuildSection.Placement, "edit.snap", "Snap", "Snap to grid", "grid"),
        R("stretch_both", BuildSection.Placement, "edit.stretch_both", "Stretch Both Sides"),
        R("stretch_textures", BuildSection.Placement, "edit.stretch_textures", "Stretch Textures"),

        // Shape (the official Object tab's building block)
        R("prim_type", BuildSection.Shape, "edit.prim_type", "Building Block Type", "prim type", "shape"),
        R("path_cut", BuildSection.Shape, "edit.path_cut", "Path Cut", "cut"),
        R("hollow", BuildSection.Shape, "edit.hollow", "Hollow"),
        R("hollow_shape", BuildSection.Shape, "edit.hollow_shape", "Hollow Shape", "hole"),
        R("twist", BuildSection.Shape, "edit.twist", "Twist"),
        R("taper", BuildSection.Shape, "edit.taper", "Taper"),
        R("top_shear", BuildSection.Shape, "edit.top_shear", "Top Shear", "shear"),
        R("slice", BuildSection.Shape, "edit.slice", "Slice"),
        R("dimple", BuildSection.Shape, "edit.dimple", "Dimple"),
        R("profile_cut", BuildSection.Shape, "edit.profile_cut", "Profile Cut"),
        R("hole_size", BuildSection.Shape, "edit.hole_size", "Hole Size"),
        R("radius", BuildSection.Shape, "edit.radius", "Radius"),
        R("revolutions", BuildSection.Shape, "edit.revolutions", "Revolutions"),
        R("skew", BuildSection.Shape, "edit.skew", "Skew"),

        // Look (the official Texture tab)
        R("texture_row", BuildSection.Look, "edit.tex.texture", "Texture", "image", "diffuse"),
        R("colour", BuildSection.Look, "edit.tex.colour", "Color", "colour", "tint"),
        R("transparency", BuildSection.Look, "edit.tex.transparency", "Transparency %", "alpha", "opacity"),
        R("glow", BuildSection.Look, "edit.tex.glow", "Glow"),
        R("fullbright", BuildSection.Look, "edit.tex.fullbright", "Full Bright", "fullbright"),
        R("mapping", BuildSection.Look, "edit.tex.mapping", "Mapping", "planar"),
        R("scale_u", BuildSection.Look, "edit.tex.scale_u", "Horizontal scale", "repeats"),
        R("scale_v", BuildSection.Look, "edit.tex.scale_v", "Vertical scale", "repeats"),
        R("per_metre", BuildSection.Look, "edit.tex.per_metre", "Repeats per meter"),
        R("texture_rotation", BuildSection.Look, "edit.tex.rotation", "Rotation degrees", "texture rotation"),
        R("offset_u", BuildSection.Look, "edit.tex.offset_u", "Horizontal offset"),
        R("offset_v", BuildSection.Look, "edit.tex.offset_v", "Vertical offset"),
        R("align_row", BuildSection.Look, "edit.tex.align", "Align planar faces", "align"),
        R("face_clipboard", BuildSection.Look, "edit.copy", "copy", "paste"),
        R("material_row", BuildSection.Look, "edit.tex.material", "PBR material", "GLTF material", "material"),
        R("pbr_scale_u", BuildSection.Look, "edit.tex.pbr_scale_u", "PBR"),
        R("pbr_scale_v", BuildSection.Look, "edit.tex.pbr_scale_v", "PBR"),
        R("pbr_offset_u", BuildSection.Look, "edit.tex.pbr_offset_u", "PBR"),
        R("pbr_offset_v", BuildSection.Look, "edit.tex.pbr_offset_v", "PBR"),
        R("pbr_rotation", BuildSection.Look, "edit.tex.pbr_rotation", "PBR"),

        // Object (the official General tab, and the object's flags and links)
        R("name", BuildSection.Object, "edit.name", "Name"),
        R("description", BuildSection.Object, "edit.description", "Description"),
        R("creator_row", BuildSection.Object, "edit.creator", "Creator"),
        R("owner_row", BuildSection.Object, "edit.owner", "Owner"),
        R("share_group", BuildSection.Object, "edit.share_group", "Share with group", "group"),
        R("anyone_move", BuildSection.Object, "edit.anyone_move", "Anyone can move", "permissions"),
        R("anyone_copy", BuildSection.Object, "edit.anyone_copy", "Anyone can copy", "permissions"),
        R("next_row", BuildSection.Object, "edit.next_owner", "Next owner can", "permissions", "modify", "transfer"),
        R("for_sale", BuildSection.Object, "edit.for_sale", "For Sale", "price", "sell"),
        R("locked", BuildSection.Object, "edit.locked", "Locked"),
        R("physical", BuildSection.Object, "edit.physical", "Physical"),
        R("temporary", BuildSection.Object, "edit.temporary", "Temporary", "temp"),
        R("phantom", BuildSection.Object, "edit.phantom", "Phantom"),
        R("linked", BuildSection.Object, "edit.linked", "Edit linked", "Edit linked parts", "link"),
        R("link_row", BuildSection.Object, "edit.link", "Link", "Unlink", "linkset"),
        R("owned_only", BuildSection.Object, "edit.owned_only", "Select Only My Objects"),

        // Behaviour (the official Features tab)
        R("flexible", BuildSection.Behaviour, "edit.feat.flexible", "Flexible Path", "flexi"),
        R("softness_row", BuildSection.Behaviour, "edit.feat.softness", "Softness", "flexi"),
        R("flex_gravity_row", BuildSection.Behaviour, "edit.feat.gravity", "flexi"),
        R("drag_row", BuildSection.Behaviour, "edit.feat.drag", "flexi"),
        R("wind_row", BuildSection.Behaviour, "edit.feat.wind", "flexi"),
        R("tension_row", BuildSection.Behaviour, "edit.feat.tension", "flexi"),
        R("force_x_row", BuildSection.Behaviour, "edit.feat.force_x", "flexi"),
        R("force_y_row", BuildSection.Behaviour, "edit.feat.force_y", "flexi"),
        R("force_z_row", BuildSection.Behaviour, "edit.feat.force_z", "flexi"),
        R("light", BuildSection.Behaviour, "edit.feat.light", "Light"),
        R("light_colour_row", BuildSection.Behaviour, "edit.feat.light_colour", "light color", "projector", "light texture"),
        R("intensity_row", BuildSection.Behaviour, "edit.feat.intensity", "Intensity", "light"),
        R("fov_row", BuildSection.Behaviour, "edit.feat.fov", "FOV", "projector"),
        R("light_radius_row", BuildSection.Behaviour, "edit.feat.radius", "light radius"),
        R("focus_row", BuildSection.Behaviour, "edit.feat.focus", "projector focus"),
        R("falloff_row", BuildSection.Behaviour, "edit.feat.falloff", "Falloff", "light"),
        R("ambiance_row", BuildSection.Behaviour, "edit.feat.ambiance", "Ambiance", "projector"),
        R("shape_type", BuildSection.Behaviour, "edit.feat.shape_type", "Physics Shape Type", "convex hull"),
        R("feat_material", BuildSection.Behaviour, "edit.feat.material", "Material", "wood", "stone"),
        R("physics_gravity_row", BuildSection.Behaviour, "edit.feat.physics_gravity", "Gravity", "physics"),
        R("friction_row", BuildSection.Behaviour, "edit.feat.friction", "Friction", "physics"),
        R("density_row", BuildSection.Behaviour, "edit.feat.density", "Density", "physics"),
        R("restitution_row", BuildSection.Behaviour, "edit.feat.restitution", "Restitution", "Bounciness", "physics"),

        // Contents (the official Content tab)
        R("new_script", BuildSection.Contents, "edit.content.new_script", "New Script", "script"),
        R("contents", BuildSection.Contents, "edit.content.list", "Contents", "inventory", "items"),
        R("content_buttons", BuildSection.Contents, "edit.content.open", "delete", "copy", "open"),
    ];

    /// <summary>The rows a search finds: every word of the query appears in the row's label, its other names or its section's title
    /// (case and accents of the query ignored). An empty query finds nothing.</summary>
    public static IReadOnlyList<BuildRow> Search(string? query, StringTable strings)
    {
        var words = (query ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        var hits = new List<BuildRow>();
        foreach (var row in Rows)
        {
            string hay = (strings.Get(row.LabelKey) + " " + string.Join(" ", row.Aliases)
                + (row.Section is { } s ? " " + strings.Get(TitleKey(s)) : "")).ToLowerInvariant();
            if (words.All(w => hay.Contains(w, StringComparison.Ordinal)))
                hits.Add(row);
        }
        return hits;
    }

    /// <summary>The sections a search opens: those holding a row it found.</summary>
    public static IReadOnlyList<BuildSection> SectionsOf(IEnumerable<BuildRow> hits) =>
        hits.Where(r => r.Section is not null).Select(r => r.Section!.Value).Distinct().OrderBy(s => s).ToArray();

    private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A closed section's one line about the selection; with nothing selected, "nothing selected".</summary>
    public static string Summary(BuildSection section, BuildFacts? f, StringTable s)
    {
        if (f is null)
            return s.Get("edit.summary.none");
        switch (section)
        {
            case BuildSection.Placement:
                return f.Position is { } p && f.Size is { } z
                    ? s.Get("edit.summary.placement", $"{N(p.X)}, {N(p.Y)}, {N(p.Z)}", $"{N(z.X)} × {N(z.Y)} × {N(z.Z)}")
                    : s.Get("edit.summary.none");
            case BuildSection.Shape:
            {
                var parts = new List<string> { f.ShapeName ?? s.Get("edit.summary.unknown") };
                if (f.Hollow > 0) parts.Add(s.Get("edit.summary.hollow", Math.Round(f.Hollow).ToString(CultureInfo.InvariantCulture)));
                if (Math.Abs(f.TwistDegrees) > 0.001) parts.Add(s.Get("edit.summary.twisted"));
                return string.Join(", ", parts);
            }
            case BuildSection.Look:
            {
                string faces = f.SelectFaces && f.FacesPicked > 0
                    ? (f.FacesPicked == 1 ? s.Get("edit.summary.face_one") : s.Get("edit.summary.faces", f.FacesPicked))
                    : s.Get("edit.summary.faces_all");
                return f.Texture is { Length: > 0 } t ? faces + ", " + t : faces;
            }
            case BuildSection.Object:
            {
                var parts = new List<string> { f.Name is { Length: > 0 } n ? n : s.Get("edit.summary.unnamed") };
                if (f.Locked) parts.Add(s.Get("edit.locked"));
                if (f.Physical) parts.Add(s.Get("edit.physical"));
                if (f.Temporary) parts.Add(s.Get("edit.temporary"));
                if (f.Phantom) parts.Add(s.Get("edit.phantom"));
                return string.Join(", ", parts);
            }
            case BuildSection.Behaviour:
            {
                var parts = new List<string>();
                if (f.Flexible) parts.Add(s.Get("edit.feat.flexible"));
                if (f.Light) parts.Add(s.Get("edit.feat.light"));
                if (f.PhysicsShape is { Length: > 0 } ps) parts.Add(ps);
                return parts.Count == 0 ? s.Get("edit.summary.plain") : string.Join(", ", parts);
            }
            case BuildSection.Contents:
                // the contents are asked for only while the section is open: until then the line says how to see them
                return f.ContentCount is { } c ? (c == 0 ? s.Get("edit.content.empty") : s.Get("edit.summary.items", c)) : s.Get("edit.summary.contents_closed");
        }
        return "";
    }

    /// <summary>The hint line under the tools: what the mouse does with the current tool, and what Ctrl and Ctrl+Shift change. A held
    /// modifier switches the Grab and Edit tools' mode while it is held, as in the official viewer, and the line follows it.</summary>
    public static string Hint(BuildTool tool, ManipMode manip, bool selectFaces, bool ctrl, bool shift, StringTable s)
    {
        switch (tool)
        {
            case BuildTool.Focus: return s.Get(ctrl ? (shift ? "edit.hint.focus.pan" : "edit.hint.focus.orbit") : "edit.hint.focus");
            case BuildTool.Move: return s.Get(ctrl ? (shift ? "edit.hint.grab.spin" : "edit.hint.grab.lift") : "edit.hint.grab");
            case BuildTool.Create: return s.Get("edit.hint.create");
            case BuildTool.Land: return s.Get("edit.hint.land");
        }
        if (selectFaces && !ctrl)
            return s.Get("edit.hint.face");
        var shown = ctrl ? (shift ? ManipMode.Scale : ManipMode.Rotate) : manip;
        return s.Get(shown switch { ManipMode.Rotate => "edit.hint.rotate", ManipMode.Scale => "edit.hint.stretch", _ => "edit.hint.move" });
    }
}

/// <summary>Which Build window sections are open, kept in the user's settings so the window opens the way it was left.</summary>
public sealed class BuildSectionMemory(SettingsService settings)
{
    /// <summary>The settings key holding the open sections, by name, comma separated.</summary>
    public const string Key = "build_sections_open";

    private HashSet<BuildSection> Read()
    {
        if (settings.GetText(Key) is not { } text)
            return BuildLayout.OpenByDefault.ToHashSet();
        var open = new HashSet<BuildSection>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<BuildSection>(part, true, out var s))
                open.Add(s);
        return open;
    }

    /// <summary>Whether a section is open.</summary>
    public bool IsOpen(BuildSection section) => Read().Contains(section);

    /// <summary>Opens or closes a section and keeps the choice.</summary>
    public void Set(BuildSection section, bool open)
    {
        var all = Read();
        if (open ? !all.Add(section) : !all.Remove(section))
            if (settings.GetText(Key) is not null)
                return;
        settings.SetText(Key, string.Join(",", BuildLayout.Sections.Where(all.Contains).Select(BuildLayout.NameOf)));
    }
}
