# Changelog

## Unreleased

- New: **Revit 2026 and 2027.** One zip per Revit version, 2024 to 2027.
  Revit 2027 is built for .NET 10.
- New: **Blender 4.2 LTS and 4.5 LTS.** The add-on now installs on
  Blender 4.2 and later; CI runs the smoke tests on 4.2, 4.5, 5.1 and 5.2.
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
