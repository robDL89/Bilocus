# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Mesh extraction from an evaluated Blender object.
# This module touches bpy: it must be kept as thin as possible.
#
# Non-negotiable rules in here:
#  - foreach_get on preallocated lists, NEVER a Python loop over vertices:
#    on a 300k-vertex mesh the difference is between tenths of a second and
#    tens
#  - always evaluated_get(depsgraph): without it, the shape sent would be
#    the one BEFORE modifiers, which is the opposite of the reason this
#    bridge exists
#  - to_mesh() must always be closed with to_mesh_clear() in a finally,
#    otherwise memory leaks on every Sync, and Sync gets pressed hundreds of
#    times a day
#
# Does not import bpy: it receives the object and the depsgraph already
# ready. It is not, however, a testable module like bridge_mesh.py, because
# every line still talks to Blender's RNA.

import bridge_bake as bake


def extract(obj, depsgraph, reject_over=0):
    """Returns (positions, normals, indices, vertex_count, triangle_count).

    Positions are in LOCAL coordinates: the object's matrix travels
    separately in the header, so a transform does not force resending the
    geometry (decision 5 of CLAUDE.md).

    If `reject_over` is positive and the evaluated mesh exceeds that number
    of vertices, the three lists come back None while the counts are still
    valid: the caller must be able to write how many vertices the rejected
    object had, otherwise the warning message would be useless. The check
    happens BEFORE the allocations, which are the expensive part.
    """
    evaluated = obj.evaluated_get(depsgraph)

    mesh = evaluated.to_mesh()
    if mesh is None:
        # to_mesh() returns None for objects that produce no evaluable
        # geometry. Should not happen: objects_to_send already filters on
        # type == 'MESH'. If it does happen, it is an object to skip, not
        # an error to propagate.
        evaluated.to_mesh_clear()
        return None, None, None, 0, 0

    try:
        vertex_count = len(mesh.vertices)
        if reject_over > 0 and vertex_count > reject_over:
            return None, None, None, vertex_count, 0

        # In Blender 4.x calc_loop_triangles() populates mesh.loop_triangles.
        # The hasattr is a hedge for the case that 5.2 has already made the
        # computation implicit and dropped the method: without it the addon
        # would stop working entirely on one of the two supported versions,
        # and with it, at worst, an already-ready list is read.
        if hasattr(mesh, "calc_loop_triangles"):
            mesh.calc_loop_triangles()

        positions = [0.0] * (vertex_count * 3)
        mesh.vertices.foreach_get("co", positions)

        # Normals PER VERTEX, so always smooth: a flat-shaded face arrives
        # in Revit with interpolated normals. Sending per-loop normals
        # would mean duplicating vertices at hard edges, i.e. changing the
        # outgoing topology, and the protocol has only one normals array,
        # as long as the positions array. This is a known limit of Phase A,
        # not an oversight.
        normals = [0.0] * (vertex_count * 3)
        mesh.vertices.foreach_get("normal", normals)

        triangle_count = len(mesh.loop_triangles)
        indices = [0] * (triangle_count * 3)
        mesh.loop_triangles.foreach_get("vertices", indices)

        return positions, normals, indices, vertex_count, triangle_count
    finally:
        # on the EVALUATED object, not the original: it is the one that
        # allocated the temporary mesh
        evaluated.to_mesh_clear()


# --- bake: polygons instead of triangles ------------------------------------------

def evaluated_vertex_count(obj, depsgraph):
    """The vertex count of the EVALUATED mesh, without reading its data.

    Needed by bake to decide, BEFORE bake_begin, which objects exceed the
    rejection threshold: an object announced and then never sent only
    counts as "missing" to Revit, while one never announced can be
    reported skipped with its name and its vertex count. The count must be
    taken on the evaluated mesh, not on obj.data: an Array with 20 vertices
    can be worth 60.

    to_mesh() costs a copy of the evaluated mesh, done in C: it is the
    cheap part of extraction, the expensive parts are foreach_get and
    struct.pack."""
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    try:
        if mesh is None:
            return 0
        return len(mesh.vertices)
    finally:
        evaluated.to_mesh_clear()


def extract_polygons(obj, depsgraph, reject_over=0):
    """Returns (positions, face_sizes, face_vertices, tri_vertices,
    tri_faces, vertex_count): the POLYGONS of the evaluated mesh, for
    bake_mesh.

    Unlike extract, triangulation alone is not enough: Revit decides,
    polygon by polygon, whether to keep the whole face (a flat quad stays a
    quad, without a diagonal), and to fall back to triangles where the
    polygon is not planar it needs the ones Blender splits it into, with
    their owning polygon. See bridge_bake.py and the contract in
    docs/plans/2026-09-14-fase-b.md.

    The sequences have the shape pack_bake_payload expects, and the
    header's counts are derived from their lengths. LOCAL positions, as in
    extract: the matrix travels in the header.

    Same contract as extract on reject_over: past the threshold the five
    lists come back None and vertex_count is still valid. If to_mesh()
    produces nothing, five Nones and vertex_count zero."""
    evaluated = obj.evaluated_get(depsgraph)

    mesh = evaluated.to_mesh()
    if mesh is None:
        evaluated.to_mesh_clear()
        return None, None, None, None, None, 0

    try:
        vertex_count = len(mesh.vertices)
        if reject_over > 0 and vertex_count > reject_over:
            return None, None, None, None, None, vertex_count

        # same hasattr as extract, for the same reason
        if hasattr(mesh, "calc_loop_triangles"):
            mesh.calc_loop_triangles()

        positions = [0.0] * (vertex_count * 3)
        mesh.vertices.foreach_get("co", positions)

        face_count = len(mesh.polygons)
        face_sizes = [0] * face_count
        mesh.polygons.foreach_get("loop_total", face_sizes)
        loop_starts = [0] * face_count
        mesh.polygons.foreach_get("loop_start", loop_starts)

        face_vertices = [0] * len(mesh.loops)
        mesh.loops.foreach_get("vertex_index", face_vertices)

        triangle_count = len(mesh.loop_triangles)
        tri_vertices = [0] * (triangle_count * 3)
        mesh.loop_triangles.foreach_get("vertices", tri_vertices)
        tri_faces = [0] * triangle_count
        mesh.loop_triangles.foreach_get("polygon_index", tri_faces)

        # loops.vertex_index is "polygon after polygon" only if the loops
        # are contiguous in polygon order. Blender does this, but the API
        # does not promise it: if it does not hold, reorder instead of
        # sending vertices attributed to the wrong polygon with every
        # index still in range.
        if not bake.check_loop_layout(loop_starts, face_sizes):
            face_vertices = polygon_ordered_loops(face_vertices, loop_starts, face_sizes)

        return positions, face_sizes, face_vertices, tri_vertices, tri_faces, vertex_count
    finally:
        evaluated.to_mesh_clear()


def polygon_ordered_loops(loop_vertices, loop_starts, loop_totals):
    """loops.vertex_index rewritten polygon after polygon.

    This is the ONLY Python loop allowed in extraction, and it runs over
    POLYGONS, not vertices: each pass copies a slice with extend, which is
    C. This is only reached when check_loop_layout says the loops are not
    contiguous, which does not happen on a normal Blender mesh: in the
    common case this function is not called at all.

    Does not touch bpy: it receives the lists already read, so the smoke
    test can exercise it with a scrambled layout that Blender would never
    produce."""
    ordered = []
    for start, total in zip(loop_starts, loop_totals):
        ordered.extend(loop_vertices[start:start + total])
    return ordered
