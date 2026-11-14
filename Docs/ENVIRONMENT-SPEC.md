# Water and environment: behaviour spec (first look)

A behaviour description of the official viewer (secondlife/viewer, `develop`) and of what the
grid serves, in our own words. The implementation (`core/Viewer.Core/World/RegionEnvironment.cs`,
`host/Viewer.Godot/Presentation/EnvironmentLayer.cs`, `WorldPresenter`) follows it; no viewer code is used.

Observed from: the grid's ExtEnvironment capability answer (the EEP settings format; the captured region),
`indra/newview/llenvironment.cpp` (the day-cycle clock and the blend between keyframes),
`indra/llinventory/llsettingssky.cpp` (sun and moon direction from their rotations), and the region handshake
(`llviewerregion.cpp`: the water height).

## 1. Where the environment comes from, and the clock

- The region's environment is the EEP day cycle the grid serves at the region's **ExtEnvironment** capability
  (fetched once the region's capabilities are known; the raw answer is kept, so captures replay it exactly). A region
  without the capability, or an answer that is not an EEP day cycle, gets the **default** environment (section 4).
  Legacy (WindLight) region settings are not read (first look; the grid here serves EEP).
- A day cycle has tracks: track 0 is the water, track 1 the ground-level sky, tracks 2 to 4 skies at higher altitudes
  (not used here). Each track holds keyframes at positions 0..1 of the day.
- The day position at a moment is `((seconds since the Unix epoch + day offset) mod day length) / day length`.
- The sky at a position blends the two keyframes around it (wrapping from the last back to the first); we blend the
  values we use linearly, directions normalised.
- **Test renders use a FIXED position** (0.3 by default, or the scene fixture's), so goldens are deterministic; the live
  viewer follows the region's clock.

## 2. Sky, sun, ambient, haze (what we use from a sky keyframe)

- Sun direction: the +X axis (east) turned by the keyframe's `sun_rotation` quaternion ([x, y, z, w]); likewise the
  moon from `moon_rotation`. The captured region's default cycle: sunrise in the east at 0.125, overhead at 0.5, sunset at 0.875.
- `sunlight_color` (values above 1 mean a brighter sun), `legacy_haze.ambient` (ambient light), `legacy_haze.blue_horizon`
  and `blue_density` (the sky's horizon and zenith colours), `haze_horizon`, `haze_density`, `density_multiplier` and
  `distance_multiplier` (how thick the haze is and how fast it grows with distance), `moon_brightness`.

**Approximated (first look):** the official viewer draws a physically based atmosphere (Rayleigh/Mie scattering,
absorption, clouds, stars, sun/moon textures, glow). Here: Godot's procedural sky with the zenith from `blue_density`
and the horizon from `blue_horizon` plus the horizon haze; the sun (or the moon, when the sun is below the horizon) as the
directional light with the frame's colour (its strongest channel as the energy, capped); the ambient colour as ambient
light; exponential fog from the haze values. No clouds, stars, sun disc texture, glow or scattering. A faithful SL sky
shader is later work.

Under a sky that sets a reflection probe ambiance (the tone-mapped path) the haze is not the engine's fog: it is the
official haze pass, run in the tone-mapping compositor effect from the depth buffer. Each non-sky pixel keeps the red
channel's survival of its colour and gains the haze's own light (doubled, made linear, scaled by the sky's scale), as
the official viewer's atmospherics compute them. The sun's (or moon's) disc is drawn at full brightness in the light's
tint, added to the sky behind it.

## 3. Water

- The water surface is a plane at the region's **water height** (region handshake; 20 m in the captured region), reaching to the
  horizon in every direction, as SL shows water beyond the region's edges.
- Its look comes from the day cycle's water keyframe: `water_fog_color` and `water_fog_density` (how quickly the water
  becomes opaque with depth), `wave1_direction` / `wave2_direction` (the ripple directions).

**Approximated:** the water colour is the fog colour, its opacity grows with the water depth behind the surface
(depth-based fog), and two ripple trains perturb its normals near the camera (fading out with distance). **Not done:**
reflections, refraction, the normal-map texture, fresnel, underwater fog.

## 4. Defaults

- No environment served: one midday-like sky (values in the range of the grid's default cycle at midday) and the grid
  default's water look.
- No water height known (older captures): no water and no sky change (the plain stage, as before), so older
  captures and their goldens are unchanged.
