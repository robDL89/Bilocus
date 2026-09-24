# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_bake.py, the Blender-side bake protocol in the two modes
# DirectShape and family: categories, loop layout, bake_mesh payload,
# headers and bake_result.
# Does NOT import bpy: runs under pytest with the system Python, like
# test_bridge_edges.py.

import array
import binascii
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_bake
from bridge_protocol import BridgeFramingError, BridgeMessageError


# --- golden vector shared with the C# side --------------------------------------
#
# The same hex as the golden vector of the bake contract, asserted
# byte for byte also by BakeMeshPayloadTests. It was double-checked with
# struct.pack in isolation from bridge_bake.py before the module was
# written.
#
# A base quad (0,1,2,3) and a triangle towards the apex (0,1,4). Blender
# splits the quad into (0,1,2) and (0,2,3): two triangles out of four
# vertices, the triangle stays itself.

GOLDEN_POSITIONS = [
    0.0, 0.0, 0.0,
    1.0, 0.0, 0.0,
    1.0, 1.0, 0.0,
    0.0, 1.0, 0.0,
    0.5, 0.5, 1.0,
]
GOLDEN_FACE_SIZES = [4, 3]
GOLDEN_FACE_VERTICES = [0, 1, 2, 3, 0, 1, 4]
GOLDEN_TRI_VERTICES = [0, 1, 2, 0, 2, 3, 0, 1, 4]
GOLDEN_TRI_FACES = [0, 0, 1]

GOLDEN_PAYLOAD_HEX = (
    "0000000000000000000000000000803f00000000000000000000803f0000803f"
    "00000000000000000000803f000000000000003f0000003f0000803f04000000"
    "0300000000000000010000000200000003000000000000000100000004000000"
    "0000000001000000020000000000000002000000030000000000000001000000"
    "04000000000000000000000001000000"
)


def golden(**overrides):
    """The arguments of pack_bake_payload for the golden mesh, with the
    given fields replaced: each validation test breaks ONE thing only."""
    args = {
        "positions": list(GOLDEN_POSITIONS),
        "face_sizes": list(GOLDEN_FACE_SIZES),
        "face_vertices": list(GOLDEN_FACE_VERTICES),
        "tri_vertices": list(GOLDEN_TRI_VERTICES),
        "tri_faces": list(GOLDEN_TRI_FACES),
    }
    args.update(overrides)
    return args


def test_golden_payload_matches_the_contract_bytes():
    produced = bridge_bake.pack_bake_payload(**golden())
    assert len(produced) == 144
    assert binascii.hexlify(produced).decode("ascii") == GOLDEN_PAYLOAD_HEX


def test_payload_length_matches_the_contract_formula():
    produced = bridge_bake.pack_bake_payload(**golden())
    assert len(produced) == bridge_bake.expected_bake_payload_length(5, 2, 7, 3)
    assert bridge_bake.expected_bake_payload_length(5, 2, 7, 3) == 144


def test_wire_constants_match_the_csharp_side():
    # MaxBakeObjects and MaxBakeFaces on the C# side: written twice, and
    # this test is the only place where a divergence shows up without
    # opening Revit.
    assert bridge_bake.MAX_BAKE_OBJECTS == 500
    assert bridge_bake.MAX_FAMILY_BAKE_OBJECTS == 50
    assert bridge_bake.MAX_BAKE_FACES == 2000000
    # BakeTarget.DirectShape / Family / All on the C# side
    assert bridge_bake.BAKE_TARGETS == ("directshape", "family")
    assert bridge_bake.REMOVE_TARGET == "all"


def test_pack_accepts_tuples_and_arrays_like_foreach_get_fills():
    produced = bridge_bake.pack_bake_payload(
        array.array("f", GOLDEN_POSITIONS),
        tuple(GOLDEN_FACE_SIZES),
        array.array("i", GOLDEN_FACE_VERTICES),
        array.array("i", GOLDEN_TRI_VERTICES),
        tuple(GOLDEN_TRI_FACES))
    assert binascii.hexlify(produced).decode("ascii") == GOLDEN_PAYLOAD_HEX


def test_triangles_out_of_polygon_order_are_accepted():
    # loop_triangles does not promise to group triangles by polygon: the
    # check counts, it does not assume the order
    produced = bridge_bake.pack_bake_payload(**golden(
        tri_vertices=[0, 1, 4, 0, 1, 2, 0, 2, 3],
        tri_faces=[1, 0, 0]))
    assert len(produced) == 144


def test_hexagon_with_four_triangles_is_accepted():
    positions = [
        1.0, 0.0, 0.0, 0.5, 0.9, 0.0, -0.5, 0.9, 0.0,
        -1.0, 0.0, 0.0, -0.5, -0.9, 0.0, 0.5, -0.9, 0.0,
    ]
    produced = bridge_bake.pack_bake_payload(
        positions, [6], [0, 1, 2, 3, 4, 5],
        [0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 5], [0, 0, 0, 0])
    assert len(produced) == bridge_bake.expected_bake_payload_length(6, 1, 6, 4)


