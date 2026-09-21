# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Orchestration of the manual Sync and of the live transform.
#
# Decision 5 of CLAUDE.md: GEOMETRY is sent by hand with the Sync button,
# TRANSFORM starts on its own. This module only holds the "what to send"
# side; the when of transform is the handler in __init__.py.

import time

import bpy

import bridge_client as client
import bridge_collection as coll
import bridge_extract as extract
import bridge_mesh as mesh
import bridge_protocol as protocol
import bridge_style as style

# Safety thresholds on the VERTEX count of the evaluated mesh, per object.
# These are the defaults: the scene properties registered below make them
# adjustable per file.
#
# 250k = warning. This is roughly four times the 65536 vertices-per-chunk
# DC3D limit verified in Phase 0, so it is where Revit starts building many
# buffers, and in practice the boundary between "a piece of the project"
# and "a scan". Below this threshold Sync is instant, above it, it is felt.
#
# 1M = object rejection. This is not a protocol limit: the payload for 1M
# vertices is around 48 MB, way inside the 256 MiB of MAX_PAYLOAD_BYTES. It
# is a TIME limit. Extraction, packing and send all run on Blender's main
# thread: at those numbers the window stays frozen for several seconds,
# with no progress bar and no way to cancel. Better to skip an object and
# say so than to hijack the entire application. Whoever knows what they are
# doing raises the threshold from the panel.
DEFAULT_WARN_VERTICES = 250000
DEFAULT_MAX_VERTICES = 1000000

# State of the last Sync, read by the panel. Lives in the module and not in
# the scene: it is session information, it must not be saved in the .blend.
LAST_SYNC = {
    "objects": 0,
    "triangles": 0,
    "seconds": 0.0,
    "message": "no sync in this session",
}


# --- scene properties ----------------------------------------------------

def register_properties():
    bpy.types.Scene.bilocus_warn_vertices = bpy.props.IntProperty(
        name="Vertex warning",
        description="Above this vertex count the object is still sent but "
                    "flagged. 0 disables the warning",
        default=DEFAULT_WARN_VERTICES,
        min=0)
    bpy.types.Scene.bilocus_max_vertices = bpy.props.IntProperty(
        name="Vertex limit",
        description="Above this vertex count the object is NOT sent. 0 "
                    "removes the limit",
        default=DEFAULT_MAX_VERTICES,
        min=0)
    bpy.types.Scene.bilocus_face_color = bpy.props.FloatVectorProperty(
        name="Faces",
        description="Color of the preview faces inside Revit",
        subtype='COLOR_GAMMA',
        size=3,
        min=0.0,
        max=1.0,
        default=style.DEFAULT_FACE_COLOR,
        update=_on_style_changed)
    bpy.types.Scene.bilocus_edge_color = bpy.props.FloatVectorProperty(
        name="Edges",
        description="Color of the preview edges inside Revit",
        subtype='COLOR_GAMMA',
        size=3,
        min=0.0,
        max=1.0,
        default=style.DEFAULT_EDGE_COLOR,
        update=_on_style_changed)


def _on_style_changed(self, context):
    # Property update callbacks run on the main thread, like operators:
    # sending from here is safe. Not connected: nothing to do, the colors
    # go out at the next Connect.
    send_style(context.scene)


def unregister_properties():
    for name in ("bilocus_warn_vertices", "bilocus_max_vertices",
                 "bilocus_face_color", "bilocus_edge_color"):
        if hasattr(bpy.types.Scene, name):
            delattr(bpy.types.Scene, name)


def limits(scene):
    """(warning threshold, rejection threshold). The getattr calls cover the
    case where the properties are not registered yet, which can happen if
    something calls this while the addon is still registering."""
    warn = getattr(scene, "bilocus_warn_vertices", DEFAULT_WARN_VERTICES)
    reject = getattr(scene, "bilocus_max_vertices", DEFAULT_MAX_VERTICES)
    return warn, reject


# --- Edit Mode ------------------------------------------------------------

def objects_in_edit_mode(objects):
    """The names of the collection's objects that are in Edit Mode.

    DECISION: in Edit Mode, Sync is REFUSED, not attempted with bmesh.
    Three reasons, in order of weight:

    1. bmesh.from_edit_mesh() gives the editing mesh, i.e. the shape BEFORE
       modifiers. It would send geometry of a different nature than the
       same button sends in Object Mode, and without booleans, arrays and
       subdivision applied: exactly the shape this bridge exists to NOT
       show.
    2. It would be a second extraction path, with its own hand-rolled
       triangulation (bmesh has no loop_triangles), to keep correct in
       parallel with the first one forever.
    3. The cost to the user is one press of Tab. The cost of a Sync that
       silently sends a different shape than expected is that from that
       moment on nobody trusts any Sync anymore.

    The check is on obj.mode, not on context.mode: this way staying in
    Edit Mode on an object OUTSIDE the collection blocks nothing, and
    multi-object editing is also seen on the non-active objects."""
    return [obj.name for obj in objects if obj.mode == 'EDIT']


