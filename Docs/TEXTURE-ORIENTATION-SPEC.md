# Texture orientation: behaviour spec

A behaviour description of the official viewer (secondlife/viewer, `develop`), in our own
words. The implementation (`PrimGeometry.GroupFaces(..., slTextureOrientation: true)`, `SculptMesher.BuildOfficial`)
and the tests (`TextureOrientationTests`, `SculptRealMapTests`, the render case `colour_*`) follow this spec; no viewer
code or comments are copied.

Observed from: `indra/llmath/llvolume.cpp` (the side, cap and uncut-box-cap texture coordinates of a volume face),
`indra/llimagej2coj/llimagej2coj.cpp` (decoded images are stored bottom-up) and `indra/newview/llface.cpp` (the
texture transform and planar mapping, applied on top of these coordinates).

Default texgen, repeats 1, offset 0, rotation 0 throughout.

## 1. Conventions

- A texture coordinate (u, v) has u running to the image's right and **v running UP the image** (v = 0 is the bottom
  row, v = 1 the top row), because the official viewer keeps decoded images bottom-up.
- "Mirrored" means the image appears flipped when the face is seen from outside. **No prim face is mirrored**:
  seen from outside, u to the right and v up form a right-handed pair with the face's outward normal.

## 2. End caps (the path's start and end)

In the profile's own coordinates (x, y, each -0.5..0.5; on a straight path these are the object's X and Y):

- **Top cap** (the path's end, facing +Z on a straight path): u grows with +x, v grows with +y. Seen from above with
  north (+Y) up, the image is upright.
- **Bottom cap** (the path's start, facing -Z): u grows with +x, v grows with -y. Seen from below, the image is
  upright (not mirrored).

## 3. Side faces

- v follows the path: on a straight path it grows upward (+Z), 0 at the bottom edge and 1 at the top edge.
- u follows the profile in its own direction (anticlockwise seen from above): on the south side of a box (facing -Y)
  it grows toward +X, on the east side (+X) toward +Y, on the north side toward -X, on the west side toward -Y.
  Seen from outside, each side shows the image upright.
- A flat side (a box or prism side, a cut face) shows the whole image once; a curved side (a cylinder, the profile of
  a sphere, torus, tube or ring) wraps the image around once along the profile.
- On circular paths (sphere, torus, tube, ring) the same rule holds: u along the profile, v along the path. A sphere's
  profile is the half circle from pole to pole and its path turns around the object, so the image runs pole-to-pole
  in u and around in v (SL spheres show textures turned a quarter from what one might expect; this is SL's behaviour
  and is kept).

## 4. Planar texgen

- Unchanged from the first textured version (`FaceShader`): the object-scaled position projected on the plane chosen from the face normal,
  then the texture transform. Not affected by this spec.

## 5. Sculpts

- u follows the map's columns (left to right), v follows its rows **from the bottom row up** (the map is read
  bottom-up, spec in `Docs/SCULPT-SAMPLER-SPEC.md` section 3), so a texture sits on a sculpt the way the map's
  image is oriented.

## 6. What changed in the viewer

- The PrimMesher port (LibreMetaverse's, via the web viewer) produces u, v with **v running down** the image and gives
  the bottom cap the top cap's mapping. The viewer now maps them to SL's orientation: `v -> 1 - v` on every face
  except the bottom cap, which is already SL's as the port makes it. Before the fix every side face and every top cap
  showed the image upside down (vertically flipped: mirrored), and the bottom cap was correct.
- The sculpt sampler read the map top-down; it now reads it bottom-up, with the winding that makes real SL maps face
  outward.
- **The winding (the main fault):** SL geometry winds outward faces anticlockwise, Godot's front faces are clockwise,
  and textured faces are drawn with back faces culled. So every closed textured object showed its INSIDE: the colour
  chart seen from above was the bottom cap seen from within (hence upside down), and the UV fix above could not change
  it. The viewer now reverses each triangle when it uploads geometry (`PrimLayer.EngineWinding`: prims, meshes and
  sculpts). Untextured faces were drawn two-sided, which is why the fault never showed on them.
- `FaceShader` keeps sampling with v flipped (SL's v runs up the image, the decoded image's first row is its top).
- Verified end to end: the colour chart's top face is upright seen from above, north up, with exact colours, on
  vulkan and d3d12 (render case `colour_*`, which now requires "upright").
