# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_fit.py, the arc-and-line approximation of the
# "Arcs and lines" mode of Create proxy, and of the arc wire contract in
# bridge_edges.py.
# Does NOT import bpy: runs under pytest with the system Python.

import math
import os
import struct
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_edges
import bridge_fit
from bridge_protocol import BridgeFramingError


def polyline_edges(points):
    return [(points[k], points[k + 1]) for k in range(len(points) - 1)]


def circle_points(radius, start_deg, end_deg, count, center=(0.0, 0.0, 0.0), z_rise=0.0):
    points = []
    for k in range(count + 1):
        angle = math.radians(start_deg + (end_deg - start_deg) * k / count)
        points.append((center[0] + radius * math.cos(angle),
                       center[1] + radius * math.sin(angle),
                       center[2] + z_rise * k / count))
    return points


def distance(a, b):
    return math.sqrt(sum((a[i] - b[i]) ** 2 for i in range(3)))


def endpoints(lines, arcs):
    result = []
    for start, end in lines:
        result.append((start, end))
    for start, end, _mid in arcs:
        result.append((start, end))
    return result


# --- deviation ---------------------------------------------------------------

def test_zero_tolerance_uses_the_minimum_and_says_so():
    meters, minimum = bridge_fit.resolve_tolerance(0.0)
    assert minimum is True
    assert meters == pytest.approx(bridge_fit.MIN_TOLERANCE_MM / 1000.0)


def test_positive_tolerance_is_converted_to_meters():
    meters, minimum = bridge_fit.resolve_tolerance(5.0)
    assert minimum is False
    assert meters == pytest.approx(0.005)


@pytest.mark.parametrize("value", [-1.0, float("nan"), float("inf")])
def test_invalid_tolerance_is_rejected(value):
    with pytest.raises(ValueError):
        bridge_fit.resolve_tolerance(value)


# --- chains --------------------------------------------------------------------

def test_edges_in_any_order_become_one_open_chain():
    a, b, c, d = (0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 0, 0)
    chains = bridge_fit.build_chains([(c, d), (a, b), (c, b)])
    assert len(chains) == 1
    assert chains[0] in ([a, b, c, d], [d, c, b, a])


def test_a_branch_splits_the_chains():
    center = (0, 0, 0)
    chains = bridge_fit.build_chains([
        (center, (1, 0, 0)), (center, (0, 1, 0)), (center, (-1, 0, 0))])
    assert len(chains) == 3


def test_a_closed_loop_starts_and_ends_on_the_same_point():
    square = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 0)]
    chains = bridge_fit.build_chains(polyline_edges(square))
    assert len(chains) == 1
    assert chains[0][0] == chains[0][-1]
    assert len(chains[0]) == 5


# --- lines ---------------------------------------------------------------------

def test_collinear_edges_collapse_into_one_line():
    points = [(float(k), 0.0, 0.0) for k in range(11)]
    lines, arcs, chains = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert chains == 1
    assert arcs == []
    assert lines == [((0.0, 0.0, 0.0), (10.0, 0.0, 0.0))]


def test_a_corner_keeps_two_lines():
    points = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (2.0, 0.0, 0.0),
              (2.0, 1.0, 0.0), (2.0, 2.0, 0.0)]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert arcs == []
    assert lines == [((0.0, 0.0, 0.0), (2.0, 0.0, 0.0)),
                     ((2.0, 0.0, 0.0), (2.0, 2.0, 0.0))]


def test_a_line_that_goes_back_on_itself_is_not_merged():
    points = [(0.0, 0.0, 0.0), (2.0, 0.0, 0.0), (1.0, 0.0, 0.0)]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert len(lines) == 2
    assert arcs == []


# --- arcs ---------------------------------------------------------------------

def test_a_quarter_circle_becomes_one_arc():
    points = circle_points(3.0, 0.0, 90.0, 24)
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert lines == []
    assert len(arcs) == 1
    start, end, mid = arcs[0]
    assert start == points[0]
    assert end == points[-1]
    # the point on the wire sits on the real circle, at half sweep
    expected = (3.0 * math.cos(math.radians(45)), 3.0 * math.sin(math.radians(45)), 0.0)
    assert distance(mid, expected) < 1e-9


