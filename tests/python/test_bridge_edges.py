# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_edges.py, the preparation and packing of edges for the
# proxy_edges message.
# Does NOT import bpy: runs under pytest with the system Python, like
# test_bridge_mesh.py.

import binascii
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_edges
from bridge_protocol import BridgeFramingError


# --- agreement with the C# side -------------------------------------------
#
# The expected bytes were computed with a separate script (not written from
# memory), packing the same two edges with struct.pack in isolation from
# bridge_edges.py: see the task report for the script used.
#
# Two edges: (0,0,0)-(1,0,0) and (1,0,0)-(1,2.5,-0.5). The second has
# non-trivial components on purpose: 2.5 and -0.5 are exact in float32 but
# have non-zero bytes in three positions out of four, so an ordering error
# (big-endian) or a stride error would show up.

TWO_EDGES = [
    ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
    ((1.0, 0.0, 0.0), (1.0, 2.5, -0.5)),
]

TWO_EDGES_PAYLOAD_HEX = (
    "0000000000000000000000000000803f00000000000000000000803f00000000"
    "000000000000803f00002040000000bf"
)


def test_two_edges_payload_matches_golden_bytes():
    produced = bridge_edges.pack_edge_payload(TWO_EDGES)
    assert binascii.hexlify(produced).decode("ascii") == TWO_EDGES_PAYLOAD_HEX


# --- payload length consistent with edge_count --------------------------

def test_payload_length_matches_edge_count_formula():
    # the same formula used by ProxyEdgeRequest.Parse on the C# side:
    # edge_count * 6 float32 = edge_count * 24 bytes
    produced = bridge_edges.pack_edge_payload(TWO_EDGES)
    assert len(produced) == len(TWO_EDGES) * bridge_edges.BYTES_PER_EDGE
    assert len(produced) == 48


def test_wire_constants_match_the_csharp_side():
    # ProxyEdgeRequest.FloatsPerEdge, BytesPerEdge and MaxEdges. Written
    # twice, here and in C#, and this test is the only place where the
    # divergence shows up without opening Revit.
    assert bridge_edges.FLOATS_PER_EDGE == 6
    assert bridge_edges.BYTES_PER_EDGE == 24
    assert bridge_edges.MAX_EDGES == 10000


# --- degenerate edge filter ----------------------------------------------