# --- payload validations: one test per rule -----------------------------------

def test_pack_rejects_positions_not_multiple_of_three():
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(positions=GOLDEN_POSITIONS[:-1]))


def test_pack_rejects_tri_vertices_not_multiple_of_three():
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(tri_vertices=GOLDEN_TRI_VERTICES[:-1]))


def test_pack_rejects_fewer_than_three_vertices():
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(
            [0.0, 0.0, 0.0, 1.0, 0.0, 0.0], [3], [0, 1, 1], [0, 1, 1], [0])


def test_pack_rejects_zero_faces():
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(face_sizes=[], face_vertices=[]))


def test_pack_rejects_zero_triangles():
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(tri_vertices=[], tri_faces=[]))


def test_pack_rejects_vertices_over_the_real_maximum():
    # With the real ceiling, not a lowered one: the count check comes before
    # any work on the data, so the rejection is immediate even with six
    # million coordinates.
    positions = [0.0] * (3 * (bridge_bake.MAX_BAKE_FACES + 1))
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(positions=positions))


def test_pack_rejects_faces_over_the_real_maximum():
    # only face_sizes overshoots: the rejection comes from the counts,
    # before the sum that would not add up with face_vertices
    face_sizes = [3] * (bridge_bake.MAX_BAKE_FACES + 1)
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(face_sizes=face_sizes))
    assert "face_count" in str(error.value)


def test_pack_rejects_triangles_over_the_real_maximum():
    tri_vertices = [0] * (3 * (bridge_bake.MAX_BAKE_FACES + 1))
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(tri_vertices=tri_vertices))
    assert "tri_count" in str(error.value)


def test_pack_accepts_counts_exactly_at_the_maximum(monkeypatch):
    # Ceiling lowered so millions of elements do not need to be built: the
    # vertex count is the highest count of the golden mesh, 5, so ceiling 5
    # is the exact boundary for all three counts.
    monkeypatch.setattr(bridge_bake, "MAX_BAKE_FACES", 5)
    produced = bridge_bake.pack_bake_payload(**golden())
    assert len(produced) == 144


def test_pack_rejects_vertices_one_over_a_lowered_maximum(monkeypatch):
    # ceiling 4: only vert_count (5) overshoots, faces (2) and triangles (3)
    # stay under, so the boundary is tested exactly at +1
    monkeypatch.setattr(bridge_bake, "MAX_BAKE_FACES", 4)
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden())
    assert "vert_count" in str(error.value)


def test_pack_rejects_tri_faces_of_the_wrong_length():
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(tri_faces=[0, 0]))


def test_pack_rejects_a_polygon_with_two_vertices():
    # face_sizes [2, 5]: the sum stays 7, so ONLY the minimum-of-three-
    # vertices-per-polygon rule fails
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(face_sizes=[2, 5]))
    assert "at least 3" in str(error.value)


def test_pack_rejects_face_sizes_that_do_not_sum_to_the_loop_count():
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(face_sizes=[4, 4]))
    assert "sum" in str(error.value)


@pytest.mark.parametrize("bad", [float("nan"), float("inf"), float("-inf")])
def test_pack_rejects_non_finite_positions(bad):
    positions = list(GOLDEN_POSITIONS)
    positions[7] = bad
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(positions=positions))
    assert "non-finite" in str(error.value)


def test_pack_rejects_opposite_infinities_that_would_cancel_out():
    # inf + (-inf) gives NaN in the sum: the check must not be fooled by two
    # sick values that "cancel out"
    positions = list(GOLDEN_POSITIONS)
    positions[0] = float("inf")
    positions[1] = float("-inf")
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(positions=positions))


def test_pack_rejects_a_finite_position_beyond_float32():
    # 1e39 is finite in Python but does not fit a float32: on the wire it
    # would become an infinity, or an obscure struct error
    positions = list(GOLDEN_POSITIONS)
    positions[2] = 1e39
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(positions=positions))
    assert "float32" in str(error.value)


def test_pack_rejects_a_face_vertex_index_out_of_range():
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(face_vertices=[0, 1, 2, 3, 0, 1, 5]))
    assert "face_vertices" in str(error.value)


def test_pack_rejects_a_negative_face_vertex_index():
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(face_vertices=[0, 1, 2, 3, 0, 1, -1]))
    assert "face_vertices" in str(error.value)


def test_pack_rejects_a_triangle_vertex_index_out_of_range():
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(tri_vertices=[0, 1, 2, 0, 2, 3, 0, 1, 5]))
    assert "tri_vertices" in str(error.value)


def test_pack_rejects_a_negative_triangle_vertex_index():
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(tri_vertices=[0, 1, 2, 0, 2, 3, 0, -1, 4]))
    assert "tri_vertices" in str(error.value)


def test_pack_rejects_a_triangle_pointing_to_a_missing_polygon():
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(tri_faces=[0, 0, 2]))
    assert "tri_faces" in str(error.value)