def test_an_arc_endpoints_are_original_vertices():
    points = circle_points(2.0, 10.0, 170.0, 40)
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.0005)
    for start, end in endpoints(lines, arcs):
        assert start in points
        assert end in points


def test_a_full_circle_is_split_into_arcs_of_at_most_half_a_turn():
    points = circle_points(1.5, 0.0, 360.0, 64)
    points[-1] = points[0]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert lines == []
    assert 2 <= len(arcs) <= 3


def arc_plane_kind(arc):
    """'plan', 'section' or None: which allowed plane the arc sits on, read
    from its three points the way Revit does."""
    start, end, mid = arc
    a = tuple(mid[i] - start[i] for i in range(3))
    b = tuple(end[i] - start[i] for i in range(3))
    n = (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
    length = math.sqrt(sum(c * c for c in n))
    n = tuple(c / length for c in n)
    horizontal = math.sqrt(b[0] * b[0] + b[1] * b[1])
    if horizontal > 1e-9:
        side = (b[1] / horizontal, -b[0] / horizontal, 0.0)
        if abs(sum(n[i] * side[i] for i in range(3))) < 1e-6:
            return 'plan'
    if abs(n[2]) < 1e-6:
        return 'section'
    return None


# Measured in the field on 2026-09-16: an arc on a free plane in space
# makes a beam picked up with Pick Lines roll. A plane tilted sideways is
# not allowed: it becomes more segments, and the arcs that remain sit on
# allowed planes.
def test_an_arc_banked_sideways_is_not_kept_on_its_own_plane():
    tilt = math.radians(30.0)
    flat = circle_points(4.0, 0.0, 90.0, 30)
    points = [(x, y * math.cos(tilt), y * math.sin(tilt) + 5.0) for x, y, _z in flat]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert len(lines) + len(arcs) > 1
    for arc in arcs:
        assert arc_plane_kind(arc) is not None


def test_a_plan_arc_at_height_stays_one_arc_on_the_plan_plane():
    points = [(x, y, 5.0) for x, y, _z in circle_points(3.0, 165.0, 15.0, 40)]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert lines == []
    assert len(arcs) == 1
    assert arc_plane_kind(arcs[0]) == 'plan'


def test_an_elevation_arc_stays_one_arc_on_the_section_plane():
    points = [(r * 0.7071067811865476, r * 0.7071067811865476, z)
              for r, _y, z in [(x, 0.0, y) for x, y, _z in circle_points(5.0, 180.0, 0.0, 30)]]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert lines == []
    assert len(arcs) == 1
    assert arc_plane_kind(arcs[0]) == 'section'


def test_a_descending_s_curve_gives_only_admitted_planes():
    # the field case: an S in plan view that descends 2.3 m
    first = circle_points(6.0, 180.0, 90.0, 40, center=(6.0, 0.0, 0.0))
    second = circle_points(6.0, 270.0, 360.0, 40, center=(6.0, 12.0, 0.0))
    flat = first + second[1:]
    count = len(flat) - 1
    points = [(x, y, 2.3 * (1.0 - float(k) / count)) for k, (x, y, _z) in enumerate(flat)]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.002)
    assert arcs
    for arc in arcs:
        assert arc_plane_kind(arc) is not None


def test_a_horizontal_plan_curve_has_vertical_normals():
    points = circle_points(3.0, 0.0, 90.0, 24)
    _lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert arc_plane_kind(arcs[0]) == 'plan'


def test_plane_normals_for_a_vertical_chord_use_the_middle_point():
    normals = bridge_fit.arc_plane_normals((0.0, 0.0, 0.0), (1.0, 0.0, 1.0), (0.0, 0.0, 2.0))
    assert len(normals) == 1
    assert abs(normals[0][2]) < 1e-12
    assert abs(normals[0][0]) < 1e-12


def test_a_helix_is_not_forced_into_one_arc():
    # rises 1 m over a quarter turn: no flat arc contains it within 1 mm
    points = circle_points(3.0, 0.0, 90.0, 30, z_rise=1.0)
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert len(lines) + len(arcs) > 1


