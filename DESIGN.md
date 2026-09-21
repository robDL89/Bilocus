# Bilocus - Design

Status: preview (Blender -> Revit), selection pull (Revit -> Blender),
snappable proxy lines and bake (DirectShape and family) implemented and
verified on real Revit and Blender processes.

---

## 1. Goal

See in Revit, in real time and without creating elements in the document,
the geometry being modeled in Blender. As a second step, bring selected
portions of the Revit model into Blender as reference context.

Functional equivalent of the Grasshopper preview in Rhino.Inside.Revit, but
with a geometry engine that lives in a separate process.

---

## 2. Why it cannot be done like Rhino.Inside

Rhino.Inside loads RhinoCommon (a .NET library) in the same process as
Revit. Blender is a monolithic C/C++ executable with its own event loop,
embedded Python and a GPL license: it cannot be loaded in-process.

The preview, however, does not require the same process. It only requires
that something, inside Revit, knows how to draw arbitrary triangles in the
view. That something is DirectContext3D, available since 2017 and used by
Dynamo.

---

## 3. Architecture

```
BLENDER (process A)                     REVIT (process B)
----------------------------            ---------------------------------
Python addon                            C# add-in (IExternalApplication)

Panel N-sidebar "Bilocus"           Ribbon tab + DockablePane (status)
Collection "ToRevit"                    TcpListener 127.0.0.1  [bg thread]
handler depsgraph_update_post                    |
  -> detects is_updated_transform                v
  -> queues transform message            ConcurrentQueue<Message>
"Sync" button                                    |
  -> extracts evaluated mesh                     v  ExternalEvent.Raise()
  -> queues geometry message             MessageHandler [main thread]
                                                 |
socket client (Connect)    <----TCP---->         v
  rx/tx thread                            GeometryStore (in RAM)
  queue + bpy.app.timers                          |
  (never bpy.data from a thread)                  v
                                        PreviewServer
                                        : IDirectContext3DServer
                                          -> RenderScene() on every redraw
                                          -> vertex/index buffers reused
                                          -> 3D views only
                                                 |
                                                 v  [phase B]
                                        Bake -> TessellatedShapeBuilder
                                             -> DirectShape
```

No intermediate file. The preview lives only in RAM on the Revit side and
disappears when Revit closes or on disconnection.

---

## 4. Network roles

- **Revit = server.** `TcpListener` on `127.0.0.1`, default port `9877`
  (9876 is the default port of blender-mcp: do not
  reuse it). Starts in `OnStartup`, dies in `OnShutdown`.
- **Blender = client.** Connects only when Connect is pressed: no automatic
  retry, by decision (2026-09-14). If the connection drops, the panel
  status says so ("disconnected: connection interrupted",
  "connection closed by Revit") and pending transforms are discarded.
  Survives a Blender restart without touching Revit; after a Revit restart
  the preview restarts empty and a Sync is needed.
- Only one active connection at a time. A second connection is
  refused with an explicit error message.

---

## 5. Protocol

Binary frame over TCP, little-endian:

```
[uint32]  header length in bytes
[bytes]   JSON header, UTF-8
[uint32]  payload length in bytes (0 if absent)
[bytes]   binary payload
```

Header always present, payload optional. The header's `type` field
determines the semantics.

Two size limits are also part of the wire contract:

| constant | value | meaning |
|---|---|---|
| `MaxHeaderBytes` / `MAX_HEADER_BYTES` | 1048576 (1 MiB) | header beyond this threshold = invalid frame |
| `MaxPayloadBytes` / `MAX_PAYLOAD_BYTES` | 268435456 (256 MiB) | payload beyond this threshold = invalid frame |

Both implementations reject beyond these thresholds **both on write and on
read**. Rejecting only on read would not be enough: the frame would be
written silently and would kill the connection on the other side.

### 5.1 Error contract

Whoever builds a receive loop must distinguish four outcomes, because the
correct response differs in each case.

| outcome | how it manifests | what to do |
|---|---|---|
| **clean close** at a frame boundary | `FrameCodec.Read` returns `null`, `decode_frame` / `decode_message` return `None` | not an error: the peer left, go back to listening. Blender can be restarted N times during a Revit session |
| **length error or invalid UTF-8** | `InvalidDataException` / `DecoderFallbackException` on the C# side, `BridgeFramingError` on the Python side | **terminal: close the socket.** If the rejected length was the payload's, the payload bytes are still in the socket and the stream is permanently desynchronized. The protocol has no delimiters, no realignment exists. A catch/log/continue would read garbage forever |
| **frame truncated** midway | `EndOfStreamException` / `EOFError` | the peer died mid-frame: close and go back to listening |
| **malformed JSON** with intact framing | `BridgeMessageError` from `decode_message` on the Python side; on the C# side `Read` returns the raw string and validation happens downstream | **recoverable:** the stream is still aligned. Reply with an `error` frame and continue |

