# Revit -> Blender mesh instancing - Design

Date: 2026-10-04
Status: approved in brainstorming, not yet implemented.

## 1. Goal

Today every pulled Revit element becomes a Blender object with its own
mesh datablock. Ten identical windows are ten unrelated meshes: texturing,
editing or replacing a placeholder with a real asset has to be done ten
times.

After this change, family instances whose geometry is identical share one
mesh datablock in Blender (linked data, like Alt+D). Work done on one -
edit mode, UVs, materials, a Ctrl+L Link Object Data from an asset -
shows on all of them, like an FBX export from Revit.

The target workflows are pull -> model new geometry against it -> bake,
and pull -> edit -> render. Pull -> edit -> push back is rare and is not
designed for: bake must keep working on objects with shared meshes (it
already extracts the evaluated mesh per object), nothing more.

Out of scope: collection instances (multi-object assets are joined with
Ctrl+J first), caching tessellation per symbol on the Revit side,
materials and textures from Revit.

## 2. Revit side

### 2.1 When an element is instanceable

An element is instanceable when its top-level geometry is **exactly one
`GeometryInstance`** and nothing else that produces triangles. Everything
else - walls, floors, family instances cut or joined (which Revit already
returns as plain solids), mixed elements - takes the current flat path,
unchanged. When in doubt, flat.

### 2.2 What is computed for an instanceable element

- The symbol geometry (`GetSymbolGeometry()`), tessellated in the
  family's local space, nested `GeometryInstance` flattened into symbol
  space, converted to meters.
- `mesh_key`: a hash of the positions quantized to 0.01 mm, so float noise
  does not split identical geometry. Two instances with identical symbol
  geometry get the same key by construction; instance parameters that
  change the shape produce a different key without any heuristic.
- `matrix`: `instance.Transform` as a 4x4 matrix in meters, same
  convention as the existing `matrix` fields (16 floats, row-major, 5.2 of
  DESIGN.md). Rotation and mirroring included: a mirrored instance has a
  negative determinant and becomes a negative scale in Blender.

The object's origin is therefore the **family insertion point**, not the
bounding box center used by flat elements.

Quantization, hashing and matrix conversion live in a file with no Revit
API references, linked into the test project like `TessellatedMesh`.

Revit still tessellates once per instance (the hash needs it). The saving
is on the wire and in Blender memory. A per-symbol tessellation cache is
added only if a real model shows the Revit side is the bottleneck.

## 3. Protocol (version 2)

| type | header | payload |
|---|---|---|
| `revit_mesh` (new) | `{type, mesh_key, vert_count, tri_count}` | positions + normals + indices, same as `revit_geometry` |
| `revit_instance` (new) | `{type, element_id, name, category, type_name, mesh_key, matrix, color}` | - |
| `revit_geometry` | unchanged | unchanged |

- Each `revit_mesh` is sent **once per batch**, before the first
  `revit_instance` that uses it. Revit does not know what Blender already
  has and does not try to guess: Blender decides (section 4).
- `revit_batch_begin.count` keeps counting **elements**
  (`revit_geometry` + `revit_instance`), never meshes.
- `mesh_key` is a non-empty string. `matrix` is read **strictly**, like
  `origin`: 16 finite floats with a non-zero determinant, otherwise a
  content error (`BridgeMessageError`).
- `ProtocolVersion` / `PROTOCOL_VERSION` go from 1 to 2. The hello check
  is already strict equality, so a mismatched pair fails with a clear
  message. Both components ship in the same release.
- Golden vectors in `tests/vectors/frames.json` for both new messages.

## 4. Blender side

### 4.1 Marks

- On a **mesh** created by the bridge: `bilocus_mesh_key` (the key) and
  `bilocus_mesh_sig` (signature of the geometry at pull time: hash of
  `vertices.co` read with `foreach_get`, plus the face count).
- On an **object** pulled as an instance: the current properties plus
  `revit_mesh_key`.

### 4.2 Where the mesh of a key lives

