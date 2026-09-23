# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_mesh.py, the pure packing of the geometry.
# Does NOT import bpy: runs under pytest with the system Python, like
# test_protocol.py and test_bridge_client.py.

import binascii
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_mesh
from bridge_protocol import BridgeFramingError


# --- agreement with the C# side -------------------------------------------------
#
# The expected bytes were computed with a separate script (not written from
# memory), packing the same triangle with struct.pack in isolation from
# bridge_mesh.py.
#
# Triangle: 3 vertices (0,0,0) (1,0,0) (0,1,0), normals all (0,0,1),
# a single triangle with indices [0, 1, 2].

TRIANGLE_POSITIONS = [0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0]
TRIANGLE_NORMALS = [0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0]
TRIANGLE_INDICES = [0, 1, 2]

TRIANGLE_PAYLOAD_HEX = (
    "0000000000000000000000000000803f0000000000000000000000000000803f"
    "0000000000000000000000000000803f00000000000000000000803f00000000"
    "000000000000803f000000000100000002000000"
)


def test_triangle_payload_matches_golden_bytes():
    produced = bridge_mesh.pack_mesh_payload(TRIANGLE_POSITIONS, TRIANGLE_NORMALS, TRIANGLE_INDICES)
    assert binascii.hexlify(produced).decode("ascii") == TRIANGLE_PAYLOAD_HEX


# --- payload length consistent with the declared counts ---------------

def test_payload_length_matches_header_counts_formula():
    # the same formula used by MeshPayload.Parse on the C# side:
    # vert_count * 3 * 4 * 2 (positions + normals) + tri_count * 3 * 4 (indices)
    produced = bridge_mesh.pack_mesh_payload(TRIANGLE_POSITIONS, TRIANGLE_NORMALS, TRIANGLE_INDICES)
    vert_count = len(TRIANGLE_POSITIONS) // 3
    tri_count = len(TRIANGLE_INDICES) // 3
    expected_length = vert_count * 3 * 4 * 2 + tri_count * 3 * 4
    assert len(produced) == expected_length


def test_payload_length_for_larger_mesh():
    # two triangles, four vertices: verifies that the formula holds beyond
    # the minimal single-triangle case
    positions = [0.0] * 12
    normals = [0.0] * 12
    indices = [0, 1, 2, 1, 2, 3]
    produced = bridge_mesh.pack_mesh_payload(positions, normals, indices)
    assert len(produced) == 4 * 3 * 4 * 2 + 2 * 3 * 4


# --- empty arrays (zero counts) --------------------------------------------

def test_empty_arrays_produce_empty_payload():
    produced = bridge_mesh.pack_mesh_payload([], [], [])
    assert produced == b""


# --- rejections: inconsistent lengths ------------------------------------------

def test_rejects_normals_length_mismatch_with_positions():
    with pytest.raises(BridgeFramingError):
        bridge_mesh.pack_mesh_payload([0.0, 0.0, 0.0], [0.0, 0.0], [])


def test_rejects_positions_length_not_multiple_of_three():
    with pytest.raises(BridgeFramingError):
        bridge_mesh.pack_mesh_payload([0.0, 0.0], [0.0, 0.0], [])


def test_rejects_indices_length_not_multiple_of_three():
    with pytest.raises(BridgeFramingError):
        bridge_mesh.pack_mesh_payload(TRIANGLE_POSITIONS, TRIANGLE_NORMALS, [0, 1])


def test_rejects_index_out_of_range_above_vertex_count():
    with pytest.raises(BridgeFramingError):
        bridge_mesh.pack_mesh_payload(TRIANGLE_POSITIONS, TRIANGLE_NORMALS, [0, 1, 3])


def test_rejects_negative_index():
    with pytest.raises(BridgeFramingError):
        bridge_mesh.pack_mesh_payload(TRIANGLE_POSITIONS, TRIANGLE_NORMALS, [0, 1, -1])


# --- matrix_to_list ----------------------------------------------------------