def test_an_s_curve_becomes_two_arcs():
    first = circle_points(2.0, 180.0, 90.0, 20, center=(2.0, 0.0, 0.0))
    second = circle_points(2.0, 270.0, 360.0, 20, center=(2.0, 4.0, 0.0))
    points = first + second[1:]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert lines == []
    assert len(arcs) == 2


def test_line_then_arc_then_line():
    straight_in = [(float(-k), 0.0, 0.0) for k in range(5, 0, -1)]
    bend = circle_points(1.0, 270.0, 360.0, 16, center=(0.0, 1.0, 0.0))
    straight_out = [(1.0, 1.0 + k, 0.0) for k in range(1, 6)]
    points = straight_in + bend + straight_out
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.001)
    assert len(arcs) == 1
    assert len(lines) == 2


def test_a_coarse_curve_with_a_tight_tolerance_keeps_its_segments():
    # eight sides over half a turn with 0.1 mm: the vertices DO sit on a
    # circle, so even with the minimum deviation the arc holds up
    points = circle_points(1.0, 0.0, 180.0, 8)
    _lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(points), 0.0001)
    assert len(arcs) == 1

    # vertices shifted 1 mm alternately: with 0.1 mm nothing holds up
    noisy = [(x + (0.001 if k % 2 else 0.0), y, z) for k, (x, y, z) in enumerate(points)]
    lines, arcs, _ = bridge_fit.fit_edges(polyline_edges(noisy), 0.0001)
    assert len(lines) + len(arcs) >= 4


def test_the_same_edges_give_the_same_result_twice():
    points = circle_points(2.0, 0.0, 135.0, 33)
    edges = polyline_edges(points)
    assert bridge_fit.fit_edges(edges, 0.001) == bridge_fit.fit_edges(edges, 0.001)


def test_three_collinear_points_have_no_circle():
    assert bridge_fit.circle_through((0, 0, 0), (1, 0, 0), (2, 0, 0)) is None


# --- arc wire contract ----------------------------------------------

def test_header_without_arcs_is_unchanged():
    header = bridge_edges.build_proxy_edges_header("id", "Cube", 2)
    assert header == {"type": "proxy_edges", "obj_id": "id", "name": "Cube", "edge_count": 2}


def test_header_with_arcs_carries_arc_count():
    header = bridge_edges.build_proxy_edges_header("id", "Cube", 1, 3)
    assert header["edge_count"] == 1
    assert header["arc_count"] == 3


def test_header_accepts_only_arcs():
    header = bridge_edges.build_proxy_edges_header("id", "Cerchio", 0, 2)
    assert header["edge_count"] == 0
    assert header["arc_count"] == 2


def test_header_rejects_nothing_at_all():
    with pytest.raises(BridgeFramingError):
        bridge_edges.build_proxy_edges_header("id", "Cube", 0, 0)


def test_header_counts_arcs_in_the_maximum():
    with pytest.raises(BridgeFramingError):
        bridge_edges.build_proxy_edges_header("id", "Cube", bridge_edges.MAX_EDGES, 1)


def test_payload_without_arcs_is_the_polyline_payload():
    edges = [((0.0, 0.0, 0.0), (1.0, 0.0, 0.0))]
    assert bridge_edges.pack_proxy_payload(edges, []) == bridge_edges.pack_edge_payload(edges)


def test_payload_puts_arcs_after_edges_as_start_end_mid():
    edges = [((0.0, 0.0, 0.0), (1.0, 0.0, 0.0))]
    arcs = [((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.5, 0.5, 0.25))]
    payload = bridge_edges.pack_proxy_payload(edges, arcs)
    assert len(payload) == bridge_edges.BYTES_PER_EDGE + bridge_edges.BYTES_PER_ARC
    values = struct.unpack("<15f", payload)
    assert values[6:] == (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.5, 0.5, 0.25)


def test_payload_with_only_arcs():
    arcs = [((1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (0.0, 1.0, 0.0))]
    payload = bridge_edges.pack_proxy_payload([], arcs)
    assert len(payload) == bridge_edges.BYTES_PER_ARC


def test_payload_rejects_non_finite_arc_coordinates():
    arcs = [((1.0, 0.0, 0.0), (-1.0, 0.0, 0.0), (0.0, float("nan"), 0.0))]
    with pytest.raises(BridgeFramingError):
        bridge_edges.pack_proxy_payload([], arcs)