Hence the separation between `decode_frame` (pure framing, header as an
unparsed string, identical to the C# side) and `decode_message`
(interprets the header as JSON). Merging the two levels would make it
impossible to distinguish "stream lost" from "a message was wrong".

On the Python side the two categories are distinct types, not variants of
the same `ValueError`: `BridgeFramingError` embodies the terminal case,
`BridgeMessageError` the recoverable case, both under the common root
`BridgeError`. The distinction exists on purpose because
`UnicodeDecodeError` is a subclass of `ValueError` in Python, exactly like
`json.JSONDecodeError`: an `except ValueError` written for the recoverable
branch would mistakenly also catch a corrupted UTF-8 header, which is
instead terminal. With two dedicated exceptions the distinction holds up
against an `except` written in good faith.

`FrameCodec.Write` is not atomic: it issues several consecutive writes and
does not own the stream. Whoever owns it must serialize writes with a
lock, and every write path must go through the same lock, including error
and shutdown paths.

This gives a fifth outcome, which is on the write side rather than the
read side and has the same structure as the four above:

| outcome | how it manifests | what to do |
|---|---|---|
| **frame rejected before touching the stream** | `ArgumentException` from `Write` for a header or payload beyond the limits | **recoverable:** not a single byte has gone out, the connection is intact. Discard the frame and continue |
| **write interrupted mid-frame** | any other exception from `Write` or from `Flush` | **terminal: close the socket.** The peer has already read a length prefix and will not receive the promised bytes: it is desynchronized exactly as in the read case |

The distinction is possible only because `Write` validates the lengths
**before** writing to the stream: without that guarantee every write
failure would have to be treated as terminal.

### 5.2 Wire format conventions

- Units: **meters**. Always. No exceptions.
- Axes: **Z-up, right-handed**. No conversion between the two sides.
- Matrices: 16 floats, **row-major**.
- Positions and normals: `float32`. Indices: `uint32` (the 16-bit split is
  an internal Revit detail, not a protocol one).
- Colors: 4 floats 0..1, RGBA.

### 5.3 Blender -> Revit messages

| type | header | payload |
|---|---|---|
| `hello` | `{type, protocol_version, client}` | - |
| `geometry` | `{type, obj_id, name, vert_count, tri_count, color, matrix}` | positions (vert_count*3 f32) + normals (vert_count*3 f32) + indices (tri_count*3 u32) |
| `transform` | `{type, obj_id, matrix}` | - |
| `remove` | `{type, obj_id}` | - |
| `clear` | `{type}` | - |
| `sync_begin` | `{type, obj_ids}` | - |
| `sync_end` | `{type}` | - |
| `proxy_edges` | `{type, obj_id, name, edge_count, arc_count?}` | `edge_count * 6` float32: two endpoints per edge, x y z each; then `arc_count * 9` float32: start, end, mid-angle point per arc |
| `bake_begin` | `{type, obj_ids, target}` | - |
| `bake_mesh` | `{type, obj_id, name, category, matrix, vert_count, face_count, loop_count, tri_count, accept_open}` | positions (vert_count*3 f32) + face_sizes (face_count u32) + face_vertices (loop_count u32) + tri_vertices (tri_count*3 u32) + tri_faces (tri_count u32) |
| `bake_end` | `{type}` | - |
| `bake_remove` | `{type, obj_ids}` | - |

`sync_begin` lists the ids that are about to arrive: it lets Revit remove,
at the end, the objects no longer present in the collection, atomically and
without flicker.

A consequence worth writing down follows: being announced in `sync_begin`
PROTECTS from removal. An object that the Blender side decides to skip
mid-sync (because it exceeds the vertex threshold, or because its payload
is rejected) has already been announced, so `sync_end` does not remove it:
it would remain visible in Revit with the geometry from the previous sync,
which is the worse of the two possible errors. For this reason the Blender
side sends an explicit `remove` for every skipped object, before
`sync_end`.

`obj_id` is a stable identifier of the Blender object, not the name
(the name can change). It is generated as a UUID saved in a custom
property of the object at the first sync.

The id must be treated as identity in every respect: two Blender objects
with the same `obj_id` collapse into a single entry of the `GeometryStore`
and the last one to arrive wins, with no error. This is not an academic
case: `Shift+D` also copies custom properties, so every duplicate is born
with the id of the original. The Blender side breaks the collisions at
every sync.

#### `proxy_edges`

This is the only message in the protocol that **writes to the Revit
document**: the edges selected in Blender become snappable `ModelCurve`
elements (Phase A3). All other messages populate a preview that lives in
RAM.

**World coordinates, in meters.** Unlike `geometry` it carries neither a
`matrix` nor an `origin`, and the difference is intentional: proxies are
one-off creations that must end up where they are seen, not objects the
user will reposition later. There is no notion of a subsequent transform
for them.

`obj_id` is the same stable identifier used by `geometry` and `transform`,
and it is what makes replacement possible: **re-sending the same edges
replaces that object's proxies instead of accumulating new ones**, silently
and without confirmation. `name` is just a label for the summary; if
missing, it falls back to `obj_id`.

These are **content** errors (`error` frame, connection alive): negative
`edge_count`, payload length different from `edge_count * 24` bytes,
non-finite coordinates, `obj_id` empty or containing control characters.
Two more cases each deserve a line:

- **`edge_count` zero is an error, not a removal.** It would have been
  convenient to read it as "I'm sending nothing, nothing remains", but
  that way a defect on the Blender side - a badly read selection - would
  silently delete proxies created on purpose. Removal has its own command,
  with confirmation and an explicit count.
- **`edge_count` above 10000 is an error.** Every edge costs at least two
  document elements, and the protocol's ceiling would let through eleven
  million edges: a wrong selection would block Revit for hours inside a
  transaction, with no way to stop it.

**Sketch planes are cleaned up together with the curves.** Every
`ModelCurve` rests on a `SketchPlane`, and Revit does **not** take it away
when the curve is gone: `Document.Delete` only promises the elements
"totally dependent upon" the one deleted, and the dependency runs the
opposite way - it is the curve that depends on the plane. Measured in the
field: 60 proxies removed, 60 elements deleted, 60 distinct planes left in
the document with nothing on top. Two document elements per edge, half of
which remained on every create-remove cycle, invisible in every view and
every schedule.

Deleting the plane is however **conditional, not assumed**: the plane
passed to `SketchPlane.Create` may not be the one Revit actually attaches
to the curve, and Revit can reuse an existing plane that the user's own
sketches rest on. Before removing a plane, Revit is asked
`Element.GetDependentElements(null)`, which by contract returns the
elements that "will be deleted if the input Element is deleted": if the
set is empty - aside from the element itself, which the documentation does
not say whether it includes and which is therefore always discarded -
nothing else depends on the plane and it is safe to delete it. If it
returns something, the plane holds up something else and is left alone.

The sequence lives in `SketchPlaneCleaner` and applies to **both** paths
that delete proxy curves: explicit removal and replacement inside
`ProxyBuilder`. It runs inside the caller's own transaction, so a
`Ctrl+Z` keeps undoing everything in one go. A cleanup that fails does not
fail the deletion: the curves go away all the same and the summary says
"not measured", because a diagnostic count is not worth the operation the
user asked for.

Failure of the **write** is a different matter and still travels as an
`error` frame, but it originates on the other side of the boundary:
read-only document, a family instead of a project, a transaction already
open, a rejected commit. The router cannot even detect it, because it
never touches the Revit API; creation happens on the main thread, in a
**single** transaction called `Bilocus: create proxy`, so a `Ctrl+Z`
undoes it entirely.

**Edges under Revit's minimum tolerance are skipped and counted**, they do
not fail the request: a real mesh contains some, and a single
zero-length edge must not take down the twenty good ones next to it.

**What the Blender side does before sending.** It reads the edge selection
of the active object - which **persists in Object Mode** and is read from
`mesh.edges[i].select`, so entering Edit Mode is not needed - and brings
it into world coordinates with `matrix_world`. Then it filters, in
`bridge_edges.py`, which does not import `bpy` and is covered by tests:

- **zero-length edges** are removed. This side's threshold is tiny (1e-9 m)
  on purpose: the real tolerance is `Application.ShortCurveTolerance`,
  which only Revit knows, and reproducing it here would mean silently
  discarding edges that Revit would have accepted. The ones that are short
  but not zero are sent, Revit counts them as skipped and reports it back
  with `proxy_result`.
- **duplicates** are removed, **direction included**: `(a,b)` and `(b,a)`
  are the same segment and would produce two overlapping `ModelCurve`
  elements, invisible to the eye and impossible to select separately. The
  first one to arrive survives with its own direction, so two sends of the
  same selection produce the same bytes.
- **non-finite coordinates** are removed along with their edge. A single
  `NaN` would make `ProxyEdgeRequest.Parse` reject the entire message:
  discarding the single bad edge saves the others, the same choice already
  made for degenerate ones.

If nothing remains after filtering, **the message is not sent at all**: an
`edge_count` of zero is a content error, and the user deserves to read "no
usable edge" in Blender instead of an `error` frame from Revit.

**Two modes: Polyline and Arcs and lines** (2026-09-16). The panel chooses
how the edges become lines. *Polyline* is the behavior described so far,
one edge one line. *Arcs and lines* is meant for tracing beams with Pick
Lines over curved paths: the selected chains of edges are rewritten in
`bridge_fit.py` (without `bpy`, covered by tests) with lines and arcs
within a **maximum deviation from the original vertices**, in millimeters.

- **On the wire** arcs travel after the edges in the same payload, nine
  float32 each: start, end, mid-angle point, the order of
  `Arc.Create(end0, end1, pointOnArc)`. `arc_count` is **optional** and
  travels only if positive: in Polyline mode the header and payload
  remain byte-for-byte identical. With arcs, `edge_count` can be zero (a
  closed circle is arcs only); the total cannot, and the 10000 ceiling
  applies to edges plus arcs.
- **The midpoint is the one at mid-angle on the fitted circle**, not a mesh
  vertex: the vertices lie on the arc only within tolerance.
- **The endpoints of every segment are original vertices.** The chains
  stay connected to each other and to the other proxies; tangency between
  consecutive segments is not imposed.
- **Chains break at every node that does not have exactly two edges**
  (endpoints, crossings, branches). An arc never crosses a junction.
- **Arcs only lie on two planes for their chord** (2026-09-16, after the
  beam test). A beam taken with Pick Lines orients its section on the
  curve's sketch plane, and an arc on a free plane follows the osculating
  plane: measured, a 114 m radius arc on a descending stretch had its
  plane tilted 40 degrees and the beam rolled. Allowed planes: *plan*,
  which contains the chord and the horizontal perpendicular to it (tilted
  only by the chord's slope), and *section*, which contains the chord and
  the vertical. The circle passes through the endpoints and through the
  projection onto the plane of the middle vertex; the deviation is
  measured in 3D. A curve that descends while turning becomes more arcs,
  none of them rolled. Known limit: an arc on a plane tilted around the
  chord is only recognized if the chord coincides, because the greedy
  advance tries short chords first. The arc on a free plane is a possible
  future third option.
- **Arcs of at most half a turn and a radius of at most 1000 m.** Beyond
  that, three nearly aligned points give a numerically fragile center and
  the line wins.
- **Greedy advance**: from each vertex the segment that reaches farthest,
  ties won by the line. Where neither one holds, the original segment
  remains.
- **A deviation of 0 becomes a minimum of 0.1 mm, and the user reads it**
  in the panel and in the operator's report. A literal zero would give
  Polyline, because with floats no vertex sits exactly on a line or on a
  circle.
- **On the Revit side every arc has its own sketch plane** with the normal
  of `arc.Normal` oriented by `ProxyPlaneNormal.Orient`: upward, or
  positive X if the plane is vertical, and positive Y as a tiebreaker.
  `arc.Normal` depends on the direction of travel, and the arcs of an S
  had half their normals pointing down. **Lines too** now use the plan
  plane (`ProxyPlaneNormal.ForLine`); `PerpendicularVector` remains only
  for vertical lines, where the plan plane does not exist. This also
  applies in Polyline mode. An arc with two points within
  `ShortCurveTolerance` is skipped and counted like short lines; a
  rejected `Arc.Create` (three points aligned after float32) is counted
  among the failed ones. Replacement, marking and plane cleanup are the
  same regardless of mode: re-sending in Arcs and lines an object sent in
  Polyline replaces, it does not add.
- **Only lines and arcs, no splines.** NURBS from Blender to Revit is
  separate work, deferred.

**Creation is refused in Edit Mode.** The flags in `mesh.edges` are those
from the last time Edit Mode was exited, not the current ones: the real
selection lives in the editing BMesh. Sending those would mean creating
real elements in a project file from a stale selection, which is exactly
the kind of error that is not noticed right away. Same choice already made
for Sync in Phase A, for the same reason.

#### `bake_begin` / `bake_mesh` / `bake_end` / `bake_remove`

The Phase B bake: the objects **selected** in Blender that are also in
`ToRevit` become `DirectShape` elements in the Revit document. The golden
vector for the payload lives in the tests.

**A batch, not one message per object.** `bake_begin` announces the ids,
`bake_mesh` carries one object, `bake_end` puts the batch into execution.
Revit only writes when `bake_end` arrives, in an assimilated
`TransactionGroup`: a `Ctrl+Z` undoes the entire bake, and a batch left
open by a disconnection has written nothing. A new `bake_begin` discards
the open batch and counts it in the Status.

**Local coordinates and `matrix`**, like `geometry` and unlike
`proxy_edges`: the same payload will serve the Phase B2 family, where the
geometry sits in the family's system and the instance carries the
position. For the `DirectShape`, Revit applies the matrix to the vertices;
with a negative determinant (mirrored scale) it reverses the loop order,
otherwise the normals come out flipped.

**Polygons, not triangles.** Blender sends the evaluated mesh's polygons
and, alongside them, the triangles it splits them into itself
(`loop_triangles`, with `polygon_index`). Planarity is decided by
**Revit**, polygon by polygon, with a threshold derived from
`Application.VertexTolerance`: a planar polygon becomes a whole
`TessellatedFace` (a quad with no diagonal), a non-planar one falls back
to ITS OWN Blender triangles. Same principle as the proxy tolerance: only
Revit knows the real threshold. Triangles are made by Blender and not
Revit because Blender correctly triangulates even concave n-gons.

**Re-bake = replacement.** The `DirectShape` is found again via the
Extensible Storage mark of the proxies (same schema, filtered by class).
Same category: `SetShape`, same ElementId. Category changed: delete and
recreate, reported as `recreated`. Multiple elements with the same
`obj_id` (a copy made in Revit carries the mark along): the object fails
with a clear message, the bridge does not pick at random which one to
update.

**Category per object**, a `BuiltInCategory` name (`OST_GenericModel` by
default). The format is checked on the wire; whether the category is
valid for a `DirectShape` is decided by `DirectShape.IsValidCategoryId`,
and a rejection fails only that object.

**Two modes, `target` on the batch** (Phase B2): `"directshape"` (default
if absent) or `"family"`. In family mode each object becomes a loadable
family `BL_<name>` with a `FreeFormElement`, created from
`English\Metric Generic Model.rft`, loaded without saving and with an
instance on the nearest level below the origin. Revit decomposes the
matrix (`FamilyPlacement`): translation and rotation around Z go to the
instance, tilts and scale go into the geometry. Re-bake does `EditFamily`
-> `UpdateSolidGeometry` -> `LoadFamily` with overwrite, so manually made
voids remain; the bridge's instance is moved only if there is a single
one (otherwise `not_moved`). `accept_open` per object allows the `Sheet`
outcome (open mesh); `Mesh`/`Mixed` do not yield a `Solid` and stay as
DirectShape only. An object has in Revit either a DirectShape or a
family: baking in the other mode removes the previous one (`switched`).
At most 50 objects per family batch.

`bake_remove` deletes all `DirectShape` elements, families and instances
marked with those ids, copies included: it is the explicit request to
remove what comes from those objects. No automatic removal: deleting an
object in Blender deletes nothing in Revit.

### 5.4 Revit -> Blender messages

| type | header | payload |
|---|---|---|
| `hello_ack` | `{type, protocol_version, revit_version, doc_title}` | - |
| `revit_batch_begin` | `{type, count}` | - |
| `revit_geometry` | `{type, element_id, name, category, type_name, vert_count, tri_count, origin, color}` | positions (vert_count*3 f32) + normals (vert_count*3 f32) + indices (tri_count*3 u32) |
| `revit_batch_end` | `{type}` | - |
| `proxy_result` | `{type, obj_id, name, ok, requested, created, arcs, replaced, skipped, failed, planes_deleted, planes_kept, message}` | - |
| `bake_result` | `{type, action, ok, requested, created, replaced, recreated, removed, failed, missing, as_mesh, faces_planar, faces_triangulated, target, switched, not_moved, message}` | - |
| `error` | `{type, message}` | - |

#### `proxy_result`

The response to `proxy_edges`, and the only message that reports the
outcome of a **write** to the Revit document.

It exists to close a gap left open on purpose. Proxy creation runs on an
`ExternalEvent` triggered from the network and opens **no** `TaskDialog`
at all: a modal popping up while the user is in the middle of a Revit
command is worse than the problem it would report. The consequence,
though, is that on success whoever pressed the button - which is on the
Blender side - would receive nothing, and the only message to reach them
would be the `error` frame on failure. A button that only speaks when
things go wrong is a button nobody trusts.

What travels are the **counts**, not the text summary from
`BuildSummaryText`: that text is multi-line, talks about sketch planes and
milliseconds, and is written for the Status button inside Revit. The
Blender side formats its own panel line from the numbers, the same way it
already does for the pull. Only `message` travels on the wire, carrying
the reason for failure and empty when `ok` is true.

The counts are read **strictly** on the Blender side, unlike `color`: a
missing field is a content error, not a zero. A deduced zero would say
"it created nothing" when in fact it is not known, and that is the
difference between a message discarded with a console line and a lie in
the panel. This also applies to `planes_deleted` and `planes_kept`, which
are zero when there was no replacement but can never be absent.

`planes_deleted` and `planes_kept` refer to the **sketch planes** of the
replaced proxies: how many were cleaned up and how many were left because
something else still rests on them. They travel because replacement is
the path the user uses the most - resending the edges many times for
every "Remove proxy" - and it opens no dialog in Revit: without these two
numbers, the only place one would see whether the cleanup is working would
be on the other side of the bridge from whoever pressed the button.

`requested` counts the segments (edges plus arcs) and `created` the curves
created, arcs included; `arcs` says how many of these are arcs. **`arcs`
is the only exception to strict reading**: the Blender side treats it as
optional and absent means zero, because an earlier Revit add-in does not
send it and for that add-in arcs really are zero. When present, the rule
for the other counts applies.

**A failure produces two frames**, `proxy_result` with `ok` false and the
`error` frame already covered in 5.3. This is not an oversight: the first
feeds the last-proxy line in the panel, the second the connection status
line, which is the place where all of Revit's rejections are watched
regardless of which command caused them.

**Positions travel in coordinates LOCAL to the element**, and `origin`
(three floats, meters, world coordinates) says where the element is. The
`GeometryInstance` objects are flattened on the Revit side regardless:
there is no sensible local rotation left to send, so `origin` is a
translation and not a full matrix.

`origin` is the **center of the bounding box** of the element, computed
during tessellation by `TessellatedMesh.ComputeOrigin`; the vertices are
translated by `-origin` before being serialized. Center of the bounding
box and not centroid of the vertices: a tessellated wall has many more
vertices around a door than along the rest of its length, and a centroid
would follow the density of the tessellation instead of the extent.

**Element with no vertices**: it has no bounding box, and `origin` is
`(0,0,0)`. This is declared but never reaches the wire, because a mesh
with no vertices is empty and `SendSelectionCommand` skips it before
building the header.

The field is **always** written, even when it is zero, and is
**mandatory on read**: a missing or malformed `origin` is a **content**
error (`BridgeMessageError`), not a framing one. Unlike `color`, which is
cosmetic and falls back to a default gray, a wrong origin would place the
element in the wrong spot, which for reference geometry is the worst
error. Even a non-finite component is rejected: a `NaN` in `obj.location`
would make the object disappear from the viewport without a single
message.

**Why local and not world.** With vertices in world coordinates the
Blender object was born with an identity matrix, i.e. with its origin at
`(0,0,0)`. Rotating it made it rotate **around the scene origin** instead
of on itself: a wall thirty meters from the origin, rotated fifteen
degrees, ended up very far away; scaled, it migrated instead of growing.
There was also a **double transform on update**: if the user moved the
object and then it was resent from Revit, the absolute vertices would be
re-transformed by the user's matrix. With local coordinates the object's
origin sits on the element and replacing the mesh no longer moves anything
on its own.

The payload is **identical in both directions**: same order, same units,
same writer and same parser. In the Revit -> Blender direction the
normals are in fact redundant, because the tessellation emits
non-indexed triangles and Blender would recompute the same normals
itself. They are sent anyway, and the choice is deliberate: two wire
formats would mean two writers, two parsers, two test suites and a new
chance for the two sides to diverge. On a manual path limited to the
selection, a third more bytes go unnoticed; a divergence between the two
formats would be noticed late and badly.

Every object created in Blender carries `element_id`, `category` and
`type_name` as custom properties (`revit_element_id`, `revit_category`,
`revit_type_name`). They will be needed for a possible future round-trip.

`revit_element_id` is a **string**, not an integer. Blender's integer
custom properties are 32-bit, while `ElementId.Value` in Revit 2024+ is a
`long`: an id beyond 2^31 would make the assignment fail, i.e. the
element's import. The string also removes the ambiguity between `348219`
and `348219.0`, which `json.loads` can produce depending on how the
number is written on the wire.

Objects arrive in the `FromRevit` collection, a twin of `ToRevit` but with
three rules of its own:

- **on creation the object receives `obj.location = origin`.** This way it
  has its origin on the element and rotates and scales on itself like any
  other Blender object.
- **the update is in place.** An already imported element is found again
  by `revit_element_id` and only its mesh datablock is replaced: position,
  collections chosen by the user and added modifiers remain. The old mesh
  is removed from `bpy.data.meshes` if it has no other users, otherwise an
  orphan one would pile up on every repeated pull.
- **on update the user's position is respected**, unless the scene toggle
  "Reset position on pull"
  (`bilocus_reset_location_on_pull`, **off by default**) is active: then
  the object goes back to `origin` on every pull. The default is this way
  because in the normal flow the user moves the element aside to model
  against it with room around it, and snapping it back to place on every
  pull would punish that; the toggle exists because a reference that lies
  about its position is dangerous when aligning geometry that later goes
  back into Revit.

  Consequence worth knowing: **with the toggle off, an element moved in
  Revit does not move the Blender object**, it only changes its mesh. This
  is the correct behavior for the chosen default, and it is still better
  than before: with absolute coordinates the object would have drifted.
- **the object is searched across all of `bpy.data.objects`**, not only
  inside `FromRevit`: whoever moved it to another collection must not find
  a duplicate of it on the next pull.
- **`FromRevit` is not cleared at the start of a batch.** A subsequent pull
  updates what it recognizes and adds what is new; objects from previous
  pulls remain.

**Interrupted batch.** A batch can be left open: `SendSelectionCommand` can
abort midway, and even its single final attempt at `revit_batch_end` can
fail to go out. On the Blender side the batch is closed as interrupted in
three cases: on arrival of a `revit_batch_begin` with the previous one
still open, on connection loss, and after 15 s of silence on a live
connection. In all three cases **the objects already imported remain in
the scene**: they are geometry that arrived in full, and what is missing
is only the closing message. The accounting is closed and the panel
reports the pull as partial. A `revit_geometry` arriving without a
`revit_batch_begin` opens an implicit batch instead of being discarded:
same choice, a lost header should not cost a wall.

#### `bake_result`

The response to `bake_end` (`action` = `"bake"`) and to `bake_remove`
(`action` = `"remove"`). Same role and same rules as `proxy_result`: no
`TaskDialog` in Revit, counts always present and read strictly on the
Blender side, a failure also produces the `error` frame.

`ok` says whether the write to the document succeeded. An object that
fails inside an otherwise successful bake does not make it false: it ends
up in `failed` and `message` carries the reason for the last one that
failed. `as_mesh` counts the objects that came out of the builder as a
mesh rather than a solid (open mesh or non-manifold). `faces_planar` and
`faces_triangulated` say how many polygons arrived whole and how many
fell back to triangles. `target` repeats the batch's mode (`"all"` for
removal, a pair read strictly on the Blender side); `switched` counts the
elements of the other mode removed, `not_moved` the family instances not
moved because they are duplicated in Revit. `removed` and `switched`
count only DirectShape elements, families and instances, not types and
annotations that Revit deletes along with them.

### 5.5 What the golden vectors fix, and what they do not

`tests/vectors/frames.json` is the executable contract of the wire format:
both suites read it and the `constants` block fails the other side's tests
if someone changes a value on only one side.

The vectors deliberately fix only the string -> bytes step, never
dict -> string. JSON serialization on the two sides does NOT produce the
same bytes for the same logical header: Python's `json.dumps` uses
`ensure_ascii=True` and turns every non-ASCII character into the `\uXXXX`
escape sequence, while `System.Text.Json` applies a different escaping.
Key order is not guaranteed identical either.

This is not a bug: the protocol carries JSON, and two JSON payloads that
differ byte for byte can be the same document. But whoever one day builds
a hash, a signature or a dedup on the frame's bytes must know this: those
bytes are not canonical between the two sides. Dedup must be done on the
parsed header's fields, not on the raw bytes.

---

## 6. Components

### 6.1 Blender side - addon

- `panel` - N-sidebar panel, "Bilocus" tab: connection status,
  Connect/Disconnect, Sync button, object and triangle counts.
- `collection` - management of the `ToRevit` collection: creation if
  missing, object queries, stable ids.
- `extract` - mesh extraction from the evaluated object: `loop_triangles`,
  `foreach_get` on preallocated arrays (never a Python loop over
  vertices), normals, color from the viewport display color.
- `client` - socket, rx/tx thread, queues, manual connection.
- `handlers` - `depsgraph_update_post` for the live transform, throttled
  to ~30Hz via `bpy.app.timers`.
- `bridge_bake` (pure) and `bridge_bake_send` (bpy) - bake as DirectShape:
  category per object, polygon packing, panel section, outcome. Phase B.

### 6.2 Revit side - C# add-in

- `App` - `IExternalApplication`. Starts the listener, registers the DC3D
  server, builds the ribbon and the dockable pane.
- `BridgeServer` - background-thread TCP listener. Frame parsing,
  queuing, no calls to the Revit API. The rule is not left to discipline:
  the file has no `using Autodesk.*` at all and the `Bilocus.Revit.Net.Tests`
  test project compiles it as a linked source, with no references to the
  Revit API. Adding a call into Revit there breaks the test build.
- `IEventRaiser` / `ExternalEventRaiser` - the sole point of contact
  between the socket thread and Revit. The server calls
  `IEventRaiser.Raise()`, in production the adapter forwards to
  `ExternalEvent.Raise()`, the only API method designed to be called from
  any thread. In tests the implementation is a counter, which is why the
  server is testable without opening Revit.
- `MessageQueue` - lock-protected `Frame` queue. The socket thread
  enqueues, the `ExternalEvent` drains.
- `MessageHandler` - `IExternalEventHandler`. Drains the queue on the main
  thread and updates the GeometryStore.
- `GeometryStore` - `obj_id -> MeshData` dictionary. Holds the GPU buffers
  and the current matrix. Single source of truth for the preview.
- `PreviewServer` - `IDirectContext3DServer`. `CanExecute` limited to
  `View3D`, `UsesHandles` = false, `RenderScene` reuses the buffers.
- `SendSelectionCommand` - ribbon command: reads the selection,
  tessellates, sends. Phase A2.
- `Bake/` - Phase B. `BakeBatch`, `BakeMeshRequest`, `BakeResult`,
  `BakeCategory` are pure and compiled by the tests; `BakeBuilder`
  (transactions, `TessellatedShapeBuilder`, `DirectShape`) and
  `BakeRemover` touch the API and are called by `MessageHandler` after
  draining, like `ProxyBuilder`. The pure geometry (payload, matrix,
  planarity, faces) lives in `Bilocus.Geometry`. No ribbon command: the
  bake is commanded from Blender.

---

## 7. Phases

### Phase A - Blender -> Revit preview
Live transform, geometry on manual Sync. 3D views only. No bake.
Success criterion: move a cube in Blender, it moves in Revit with no
perceptible lag; sculpt it, press Sync, the new shape appears.

### Phase A2 - Revit -> Blender selection pull
"Send Selection to Blender" button. Selection only, never the model.

### Phase B - bake as DirectShape
`TessellatedShapeBuilder` -> `DirectShape`, one element per Blender object
selected in `ToRevit`, category per object (default Generic Models), quads
and planar n-gons preserved, no material. Ownership mark to replace,
remove and avoid re-exporting to Blender what the bridge generated
(feedback loop prevention).

### Phase B2 - bake as family
`FreeFormElement` in a family from `Metric Generic Model.rft` (`English`
folder), loadable category per object, instance on the nearest level,
re-bake that updates the geometry while preserving manually made voids.
`FamilyBaker` opens a `TransactionGroup` around the batch. Verified in
Revit on 2026-09-14 that `LoadFamily`/`EditFamily` allow it: a family bake
is undone with a single `Ctrl+Z`. The fallback with no group for the rest
of the session (more undo entries, written to the Status) stays in the
code as a safety net, in case Revit refused the group.
Every family document closes with `Close(false)` in a `finally`; at the
end of the batch `FamilyDocumentLeaks` closes any left open by the bake
(an invisible document would block re-bake with a misleading "close the
editor") and writes it to the Note. It never touches the user's own
documents.

### Phase C - closed with no work (2026-09-14)
All items were removed or set aside by Roberto. The preview remains
limited to 3D views only: for plan and elevation an orthographic 3D view
is used. The origin remains Revit's internal origin for all channels,
with neither Project Base Point nor Survey Point. No auto-sync of
geometry. Materials and textures set aside.

---

## 8. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| ~~Unconfirmed 16-bit limit on index buffers~~ **RESOLVED 2026-09-06** | - | Measured: indices are 16-bit, the maximum is 65536 vertices per buffer. Chunking is needed. |
| Exceeding the limit raises no exceptions | **high**: Revit silently truncates and draws the wrong geometry | validate the vertex count BEFORE building the buffers, never rely on a try/catch |
| Blender crash from accessing bpy.data off-thread | high | absolute rule: only queue + timer |
| Revit freeze from too-frequent messages | medium | throttling on the Blender side, coalescing of transforms on the same obj_id |
| Very dense meshes make Sync slow | medium | measure; optional decimation if real usage calls for it |
| Feedback loop after the bake | high but only from phase B | ownership tag on generated elements |
| Revit 2024 (net48) and 2025 (net8) diverge | low | identical DC3D API; multi-target net48 / net8.0-windows |
