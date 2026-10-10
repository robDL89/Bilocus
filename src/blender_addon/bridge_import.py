# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# The FromRevit collection and the creation of objects imported from Revit.
#
# This module touches bpy: everything that can be decided without Blender
# lives in bridge_receive.py (grouping, index checking, header reading,
# batch bookkeeping) and is covered by the tests. Only the bpy calls stay
# here, as much in a row and as logic-free as possible.
#
# Everything in this file runs on the MAIN THREAD, inside the queue drain in
# __init__.py. The socket thread never calls anything in this file. See
# DESIGN.md 5.4.

from array import array

import bmesh
import bpy
from mathutils import Matrix

import bridge_receive as receive
from bridge_protocol import BridgeMessageError

# ASCII mandatory, like for ToRevit: the name ends up in logs and in the panel.
COLLECTION_NAME = "FromRevit"

# Custom properties linking the Blender object to the Revit element. The
# first one is also the key used to find the object again on the next pull:
# it is a string, for the reason written in receive.element_id_key.
ID_PROPERTY = "revit_element_id"
CATEGORY_PROPERTY = "revit_category"
TYPE_PROPERTY = "revit_type_name"

# The object's name as the last pull left it: tells the pulled object from
# its Shift+D copies, see receive.pick_pulled.
PULL_NAME_PROPERTY = "revit_pull_name"

# Instancing (spec 2026-10-04). On the OBJECT: the key of the Revit geometry
# it shows, set for every object pulled as an instance and removed when the
# element arrives flat again. On the MESH: the key it was built from (shared
# meshes only), and the signature of its geometry at pull time, on EVERY
# mesh the bridge builds - that is also how the bridge tells its own meshes
# from the user's when cleaning up.
MESH_KEY_PROPERTY = "revit_mesh_key"
MESH_KEY_MARK = "bilocus_mesh_key"
MESH_SIG_MARK = "bilocus_mesh_sig"

# Revit triangulates face by face and the payload carries non-indexed
# triangles: every triangle arrives with its own three vertices. Merging
# the coincident ones gives a connected mesh (edge loops, bevel, select
# linked work). In meters: 0.005 mm, far below any modeled detail.
WELD_DISTANCE = 0.000005

# Scene property for the "Reset transform on pull" checkbox. Lives on the
# Scene and not in the module because it is a FILE preference: whoever
# aligns geometry against a reference wants it on for that whole project,
# not for one session.
RESET_LOCATION_PROPERTY = "bilocus_reset_location_on_pull"

# State of the last pull, read by the panel. Lives in the module and not in
# the scene: it is session information, it must not be saved in the .blend.
# Same choice as sync.LAST_SYNC.
LAST_PULL = receive.BatchState()


# --- scene properties ----------------------------------------------------

def register_properties():
    setattr(bpy.types.Scene, RESET_LOCATION_PROPERTY, bpy.props.BoolProperty(
        name="Reset transform on pull",
        description="If enabled, on every pull the object goes back to where "
                    "it is in Revit and loses rotation and scale. If disabled "
                    "it stays where you put it",
        default=False))


def unregister_properties():
    if hasattr(bpy.types.Scene, RESET_LOCATION_PROPERTY):
        delattr(bpy.types.Scene, RESET_LOCATION_PROPERTY)


def reset_location_enabled(scene):
    """Whether the update must bring the object back to its Revit origin.

    DEFAULT DISABLED. In normal use the element is moved aside to model
    against it with room around it, and bringing it back to its place on
    every pull would be a punishment. Whoever aligns geometry that then goes
    back into Revit turns the checkbox on, because there a reference that
    lies about position is dangerous.

    Consequence worth knowing: with the checkbox disabled, an element MOVED
    in Revit does not move the Blender object, it only changes its mesh.
    This is the correct behavior for this default, and is still better than
    before: with vertices in world coordinates the object would have
    drifted.

    The getattr covers the case where the property is not registered yet,
    like limits() in bridge_sync."""
    if scene is None:
        return False
    return bool(getattr(scene, RESET_LOCATION_PROPERTY, False))


def find_collection():
    """The collection if it exists, otherwise None. Does NOT create it.

    Like its twin in bridge_collection: the panel's draw() calls this,
    because an open panel must not mark the file as modified."""
    return bpy.data.collections.get(COLLECTION_NAME)