# --- Sync -------------------------------------------------------------------

def sync_all(context=None):
    """Sends sync_begin, one geometry per object, sync_end.

    Returns (level, message) with level in 'INFO', 'WARNING', 'ERROR': the
    panel's operator passes them straight to self.report."""
    if context is None:
        context = bpy.context

    if not client.CLIENT.running:
        return 'ERROR', "not connected to Revit: press Connect"

    scene = context.scene
    objects = coll.objects_to_send()

    editing = objects_in_edit_mode(objects)
    if editing:
        return 'ERROR', "Sync refused: {} in Edit Mode. Go back to Object " \
                        "Mode (Tab) and press Sync again".format(", ".join(editing[:3]))

    # The colors travel with every Sync too: a Revit restarted in the
    # meantime would otherwise draw the defaults until the next change.
    send_style(scene)

    started = time.time()
    warn_limit, reject_limit = limits(scene)

    ids = coll.assign_ids(objects)

    # sync_begin announces ALL the ids: sync_end on the Revit side removes
    # only what was not announced, so the announcement protects against
    # removal. An object later rejected by the threshold gets an explicit
    # `remove` further down: without that it would stay in Revit with the
    # geometry from the previous Sync, which is the worse of the two
    # possible errors.
    if not client.CLIENT.send(mesh.build_sync_begin_header(ids)):
        return 'ERROR', "connection lost during sync"

    depsgraph = context.evaluated_depsgraph_get()

    sent = 0
    triangles = 0
    skipped = []
    warned = []

    for index, obj in enumerate(objects):
        obj_id = ids[index]
        positions, normals, indices, vertex_count, triangle_count = extract.extract(
            obj, depsgraph, reject_limit)

        if positions is None:
            skipped.append("{} ({} vertices)".format(obj.name, vertex_count))
            client.CLIENT.send(mesh.build_remove_header(obj_id))
            continue

        if mesh.check_vertex_budget(vertex_count, warn_limit, 0) == mesh.BUDGET_WARN:
            warned.append("{} ({} vertices)".format(obj.name, vertex_count))

        try:
            payload = mesh.pack_mesh_payload(positions, normals, indices)
        except Exception as error:
            # pack_mesh_payload rejects before producing bytes: the
            # connection is intact, the object is not. Skip this one and
            # continue.
            skipped.append("{} ({})".format(obj.name, error))
            client.CLIENT.send(mesh.build_remove_header(obj_id))
            continue

        header = mesh.build_geometry_header(
            obj_id, obj.name, vertex_count, triangle_count,
            obj.color, obj.matrix_world)

        if not client.CLIENT.send(header, payload):
            _record(sent, triangles, time.time() - started,
                    "connection lost after {} objects".format(sent))
            return 'ERROR', LAST_SYNC["message"]

        sent += 1
        triangles += triangle_count

    client.CLIENT.send(mesh.build_sync_end_header())

    elapsed = time.time() - started
    message = "{} objects, {} triangles, {:.2f} s".format(sent, triangles, elapsed)
    level = 'INFO'
    if skipped:
        message = "{} - SKIPPED: {}".format(message, "; ".join(skipped[:3]))
        level = 'WARNING'
    elif warned:
        message = "{} - heavy: {}".format(message, "; ".join(warned[:3]))
        level = 'WARNING'

    _record(sent, triangles, elapsed, message)
    return level, message


def _record(objects, triangles, seconds, message):
    LAST_SYNC["objects"] = objects
    LAST_SYNC["triangles"] = triangles
    LAST_SYNC["seconds"] = seconds
    LAST_SYNC["message"] = message


# --- transform and removals --------------------------------------------------

def send_transform(obj_id, matrix):
    """Only id and matrix: 84 bytes of header and zero payload. This is the
    30 Hz path, it must not touch the geometry nor read anything back from
    bpy."""
    return client.CLIENT.send(mesh.build_transform_header(obj_id, matrix))


def send_remove(obj_id):
    return client.CLIENT.send(mesh.build_remove_header(obj_id))


def send_clear():
    return client.CLIENT.send(mesh.build_clear_header())


def send_style(scene):
    """Sends the preview colors of the scene. False if not connected or if
    the colors are invalid (they cannot be, the properties are clamped)."""
    if not client.CLIENT.running:
        return False
    try:
        header = style.build_style_header(scene.bilocus_face_color, scene.bilocus_edge_color)
    except protocol.BridgeFramingError:
        return False
    return client.CLIENT.send(header)
