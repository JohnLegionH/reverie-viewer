# Terrain composition: behaviour spec

A behaviour description of the official viewer (secondlife/viewer, `develop`), in our own
words. The implementation (`core/Viewer.Core/World/TerrainComposition.cs`, the terrain shader in
`host/Viewer.Godot/Presentation/TerrainView.cs`, `WorldPresenter`) follows it; no viewer code or comments are copied.

Observed from: `indra/newview/llviewerregion.cpp` (the region handshake's terrain fields and where they go),
`indra/newview/llvlcomposition.cpp` / `.h` (the composition value, the corner interpolation, the noise scales and
strengths, the default detail textures), `indra/newview/lldrawpoolterrain.cpp` and `app_settings/settings.xml`
(`RenderTerrainScale`, the detail texture repeat).

This is SL's **legacy** terrain (four detail textures). PBR terrain (glTF materials as terrain layers) is out of scope.

## 1. What the region sends

The region handshake carries, for the whole region:

- four detail texture asset ids, layers 0 to 3 (low ground to high ground);
- four **start heights** and four **height ranges**, one per region corner, named by the corner's (x, y) in the
  handshake's field names `..00`, `..01`, `..10`, `..11`: `00` is the south-west corner, `10` the south-east (x at the
  far edge), `01` the north-west (y at the far edge), `11` the north-east. (The official code stores them under corner
  names that do not match these positions, but its interpolation swaps them back; the net behaviour is the (x, y)
  reading above.)

## 2. The composition value

For a ground point at region-local (x, y) metres with height h:

- `fx = x / regionWidth`, `fy = y / regionDepth` (0..1).
- The start height S and the range R at the point are the bilinear blend of the four corners' values at (fx, fy).
- `c = clamp((h + noise - S) * 4 / R, 0, 3)`.
- The value is computed on a grid of about one sample per metre across the region.

So with no noise, ground at the start height is pure layer 0, and each quarter of the range above it moves one layer up;
ground at `S + 3R/4` and above is pure layer 3.

## 3. The noise

The noise breaks up the height bands so the layers do not form contour lines:

- It is sampled at the point's GLOBAL position (so it is continuous across region borders) divided by 4.9215 m
  horizontally. It is 2D: the height is not part of the noise (the official code passes the height to its noise call but
  the 2D noise function ignores it).
- A low-frequency part, sampled at 0.2222 times those coordinates, with strength 6.5 (metres);
- plus a turbulence part of two octaves, at frequencies 2 and 1, each octave's SIGNED noise divided by its frequency,
  with strength 1.5^2 = 2.25 (signed, so the turbulence has zero mean like the rest);
- the sum is doubled.

The official viewer uses classic 2D gradient (Perlin) noise with unit gradients; **ours is our own gradient noise with the
same scales and strengths**, so the bands are broken up the same way in character and with the same statistics (zero mean,
standard deviation near 2.9 m, which keeps ground at 26 m with start 10 and range 60 about 83 percent in the grass layer), but not
pixel-for-pixel. (An earlier version used value noise with an always-positive turbulence and a height term: it leaned
towards the higher layers and put 36 to 52 percent of the weight on the mountain layer on flat ground at 27 m.)

## 4. Drawing

- Each detail texture repeats every 12 m in world X and Y (`RenderTerrainScale`, default 12), offset by the region's
  global origin modulo 12 m, so the pattern is continuous across regions.
- The four layers are blended as the official terrain shader blends them, from a two-dimensional ramp picture: alpha1, alphaFinal and
  alpha2 are the ramp read at (c, j), (c - 1, j) and (c - 2, j), with j a per-vertex jitter in 0..1; the colour is
  `mix(mix(layer3, layer2, alpha2), mix(layer1, layer0, alpha1), alphaFinal)`. The ramp is 1 at its first coordinate 0 and falls to 0 at 1,
  and not in a straight line; the jitter moves where it falls, so the edge between two layers is broken up. The jitter is
  `clamp(0.5 + 0.75 * noise, 0, 1)` of the vertex's global position at 1 / (4.9215 * 7) of a metre times 0.2222 per lattice step, the same slow
  noise across region borders (ours is our own gradient noise, as above).
- As for prim faces, the blend happens on the textures' sRGB values and the result is converted to linear for
  lighting.

## 5. Defaults

- The official viewer falls back to built-in Linden detail textures (dirt, grass, mountain, rock) when a region leaves
  a layer unset. Those are Linden content and are not used here: a layer that is unset, or whose texture cannot
  be fetched, uses our own procedural texture of the same role (`Viewer.Shell.TerrainDetails`: dirt, grass, mountain,
  rock tones). A region without terrain settings at all (older captures) keeps the height-coloured ground.