def test_pack_rejects_a_negative_polygon_index():
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(tri_faces=[0, -1, 1]))
    assert "tri_faces" in str(error.value)


def test_pack_rejects_triangles_misattributed_between_polygons():
    # Everything in range and the triangle total adds up (3), but the quad
    # has 1 and the triangle has 2: polygons and triangles come from
    # different readings.
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(**golden(tri_faces=[0, 1, 1]))
    assert "polygon 0" in str(error.value)


def test_pack_names_the_first_polygon_with_the_wrong_triangle_count():
    positions = GOLDEN_POSITIONS + [2.0, 0.0, 0.0]
    # three triangles: quad (4) wants 2, triangle wants 1, triangle wants 1.
    # Here the second has 0 and the third has 2: the first wrong one is
    # polygon 1.
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.pack_bake_payload(
            positions, [4, 3, 3], [0, 1, 2, 3, 0, 1, 4, 1, 5, 2],
            [0, 1, 2, 0, 2, 3, 1, 5, 2, 1, 5, 2], [0, 0, 2, 2])
    assert "polygon 1" in str(error.value)


def test_pack_rejects_non_integer_indices_with_a_framing_error():
    # 1.0 passes the range checks but does not fit a uint32: it must come
    # out as BridgeFramingError, not as struct.error
    with pytest.raises(BridgeFramingError):
        bridge_bake.pack_bake_payload(**golden(face_vertices=[0, 1, 2, 3, 0, 1.0, 4]))


# --- loop layout ------------------------------------------------------------

def test_contiguous_loops_in_polygon_order_are_accepted():
    assert bridge_bake.check_loop_layout([0, 4], [4, 3]) is True


def test_loops_out_of_polygon_order_are_refused():
    # the second polygon comes before the first in the loops array
    assert bridge_bake.check_loop_layout([3, 0], [4, 3]) is False


def test_loops_with_a_gap_are_refused():
    assert bridge_bake.check_loop_layout([0, 5], [4, 3]) is False


def test_loop_layout_with_different_lengths_is_refused():
    assert bridge_bake.check_loop_layout([0, 4, 7], [4, 3]) is False


def test_empty_loop_layout_is_trivially_contiguous():
    assert bridge_bake.check_loop_layout([], []) is True


def test_loop_layout_accepts_arrays_filled_by_foreach_get():
    # array.array == list is always false: the function must convert
    starts = array.array("i", [0, 4, 7])
    totals = array.array("i", [4, 3, 6])
    assert bridge_bake.check_loop_layout(starts, totals) is True


# --- categories ----------------------------------------------------------------

PLAN_CATEGORY_IDS = [
    "OST_GenericModel", "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings",
    "OST_Columns", "OST_StructuralColumns", "OST_StructuralFraming",
    "OST_Furniture", "OST_Casework", "OST_SpecialityEquipment", "OST_Planting",
    "OST_Site", "OST_Mass", "OST_Stairs", "OST_StairsRailing", "OST_Doors",
    "OST_Windows", "OST_Entourage", "OST_Parking",
]


def test_default_category_is_generic_model_and_comes_first():
    # EnumProperty wants the default among the items; the first one is the
    # one read first in the menu
    assert bridge_bake.DEFAULT_CATEGORY == "OST_GenericModel"
    assert bridge_bake.BAKE_CATEGORIES[0][0] == bridge_bake.DEFAULT_CATEGORY


def test_categories_contain_the_plan_list_in_order():
    ids = [item[0] for item in bridge_bake.BAKE_CATEGORIES]
    assert ids[:len(PLAN_CATEGORY_IDS)] == PLAN_CATEGORY_IDS


def test_categories_are_enum_property_items():
    for item in bridge_bake.BAKE_CATEGORIES:
        assert len(item) == 3
        assert all(isinstance(part, str) and part for part in item)


def test_category_ids_are_unique_and_well_formed():
    ids = [item[0] for item in bridge_bake.BAKE_CATEGORIES]
    assert len(set(ids)) == len(ids)
    assert all(bridge_bake.is_well_formed_category(value) for value in ids)


def test_category_texts_are_ascii():
    # a non-ASCII character in a UI name is forbidden by the project's rule,
    # and in an id it would not pass the C# side's regex
    for item in bridge_bake.BAKE_CATEGORIES:
        for part in item:
            part.encode("ascii")


@pytest.mark.parametrize("category", [
    "OST_Walls", "OST_GenericModel", "OST_A1_b2",
])
def test_well_formed_categories(category):
    assert bridge_bake.is_well_formed_category(category) is True


@pytest.mark.parametrize("category", [
    "", "OST_", "Walls", "ost_Walls", "OST_Wa lls", "OST_Walls\n", " OST_Walls",
    # chr(0xE8) is an accented e: the ASCII rule also applies to the test
    # sources, so the character is built instead of written literally
    "OST_Mur" + chr(0xE8), "OST_Walls-2", None, 42,
])
def test_malformed_categories(category):
    assert bridge_bake.is_well_formed_category(category) is False


