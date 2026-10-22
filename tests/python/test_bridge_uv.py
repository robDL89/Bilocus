# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_uv.py, the metric UVs given to every pulled mesh.
# Does NOT import bpy: runs under pytest with the system Python and numpy.

import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import numpy as np
import pytest
import bridge_uv


def quad_uvs(corners, normal):
    """UVs of one quad given by its 4 corners, in loop order."""
    coords = np.array(corners, dtype=np.float64)
    normals = np.array([normal], dtype=np.float64)
    loop_poly = np.zeros(4, dtype=np.int64)
    loop_vert = np.arange(4)
    return bridge_uv.planar_uvs(coords, normals, loop_poly, loop_vert)


def test_wall_is_mapped_in_meters_u_along_v_up():
    # a 4 x 3 m wall facing -Y, seen from outside: u to the right, v up
    uvs = quad_uvs([(0, 0, 0), (4, 0, 0), (4, 0, 3), (0, 0, 3)], (0, -1, 0))
    assert np.allclose(uvs, [(0, 0), (4, 0), (4, 3), (0, 3)])


def test_rotated_wall_is_not_stretched():
    # a 2 x 3 m wall at 45 degrees: box mapping would shrink its length to 1.41
    c, s = math.cos(math.radians(45)), math.sin(math.radians(45))
    corners = [(0, 0, 0), (2 * c, 2 * s, 0), (2 * c, 2 * s, 3), (0, 0, 3)]
    uvs = quad_uvs(corners, (s, -c, 0))
    assert math.isclose(abs(uvs[1][0] - uvs[0][0]), 2.0, abs_tol=1e-9)
    assert math.isclose(uvs[1][1], uvs[0][1], abs_tol=1e-9), "u runs horizontal"
    assert math.isclose(uvs[3][1] - uvs[0][1], 3.0, abs_tol=1e-9), "v runs up, in meters"


def test_floor_and_its_underside_use_world_x_y():
    corners = [(1, 2, 0), (3, 2, 0), (3, 5, 0), (1, 5, 0)]
    expected = [(1, 2), (3, 2), (3, 5), (1, 5)]
    assert np.allclose(quad_uvs(corners, (0, 0, 1)), expected)
    assert np.allclose(quad_uvs(corners, (0, 0, -1)), expected)


def test_a_nearly_flat_floor_keeps_world_x_y():
    # a 2 degree ramp: a tangent frame would turn the floor texture with the slope
    n = (0.0, -math.sin(math.radians(2)), math.cos(math.radians(2)))
    corners = [(0, 0, 0), (2, 0, 0), (2, 1, 0.035), (0, 1, 0.035)]
    uvs = quad_uvs(corners, n)
    assert np.allclose(uvs[:, 0], [0, 2, 2, 0])


def test_roof_runs_u_along_the_eave_and_v_up_the_slope():
    # a 30 degree pitch rising toward +Y, eave along X
    a = math.radians(30)
    up = (0.0, math.cos(a), math.sin(a))
    corners = [(0, 0, 0), (5, 0, 0), (5, 2 * up[1], 2 * up[2]), (0, 2 * up[1], 2 * up[2])]
    uvs = quad_uvs(corners, (0.0, -math.sin(a), math.cos(a)))
    assert math.isclose(uvs[1][1], uvs[0][1], abs_tol=1e-9), "eave is horizontal in u"
    assert math.isclose(abs(uvs[1][0] - uvs[0][0]), 5.0, abs_tol=1e-9)
    assert math.isclose(uvs[3][1] - uvs[0][1], 2.0, abs_tol=1e-9), "slope length, in meters"


def test_degenerate_face_does_not_produce_nan():
    uvs = quad_uvs([(0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0)], (0, 0, 0))
    assert np.all(np.isfinite(uvs))


def test_loops_follow_their_own_polygon():
    # two faces sharing a vertex: each loop is projected with its own face
    coords = np.array([(0, 0, 0), (1, 0, 0), (1, 0, 1), (1, 1, 0)], dtype=np.float64)
    normals = np.array([(0, -1, 0), (0, 0, 1)], dtype=np.float64)
    loop_poly = np.array([0, 0, 0, 1, 1, 1])
    loop_vert = np.array([0, 1, 2, 0, 1, 3])
    uvs = bridge_uv.planar_uvs(coords, normals, loop_poly, loop_vert)
    assert np.allclose(uvs, [(0, 0), (1, 0), (1, 1), (0, 0), (1, 0), (1, 1)])
