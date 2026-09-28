# Changelog

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