def test_non_loadable_categories_are_the_plan_list():
    assert bridge_bake.NON_LOADABLE_CATEGORIES == frozenset((
        "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings",
        "OST_Stairs", "OST_StairsRailing"))


def test_non_loadable_categories_are_all_in_the_menu():
    # the filter serves the bake menu: an entry missing from the menu would
    # be a typo that filters nothing
    ids = set(item[0] for item in bridge_bake.BAKE_CATEGORIES)
    assert bridge_bake.NON_LOADABLE_CATEGORIES <= ids


@pytest.mark.parametrize("category", sorted([
    "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Stairs", "OST_StairsRailing",
]))
def test_system_categories_are_not_family_categories(category):
    assert bridge_bake.is_family_category(category) is False


@pytest.mark.parametrize("category", [
    "OST_GenericModel", "OST_Furniture", "OST_Doors", "OST_Windows", "OST_Planting",
    # not in the menu but well formed: the final answer belongs to Revit
    "OST_LightingFixtures",
])
def test_loadable_categories_are_family_categories(category):
    assert bridge_bake.is_family_category(category) is True


@pytest.mark.parametrize("category", ["", "Walls", "OST_Walls\n", None, 42])
def test_malformed_categories_are_not_family_categories(category):
    assert bridge_bake.is_family_category(category) is False


# --- bake_begin and bake_remove headers -------------------------------------------

ID_HEADERS = [
    ("bake_begin", bridge_bake.build_bake_begin_header),
    ("bake_remove", bridge_bake.build_bake_remove_header),
]


def test_begin_header_has_the_fields_of_the_contract():
    # target ALWAYS written, even when it is the default
    assert bridge_bake.build_bake_begin_header(["abc", "def"]) == {
        "type": "bake_begin", "obj_ids": ["abc", "def"], "target": "directshape"}


def test_remove_header_has_the_fields_of_the_contract():
    # a removal takes away in both modes: no target
    assert bridge_bake.build_bake_remove_header(["abc", "def"]) == {
        "type": "bake_remove", "obj_ids": ["abc", "def"]}


def test_begin_header_writes_the_family_target():
    header = bridge_bake.build_bake_begin_header(["abc"], target="family")
    assert header == {"type": "bake_begin", "obj_ids": ["abc"], "target": "family"}


@pytest.mark.parametrize("target", [
    "DirectShape", "Family", "all", "", " family", None, 1, True, ["family"],
])
def test_begin_header_rejects_an_unknown_target(target):
    # "all" is only the target of a removal bake_result, never of a batch
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.build_bake_begin_header(["abc"], target=target)
    assert "target" in str(error.value)


def test_family_begin_header_accepts_exactly_the_family_maximum():
    ids = ["id{}".format(index) for index in range(bridge_bake.MAX_FAMILY_BAKE_OBJECTS)]
    header = bridge_bake.build_bake_begin_header(ids, target="family")
    assert len(header["obj_ids"]) == bridge_bake.MAX_FAMILY_BAKE_OBJECTS


def test_family_begin_header_rejects_one_over_the_family_maximum():
    ids = ["id{}".format(index) for index in range(bridge_bake.MAX_FAMILY_BAKE_OBJECTS + 1)]
    with pytest.raises(BridgeFramingError) as error:
        bridge_bake.build_bake_begin_header(ids, target="family")
    assert "maximum of 50" in str(error.value)


def test_directshape_begin_header_is_not_bound_by_the_family_maximum():
    ids = ["id{}".format(index) for index in range(bridge_bake.MAX_FAMILY_BAKE_OBJECTS + 1)]
    header = bridge_bake.build_bake_begin_header(ids, target="directshape")
    assert len(header["obj_ids"]) == bridge_bake.MAX_FAMILY_BAKE_OBJECTS + 1


def test_family_begin_header_still_rejects_duplicates():
    with pytest.raises(BridgeFramingError):
        bridge_bake.build_bake_begin_header(["abc", " abc "], target="family")


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_normalizes_the_ids(message_type, build):
    # ProxyNaming.NormalizeObjectId trims on the Revit side: the mark
    # written in the document must be the same string that will be looked
    # up on the next bake
    assert build(["  abc  "])["obj_ids"] == ["abc"]


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_accepts_any_iterable(message_type, build):
    assert build(("abc", "def"))["obj_ids"] == ["abc", "def"]


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_an_empty_list(message_type, build):
    with pytest.raises(BridgeFramingError):
        build([])


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_a_bare_string(message_type, build):
    # "abc" is iterable: without the check it would become three objects
    with pytest.raises(BridgeFramingError):
        build("abc")


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_none(message_type, build):
    with pytest.raises(BridgeFramingError):
        build(None)


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_a_non_string_id(message_type, build):
    with pytest.raises(BridgeFramingError):
        build(["abc", 12])


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_a_blank_id(message_type, build):
    with pytest.raises(BridgeFramingError):
        build(["abc", "   "])


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_control_characters(message_type, build):
    with pytest.raises(BridgeFramingError):
        build(["ab\nc"])


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_duplicates(message_type, build):
    with pytest.raises(BridgeFramingError):
        build(["abc", "def", "abc"])


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_duplicates_that_differ_only_by_spaces(message_type, build):
    # after normalization they are the same mark in the document
    with pytest.raises(BridgeFramingError):
        build(["abc", " abc "])


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_accepts_exactly_the_maximum(message_type, build):
    ids = ["id{}".format(index) for index in range(bridge_bake.MAX_BAKE_OBJECTS)]
    assert len(build(ids)["obj_ids"]) == bridge_bake.MAX_BAKE_OBJECTS