def test_matrix_to_list_converts_valid_4x4_row_major():
    matrix = [
        [1.0, 0.0, 0.0, 5.0],
        [0.0, 1.0, 0.0, 6.0],
        [0.0, 0.0, 1.0, 7.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
    flat = bridge_mesh.matrix_to_list(matrix)
    assert flat == [
        1.0, 0.0, 0.0, 5.0,
        0.0, 1.0, 0.0, 6.0,
        0.0, 0.0, 1.0, 7.0,
        0.0, 0.0, 0.0, 1.0,
    ]


def test_matrix_to_list_coerces_values_to_float():
    matrix = [
        [1, 0, 0, 0],
        [0, 1, 0, 0],
        [0, 0, 1, 0],
        [0, 0, 0, 1],
    ]
    flat = bridge_mesh.matrix_to_list(matrix)
    assert all(isinstance(value, float) for value in flat)
    assert flat == [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]


def test_matrix_to_list_rejects_wrong_element_count():
    # 3x3 matrix instead of 4x4: 9 elements instead of 16
    matrix = [
        [1.0, 0.0, 0.0],
        [0.0, 1.0, 0.0],
        [0.0, 0.0, 1.0],
    ]
    with pytest.raises(BridgeFramingError):
        bridge_mesh.matrix_to_list(matrix)


def test_matrix_to_list_rejects_ragged_rows():
    # rows of different length: 4 + 4 + 4 + 3 = 15 elements instead of 16
    matrix = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0],
    ]
    with pytest.raises(BridgeFramingError):
        bridge_mesh.matrix_to_list(matrix)


# --- flat_matrix -------------------------------------------------------------