def test_zero_length_edge_is_dropped_and_counted():
    edges = [
        ((0.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
        ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
    ]
    kept, dropped = bridge_edges.prepare_edges(edges)
    assert kept == [((0.0, 0.0, 0.0), (1.0, 0.0, 0.0))]
    assert dropped["short"] == 1
    assert dropped["duplicate"] == 0
    assert dropped["invalid"] == 0


def test_edge_below_the_given_tolerance_is_dropped():
    edges = [((0.0, 0.0, 0.0), (0.0005, 0.0, 0.0))]
    kept, dropped = bridge_edges.prepare_edges(edges, min_length=0.001)
    assert kept == []
    assert dropped["short"] == 1


def test_edge_above_the_given_tolerance_survives():
    edges = [((0.0, 0.0, 0.0), (0.002, 0.0, 0.0))]
    kept, dropped = bridge_edges.prepare_edges(edges, min_length=0.001)
    assert len(kept) == 1
    assert dropped["short"] == 0


def test_default_tolerance_is_far_below_the_revit_one():
    # The default threshold only removes edges of exactly zero length. The
    # real one is applied by ProxyBuilder by reading
    # Application.ShortCurveTolerance (about 0.8 mm): if this default got
    # close to it, the Blender side would start throwing away edges Revit
    # would have accepted, without anyone knowing.
    assert bridge_edges.MIN_EDGE_LENGTH < 1e-4


# --- duplicates --------------------------------------------------------------

def test_exact_duplicate_is_dropped_and_counted():
    edges = [
        ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
        ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
    ]
    kept, dropped = bridge_edges.prepare_edges(edges)
    assert len(kept) == 1
    assert dropped["duplicate"] == 1


def test_reversed_duplicate_is_dropped_too():
    # Two edges with the same endpoints in opposite order produce the exact
    # same line inside Revit: keeping both would mean two overlapping
    # ModelCurves, which nobody wants and nobody sees.
    edges = [
        ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
        ((1.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
    ]
    kept, dropped = bridge_edges.prepare_edges(edges)
    assert len(kept) == 1
    assert dropped["duplicate"] == 1


def test_first_occurrence_keeps_its_orientation_and_position():
    edges = [
        ((1.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
        ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
        ((0.0, 0.0, 0.0), (0.0, 1.0, 0.0)),
    ]
    kept, _ = bridge_edges.prepare_edges(edges)
    # arrival order preserved and the first one's direction kept: two
    # consecutive sends of the same selection must produce the same bytes,
    # otherwise a diff between two captures means nothing
    assert kept == [
        ((1.0, 0.0, 0.0), (0.0, 0.0, 0.0)),
        ((0.0, 0.0, 0.0), (0.0, 1.0, 0.0)),
    ]


def test_nearby_but_distinct_edges_are_not_merged():
    edges = [
        ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
        ((0.0, 0.0, 0.0001), (1.0, 0.0, 0.0001)),
    ]
    kept, dropped = bridge_edges.prepare_edges(edges)
    assert len(kept) == 2
    assert dropped["duplicate"] == 0


# --- non-finite coordinates --------------------------------------------------

def test_non_finite_edge_is_dropped_and_counted():
    # A NaN would make ProxyEdgeRequest.Parse reject the ENTIRE message:
    # discarding only the sick edge here saves all the others.
    edges = [
        ((0.0, 0.0, 0.0), (float("nan"), 0.0, 0.0)),
        ((0.0, 0.0, 0.0), (float("inf"), 0.0, 0.0)),
        ((0.0, 0.0, 0.0), (1.0, 0.0, 0.0)),
    ]
    kept, dropped = bridge_edges.prepare_edges(edges)
    assert len(kept) == 1
    assert dropped["invalid"] == 2


# --- edge shape ----------------------------------------------------

def test_malformed_edge_is_rejected():
    with pytest.raises(BridgeFramingError):
        bridge_edges.prepare_edges([((0.0, 0.0), (1.0, 0.0, 0.0))])


def test_edge_with_three_points_is_rejected():
    with pytest.raises(BridgeFramingError):
        bridge_edges.prepare_edges([((0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (2.0, 0.0, 0.0))])


# --- pack limits --------------------------------------------------------

def test_pack_rejects_an_empty_list():
    # edge_count zero is a content error on the Revit side, not a removal:
    # it must not even be sent.
    with pytest.raises(BridgeFramingError):
        bridge_edges.pack_edge_payload([])


def test_pack_rejects_more_than_the_maximum():
    edges = [((0.0, 0.0, float(i)), (1.0, 0.0, float(i)))
             for i in range(bridge_edges.MAX_EDGES + 1)]
    with pytest.raises(BridgeFramingError):
        bridge_edges.pack_edge_payload(edges)


def test_pack_accepts_exactly_the_maximum():
    edges = [((0.0, 0.0, float(i)), (1.0, 0.0, float(i)))
             for i in range(bridge_edges.MAX_EDGES)]
    produced = bridge_edges.pack_edge_payload(edges)
    assert len(produced) == bridge_edges.MAX_EDGES * bridge_edges.BYTES_PER_EDGE


def test_pack_rejects_non_finite_coordinates():
    # last line of defense: prepare_edges has already removed these, but
    # pack must not trust it is always called after it
    with pytest.raises(BridgeFramingError):
        bridge_edges.pack_edge_payload([((0.0, 0.0, 0.0), (float("nan"), 0.0, 0.0))])


# --- header -----------------------------------------------------------------

def test_header_has_the_four_fields_of_the_contract():
    header = bridge_edges.build_proxy_edges_header("abc123", "Cube", 2)
    assert header == {
        "type": "proxy_edges",
        "obj_id": "abc123",
        "name": "Cube",
        "edge_count": 2,
    }


def test_header_trims_the_object_id():
    # ProxyNaming.NormalizeObjectId trims on the Revit side: sending it
    # already normalized avoids the mark written in the document being
    # different from the one that will be looked up on replacement
    header = bridge_edges.build_proxy_edges_header("  abc123  ", "Cube", 1)
    assert header["obj_id"] == "abc123"


def test_header_rejects_an_empty_object_id():
    with pytest.raises(BridgeFramingError):
        bridge_edges.build_proxy_edges_header("   ", "Cube", 1)


def test_header_rejects_control_characters_in_the_object_id():
    with pytest.raises(BridgeFramingError):
        bridge_edges.build_proxy_edges_header("ab\nc", "Cube", 1)


def test_header_falls_back_to_the_object_id_when_the_name_is_empty():
    header = bridge_edges.build_proxy_edges_header("abc123", "   ", 1)
    assert header["name"] == "abc123"


def test_header_rejects_a_non_positive_edge_count():
    with pytest.raises(BridgeFramingError):
        bridge_edges.build_proxy_edges_header("abc123", "Cube", 0)


def test_header_rejects_an_edge_count_over_the_maximum():
    with pytest.raises(BridgeFramingError):
        bridge_edges.build_proxy_edges_header("abc123", "Cube", bridge_edges.MAX_EDGES + 1)


# --- transformation into world coordinates -------------------------------------
#
# The matrix comes from obj.matrix_world as four rows of four. The
# calculation lives here and not in bridge_proxy.py on purpose: it is the
# only part of the proxy path that can be verified without Blender.

def _identity():
    return [[1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, 1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0]]


def test_identity_matrix_leaves_the_point_where_it_is():
    assert bridge_edges.transform_point(_identity(), 1.0, 2.0, 3.0) == (1.0, 2.0, 3.0)


def test_translation_moves_the_point():
    matrix = _identity()
    matrix[0][3] = 10.0
    matrix[1][3] = -5.0
    matrix[2][3] = 0.5
    assert bridge_edges.transform_point(matrix, 1.0, 2.0, 3.0) == (11.0, -3.0, 3.5)


def test_rotation_of_ninety_degrees_around_z():
    # row by row, the +90 degree rotation matrix around Z:
    # x' = -y, y' = x, z' = z
    matrix = [[0.0, -1.0, 0.0, 0.0],
              [1.0, 0.0, 0.0, 0.0],
              [0.0, 0.0, 1.0, 0.0],
              [0.0, 0.0, 0.0, 1.0]]
    assert bridge_edges.transform_point(matrix, 1.0, 0.0, 2.0) == (0.0, 1.0, 2.0)


def test_scale_and_translation_together():
    matrix = [[2.0, 0.0, 0.0, 1.0],
              [0.0, 3.0, 0.0, 0.0],
              [0.0, 0.0, 4.0, -1.0],
              [0.0, 0.0, 0.0, 1.0]]
    assert bridge_edges.transform_point(matrix, 1.0, 1.0, 1.0) == (3.0, 3.0, 3.0)


def test_transform_point_rejects_a_matrix_of_the_wrong_shape():
    with pytest.raises(BridgeFramingError):
        bridge_edges.transform_point([[1.0, 0.0], [0.0, 1.0]], 1.0, 2.0, 3.0)


# --- summary for the panel ----------------------------------------------

def test_summary_counts_what_survived():
    text = bridge_edges.describe_prepared(3, {"short": 0, "duplicate": 0, "invalid": 0})
    assert "3" in text


def test_summary_mentions_what_was_dropped():
    text = bridge_edges.describe_prepared(3, {"short": 2, "duplicate": 1, "invalid": 0})
    assert "2" in text and "1" in text