@pytest.mark.parametrize("message_type,build", ID_HEADERS)
def test_id_header_rejects_one_over_the_maximum(message_type, build):
    ids = ["id{}".format(index) for index in range(bridge_bake.MAX_BAKE_OBJECTS + 1)]
    with pytest.raises(BridgeFramingError):
        build(ids)


# --- bake_mesh and bake_end headers ------------------------------------------------

IDENTITY_ROWS = [
    [1.0, 0.0, 0.0, 0.0],
    [0.0, 1.0, 0.0, 0.0],
    [0.0, 0.0, 1.0, 0.0],
    [0.0, 0.0, 0.0, 1.0],
]
IDENTITY_FLAT = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0,
                 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0]


def mesh_header(**overrides):
    args = {
        "obj_id": "abc123",
        "name": "Cube",
        "category": "OST_GenericModel",
        "matrix": IDENTITY_ROWS,
        "vert_count": 5,
        "face_count": 2,
        "loop_count": 7,
        "tri_count": 3,
    }
    args.update(overrides)
    return bridge_bake.build_bake_mesh_header(**args)


def test_mesh_header_has_the_fields_of_the_contract():
    assert mesh_header() == {
        "type": "bake_mesh",
        "obj_id": "abc123",
        "name": "Cube",
        "category": "OST_GenericModel",
        "matrix": IDENTITY_FLAT,
        "vert_count": 5,
        "face_count": 2,
        "loop_count": 7,
        "tri_count": 3,
        "accept_open": False,
        "smooth_mesh": False,
    }


def test_mesh_header_writes_accept_open_when_true():
    assert mesh_header(accept_open=True)["accept_open"] is True


def test_mesh_header_writes_smooth_mesh_when_true():
    assert mesh_header(smooth_mesh=True)["smooth_mesh"] is True


def test_mesh_header_flags_default_to_false_in_the_old_call():
    # the Phase B positional call, without accept_open and smooth_mesh,
    # must stay valid: the smoke test uses it that way
    header = bridge_bake.build_bake_mesh_header(
        "abc123", "Cube", "OST_GenericModel", IDENTITY_ROWS, 5, 2, 7, 3)
    assert header["accept_open"] is False
    assert header["smooth_mesh"] is False


@pytest.mark.parametrize("field", ["accept_open", "smooth_mesh"])
@pytest.mark.parametrize("value", [1, 0, "true", "False", None, 1.0])
def test_mesh_header_rejects_a_flag_that_is_not_a_bool(field, value):
    with pytest.raises(BridgeFramingError) as error:
        mesh_header(**{field: value})
    assert field in str(error.value)


def test_mesh_header_accepts_an_already_flat_matrix():
    assert mesh_header(matrix=IDENTITY_FLAT)["matrix"] == IDENTITY_FLAT


def test_mesh_header_keeps_the_translation_row_major():
    rows = [list(row) for row in IDENTITY_ROWS]
    rows[0][3] = 10.0
    rows[1][3] = -5.0
    rows[2][3] = 0.5
    flat = mesh_header(matrix=rows)["matrix"]
    assert (flat[3], flat[7], flat[11]) == (10.0, -5.0, 0.5)


def test_mesh_header_normalizes_the_object_id():
    assert mesh_header(obj_id="  abc123  ")["obj_id"] == "abc123"


def test_mesh_header_rejects_a_blank_object_id():
    with pytest.raises(BridgeFramingError):
        mesh_header(obj_id="   ")


def test_mesh_header_rejects_control_characters_in_the_object_id():
    with pytest.raises(BridgeFramingError):
        mesh_header(obj_id="ab\x00c")


def test_mesh_header_falls_back_to_the_object_id_when_the_name_is_empty():
    assert mesh_header(name="   ")["name"] == "abc123"
    assert mesh_header(name=None)["name"] == "abc123"


def test_mesh_header_trims_the_name():
    assert mesh_header(name="  Cube  ")["name"] == "Cube"


def test_mesh_header_accepts_every_menu_category():
    for identifier, _, _ in bridge_bake.BAKE_CATEGORIES:
        assert mesh_header(category=identifier)["category"] == identifier


@pytest.mark.parametrize("category", ["Walls", "OST_", "OST_Walls\n", "", None])
def test_mesh_header_rejects_a_malformed_category(category):
    with pytest.raises(BridgeFramingError):
        mesh_header(category=category)


