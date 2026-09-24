Palon mascot: notes
===================

Look (matches the supplied reference frame)
- Body: a near-perfect circle, r=100 in a 250-unit viewBox (-125..125), drawn as a 64-point Catmull-Rom cubic path.
- Shading: one rect filled with radialGradient (userSpaceOnUse, cx -50.56, cy -63.2, r 244.9; stops 0 #a2a2a3, .32 #3b3b3d, .72 #0a0a0c, 1 #0a0a0c),
  drawn through a mask = white body minus two black eye shapes. The eyes are HOLES.
- Under the holes: the eye paths themselves filled with a soft blue vertical gradient (#A9D8FF -> #3C86FF). This gives the blue eyes.
  (Only the eye paths are drawn blue, not the whole body, so no blue fringe shows at the body edge.)
- Eyes: capsules 0.186R x 0.412R (18.6 x 41.2), idle head pose yaw -1, pitch -5.5, roll -13, split 15.5 deg, which puts them at about
  (-28, 15) and (24, 3.5) with the slight lean of the reference.
- Palon's own touch: a small blond tuft (3 tapered, curved strands, gradient #E0AE4C -> #FFE9A8) rooted inside the top of the body and drawn
  BEHIND it, so only the strands show. It sways with a slow noise, follows head roll/yaw, and swings on hops.
- No mouth, blush, gloss or outline. Everything is done by body + eyes + tuft.

Engine API (pure JS IIFE, no DOM). In the DC file it's returned by Component.engine()
- create(moodId, now) -> instance {cur, prev, tCur, tPrev, frozen, blinkAt, seed}. Set seed to offset blink timing between instances.
- setMood(inst, moodId, now): starts a 0.5 s easeOutQuint morph from the pose currently on screen. If a morph is already running, the
  blended pose is frozen as the origin, so repeated clicks never jump. It also forces a blink.
- sample(inst, now, R=100) -> { body, eyeL, eyeR, hair }: SVG path strings, centred on 0,0. A pure function of time: same input gives the same frame.
- moods: ['idle','listen','think','talk','happy'], MORPH = 0.5.
Time is in seconds. Drive it with rAF, then setState({now}). Small instances use the same R=100 geometry and only scale the <svg> element.

Inside sample()
1. pose = mood(id, t) blended with the previous one: gaze {yaw,pitch,roll}, split, eyes[2] {w,h,open,tilt,bend}, sx, sy, offY, wander, hair.
2. Life layer (from bloub): gaze drift = sum of loopNoise at co-prime periods (11.3/3.7, 9.1/4.3, 13.7 s) scaled by wander. Tiny body float,
   breathing sy +-0.6% over 3.6 s. Deterministic blink schedule (mulberry32, 2.2-5 s apart, 18% double blinks, 0.19 s fast close/slower open).
3. Eyes live on a unit sphere: eyePoses(yaw,pitch,roll,split) returns each eye's projected centre and tangent frame. The capsule outline
   (44 points, sampled evenly by arc length, so every shape has matching points) is mapped through that frame plus the eye's own tilt,
   then squashed vertically for the blink (0.06..1). This foreshortening is what gives the volume.
4. bend > 0 arches the capsule into a "^" arc (the happy eyes), so happy morphs smoothly from the capsule shape.
5. Body squash is anchored at the bottom (y' = y*sy + (1-sy)), so hops and talk bobs feel grounded.

Moods
- idle: the reference pose, full drift, blinks, breathing.
- listen: eyes a bit larger (0.215 x 0.46), faces the viewer, less roll, small periodic nods, body a touch taller.
- think: looks up and left (yaw -21, pitch 12), eyes shorter and uneven with a slight tilt, slow searching drift.
- talk: faces the viewer. Eye height pulses to a layered-sine syllable rhythm, with a small body bob and tuft flick.
- happy: "^" arc eyes, soft repeating hop (sin^2, a 0.95 s cycle) with squash on landing, tuft swings, gentle head sway.

Porting to C#/WPF
- Port sample() verbatim (only math). Put the result in Path.Data via Geometry.Parse(d), or build PathGeometry/BezierSegments directly.
- The mask becomes a Grid: eye Paths (blue brush), then a Rectangle with the RadialGradientBrush (MappingMode=Absolute), clipped to a
  CombinedGeometry(Exclude, body, eyeL+eyeR). Tuft Path goes behind it. Drive it from CompositionTarget.Rendering.

Taken from bloub (MIT, github.com/jeremy-prt/bloub, credited in a code comment)
- A clockless engine where sample(t) is pure. Morph origins are frozen when a transition is interrupted. easeOutQuint with no body overshoot.
- The eyes-on-a-sphere tangent-frame projection (face.ts eyePoses), loopNoise drift and the mulberry32 blink schedule. Blink is a screen-space
  vertical squash, and a forced blink runs on each mood change.
- Shapes sampled at a fixed point count, so a morph is plain point interpolation. Catmull-Rom (tension 1/6) turns the points into cubics. r2 rounding.
Own additions: the bend parameter for happy arcs, the tuft, mood poses/rhythms, the bottom-anchored squash and the blue eye under-layer.
