# Glow: behaviour spec

What the official viewer shows for a face's glow, in our own words; the implementation is written from this and no viewer
code or comments are copied. Observed behaviour and the viewer's documented default settings only.

## The glow value

- Each face has a glow value from 0 to 1 in its texture entry. The viewer keeps it as one byte (the value times 255,
  truncated), so 0.5 is 127/255.
- A face with glow 0 draws no glow. Glow is also off wholesale when the viewer's glow setting is off (on by default).
- The glow value says how much of the face's own displayed colour goes into the glow picture; it does not brighten the
  face by itself. What a viewer sees as the face brightening is the blurred glow being added over the face and the
  pixels around it.

## What feeds the glow

For every pixel of a glowing face, the glow picture takes the pixel's colour as it is displayed (lit, fogged, tone-mapped
and gamma-encoded) times the glow weight:

- a legacy or plain face: the glow value times the texture's alpha at that pixel. The tint's alpha does not scale it.
  Faces in any legacy alpha mode glow (none, blend, mask, emissive);
- a face drawn alpha-blended: the same weight, added in the order the blended faces are drawn, and the blended face
  also dims the glow of whatever is behind it by its alpha (a pane in front of a glowing face shows less glow);
- a PBR face (opaque or blended): the glow value times the brightest channel of the face's emissive colour at the pixel
  (emissive factor times emissive texel, linear). A PBR face with no emissive light has no glow;
- a PBR face in mask alpha mode draws no glow at all;
- a fullbright face glows like any other: its displayed colour is its unlit colour.

Glow is depth-tested against the rest of the scene: a glowing face behind a solid wall adds no glow. Nothing glows
through terrain or other opaque faces.

## Gathering, blurring, adding

1. The weighted colours are collected in a square picture of 512 by 512 pixels covering the whole view (stretched to the
   view's shape), eight bits per channel.
2. The picture is blurred in four passes, the first horizontal, then vertical, horizontal, vertical (two iterations).
   Each pass sums eight samples of the previous picture, spaced 1.3/512 of the view apart and centred on the pixel,
   weighted 0.25, 0.5, 0.8, 1.0, 1.0, 0.8, 0.5, 0.25, and multiplies the sum by the strength 0.325. After every pass the
   result is clamped to 0..1 by the eight-bit buffer. A flat glow picture therefore gains 5.1 x 0.325 = 1.6575 times
   per pass.
3. The blurred picture is added to the displayed picture (after tone-mapping), per channel, and the sum is clamped to
   1. Pixels with no glow nearby are unchanged.

The default settings are: glow on, two iterations, a 512 pixel picture, width 1.3, strength 0.325. The viewer's optional
automatic glow for bright surfaces is off (its minimum luminance is out of reach), and its dithering noise on the glow
picture is not reproduced.

## Consequences worth testing

- Glow against a dark sky shows as a soft halo of the face's colour reaching a few percent of the view's width beyond the
  face; against a bright sky the sum clamps, so the halo is hardly visible.
- Halo colour is the face's displayed colour: a red face gives a red halo, a textured face a halo of its texture's
  colours averaged by the blur.
- Doubling the glow value does not double the halo exactly: the value is applied to the colours in the gamma-encoded
  picture, and the blur clamps after each pass.

## Where ours differs

The glow picture is drawn by a second render of the scene at twice the glow picture's width (a viewport of its own), in
which every face that does not glow is black and every glowing face is its lit colour times its glow weight (applied to the
displayed, gamma-encoded colour, as above). See `Presentation/GlowPass.cs`. Differences from the official viewer:

- The picture is only drawn while a face that glows is in view or just outside it, so a view with no glow costs nothing.
- The picture draws only the objects with a glowing face and the objects that can be in front of one from the eye (judged
  on bounding spheres, so an object that could hide a glowing one is never left out), and the terrain. The picture is the
  same as with every object drawn.
- A blended glowing face gathers its own colour, not its colour as blended over what is behind it.
- Haze (fog) does not tint the glow picture: a glowing face far away glows at its unhazed colour.
- Water and avatars are not part of the picture, so they do not hide glow behind them, and glow seen through water is not
  dimmed. Terrain whose detail textures have not arrived yet is left out too.
- The glow picture is drawn at twice its width and averaged down; the official viewer samples the finished view directly
  (with a little noise added).
