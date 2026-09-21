# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Selected edges become snappable proxies inside Revit.
# This module touches bpy: it must stay as thin as possible. Everything
# that can be decided without Blender lives in bridge_edges.py (filtering,
# dedup, packing, header, point transform) and is covered by tests.
#
# Unlike Sync, the source is NOT the ToRevit collection but the ACTIVE
# OBJECT and its edge selection. This is not an exception to decision 4 of
# CLAUDE.md: that one concerns the preview, i.e. what is seen. Here we
# write into the Revit document, and selection is exactly the way to say
# "these three edges, not the whole mesh".

import bpy

import bridge_client as client
import bridge_collection as coll
import bridge_edges as edges
import bridge_fit as fit
import bridge_receive as receive

# Above this number of edges the panel does NOT count the selection.
#
# draw() runs on every redraw of the sidebar, and counting means reading
# the selection flag of every edge of the mesh: it is the same reason the
# panel does not count triangles. On a mesh with half a million edges that
# reading, repeated on every mouse movement, turns into a permanent
# viewport slowdown that nobody would connect to this addon. Above the
# threshold the count becomes "not counted": the button stays pressable
# regardless, because the real reading is only done once by the operator.
#
# The value comes from a measurement of the Python side alone (list
# allocation plus the sum): 0.15 ms at 50k edges, 1.1 ms at 200k, 5.5 ms at
# 1M. The foreach_get on top of it was not measured outside Blender, so the
# threshold sits where the measurable cost is still about a millisecond,
# not where it would start to hurt.
COUNT_LIMIT = 200000

EDIT_MODE_MESSAGE = (
    "the object is in Edit Mode: the edge selection read from here "
    "would be the previous one. Go back to Object Mode (Tab) and press again")

NO_RESULT_MESSAGE = "no proxy created in this session"

# The two modes of Create proxy. POLYLINE is the Phase A3 behavior, one
# edge one line; ARCS rewrites the edge chains with lines and arcs within a
# maximum deviation (bridge_fit.py).
MODE_POLYLINE = 'POLYLINE'
MODE_ARCS = 'ARCS'

# Deviation proposed on first use, in millimeters. Wide enough to absorb
# the faceting of a curve modeled in Blender at normal resolution, tight
# enough not to straighten out a real curve.
DEFAULT_TOLERANCE_MM = 2.0

# Outcome of the last creation, read by the panel. Lives in the module and
# not in the scene: it is session information, it must not be saved into
# the .blend. Same choice as bridge_sync.LAST_SYNC.
LAST_PROXY = {"message": NO_RESULT_MESSAGE}


def register_properties():
    bpy.types.Scene.bilocus_proxy_mode = bpy.props.EnumProperty(
        name="Mode",
        description="How the selected edges become model lines",
        items=[
            (MODE_POLYLINE, "Polyline",
             "One edge, one line: the chain arrives as it is"),
            (MODE_ARCS, "Arcs and lines",
             "Edge chains become lines and arcs within the "
             "maximum deviation from the vertices"),
        ],
        default=MODE_POLYLINE)
    bpy.types.Scene.bilocus_proxy_tolerance_mm = bpy.props.FloatProperty(
        name="Deviation (mm)",
        description="Maximum distance of the original vertices from lines "
                    "and arcs. 0 uses the smallest possible value ({} mm)".format(
                        fit.MIN_TOLERANCE_MM),
        default=DEFAULT_TOLERANCE_MM,
        min=0.0,
        soft_max=100.0,
        precision=1)


def unregister_properties():
    for name in ("bilocus_proxy_mode", "bilocus_proxy_tolerance_mm"):
        if hasattr(bpy.types.Scene, name):
            delattr(bpy.types.Scene, name)


def active_mesh(context=None):
    """The active object if it is a usable mesh, otherwise None."""
    if context is None:
        context = bpy.context
    obj = getattr(context, "active_object", None)
    if obj is None or obj.type != 'MESH':
        return None
    return obj


def count_selected_edges(obj):
    """(count, note). The count is None when it was not done, and in that
    case the note says why.

    The edge selection PERSISTS in Object Mode and is read from
    mesh.edges[i].select: there is no need to enter Edit Mode to know what
    is selected, so there is no conflict with Phase A's decision to refuse
    to work in Edit Mode."""
    if obj is None:
        return None, "no active mesh"
    if obj.mode == 'EDIT':
        # In Edit Mode the flags in mesh.edges are the ones from the LAST
        # exit from Edit Mode, not the current ones: the real selection
        # lives in the editing BMesh. Showing its count would show a number
        # that does not match what is highlighted on screen.
        return None, "selection not readable in Edit Mode"

    mesh = obj.data
    total = len(mesh.edges)
    if total == 0:
        return 0, ""
    if total > COUNT_LIMIT:
        return None, "mesh with {} edges: not counted".format(total)

    flags = [False] * total
    mesh.edges.foreach_get("select", flags)
    return sum(flags), ""