def test_flat_matrix_accepts_nested_rows():
    matrix = [
        [1.0, 0.0, 0.0, 5.0],
        [0.0, 1.0, 0.0, 6.0],
        [0.0, 0.0, 1.0, 7.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
    assert bridge_mesh.flat_matrix(matrix) == bridge_mesh.matrix_to_list(matrix)


def test_flat_matrix_accepts_already_flat_sixteen():
    flat = [float(i) for i in range(16)]
    assert bridge_mesh.flat_matrix(flat) == flat


def test_flat_matrix_coerces_flat_integers_to_float():
    flat = list(range(16))
    produced = bridge_mesh.flat_matrix(flat)
    assert all(isinstance(value, float) for value in produced)


def test_flat_matrix_rejects_wrong_size():
    with pytest.raises(BridgeFramingError):
        bridge_mesh.flat_matrix([0.0] * 9)


# --- safety threshold -----------------------------------------------------

def test_vertex_budget_ok_below_both_limits():
    assert bridge_mesh.check_vertex_budget(1000, 250000, 1000000) == bridge_mesh.BUDGET_OK


def test_vertex_budget_warn_above_warn_limit():
    assert bridge_mesh.check_vertex_budget(250001, 250000, 1000000) == bridge_mesh.BUDGET_WARN


def test_vertex_budget_reject_above_hard_limit():
    assert bridge_mesh.check_vertex_budget(1000001, 250000, 1000000) == bridge_mesh.BUDGET_REJECT


def test_vertex_budget_limit_is_inclusive():
    # exactly on the threshold it still passes: the threshold is "beyond", not "from"
    assert bridge_mesh.check_vertex_budget(250000, 250000, 1000000) == bridge_mesh.BUDGET_OK
    assert bridge_mesh.check_vertex_budget(1000000, 250000, 1000000) == bridge_mesh.BUDGET_WARN


def test_vertex_budget_zero_disables_the_limit():
    assert bridge_mesh.check_vertex_budget(10 ** 9, 0, 0) == bridge_mesh.BUDGET_OK
    assert bridge_mesh.check_vertex_budget(10 ** 9, 0, 1000000) == bridge_mesh.BUDGET_REJECT


# --- resolve_ids -------------------------------------------------------------

def _counter_factory():
    state = {"n": 0}

    def make_id():
        state["n"] += 1
        return "generated-{}".format(state["n"])

    return make_id


def test_resolve_ids_keeps_existing_distinct_ids():
    ids, changed = bridge_mesh.resolve_ids(["a", "b", "c"], _counter_factory())
    assert ids == ["a", "b", "c"]
    assert changed == []


def test_resolve_ids_generates_for_missing():
    ids, changed = bridge_mesh.resolve_ids([None, "b", ""], _counter_factory())
    assert ids == ["generated-1", "b", "generated-2"]
    assert changed == [0, 2]


def test_resolve_ids_breaks_duplicates_keeping_the_first():
    # the Shift+D case: the copy carries the original's bilocus_id with it
    ids, changed = bridge_mesh.resolve_ids(["a", "a"], _counter_factory())
    assert ids[0] == "a"
    assert ids[1] == "generated-1"
    assert changed == [1]


def test_resolve_ids_breaks_a_chain_of_duplicates():
    ids, changed = bridge_mesh.resolve_ids(["a", "a", "a"], _counter_factory())
    assert ids == ["a", "generated-1", "generated-2"]
    assert len(set(ids)) == 3


def test_resolve_ids_rejects_non_string_values():
    # a custom property can contain an integer if someone writes it by hand
    ids, changed = bridge_mesh.resolve_ids([17], _counter_factory())
    assert ids == ["generated-1"]
    assert changed == [0]


def test_resolve_ids_on_empty_input():
    assert bridge_mesh.resolve_ids([], _counter_factory()) == ([], [])


# --- normalize_color ---------------------------------------------------------


def test_normalize_color_passes_through_rgba():
    assert bridge_mesh.normalize_color([0.1, 0.2, 0.3, 0.4]) == [0.1, 0.2, 0.3, 0.4]


def test_normalize_color_fills_missing_alpha():
    assert bridge_mesh.normalize_color([0.1, 0.2, 0.3]) == [0.1, 0.2, 0.3, 1.0]


def test_normalize_color_clamps_out_of_range():
    assert bridge_mesh.normalize_color([-1.0, 2.0, 0.5, 9.0]) == [0.0, 1.0, 0.5, 1.0]


def test_normalize_color_rejects_wrong_component_count():
    with pytest.raises(BridgeFramingError):
        bridge_mesh.normalize_color([0.1, 0.2])


# --- header: contract with MessageRouter.cs ----------------------------------
#
# The field names below are transcribed from
# src/Bilocus.Revit/Net/MessageRouter.cs. They are the only other copy of
# the contract: if someone changes them on one side only, these tests fail.

IDENTITY = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
]


def test_geometry_header_field_names_and_types():
    header = bridge_mesh.build_geometry_header(
        "abc", "Cube", 8, 12, [0.5, 0.5, 0.5, 1.0], IDENTITY)
    assert header["type"] == "geometry"
    assert header["obj_id"] == "abc"
    assert header["name"] == "Cube"
    assert header["vert_count"] == 8
    assert header["tri_count"] == 12
    assert header["color"] == [0.5, 0.5, 0.5, 1.0]
    assert len(header["matrix"]) == 16
    assert set(header.keys()) == {
        "type", "obj_id", "name", "vert_count", "tri_count", "color", "matrix"}


def test_geometry_header_counts_are_integers():
    # RequireInt on the C# side rejects a JSON number with a decimal point
    header = bridge_mesh.build_geometry_header(
        "abc", "Cube", 8.0, 12.0, bridge_mesh.DEFAULT_COLOR, IDENTITY)
    assert isinstance(header["vert_count"], int)
    assert isinstance(header["tri_count"], int)


def test_transform_header_carries_only_id_and_matrix():
    header = bridge_mesh.build_transform_header("abc", IDENTITY)
    assert set(header.keys()) == {"type", "obj_id", "matrix"}
    assert header["type"] == "transform"


def test_transform_header_accepts_a_flat_matrix():
    flat = bridge_mesh.matrix_to_list(IDENTITY)
    assert bridge_mesh.build_transform_header("abc", flat)["matrix"] == flat


def test_sync_begin_header_coerces_ids_to_strings():
    header = bridge_mesh.build_sync_begin_header(["a", 3])
    assert header == {"type": "sync_begin", "obj_ids": ["a", "3"]}


def test_trivial_headers():
    assert bridge_mesh.build_sync_end_header() == {"type": "sync_end"}
    assert bridge_mesh.build_remove_header("abc") == {"type": "remove", "obj_id": "abc"}
    assert bridge_mesh.build_clear_header() == {"type": "clear"}


def test_every_header_survives_a_frame_round_trip():
    # a header that is not JSON-serializable is not a compile error on
    # either side: it is only discovered when Sync fails
    import bridge_protocol as protocol

    headers = [
        bridge_mesh.build_geometry_header("a", "Cube", 8, 12, [0.1, 0.2, 0.3, 1.0], IDENTITY),
        bridge_mesh.build_transform_header("a", IDENTITY),
        bridge_mesh.build_sync_begin_header(["a", "b"]),
        bridge_mesh.build_sync_end_header(),
        bridge_mesh.build_remove_header("a"),
        bridge_mesh.build_clear_header(),
    ]
    for header in headers:
        raw = protocol.encode_frame(header, None)
        assert len(raw) > 8