def ensure_collection(scene=None):
    """The collection, creating it and linking it to the scene if missing."""
    if scene is None:
        scene = bpy.context.scene

    collection = bpy.data.collections.get(COLLECTION_NAME)
    if collection is None:
        collection = bpy.data.collections.new(COLLECTION_NAME)

    # children_recursive and not children, like for ToRevit: if the user
    # has nested FromRevit inside another collection it is already in the
    # scene, and relinking it to the master would raise RuntimeError.
    linked = False
    for child in scene.collection.children_recursive:
        if child is collection:
            linked = True
            break
    if not linked:
        scene.collection.children.link(collection)

    return collection


def find_object(element_id):
    """The mesh object with that element_id, or None.

    Searches ALL of bpy.data.objects and not just inside FromRevit: the
    user may have moved the object to another collection, and it is
    exactly that work that the in-place update must respect. If we searched
    only the collection, a wall moved elsewhere would be recreated as a
    duplicate on the next pull.

    It is a linear scan, repeated for every element in the batch. No cache
    on purpose: an index built at the start of the batch would hold
    references to objects the user can delete between one timer tick and
    the next, and reading a freed ID does not give an error, it gives a
    crash. The pull is limited to the current selection, never the model, so
    the cost is what it is.

    Several objects can carry the id: Shift+D copies it. Which one is the
    pulled element is decided by receive.pick_pulled."""
    matches = []
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        value = obj.get(ID_PROPERTY)
        if value is None:
            continue
        if _as_key(value) == element_id:
            matches.append(obj)

    index = receive.pick_pulled([(obj.name, obj.get(PULL_NAME_PROPERTY)) for obj in matches])
    return None if index is None else matches[index]


def _as_key(value):
    """Normalizes what is read from the custom property.

    We always write strings, but a file touched by hand or saved by a
    future version could contain an integer: normalizing here avoids the
    same element being imported twice."""
    try:
        return receive.element_id_key(value)
    except Exception:
        return None