def test_mesh_header_rejects_a_matrix_of_the_wrong_size():
    with pytest.raises(BridgeFramingError):
        mesh_header(matrix=IDENTITY_FLAT[:12])


@pytest.mark.parametrize("bad", [float("nan"), float("inf"), 1e39])
def test_mesh_header_rejects_a_matrix_value_that_is_not_a_finite_float32(bad):
    flat = list(IDENTITY_FLAT)
    flat[3] = bad
    with pytest.raises(BridgeFramingError):
        mesh_header(matrix=flat)


@pytest.mark.parametrize("field,value", [
    ("vert_count", 2),
    ("face_count", 0),
    ("tri_count", 0),
])
def test_mesh_header_rejects_counts_below_the_minimum(field, value):
    with pytest.raises(BridgeFramingError):
        mesh_header(**{field: value})


@pytest.mark.parametrize("field", ["vert_count", "face_count", "tri_count"])
def test_mesh_header_rejects_counts_over_the_maximum(field):
    over = bridge_bake.MAX_BAKE_FACES + 1
    overrides = {field: over}
    if field == "face_count":
        # keeps tri = loop - 2*face coherent, so only the ceiling fails
        overrides["loop_count"] = 2 * over + 3
    if field == "tri_count":
        overrides["loop_count"] = over + 4
    with pytest.raises(BridgeFramingError) as error:
        mesh_header(**overrides)
    assert "maximum" in str(error.value)


@pytest.mark.parametrize("value", [True, 3.0, "3", None])
def test_mesh_header_rejects_counts_that_are_not_integers(value):
    with pytest.raises(BridgeFramingError):
        mesh_header(tri_count=value)


def test_mesh_header_rejects_counts_that_break_the_triangles_per_polygon_rule():
    # 9 is len(tri_vertices) of the golden mesh passed instead of 3
    with pytest.raises(BridgeFramingError) as error:
        mesh_header(tri_count=9)
    assert "inconsistent" in str(error.value)


def test_mesh_header_counts_agree_with_the_golden_payload():
    produced = bridge_bake.pack_bake_payload(**golden())
    header = mesh_header(
        vert_count=len(GOLDEN_POSITIONS) // 3,
        face_count=len(GOLDEN_FACE_SIZES),
        loop_count=len(GOLDEN_FACE_VERTICES),
        tri_count=len(GOLDEN_TRI_FACES))
    assert len(produced) == bridge_bake.expected_bake_payload_length(
        header["vert_count"], header["face_count"],
        header["loop_count"], header["tri_count"])


def test_end_header_is_just_the_type():
    assert bridge_bake.build_bake_end_header() == {"type": "bake_end"}


# --- read_bake_result ----------------------------------------------------------

COUNT_FIELDS = [
    "requested", "created", "replaced", "recreated", "removed",
    "failed", "missing", "as_mesh", "faces_planar", "faces_triangulated",
    "switched", "not_moved",
]


def result_header(**overrides):
    # The header of a Revit with the Phase B2 add-in: target, switched and
    # not_moved always present. The default target follows action, as
    # BuildBakeResult writes it: "all" for a removal, "directshape" for a
    # bake; the tests on coherence pass it explicitly.
    action = overrides.get("action", "bake")
    header = {
        "type": "bake_result",
        "action": "bake",
        "target": "all" if action == "remove" else "directshape",
        "ok": True,
        "requested": 3,
        "created": 2,
        "replaced": 1,
        "recreated": 0,
        "removed": 0,
        "failed": 0,
        "missing": 0,
        "as_mesh": 0,
        "faces_planar": 14,
        "faces_triangulated": 2,
        "switched": 0,
        "not_moved": 0,
        "message": "",
    }
    header.update(overrides)
    return header


def test_bake_result_reads_every_field():
    assert bridge_bake.read_bake_result(result_header()) == {
        "action": "bake",
        "target": "directshape",
        "ok": True,
        "message": "",
        "requested": 3,
        "created": 2,
        "replaced": 1,
        "recreated": 0,
        "removed": 0,
        "failed": 0,
        "missing": 0,
        "as_mesh": 0,
        "faces_planar": 14,
        "faces_triangulated": 2,
        "switched": 0,
        "not_moved": 0,
    }


def test_bake_result_reads_a_remove():
    fields = bridge_bake.read_bake_result(result_header(action="remove", removed=3))
    assert fields["action"] == "remove"
    assert fields["target"] == "all"
    assert fields["removed"] == 3


def test_bake_result_reads_a_family_bake():
    fields = bridge_bake.read_bake_result(result_header(
        target="family", switched=2, not_moved=1))
    assert fields["target"] == "family"
    assert fields["switched"] == 2
    assert fields["not_moved"] == 1


def test_bake_result_of_a_phase_b_revit_is_rejected():
    # Intentional: without target, switched and not_moved the outcome would
    # be read half way. The two sides are updated together.
    header = result_header()
    for key in ("target", "switched", "not_moved"):
        del header[key]
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(header)


