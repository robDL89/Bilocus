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

import bpy

import bridge_receive as receive

# ASCII mandatory, like for ToRevit: the name ends up in logs and in the panel.
COLLECTION_NAME = "FromRevit"

# Custom properties linking the Blender object to the Revit element. The
# first one is also the key used to find the object again on the next pull:
# it is a string, for the reason written in receive.element_id_key.
ID_PROPERTY = "revit_element_id"
CATEGORY_PROPERTY = "revit_category"
TYPE_PROPERTY = "revit_type_name"

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
    crash. The pull is limited to the current selection (decision 6), so
    the cost is what it is."""
    for obj in bpy.data.objects:
        if obj.type != 'MESH':
            continue
        value = obj.get(ID_PROPERTY)
        if value is None:
            continue
        if _as_key(value) == element_id:
            return obj
    return None


def _as_key(value):
    """Normalizes what is read from the custom property.

    We always write strings, but a file touched by hand or saved by a
    future version could contain an integer: normalizing here avoids the
    same element being imported twice."""
    try:
        return receive.element_id_key(value)
    except Exception:
        return None


def import_geometry(fields, positions, indices, scene=None):
    """Creates or updates the object for a revit_geometry message.

    Returns True if the object was created, False if updated.

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

    # Grouping and index checking BEFORE creating any datablock: if the
    # message is malformed it must surface here, not after leaving an
    # orphan mesh behind.
    verts = receive.group_into_triples(positions)
    receive.check_indices(indices, len(verts))
    faces = receive.group_into_triples(indices)

    obj = find_object(element_id)

    mesh_data = bpy.data.meshes.new(fields["name"])
    mesh_data.from_pydata(verts, [], faces)
    mesh_data.update()

    created = obj is None
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
        if reset_location_enabled(scene):
            # The WHOLE transform is reset, not just location. Restoring
            # the position while leaving rotation and scale standing is
            # worse than not restoring anything: the object looks like it
            # is back in place but is still rotated, and whoever is
            # looking believes it. Observed in the field: needed a manual
            # ALT+R to finish the job.
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
        if old_mesh is not None and old_mesh.users == 0:
            bpy.data.meshes.remove(old_mesh)

        obj.name = fields["name"]

    obj[ID_PROPERTY] = element_id
    obj[CATEGORY_PROPERTY] = fields["category"]
    obj[TYPE_PROPERTY] = fields["type_name"]
    obj.color = fields["color"]

    return created


def handle_geometry(header, payload, now, scene=None):
    """A complete revit_geometry message, from the header to the objects.

    Updates LAST_PULL and returns a string to print, or None if everything
    went smoothly."""
    try:
        fields = receive.read_geometry_header(header)
        positions, normals, indices = receive.unpack_mesh_payload(
            payload, fields["vert_count"], fields["tri_count"])
        created = import_geometry(fields, positions, indices, scene)
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
