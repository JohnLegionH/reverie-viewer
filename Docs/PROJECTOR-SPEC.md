<!-- Copyright (c) 2026 Legion Builds -->

# Prim lights and projectors

How the viewer draws a prim's local light and its projector (the "light image" block of an object), what the official viewer does, and where the engine differs. Behaviour reference only: the official viewer's `LLVOVolume` (`llvovolume.cpp`), `LLPipeline` (`pipeline.cpp`), its `RenderLocalLightCount` setting and its deferred spot-light shader were read for numbers and behaviour; nothing is copied.

## What the official viewer does

- **A light** has a linear colour with an intensity, a radius, a falloff and a cutoff. It is drawn out to 1.5 times the radius (`pipeline.cpp`, deferred local lights) with half the simulator's falloff (`DEFERRED_LIGHT_FALLOFF`, 0.5). The cutoff is carried but not read by the deferred lighting.
- **How many:** the lights nearest the camera, up to `RenderLocalLightCount` (default 256, `app_settings/settings.xml`; in `pipeline.cpp` the nearby-light set is sorted nearest first and the deferred pass stops at that count). Attached lights follow `RenderAttachedLights`.
- **A projector** is a light that also has a light image block: a texture and three numbers (field of view, focus, ambiance; `LLLightImageParams`). It shines along the prim's -Z, from an apex behind the prim's face so that the face is the picture's window. The deferred spot-light shader projects the texture, blurs it by distance from the focus plane (`proj_focus`, `proj_lod`), and adds the picture's average colour times the ambiance inside the cone (`proj_ambiance`).
- **Shadows:** the official `RenderShadowDetail` default is 2 (`app_settings/settings.xml`), which includes projector shadows (`pipeline.cpp`: spot shadow maps are made only when it is above 1). Two spot-light shadow maps exist, so at most two projectors cast shadows at once. Each frame every projector is ranked by `LLVOVolume::updateSpotLightPriority`: the on-screen area of a sphere of the light's radius, centred a radius along the light's shining direction and a radius back along the camera's view direction (a distance under 16 m is shrunk to distance squared over 16), and the two highest take the maps; a projector that loses a map fades its shadow out and the newcomer fades in. The map is square, as wide as the screen times `RenderShadowResolutionScale` (default 1.0), with `RenderSpotShadowBias` -0.0002 and `RenderSpotShadowOffset` 0.04. The shadow pass is the same scene pass the sun's shadows use (prims, avatars, and objects drawn with alpha as the pass allows), except that the light's own prim is skipped (`RenderSpotLight`, so the lamp does not shadow its own beam). A projector without a shadow map lights through other objects.
- Not confirmed from the source in this pass: which faces a light skips (fullbright faces, alpha modes, PBR faces).

## What the viewer draws

- The numbers come from `LightRules` (core, engine-free, tested): the reach, the falloff, the beam's apex, near and far distances and the cone that holds the picture's corners.
- A plain light is an engine omni light; a projector is a spot light with a projector picture made by `ProjectorPicture`: the window inside a square canvas, blurred by the focus, with its edges faded and the ambiance added. The picture is made from a small copy (at most 256 a side) of the light's texture, taken from the engine image once the texture is on the GPU.
- At most `LightRules.MaxLocalLights` (256) lights are visible: the nearest to the camera, chosen once a frame.
- Lights are on the view's layer and the glow picture's layer, because a camera takes only the lights on the layers it sees. The sun (and the moon, the same engine light at night) and the ambient light (copied to the glow picture's environment) reach the glow picture too. `render.ps1` checks each kind: a glowing face lit only by that light must hold, in the glow picture, what the view shows with the glow pass off.

## Where the engine falls short

- A stock spot light projects its texture over a round cone; the picture is a rectangle, so the cone is widened to hold its corners and the picture's own border is black.
- There is no focus or ambiance control: both are baked into the picture's pixels, so they follow the viewer's rule at the light's own distance, not per pixel at each distance.
- A spot light needs a shadow slot for its projector to light anything (observed on Vulkan), so every projector keeps one, and each pays for it. A shadowed light costs the engine one more depth pass of the scene from the light (cut to what the light reaches) and a share of the positional shadow atlas; the viewer gives the shadow itself (opacity 1) only to the projectors the official viewer would, and the others keep the slot at opacity 0.
- The engine's distance curve is not the viewer's; three numbers (`FitExponent`, `FitRange`, `FitScale`) fit one to the other within about a quarter of the viewer's value.

## Projector shadows

- On by default, as the official viewer's default has them. `ProjectorLights.Shadows` is the setting that turns them off (there is no settings store yet; a test run passes `--tr-shadows=off`).
- `LightRules.ShadowCasters` picks the (at most `LightRules.MaxShadowedProjectors`, 2) projectors that cast, by the official ranking above, once a frame from the camera's place and view direction. The ranking uses the light's radius, its shining direction and the camera; it does not fade a shadow in or out when the choice changes, it switches.
- Casters: every object on the view's layer. A projector's own prim is on a layer of its own, so it is left out of every projector's shadow (the official viewer leaves out only the light's own prim; a projector prim does not shadow another projector's beam here). A projector's apex is behind its prim's face, so without this the prim blocks its whole beam.
- The shadow's bias and softness are the engine's node defaults, set explicitly: the server's smaller defaults made a thin glowing panel shadow itself and read darker than its glow picture.
- Not matched: the shadow map's size (the engine shares one atlas between all the lights and sizes each light's slot by its distance and the atlas's quadrant layout, not by the screen's width), the fade between choices, and avatars as casters (the viewer draws avatars in a separate layer from the prims' shadows; whether they cast into a projector's shadow was not checked).

## Open

- A spot light's focus and ambiance are baked into its picture, not per pixel.
