# Family dimension parameters - Design

Date: 2026-10-12
Status: approved in brainstorming, not yet implemented.

## 1. Goal

A shape modeled in Blender and baked as a family reaches Revit as a mute
solid. After this change the family carries a few rough dimensions as
family parameters with the `BL_` prefix, readable on the type in Revit:
length, width, height, thickness, area, volume.

The values are **informative**: they do not drive the geometry (a
`FreeFormElement` cannot flex), and they are rewritten at every bake from
Blender, which stays the source of truth. Whoever reshapes the object must
reassign the overridden values. Scheduling them is up to the user, who can
map them to shared parameters: Bilocus creates plain family parameters.

Out of scope: DirectShape (it has no family parameters), shared
parameters, instance parameters, parameters that drive geometry.

## 2. The parameters

All type parameters, group *Dimensions*, sent in SI units (m, m2, m3) and
converted by Revit to internal units; the project shows them in its own
units.

| Parameter | Automatic value | Override |
|---|---|---|
| `BL_Length` | larger of `obj.dimensions` X, Y | sum of the selected edges |
| `BL_Width` | smaller of `obj.dimensions` X, Y | sum of the selected edges |
| `BL_Height` | `obj.dimensions` Z | sum of the selected edges |
| `BL_Thickness` | none: exists only when overridden | sum of the selected edges |
| `BL_Area` | total surface of the evaluated mesh | sum of the selected faces |
| `BL_Volume` | volume of the evaluated mesh, only if closed (every edge manifold); otherwise not sent | not overridable |

`obj.dimensions` is Blender's own bounding box of the evaluated geometry
(modifiers included) in local axes times the object scale: the same X, Y, Z
the user reads in the N panel. Length and width are ordered by size so that
"length" stays the long side whichever local axis it lies on. Area and
volume are measured on the evaluated mesh with the object's rotation and
scale applied (no translation). Overridden edge and face measures are taken
in world scale too.

With **Bake together** the parameters are the active object's: it already
names, places and identifies the family, and the typical members are LODs
of the same shape, whose volumes must not be summed.

## 3. Blender side

- Overrides are frozen values in meters, m2: object custom properties
  `bilocus_param_<name>` (e.g. `bilocus_param_length`). Not recomputed when
  the mesh changes.
- Panel, Bake section, box *Family parameters* for the active object: one
  row per parameter with the value that will be sent, "auto" or "set",
  a button that sets it from the Edit Mode selection (edges for lengths,
  faces for area) and an X that clears the override. Volume shows its value
  or "open mesh".
- Pure logic (no bpy) in `bridge_bake.py`: the parameter list from
  dimensions, area, volume and overrides; the `params` header field.

## 4. Protocol

`bake_mesh` gets an optional `params` field: an array of
`{name, kind, value}`, `kind` one of `length`, `area`, `volume`, `value` a
finite number >= 0 in SI units, `name` starting with `BL_`. Absent means
no parameters. The DirectShape bake ignores it; an older Revit add-in
ignores it too. A malformed `params` is a content error for that object.

## 5. Revit side

- `BakeMeshRequest` parses and validates `params` (pure, tested).
- In the family document, inside the family transaction of both creation
  and re-bake: ensure a current family type, add the missing `BL_`
  parameters (`FamilyManager.AddParameter`, type parameter, group
  Dimensions, spec Length/Area/Volume), set their values in internal units
  (`UnitUtils.ConvertToInternalUnits`), and **delete the `BL_` parameters
  that did not arrive** (an override cleared, a mesh that opened): stale
  data is worse than missing data. Parameters without the `BL_` prefix are
  never touched.
- Re-bake reloads with `overwriteParameterValues = false` on purpose (the
  user's own values on project types stay): so after the load the `BL_`
  values are also written on the family's type in the project, and only
  those.

## 6. Testing

- pytest: ordering of length and width, sums, volume only when closed,
  override vs automatic, Thickness only when set, `params` in the header
  and its validation.
- xUnit: `params` parsing and validation in `BakeMeshRequest`.
- Live via MCP: bake a family, read the `BL_` values on the type; override
  thickness from an edge, re-bake, read it; clear it, re-bake, check it is
  gone; open mesh -> no `BL_Volume`.
- Docs: DESIGN.md (`bake_mesh` row and paragraph), README (informative
  values, family only), CHANGELOG.