def test_bake_result_without_target_is_rejected():
    header = result_header()
    del header["target"]
    with pytest.raises(BridgeMessageError) as error:
        bridge_bake.read_bake_result(header)
    assert "target" in str(error.value)


@pytest.mark.parametrize("value", ["DirectShape", "mesh", "", 1, None, ["family"]])
def test_bake_result_with_an_unknown_target_is_rejected(value):
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(result_header(target=value))


@pytest.mark.parametrize("action,target", [
    ("bake", "all"),
    ("remove", "directshape"),
    ("remove", "family"),
])
def test_bake_result_with_a_target_incoherent_with_action_is_rejected(action, target):
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(result_header(action=action, target=target))


@pytest.mark.parametrize("field", COUNT_FIELDS)
def test_bake_result_without_a_count_is_rejected(field):
    # strict like proxy_result: an inferred zero would be a lie in the panel
    header = result_header()
    del header[field]
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(header)


@pytest.mark.parametrize("value", [-1, 1.0, True, "2", None])
def test_bake_result_with_a_bad_count_is_rejected(value):
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(result_header(created=value))


def test_bake_result_without_ok_is_rejected():
    header = result_header()
    del header["ok"]
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(header)


@pytest.mark.parametrize("value", [1, "true", None])
def test_bake_result_with_a_non_boolean_ok_is_rejected(value):
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(result_header(ok=value))


def test_bake_result_without_action_is_rejected():
    header = result_header()
    del header["action"]
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(header)


@pytest.mark.parametrize("value", ["update", "Bake", "", 1, ["bake"]])
def test_bake_result_with_an_unknown_action_is_rejected(value):
    with pytest.raises(BridgeMessageError):
        bridge_bake.read_bake_result(result_header(action=value))


def test_bake_result_without_message_reads_as_empty():
    header = result_header()
    del header["message"]
    assert bridge_bake.read_bake_result(header)["message"] == ""


def test_failed_bake_result_carries_the_reason():
    fields = bridge_bake.read_bake_result(result_header(
        ok=False, created=0, replaced=0, failed=3,
        message="read-only document"))
    assert fields["ok"] is False
    assert fields["message"] == "read-only document"


# --- format_bake_result ---------------------------------------------------------

def formatted(**overrides):
    return bridge_bake.format_bake_result(
        bridge_bake.read_bake_result(result_header(**overrides)))


def test_format_matches_the_plan_example():
    assert formatted() == (
        "bake: 2 created, 1 updated, 0 recreated - 14 whole faces, 2 triangulated faces")


def test_format_uses_the_singular_for_one():
    assert formatted(created=1, replaced=0, recreated=1,
                     faces_planar=1, faces_triangulated=1) == (
        "bake: 1 created, 0 updated, 1 recreated - 1 whole face, 1 triangulated face")


def test_format_mentions_objects_that_came_out_as_mesh():
    text = formatted(as_mesh=1)
    assert text.endswith(" - 1 object as mesh, not solid")


def test_format_mentions_missing_objects():
    text = formatted(missing=2)
    assert text.endswith(" - 2 objects never arrived")


def test_format_mentions_failures_with_the_last_reason():
    text = formatted(
        requested=3, created=1, replaced=1, failed=1,
        message="'Table': category OST_Walls not allowed for a DirectShape")
    assert text == (
        "bake: 1 created, 1 updated, 0 recreated - 14 whole faces, 2 triangulated faces"
        " - 1 failure: 'Table': category OST_Walls not allowed for a DirectShape")


def test_format_puts_every_note_in_order():
    text = formatted(as_mesh=2, missing=1, failed=2, message="reason")
    assert text == (
        "bake: 2 created, 1 updated, 0 recreated - 14 whole faces, 2 triangulated faces"
        " - 2 objects as mesh, not solid - 1 object never arrived"
        " - 2 failures: reason")


def test_format_of_a_failed_bake_with_failures_and_reason():
    text = formatted(ok=False, requested=3, created=0, replaced=0, failed=3,
                     faces_planar=0, faces_triangulated=0,
                     message="'Cube': mesh with no valid faces")
    assert text == "bake FAILED: 3 failures out of 3 objects - 'Cube': mesh with no valid faces"


def test_format_of_a_failed_bake_without_failures():
    # non-writable document: no object was even attempted
    text = formatted(ok=False, created=0, replaced=0, failed=0,
                     message="the active document is a family")
    assert text == "bake FAILED - the active document is a family"


def test_format_of_a_failed_bake_without_a_message_says_so():
    text = formatted(ok=False, created=0, replaced=0, message="")
    assert text == "bake FAILED - reason not reported by Revit"


def test_format_of_a_failed_bake_mentions_missing_objects():
    text = formatted(ok=False, created=0, replaced=0, failed=1, requested=2,
                     missing=1, message="reason")
    assert text == "bake FAILED: 1 failure out of 2 objects - reason - 1 object never arrived"


def test_format_mentions_switched_elements_in_a_directshape_bake():
    text = formatted(switched=3)
    assert text.endswith(" - 3 elements of the other mode replaced")


