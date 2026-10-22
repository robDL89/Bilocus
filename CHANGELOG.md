# Changelog

## Unreleased

- New: **linked models in the pull.** Select a Revit link and press Send
  Selection: every model element of the linked document arrives in Blender
  in place, in `FromRevit/LinkModel - <link name>`, with identical family
  instances sharing one mesh. The sub-collection is rebuilt on every send,
  so edits to those objects are not kept. Large links take a while; Blender
  stays usable and the panel shows the progress.
- New: **MEP system in the name.** Pipes, ducts, fittings and accessories
  are named with their system type, `Pipes (Domestic Hot Water) - Standard
  [123]`, and identical fittings on different systems no longer share a
  mesh: a material per system can be assigned with Select Pattern and
  Link Materials.
- New: **arcs on inclined planes** in Create Proxy, Arcs and lines mode.
  An arc lying on a plane tilted sideways (a ring on an inclined plane, a
  curve on a roof pitch) now becomes one arc on its own plane instead of a
  run of short segments, and a beam picked on it follows that plane. Plan
  and elevation arcs, and helices, are unchanged.
- New: **preview opacity.** An Opacity slider next to the preview colors in
  the Blender panel makes the preview faces translucent in Revit, to see
  the model through them. The edges stay opaque. Applied immediately, no
  Sync needed; with an older Blender add-in the preview stays opaque.
- New: **family dimension parameters.** A family baked from Blender carries
  `BL_Length`, `BL_Width`, `BL_Height`, `BL_Area` and `BL_Volume` (closed
  meshes only) as type parameters, plus `BL_Thickness` when set. Values come
  from the object's extent and can be set by hand from an edge or face
  selection in the panel, except the volume. Informative only: they do not
  drive the geometry and are rewritten at every bake. A value set by hand
  is flagged in the panel, and in the bake message, once the object's
  dimensions change: it is still sent until you set it again or clear it.
  A `BL_` parameter you use in a formula in the family editor is kept even
  when it is no longer sent, and one you give a formula is not overwritten.
- New: **metric UVs on pulled meshes.** Every mesh from Revit arrives with
  a `UVMap`, one unit per meter: a 1 x 1 m texture shows at its real size
  on any element. Each face is projected on its own plane, so a wall at
  any angle is not stretched, and on walls and roofs the texture runs
  level. Floors keep world X and Y. Exported to Unreal it is UV channel 0,
  a clean source for Generate Lightmap UVs. Curved surfaces get small
  breaks between faces: unwrap those by hand if they show. On by default;
  turn off **Metric UVs** in the pull section to unwrap your own way.

## 0.2.0 - 2026-10-10

- New: **Revit 2026 and 2027.** One zip per Revit version, 2024 to 2027.
  Revit 2027 is built for .NET 10.
- New: **Blender 4.2 LTS and 4.5 LTS.** The add-on now installs on
  Blender 4.2 and later; CI runs the smoke tests on 4.2, 4.5, 5.1 and 5.2.
- Changed: **pulled meshes are welded.** Revit triangulates each face on
  its own, so a pulled element arrived as loose triangles. Coincident
  vertices (within 0.005 mm) are now merged: edge loops, bevel and select
  linked work. A shared mesh already in the file is reused as it is on
  the next pull: weld it with Merge by Distance, or delete the objects and
  pull them again.
- New: **instanced pull.** Family instances with identical geometry share
  one mesh in Blender: edit, UV or replace one (Ctrl+L Link Object Data
  from an asset) and every copy follows. A new element of the same type
  arrives with what its siblings show. A shared mesh you have edited is
  kept on the next pull; an untouched one follows Revit. Protocol version
  2: update both the Revit add-in and the Blender add-on.

## 0.1.1 - 2026-10-03

- Fixed: **Bake Family could overwrite an existing family.** A new family
  was loaded under Revit's temporary name ("Family1", "Family2"...), not
  `BL_<name>`, and if the project already had a family with that name its
  geometry was replaced, so the solid appeared in that family's instances,
  somewhere else in the model. New families are now loaded as `BL_<name>`
  and never overwrite an existing family. Families created by 0.1.0 are
  renamed to `BL_<name>` on their next bake.
- Fixed: **Send Selection could overwrite a Shift+D copy.** A duplicate of a
  pulled object kept its Revit id, and pulling the element again could
  update the copy (for example a cutting solid modeled from a beam) instead
  of the original. Now only the pulled object is updated; if it is gone,
  a new object is created and the copies are left alone.

## 0.1.0 - 2026-09-29

First public release.

- Live preview of Blender geometry inside Revit 3D views (DirectContext3D),
  with live transforms and manual geometry Sync. The preview survives a
  closed Blender and can be cleared from either side (Clear Preview).
- Selection pull from Revit to Blender (`FromRevit` collection), with
  in-place update on re-send.
- Snappable proxy lines in Revit from selected Blender edges, as polylines
  or arcs.
- Bake into Revit as DirectShape or as loadable family (`BL_<name>`), with
  Revit category, replacement on re-bake and Remove Bake.
- "Bake together": one family with all the selected objects, named after
  and placed at the active object; re-baking updates its members.
- Preview colors for faces and edges chosen in the Blender panel, applied
  in Revit immediately; edges drawn in every display style.
- Revit 2024 and 2025, Blender 5.1 and 5.2.
