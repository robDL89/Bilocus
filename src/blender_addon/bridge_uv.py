# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Metric UVs for pulled meshes. Revit sends no UVs, and a mesh without them
# cannot take a texture, nor give Unreal anything to build lightmap UVs
# from. Every face is projected on its own plane, one UV unit per meter:
# a 1 x 1 m texture shows at its real size on any element, and a wall at
# 45 degrees is not stretched the way box mapping stretches it. On walls
# and roofs u runs horizontal and v runs up the face, so bricks and boards
# stay level. Faces close to horizontal take world X and Y, so a floor or a
# slightly sloped ramp keeps its texture square to the building.
#
# Curved surfaces get a plane per face, so a texture breaks slightly
# between faces: a round column seen up close still wants a hand unwrap.
#
# This module must NOT import bpy: it is shared with the tests.

import math

import numpy as np

# Within 5 degrees of horizontal a face is a floor or a ceiling: world X
# and Y, distortion under 0.4%.
HORIZONTAL_COS = math.cos(math.radians(5.0))


def planar_uvs(coords, normals, loop_poly, loop_vert):
    """UVs per loop, shape (loops, 2), in meters.

    coords (vertices, 3) in the mesh's own frame, normals (polygons, 3),
    loop_poly and loop_vert the polygon and the vertex of every loop."""
    n = np.asarray(normals, dtype=np.float64)
    # u = Z x n, horizontal and to the right seen from outside; v = n x u
    u = np.stack([-n[:, 1], n[:, 0], np.zeros(len(n))], axis=1)
    length = np.linalg.norm(u, axis=1)
    # degenerate faces (zero normal) go with the horizontal ones: no NaN
    flat = (np.abs(n[:, 2]) >= HORIZONTAL_COS) | (length < 1e-9)
    u[flat] = (1.0, 0.0, 0.0)
    u[~flat] /= length[~flat, None]
    v = np.cross(n, u)
    v[flat] = (0.0, 1.0, 0.0)

    points = np.asarray(coords, dtype=np.float64)[loop_vert]
    return np.stack([np.einsum("ij,ij->i", points, u[loop_poly]),
                     np.einsum("ij,ij->i", points, v[loop_poly])], axis=1)