def _signature(mesh):
    coords = array('f', [0.0]) * (len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", coords)
    return receive.mesh_signature(coords.tobytes(), len(mesh.polygons))


def _build_mesh(name, positions, indices, mesh_key=None):
    """A new mesh datablock from the payload, already marked.

    Grouping and index checking come BEFORE meshes.new: a malformed message
    must surface here, not after leaving an orphan mesh behind."""
    verts = receive.group_into_triples(positions)
    receive.check_indices(indices, len(verts))
    faces = receive.group_into_triples(indices)

    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    # before the signature: it must describe the welded mesh the user sees
    welded = bmesh.new()
    try:
        welded.from_mesh(mesh)
        bmesh.ops.remove_doubles(welded, verts=welded.verts, dist=WELD_DISTANCE)
        welded.to_mesh(mesh)
    finally:
        welded.free()
    mesh.update()
    if mesh_key is not None:
        mesh[MESH_KEY_MARK] = mesh_key
    mesh[MESH_SIG_MARK] = _signature(mesh)
    return mesh


def _in_edit_mode(mesh):
    for obj in bpy.data.objects:
        if obj.data == mesh and obj.mode == 'EDIT':
            return True
    return False


def is_touched(mesh):
    """Rule C: whether the user worked on this mesh's geometry.

    Without the key mark it is not the bridge's shared mesh (an asset linked
    with Ctrl+L, or a flat mesh). In edit mode the data is not flushed yet,
    so the signature cannot be trusted: touched, to be safe. Otherwise the
    geometry is compared with the signature taken at pull time."""
    if mesh is None or mesh.get(MESH_KEY_MARK) is None:
        return True
    if _in_edit_mode(mesh):
        return True
    return _signature(mesh) != mesh.get(MESH_SIG_MARK)


def find_key_mesh(mesh_key):
    """The mesh the objects of this key are showing now, or None.

    No registry: the truth is what is in the scene. When the objects
    disagree (the user replaced some, not all) the majority wins. Linear
    scan, same reasoning as find_object."""
    names = [obj.data.name for obj in bpy.data.objects
             if obj.type == 'MESH' and obj.data is not None
             and obj.get(MESH_KEY_PROPERTY) == mesh_key]
    name = receive.most_used(names)
    return None if name is None else bpy.data.meshes.get(name)


def _remove_if_orphan(mesh):
    # Only meshes the bridge built: an asset left without users is still the
    # user's, and Blender purges it on save if they really do not want it.
    # Flat meshes from releases before the signature stay orphans until the
    # next save, which is harmless.
    if mesh is not None and mesh.users == 0 and mesh.get(MESH_SIG_MARK) is not None:
        bpy.data.meshes.remove(mesh)


def _mesh_for_key(fields):
    """(mesh, fresh) for an instance: the one already in the scene for its
    key, otherwise a new one from this batch's revit_mesh."""
    mesh = find_key_mesh(fields["mesh_key"])
    if mesh is not None:
        return mesh, False
    stored = LAST_PULL.meshes.get(fields["mesh_key"])
    if stored is None:
        raise BridgeMessageError(
            "mesh_key {} arrived neither in this batch nor in the scene".format(
                fields["mesh_key"]))
    positions, indices = stored
    name = fields["type_name"] or fields["name"]
    return _build_mesh(name, positions, indices, fields["mesh_key"]), True


def _write_properties(obj, fields):
    obj[ID_PROPERTY] = fields["element_id"]
    obj[CATEGORY_PROPERTY] = fields["category"]
    obj[TYPE_PROPERTY] = fields["type_name"]
    # obj.name and not fields["name"]: Blender may have cut it (63 bytes) or
    # suffixed it (name taken), and the comparison is with the real name.
    obj[PULL_NAME_PROPERTY] = obj.name
    obj.color = fields["color"]


def import_geometry(fields, positions, indices, scene=None):
    """Creates or updates the object for a revit_geometry message.

    Returns (created, overwritten). overwritten is True when the object was
    an instance whose mesh the user had touched: the element no longer
    shares geometry, so the flat mesh replaces that work (spec 4.4).

    The normals arrive in the payload but are NOT used: Revit's
    tessellation emits non-indexed triangles and Blender recomputes the
    same normals on its own. They travel over the wire only because the
    format is identical in both directions, see DESIGN.md 5.4.

    No unit conversion and no axis change: the positions are already in
    meters and already Z-up.

    Positions are in coordinates LOCAL to the element and its position in
    the world is in fields["origin"]. ON CREATION the object gets
    obj.location = origin, so it has its origin ON THE ELEMENT: it rotates
    and scales around itself like any other Blender object. ON UPDATE the
    position is left untouched, unless the scene checkbox is on: see
    reset_location_enabled."""
    element_id = fields["element_id"]

    if scene is None:
        scene = bpy.context.scene

    mesh_data = _build_mesh(fields["name"], positions, indices)
    obj = find_object(element_id)

    created = obj is None
    overwritten = False
    if created:
        obj = bpy.data.objects.new(fields["name"], mesh_data)
        ensure_collection(scene).objects.link(obj)
        # The object's origin ends up ON THE ELEMENT, not at (0,0,0): that
        # is the whole point of the fix. A wall thirty meters from the
        # scene origin, with the object's origin at zero, rotated fifteen
        # degrees ended up very far away; scaled, it migrated instead of
        # growing.
        obj.location = fields["origin"]
    else:
        was_instance = obj.get(MESH_KEY_PROPERTY) is not None
        overwritten = was_instance and is_touched(obj.data)

        # IN-PLACE replacement: only the mesh datablock changes. The
        # position in the scene, the collections the user placed the object
        # in, and the modifiers added to it all stay. This is the whole
        # point of the decision: the modified wall is fetched back and the
        # work done around it is not lost.
        old_mesh = obj.data
        obj.data = mesh_data

        # The user-given position is respected, unless the checkbox is on.
        # With local coordinates, replacing the mesh no longer moves
        # anything on its own: before, with absolute vertices, the user's
        # matrix re-transformed them and the object drifted away on every
        # pull.
        if receive.flat_update_resets_transform(was_instance, reset_location_enabled(scene)):
            # The WHOLE transform is reset, not just location. Restoring
            # the position while leaving rotation and scale standing is
            # worse than not restoring anything: the object looks like it
            # is back in place but is still rotated, and whoever is
            # looking believes it. Observed in the field: needed a manual
            # ALT+R to finish the job.
            # An object that was an instance is always reset: its frame was
            # the insertion point, the flat mesh is centered on the bounding
            # box.
            obj.location = fields["origin"]
            obj.rotation_euler = (0.0, 0.0, 0.0)
            obj.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
            obj.scale = (1.0, 1.0, 1.0)

        # The old mesh must be removed by hand, otherwise it stays in
        # bpy.data as an orphan and one accumulates on every repeated pull:
        # same family as the leak from a missing to_mesh_clear() avoided in
        # Phase A. The check on users is not pedantry: if the user has
        # linked that same mesh to a second object, removing it would empty
        # that one out.
        _remove_if_orphan(old_mesh)

        if was_instance:
            del obj[MESH_KEY_PROPERTY]

        obj.name = fields["name"]

    _write_properties(obj, fields)
    return created, overwritten


def import_instance(fields, scene=None):
    """Creates or updates the object for a revit_instance message. Returns
    True if the object was created. Spec 2026-10-04 section 4.4; the
    decision itself is receive.instance_update, tested without Blender."""
    if scene is None:
        scene = bpy.context.scene

    obj = find_object(fields["element_id"])
    exists = obj is not None
    was_instance = exists and obj.get(MESH_KEY_PROPERTY) is not None
    touched = was_instance and is_touched(obj.data)
    replace_mesh, apply_matrix = receive.instance_update(
        exists, was_instance, touched, reset_location_enabled(scene))

    mesh, fresh = (_mesh_for_key(fields) if replace_mesh else (None, False))

    try:
        if not exists:
            obj = bpy.data.objects.new(fields["name"], mesh)
            ensure_collection(scene).objects.link(obj)
        elif replace_mesh and obj.data != mesh:
            old_mesh = obj.data
            # A fresh mesh inherits the material slots of the one it replaces:
            # a texturing done on an untouched mesh survives a type change in
            # Revit. UVs do not: the geometry is new. A mesh reused from the
            # siblings already has its own materials and is left alone.
            if fresh and old_mesh is not None:
                for material in old_mesh.materials:
                    mesh.materials.append(material)
            obj.data = mesh
            _remove_if_orphan(old_mesh)

        if apply_matrix:
            obj.matrix_world = Matrix(receive.matrix_rows(fields["matrix"]))

        if exists:
            obj.name = fields["name"]
        _write_properties(obj, fields)
        obj[MESH_KEY_PROPERTY] = fields["mesh_key"]
    except Exception:
        # A fresh mesh nobody uses would stay in the file as an orphan:
        # the failure is counted by handle_instance, the mesh goes away here.
        if fresh and mesh.users == 0:
            bpy.data.meshes.remove(mesh)
        raise

    return not exists


def handle_geometry(header, payload, now, scene=None):
    """A complete revit_geometry message, from the header to the objects.

    Updates LAST_PULL and returns a string to print, or None if everything
    went smoothly."""
    try:
        fields = receive.read_geometry_header(header)
        positions, normals, indices = receive.unpack_mesh_payload(
            payload, fields["vert_count"], fields["tri_count"])
        created, overwritten = import_geometry(fields, positions, indices, scene)
    except Exception as error:
        # An element that blows up must not stop the others, exactly like
        # on the Revit side in SendSelectionCommand: it gets counted, named
        # and skipped. Except Exception and not just BridgeMessageError
        # because even a bpy RuntimeError on a single object must be
        # isolated: this function is called from a timer, and an exception
        # that bubbles up that far makes Blender disable the timer, i.e.
        # silently switches off the ENTIRE queue drain, handshake included.
        LAST_PULL.record_failure(now)
        return "element not imported: {}: {}".format(
            type(error).__name__, error)

    LAST_PULL.record(created, now, overwritten)
    return None


def handle_mesh(header, payload, now):
    """A revit_mesh message: the payload is kept in the batch state, not
    turned into a datablock. Returns a string to print, or None.

    A malformed one is not counted as a failed element, because it is not
    an element: the instances pointing at it fail on their own, and THEY
    are counted."""
    try:
        fields = receive.read_mesh_header(header)
        positions, _normals, indices = receive.unpack_mesh_payload(
            payload, fields["vert_count"], fields["tri_count"])
        receive.check_indices(indices, fields["vert_count"])
    except Exception as error:
        return "shared mesh discarded: {}: {}".format(type(error).__name__, error)

    LAST_PULL.store_mesh(fields["mesh_key"], positions, indices, now)
    return None


def handle_instance(header, payload, now, scene=None):
    """A revit_instance message. Same isolation as handle_geometry: an
    element that blows up is counted and skipped, never propagated to the
    timer. The payload is empty by protocol and ignored."""
    try:
        fields = receive.read_instance_header(header)
        created = import_instance(fields, scene)
    except Exception as error:
        LAST_PULL.record_failure(now)
        return "element not imported: {}: {}".format(
            type(error).__name__, error)

    LAST_PULL.record(created, now)
    return None


def handle_batch_begin(header, now):
    """A revit_batch_begin message. Returns a note to print, or None."""
    note = LAST_PULL.begin(receive.read_batch_count(header), now)
    if note is None:
        return None
    return "batch opened without closing the previous one ({})".format(note)


def handle_batch_end(now):
    """A revit_batch_end message. False if no batch was open."""
    return LAST_PULL.end(now)


def check_open_batch(connected, now):
    """Closes a batch left open. Must be called on every drain loop tick.

    Returns the reason for the closure, or None if there was nothing to
    close. Objects already imported stay in the scene: it is geometry that
    arrived in full, all that is missing is the closing message."""
    return LAST_PULL.check_interrupted(connected, now)
