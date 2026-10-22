# Changelog

## 0.9.1-beta — 2026-10-22

- **SubD objects now appear**, in the view and in captures. Rhino draws SubDs from its own
  display mesh and usually keeps no render mesh for them, so they were left out. ZDepth
  now meshes them itself at the default display density, including SubDs inside blocks.
- Kept meshes are also dropped when an object's mesh settings or a block definition
  change.
- Shorter package description; links point to the `ZDepth_Rh8_Mac` repository.
- README: install from the Package Manager, credits and AI disclosure with citations;
  `CITATION.cff` for citing ZDepth.

## 0.9.0-beta — 2026-10-11

First public beta of **ZDepth**, a Rhino 8 plug-in that shows the depth pass (z-buffer)
live in the viewport and saves it as a depth map. Works on the Mac, where `ShowZBuffer`
shows nothing (RH-82309).

### What's in it

- **Z Depth display mode**: every surface grey by its depth, near white, far and
  background black, without grid, curves, edges or annotations. Live while orbiting.
- **`ZDepth`**: switches the active viewport to Z Depth and back.
- **`ZDepthRange`**: automatic or fixed near/far (saved in the document), *FromView*, and
  which end is white.
- **`ZDepthCapture`**: 8-bit PNG, 16-bit PNG or float EXR (model units), at any size.
  Rendered on the CPU from the objects the view shows, so the depth is exact and lines up
  with Rhino's framing at that size. Clipping planes are applied. A JSON file records
  range, units, encoding and camera.
- **ZDepth toolbar**: both commands as buttons (right-click the first for `ZDepthRange`).

### Install

1. Download `zdepth-0.9.0-beta-rh8_0-any.yak`.
2. Drag it into a Rhino 8 window, then restart Rhino.
3. Run `ZDepth`.

Requires Rhino 8. Tested on Rhino 8.35 for Mac; Windows is untested so far.

### Known limitations

- The viewport is a preview; its greys go through Rhino's display colour handling. Save
  files with `ZDepthCapture`.
- Grasshopper previews are not document objects and don't appear. Bake them first.
- Only objects with render meshes appear; curves, points and annotations are left out by
  design.