def test_format_puts_the_new_notes_before_missing_and_failures():
    text = formatted(as_mesh=1, switched=1, missing=1, failed=1, message="reason")
    assert text == (
        "bake: 2 created, 1 updated, 0 recreated - 14 whole faces, 2 triangulated faces"
        " - 1 object as mesh, not solid - 1 element of the other mode replaced"
        " - 1 object never arrived - 1 failure: reason")


# --- format_bake_result, family bake --------------------------------------------

def family(**overrides):
    overrides.setdefault("target", "family")
    return formatted(**overrides)


def test_format_of_a_family_bake_matches_the_plan_example():
    assert family(created=1, replaced=2) == (
        "family: 1 created, 2 updated - 14 whole faces, 2 triangulated faces")


def test_format_of_a_family_bake_uses_singular_for_one():
    assert family(created=2, replaced=1, faces_planar=1, faces_triangulated=0) == (
        "family: 2 created, 1 updated - 1 whole face, 0 triangulated faces")


def test_format_of_a_family_bake_reads_as_mesh_as_open_shell():
    text = family(as_mesh=2)
    assert text.endswith(" - 2 as open shell")
    assert "mesh" not in text


def test_format_of_a_family_bake_mentions_switched_and_not_moved():
    text = family(switched=1, not_moved=2)
    assert text.endswith(
        " - 1 element of the other mode replaced"
        " - 2 instances not moved (copies in Revit)")


def test_format_of_a_family_bake_with_one_instance_not_moved():
    assert family(not_moved=1).endswith(" - 1 instance not moved (copies in Revit)")


def test_format_of_a_family_bake_hides_zero_notes():
    text = family(created=1, replaced=0)
    assert text == "family: 1 created, 0 updated - 14 whole faces, 2 triangulated faces"


def test_format_of_a_family_bake_puts_every_note_in_order():
    text = family(created=1, replaced=1, as_mesh=1, switched=2, not_moved=1,
                  missing=1, failed=1,
                  message="'Table': category OST_Walls not allowed for a family")
    assert text == (
        "family: 1 created, 1 updated - 14 whole faces, 2 triangulated faces"
        " - 1 as open shell - 2 elements of the other mode replaced"
        " - 1 instance not moved (copies in Revit) - 1 object never arrived"
        " - 1 failure: 'Table': category OST_Walls not allowed for a family")


def test_format_of_a_failed_family_bake():
    text = family(ok=False, requested=2, created=0, replaced=0, failed=2,
                  message="template Metric Generic Model.rft not found")
    assert text == (
        "family bake FAILED: 2 failures out of 2 objects"
        " - template Metric Generic Model.rft not found")


def test_format_of_a_failed_family_bake_without_a_message_says_so():
    assert family(ok=False, message="") == (
        "family bake FAILED - reason not reported by Revit")


def test_format_of_a_failed_family_bake_mentions_missing_objects():
    text = family(ok=False, failed=1, requested=2, missing=1, message="reason")
    assert text == (
        "family bake FAILED: 1 failure out of 2 objects - reason"
        " - 1 object never arrived")


def test_format_of_a_remove_matches_the_plan_example():
    assert formatted(action="remove", requested=2, created=0, replaced=0,
                     removed=3, faces_planar=0, faces_triangulated=0) == (
        "removed 3 elements from 2 objects")


def test_format_of_a_remove_uses_the_singular_for_one():
    assert formatted(action="remove", requested=1, removed=1) == (
        "removed 1 element from 1 object")


def test_format_of_a_remove_that_found_nothing():
    assert formatted(action="remove", requested=2, removed=0) == (
        "removed 0 elements from 2 objects")


def test_format_of_a_remove_with_failures_and_message():
    assert formatted(action="remove", requested=2, removed=1, failed=1,
                     message="reason") == (
        "removed 1 element from 2 objects - 1 failure: reason")


def test_format_of_a_failed_remove():
    assert formatted(action="remove", ok=False, requested=2, removed=0,
                     message="read-only document") == (
        "removal FAILED - read-only document")


def test_format_of_a_failed_remove_without_a_message_says_so():
    assert formatted(action="remove", ok=False, message="") == (
        "removal FAILED - reason not reported by Revit")


# --- bake together: host ---------------------------------------------------------

def test_bake_begin_with_host_writes_the_normalized_host():
    header = bridge_bake.build_bake_begin_header(["abc", "def"], target="family", host=" def ")
    assert header["host"] == "def"
    assert header["target"] == "family"


def test_bake_begin_without_host_has_no_host_field():
    header = bridge_bake.build_bake_begin_header(["abc", "def"], target="family")
    assert "host" not in header


def test_bake_begin_host_is_only_for_families():
    with pytest.raises(BridgeFramingError):
        bridge_bake.build_bake_begin_header(["abc", "def"], target="directshape", host="abc")


def test_bake_begin_host_must_be_announced():
    with pytest.raises(BridgeFramingError):
        bridge_bake.build_bake_begin_header(["abc", "def"], target="family", host="xyz")
