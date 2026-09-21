# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Packing of the geometry for the geometry message.
# This module must NOT import bpy: it is shared with the tests.
#
# Payload order: positions (vert*3 f32), normals (vert*3 f32),
# indices (tri*3 u32). Little-endian, meters, Z-up. See DESIGN.md 5.2.

import struct

from bridge_protocol import BridgeFramingError


def pack_mesh_payload(positions, normals, indices):
    """positions and normals are sequences of floats, indices of ints.
    Returns the payload bytes."""
    if len(normals) != len(positions):
        raise BridgeFramingError(
            "normals is {} long but positions is {} long".format(len(normals), len(positions)))
    if len(positions) % 3 != 0:
        raise BridgeFramingError(
            "positions must have a length multiple of 3, has {}".format(len(positions)))
    if len(indices) % 3 != 0:
        raise BridgeFramingError(
            "indices must have a length multiple of 3, has {}".format(len(indices)))

    # min and max are builtins written in C: on a mesh with 300k triangles,
    # 900k indices go by without paying a bytecode cycle per element. A
    # "for value in indices" here would cost more than the whole underlying
    # struct.pack, and it is exactly the kind of Python loop over data that
    # the project rule forbids in extraction.
    vertex_count = len(positions) // 3
    if len(indices):
        lowest = min(indices)
        highest = max(indices)
        if lowest < 0 or highest >= vertex_count:
            bad = lowest if lowest < 0 else highest
            raise BridgeFramingError(
                "index {} out of the range 0..{}".format(bad, vertex_count - 1))

    return b"".join([
        struct.pack("<{}f".format(len(positions)), *positions),
        struct.pack("<{}f".format(len(normals)), *normals),
        struct.pack("<{}I".format(len(indices)), *indices),
    ])


def matrix_to_list(matrix):
    """Converts a 4x4 matrix (sequence of 4 rows of 4) into the row-major
    list of 16 floats expected by the protocol."""
    flat = []
    for row in matrix:
        for value in row:
            flat.append(float(value))
    if len(flat) != 16:
        raise BridgeFramingError("matrix with {} elements instead of 16".format(len(flat)))
    return flat


def flat_matrix(matrix):
    """Accepts either a 4x4 matrix (mathutils.Matrix or 4 rows of 4) or an
    already-flat list of 16 floats, and always returns the flat list.

    Needed because the live transform path keeps the matrix ALREADY
    flattened in the coalescing dict: the depsgraph handler reads it once,
    and the timer that sends it 33 ms later must not read anything back from
    bpy (the object may have been deleted meanwhile)."""
    values = list(matrix)
    # a row of mathutils.Matrix is a Vector and has __len__, a scalar does
    # not: that is how the nested shape is told apart from the flat one
    if values and not hasattr(values[0], "__len__"):
        if len(values) != 16:
            raise BridgeFramingError(
                "flat matrix with {} elements instead of 16".format(len(values)))
        return [float(value) for value in values]
    return matrix_to_list(values)


# --- safety limits ------------------------------------------------------

BUDGET_OK = "ok"
BUDGET_WARN = "warn"
BUDGET_REJECT = "reject"


def check_vertex_budget(vertex_count, warn_limit, reject_limit):
    """Classifies an object based on the vertex count of its evaluated mesh.

    A limit <= 0 is disabled. The threshold exists because the whole Sync
    runs on Blender's main thread: during the send the window is frozen and
    there is no progress bar to watch."""
    if reject_limit > 0 and vertex_count > reject_limit:
        return BUDGET_REJECT
    if warn_limit > 0 and vertex_count > warn_limit:
        return BUDGET_WARN
    return BUDGET_OK


# --- stable ids ---------------------------------------------------------

def resolve_ids(current_ids, make_id):
    """Given the list of ids already written on the objects (None or "" when
    missing), returns (ids, changed).

    `changed` are the INDICES that were assigned a new id: the caller must
    write them back to the object's custom property.

    The case that makes this function necessary is not the missing id but
    the DUPLICATE one: Shift+D in Blender also copies custom properties, so
    two distinct objects end up with the same `bilocus_id`. On the wire the
    id IS the identity: two objects with the same id collapse into one
    inside Revit's GeometryStore and the last one wins, silently making the
    other disappear without any error. The first one encountered keeps the
    id, the following ones get a new one."""
    ids = []
    changed = []
    seen = set()
    for index, value in enumerate(current_ids):
        if not isinstance(value, str) or not value or value in seen:
            value = make_id()
            changed.append(index)
        seen.add(value)
        ids.append(value)
    return ids, changed


# --- headers of Blender -> Revit messages -------------------------------
#
# The field names are the contract with MessageRouter.cs (DESIGN.md 5.3).
# Changing one here without changing it there does not produce a compile
# error on either side: it produces an `error` frame at runtime, which is
# only seen by opening Revit. This module's tests are the only other place
# where that contract is written down, on purpose.

DEFAULT_COLOR = (0.8, 0.8, 0.8, 1.0)


def normalize_color(color):
    """Brings `obj.color` into the protocol's RGBA list of 4 floats 0..1.

    The clamp is not gratuitous defensiveness: on the Revit side
    ToRevitColor multiplies by 255 and ToRevitTransparency by 100, and a
    value out of range would become a wrong channel instead of a visible
    error."""
    values = [float(value) for value in color]
    if len(values) == 3:
        values.append(1.0)
    if len(values) != 4:
        raise BridgeFramingError(
            "color with {} components instead of 4".format(len(values)))

    clamped = []
    for value in values:
        if value < 0.0:
            value = 0.0
        elif value > 1.0:
            value = 1.0
        clamped.append(value)
    return clamped


def build_geometry_header(obj_id, name, vert_count, tri_count, color, matrix):
    return {
        "type": "geometry",
        "obj_id": obj_id,
        "name": name,
        "vert_count": int(vert_count),
        "tri_count": int(tri_count),
        "color": normalize_color(color),
        "matrix": flat_matrix(matrix),
    }


def build_transform_header(obj_id, matrix):
    return {
        "type": "transform",
        "obj_id": obj_id,
        "matrix": flat_matrix(matrix),
    }


def build_sync_begin_header(obj_ids):
    # obj_ids is consumed by RequireStrings on the C# side, which rejects
    # the array if a single element is not a string
    return {"type": "sync_begin", "obj_ids": [str(value) for value in obj_ids]}


def build_sync_end_header():
    return {"type": "sync_end"}


def build_remove_header(obj_id):
    return {"type": "remove", "obj_id": obj_id}


def build_clear_header():
    return {"type": "clear"}