def read_selected_edges(obj):
    """(edges, problem). The edges are pairs of points in WORLD
    coordinates, meters: this is what the protocol expects, because
    proxy_edges carries no matrix.

    The readings are foreach_get on preallocated lists, as in
    bridge_extract.extract: the Python loop only runs over the SELECTED
    edges, which are at most MAX_EDGES, not over the whole mesh."""
    if obj is None:
        return None, "no active mesh object: select one"
    if obj.mode == 'EDIT':
        return None, EDIT_MODE_MESSAGE

    mesh = obj.data
    total = len(mesh.edges)
    if total == 0:
        return [], None

    flags = [False] * total
    mesh.edges.foreach_get("select", flags)

    selected = sum(flags)
    if selected == 0:
        return [], None
    if selected > edges.MAX_EDGES:
        # check BEFORE allocating the coordinates: on an absurd selection
        # (Ctrl+A on a dense mesh) the allocations are the expensive part,
        # and the message can be written without doing them
        return None, "{} edges selected, the maximum for a creation is {}".format(
            selected, edges.MAX_EDGES)

    pairs = [0] * (total * 2)
    mesh.edges.foreach_get("vertices", pairs)

    coordinates = [0.0] * (len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", coordinates)

    # The mesh is the one in bpy.data, NOT the one evaluated by the
    # depsgraph: selectable edges only exist on the original mesh. A
    # boolean or an array has no selectable edges, and asking Revit to snap
    # onto the result of a modifier is not what this command does. As a
    # consequence, the matrix to apply is obj.matrix_world, and that is it:
    # no modifier transform to compose.
    matrix = [list(row) for row in obj.matrix_world]

    result = []
    for index in range(total):
        if not flags[index]:
            continue
        first = pairs[index * 2] * 3
        second = pairs[index * 2 + 1] * 3
        result.append((
            edges.transform_point(
                matrix, coordinates[first], coordinates[first + 1], coordinates[first + 2]),
            edges.transform_point(
                matrix, coordinates[second], coordinates[second + 1], coordinates[second + 2]),
        ))

    return result, None


def send_proxy(context=None):
    """Sends proxy_edges for the selected edges of the active object.

    Returns (level, message) with level in 'INFO', 'WARNING', 'ERROR': the
    panel operator passes them as-is to self.report.

    Does NOT wait for the outcome: the reply is the proxy_result message,
    which arrives on the queue and is picked up by the drain. Waiting here
    would mean blocking Blender's main thread for the duration of a Revit
    transaction."""
    if context is None:
        context = bpy.context

    if not client.CLIENT.running:
        return 'ERROR', "not connected to Revit: press Connect"

    obj = active_mesh(context)
    if obj is None:
        return 'ERROR', "no active mesh object: select one"

    raw, problem = read_selected_edges(obj)
    if problem is not None:
        return 'ERROR', problem
    if not raw:
        return 'ERROR', ("no edge selected on '{}': enter Edit "
                         "Mode, select the edges, go back to Object Mode".format(obj.name))

    kept, dropped = edges.prepare_edges(raw)
    if not kept:
        # all dropped: sending anyway would mean an edge_count of zero,
        # which on the Revit side is a content error and not a removal
        return 'ERROR', "no usable edge: {}".format(
            edges.describe_prepared(0, dropped))

    scene = context.scene
    mode = getattr(scene, "bilocus_proxy_mode", MODE_POLYLINE)

    arcs = []
    notes = []
    if mode == MODE_ARCS:
        try:
            tolerance, minimum_used = fit.resolve_tolerance(
                getattr(scene, "bilocus_proxy_tolerance_mm", DEFAULT_TOLERANCE_MM))
        except ValueError as error:
            return 'ERROR', str(error)
        if minimum_used:
            # A zero is not silently changed: the user must know about it.
            notes.append("deviation 0: using the minimum of {} mm".format(
                fit.MIN_TOLERANCE_MM))
        lines, arcs, _chains = fit.fit_edges(kept, tolerance)
    else:
        lines = kept

    # stable_id and not peek_id: here WRITING the custom property is
    # correct, we are in an operator and not inside a depsgraph handler.
    # It is the same id used by geometry and transform, and it is what
    # makes it possible to replace the proxies of the same object.
    obj_id = coll.stable_id(obj)

    try:
        header = edges.build_proxy_edges_header(
            obj_id, obj.name, len(lines), len(arcs))
        payload = edges.pack_proxy_payload(lines, arcs)
    except Exception as error:
        # rejected before producing any bytes: the connection is intact
        return 'ERROR', "edges cannot be sent: {}".format(error)

    if not client.CLIENT.send(header, payload):
        return 'ERROR', "connection lost while sending the edges"

    sent = edges.describe_prepared(len(kept), dropped)
    if mode == MODE_ARCS:
        sent = "{} -> {} lines and {} arcs".format(sent, len(lines), len(arcs))
    notes.insert(0, "sent: {} - waiting for the outcome from Revit".format(sent))
    message = "; ".join(notes)
    LAST_PROXY["message"] = message

    level = 'WARNING' if (dropped["short"] or dropped["duplicate"]
                          or dropped["invalid"] or len(notes) > 1) else 'INFO'
    return level, message


def handle_result(header):
    """Records a proxy_result that arrived from Revit. Returns the message.

    Raises BridgeMessageError if the header is not readable: the drain in
    __init__.py catches it and writes it to the console, without turning
    off the timer."""
    fields = receive.read_proxy_result(header)
    LAST_PROXY["message"] = receive.format_proxy_result(fields)
    return LAST_PROXY["message"]