There is no separate registry. The mesh of key K is the one used by the
objects with `revit_mesh_key == K`, searched in `bpy.data.objects` like
`find_object` already does. If siblings use different meshes (some were
replaced, some not), the mesh used by **the most objects** wins.

### 4.3 Touched mesh (rule C)

A mesh is touched when any of these holds:

1. it has no `bilocus_mesh_key` (replaced by an asset via Ctrl+L);
2. its current signature differs from `bilocus_mesh_sig` (edited);
3. an object using it is in edit mode right now (data not flushed yet:
   treated as touched).

UVs and materials are not part of the signature: texturing is not
touching. When an untouched mesh is replaced on pull, the material slots
it had are carried over to the new mesh; UVs are not (the geometry is
new).

### 4.4 Decision table on `revit_instance` (element E, key K)

| Situation | Mesh | Transform |
|---|---|---|
| E does not exist | mesh of K: siblings' if any (even touched - this is how an 11th window arrives with the asset), otherwise built from the batch's `revit_mesh` | `matrix_world = matrix` |
| E exists, was an instance, mesh touched | kept; `revit_mesh_key = K` | current rule: kept unless "Reset position on pull" is on, then `matrix` |
| E exists, was an instance, mesh untouched | switched to the mesh of K (reuse or create) | same as above |
| E exists, was flat (v1 pull or previously cut) | mesh of K | `matrix_world = matrix` **always**: the local frame changed (bbox center -> insertion point + rotation), keeping the old location would silently offset the geometry |

On `revit_geometry` for an object that was an instance (for example it
was cut in Revit): flat mesh replaces the shared one **even if touched**,
and the transform is reset to `origin` with identity rotation and scale.
The element no longer has the shared geometry, and showing it would be a
lie. This is the only case where user work on that object is lost; the
pull summary in the panel reports it.

The decision table is a pure function (inputs: exists, was instance,
touched, reset toggle -> actions), tested outside Blender.

### 4.5 Cleanup

- A bridge mesh (has `bilocus_mesh_key`) left with `users == 0` is
  removed, as today. A mesh without the mark is never removed by the
  bridge.
- Received `revit_mesh` payloads do **not** become datablocks on arrival:
  they stay in a per-batch Python dict and a mesh is created only when the
  first instance uses it. An interrupted batch leaves no orphan meshes.
  The dict is cleared when the batch closes, in all three interruption
  cases already handled.

### 4.6 Unchanged

Shift+D copies are handled by `pick_pulled` as today (Alt+D copies
already share the mesh, fine). Bake extracts the evaluated mesh per
object. `FromRevit` collection rules unchanged.

## 5. Errors

Content errors per 5.1 of DESIGN.md: message discarded, console line,
batch continues.

- `revit_instance` whose `mesh_key` neither arrived in the batch nor has
  siblings in the scene: counted as a failure (`record_failure`), pull
  reported as partial.
- Malformed `revit_mesh`: discarded; the instances that reference it fail
  as above.
- Malformed `matrix` or `mesh_key`: content error.

## 6. Tests

- **C# pure**: quantization and hash (same geometry -> same key, noise
  below 0.01 mm -> same key, different geometry -> different key), matrix
  conversion (feet -> meters, mirrored instance -> negative determinant).
- **Golden vectors** for `revit_mesh` and `revit_instance`.
- **Python pure**: strict header readers, mesh signature, decision table.
- **Blender smoke**: three instances with one key share one datablock;
  edited mesh survives a re-pull; Ctrl+L to an asset, then a new element
  arrives with the asset; flat <-> instance transitions reset the
  transform.
- **Manual in Revit**: repeated windows, a mirrored instance, an instance
  cut by a void (must arrive flat), a nested family.

## 7. Documentation

On implementation, DESIGN.md 5.4 is updated: the two new messages, origin
semantics for instances (insertion point + full matrix) next to the flat
ones (bbox center + translation), and the touched-mesh rule.
