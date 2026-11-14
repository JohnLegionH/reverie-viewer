# Sculpt sampler: behaviour spec

This is a behaviour description of the official viewer (secondlife/viewer, `develop`),
written in our own words. The implementation (`core/Viewer.Core/Meshing/SculptMesher.cs`, `OfficialGrid` and
`SampleOfficial`) is written from this spec; no viewer code or comments are copied.

Observed from: `indra/llmath/llvolume.cpp` (the sculpt resolution choice, the map-to-vertex sampling, the path and
profile point generation for sculpts, the placeholders), `indra/llmath/llvolumemgr.cpp` (detail per LOD) and
`indra/newview/llvovolume.cpp` (the LOD radius and when a map counts as missing).

## 1. Budget per LOD

- LOD 0-3 use the volume detail 1, 1.5, 2.5 and 4.
- The side budget `sides` is 6 for detail up to 1, 8 up to 2, 16 up to 3 and 32 above (so 6/8/16/32 for LOD 0-3).
- The vertex budget is `sides * sides`, reduced to `W * H / 4` (integer division) when the map has fewer pixels
  than that and `W * H / 4` is above 0. A map with `W * H / 4 == 0` keeps the full budget.

## 2. Grid shape (any aspect ratio)

With `V` the budget and `r = W / H` (1 when either side is 0):

1. `s = floor(sqrt(V / r))`, at least 4. `s` counts segments along the path, which runs down the map (rows, `y`).
2. `t = V / s` (integer division), at least 4. `t` counts segments along the profile, across the map (columns, `x`).
3. `s = V / t` again (integer division), but at least 1: the path always has one segment or more (this only matters
   for maps of 1-3 pixels, where `V` is below 4).

So `s * t <= sides * sides` (and `<= V` whenever `V >= 4`): never more segments than the official grid, and the grid keeps the map's aspect
ratio as closely as whole numbers allow. A sculpt's path and profile are closed circles, so each has one more point
than segments: the vertex grid is `(s + 1)` rows by `(t + 1)` columns.

## 3. Where the samples fall

For row `i` (0..s) and column `j` (0..t):

- The column is read reversed (`j' = t - j`) when exactly one of the invert and mirror flags is set; otherwise `j' = j`.
- `x = floor(j' / t * W)`, `y = floor(i / s * H)` (so the first sample is the map's top-left pixel and the last row
  and column land exactly on `H` and `W`, one past the map).
- Edges, by sculpt type:
  - **Top row** (`y == 0`), sphere: every column reads the map's top-middle pixel (`x = W / 2`, the pole).
  - **Bottom row** (`y == H`): torus wraps to the top row (`y = 0`); every other type clamps to `y = H - 1`.
    Sphere then also reads the middle pixel (`x = W / 2`, the other pole).
  - **Last column** (`x == W`): sphere, torus and cylinder wrap to column 0; plane clamps to `x = W - 1`.
- The point is `(R, G, B) / 255 - 0.5` of that pixel. **Row 0 is the map's BOTTOM row**: the official viewer stores
  decoded images bottom-up (`indra/llimagej2coj/llimagej2coj.cpp` writes rows from the last to the first), so `y`
  counts up from the bottom of the image (an earlier version read it top-down). Mirror negates the point's x.

## 4. Surface

- Every grid cell is two triangles, wound so that a map made for SL faces outward (checked on two real
  sculpt maps, which face out with this winding and in with the other). The column reversal above reverses the
  winding, so an inverted sculpt shows its other side; mirror both negates x and reverses the columns, so a mirrored
  sculpt still faces out.
- UVs: `u = j / t`, `v = i / s` (v runs up the image, like every SL texture coordinate).
- Normals: smoothed (the sum of the adjacent triangles' normals); on closed types (not plane) the first and last
  columns share their normal, as they are the same seam. (Triangulation and normals are the BSD port's, unchanged.)

## 5. Fallbacks (unchanged from the first version)

- No map because the asset is missing: a sphere of radius 0.3 on the same grid (visible stand-in).
- A map the decode guard refuses, or no data: nothing is drawn.
- Above the lowest LOD, a surface area (in map units) below 0.002 or above 384: the sphere stand-in.
- At the lowest LOD the area is not checked; a surface of zero area draws nothing.

## Not reproduced

- Very old sculpts whose path or profile is not the default circle: the official viewer generates those with its prim
  path/profile code; here every sculpt uses the grid above.
