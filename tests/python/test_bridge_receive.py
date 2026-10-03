# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_receive.py, the pure unpacking of the geometry payload on
# the Blender side. Does NOT import bpy: runs under pytest with the system
# Python, like test_protocol.py and test_bridge_mesh.py.

import binascii
import os
import struct
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_receive
from bridge_protocol import PROTOCOL_VERSION, BridgeMessageError


# --- unpacking a triangle from known bytes --------------------------
#
# Bytes built by hand with struct.pack, in this file, independently of
# bridge_mesh.pack_mesh_payload: a triangle (0,0,0) (2,0,0) (0,2,0), normals
# all (0,0,1), a single triangle with indices [0, 1, 2].

KNOWN_POSITIONS = (0.0, 0.0, 0.0, 2.0, 0.0, 0.0, 0.0, 2.0, 0.0)
KNOWN_NORMALS = (0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0)
KNOWN_INDICES = (0, 1, 2)

KNOWN_PAYLOAD = b"".join([
    struct.pack("<9f", *KNOWN_POSITIONS),
    struct.pack("<9f", *KNOWN_NORMALS),
    struct.pack("<3I", *KNOWN_INDICES),
])


def test_unpacks_triangle_from_known_bytes():
    positions, normals, indices = bridge_receive.unpack_mesh_payload(KNOWN_PAYLOAD, 3, 1)
    assert positions == KNOWN_POSITIONS
    assert normals == KNOWN_NORMALS
    assert indices == KNOWN_INDICES


def test_hello_ack_with_same_protocol_version_is_accepted():
    header = {"type": "hello_ack", "protocol_version": PROTOCOL_VERSION}
    assert bridge_receive.check_hello_ack(header) is None


@pytest.mark.parametrize("version", [PROTOCOL_VERSION + 1, None, "1", True, 1.0])
def test_hello_ack_with_other_or_unreadable_version_is_a_mismatch(version):
    header = {"type": "hello_ack"}
    if version is not None:
        header["protocol_version"] = version
    problem = bridge_receive.check_hello_ack(header)
    assert problem is not None
    assert "protocol version mismatch" in problem


def test_unpacked_arrays_are_tuples():
    # struct.unpack_from returns tuples: worth checking explicitly because
    # import_geometry has to group them into triples for from_pydata.
    positions, normals, indices = bridge_receive.unpack_mesh_payload(KNOWN_PAYLOAD, 3, 1)
    assert isinstance(positions, tuple)
    assert isinstance(normals, tuple)
    assert isinstance(indices, tuple)


# --- agreement with the C# side --------------------------------------------------
#
# Bytes identical to the ones verified by MeshPayloadWriterTests.WritesExpectedBytes_SingleTriangle
# in tests/Bilocus.Geometry.Tests/MeshPayloadWriterTests.cs. The hex was
# obtained by programmatically converting that file's literal byte array
# with a Python script (bytes(...).hex()), not recomputed by hand and not
# transcribed from memory.
#
# Triangle: 3 vertices (0,0,0) (1,0,0) (0,1,0), normals all (0,0,1),
# a single triangle with indices [0, 1, 2].

CSHARP_TRIANGLE_PAYLOAD_HEX = (
    "0000000000000000000000000000803f0000000000000000000000000000803f"
    "0000000000000000000000000000803f00000000000000000000803f00000000"
    "000000000000803f000000000100000002000000"
)

CSHARP_TRIANGLE_POSITIONS = (0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0)
CSHARP_TRIANGLE_NORMALS = (0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0)
CSHARP_TRIANGLE_INDICES = (0, 1, 2)


def test_agrees_with_csharp_meshpayloadwriter_golden_bytes():
    payload = binascii.unhexlify(CSHARP_TRIANGLE_PAYLOAD_HEX)
    positions, normals, indices = bridge_receive.unpack_mesh_payload(payload, 3, 1)
    assert positions == CSHARP_TRIANGLE_POSITIONS
    assert normals == CSHARP_TRIANGLE_NORMALS
    assert indices == CSHARP_TRIANGLE_INDICES


# --- zero counts ----------------------------------------------------------

def test_zero_counts_produce_empty_tuples():
    positions, normals, indices = bridge_receive.unpack_mesh_payload(b"", 0, 0)
    assert positions == ()
    assert normals == ()
    assert indices == ()


# --- payload shorter or longer than declared ----------------------------

def test_rejects_payload_shorter_than_declared():
    truncated = KNOWN_PAYLOAD[:-1]
    with pytest.raises(BridgeMessageError):
        bridge_receive.unpack_mesh_payload(truncated, 3, 1)


def test_rejects_payload_longer_than_declared():
    padded = KNOWN_PAYLOAD + b"\x00"
    with pytest.raises(BridgeMessageError):
        bridge_receive.unpack_mesh_payload(padded, 3, 1)


def test_length_mismatch_raises_message_error_not_framing_error():
    # The point of the task: a payload inconsistent with the header is a
    # content error (stream still aligned), not a framing one (stream
    # lost). BridgeMessageError does not derive from BridgeFramingError: if
    # this raised BridgeFramingError instead, a downstream except
    # BridgeMessageError would not catch it and the connection would be
    # closed by mistake.
    from bridge_protocol import BridgeFramingError

    with pytest.raises(BridgeMessageError):
        bridge_receive.unpack_mesh_payload(KNOWN_PAYLOAD[:-1], 3, 1)

    assert not issubclass(BridgeMessageError, BridgeFramingError)


# --- negative counts ---------------------------------------------------------

def test_rejects_negative_vertex_count():
    with pytest.raises(BridgeMessageError):
        bridge_receive.unpack_mesh_payload(b"", -1, 0)


def test_rejects_negative_triangle_count():
    with pytest.raises(BridgeMessageError):
        bridge_receive.unpack_mesh_payload(b"", 0, -1)


# --- mesh larger than a triangle -------------------------------------------

def test_unpacks_two_triangles_four_vertices():
    positions = tuple(float(value) for value in range(12))
    normals = tuple(float(value) for value in range(12))
    indices = (0, 1, 2, 1, 2, 3)
    payload = b"".join([
        struct.pack("<12f", *positions),
        struct.pack("<12f", *normals),
        struct.pack("<6I", *indices),
    ])
    unpacked_positions, unpacked_normals, unpacked_indices = bridge_receive.unpack_mesh_payload(
        payload, 4, 2)
    assert unpacked_positions == positions
    assert unpacked_normals == normals
    assert unpacked_indices == indices


# --- group_into_triples --------------------------------------------------------
#
# struct.unpack_from returns FLAT arrays, from_pydata wants coordinates
# grouped per vertex. The grouping lives here and not in the module that
# touches bpy because offset, stride and the remainder of the division are
# the three easiest things to get wrong, and there they would be outside
# the tests' coverage.

def test_groups_flat_positions_into_vertices():
    flat = (0.0, 1.0, 2.0, 3.0, 4.0, 5.0)
    assert bridge_receive.group_into_triples(flat) == ((0.0, 1.0, 2.0), (3.0, 4.0, 5.0))


def test_groups_flat_indices_into_faces():
    assert bridge_receive.group_into_triples((0, 1, 2, 1, 2, 3)) == ((0, 1, 2), (1, 2, 3))


def test_groups_empty_sequence_into_empty_tuple():
    assert bridge_receive.group_into_triples(()) == ()


def test_grouping_keeps_the_order_of_the_flat_sequence():
    # vertex 0 must stay vertex 0: if the grouping were off by one element
    # the mesh would arrive in Blender warped but with no errors, which is
    # the worst way to get it wrong
    flat = tuple(range(9))
    assert bridge_receive.group_into_triples(flat) == ((0, 1, 2), (3, 4, 5), (6, 7, 8))


def test_grouping_returns_a_tuple_of_tuples():
    grouped = bridge_receive.group_into_triples((1.0, 2.0, 3.0))
    assert isinstance(grouped, tuple)
    assert isinstance(grouped[0], tuple)


def test_rejects_length_not_multiple_of_three():
    with pytest.raises(BridgeMessageError):
        bridge_receive.group_into_triples((1.0, 2.0, 3.0, 4.0))


def test_rejects_length_of_one():
    with pytest.raises(BridgeMessageError):
        bridge_receive.group_into_triples((1.0,))


def test_grouping_accepts_a_list_as_well_as_a_tuple():
    assert bridge_receive.group_into_triples([1, 2, 3]) == ((1, 2, 3),)


def test_grouping_roundtrips_the_unpacked_payload():
    positions, normals, indices = bridge_receive.unpack_mesh_payload(KNOWN_PAYLOAD, 3, 1)
    verts = bridge_receive.group_into_triples(positions)
    faces = bridge_receive.group_into_triples(indices)
    assert verts == ((0.0, 0.0, 0.0), (2.0, 0.0, 0.0), (0.0, 2.0, 0.0))
    assert faces == ((0, 1, 2),)
    assert len(bridge_receive.group_into_triples(normals)) == 3


# --- check_indices --------------------------------------------------------------
#
# from_pydata with an out-of-range index does not raise a readable error: it
# writes past the vertex count. The check must be done BEFORE, and here it
# is testable.

def test_accepts_indices_inside_the_vertex_range():
    bridge_receive.check_indices((0, 1, 2, 2, 1, 0), 3)


def test_accepts_empty_indices_with_zero_vertices():
    bridge_receive.check_indices((), 0)


def test_rejects_index_equal_to_vertex_count():
    with pytest.raises(BridgeMessageError):
        bridge_receive.check_indices((0, 1, 3), 3)


def test_rejects_negative_index():
    with pytest.raises(BridgeMessageError):
        bridge_receive.check_indices((0, -1, 2), 3)


def test_rejects_any_index_when_there_are_no_vertices():
    with pytest.raises(BridgeMessageError):
        bridge_receive.check_indices((0, 0, 0), 0)


# --- element_id_key --------------------------------------------------------------
#
# The id ends up in a custom property and is read back to find the object
# again on the next pull. The key is a STRING: Blender's integer custom
# properties are 32-bit and ElementId.Value in Revit 2024+ is a long, so an
# id beyond 2^31 would make the assignment fail. The string also removes
# the int-vs-float comparison ambiguity on read-back.

def test_element_id_key_of_an_int_is_its_decimal_string():
    assert bridge_receive.element_id_key(348219) == "348219"


def test_element_id_key_of_a_string_is_the_same_string():
    assert bridge_receive.element_id_key("348219") == "348219"


def test_element_id_key_of_a_large_id_does_not_lose_digits():
    big = 2 ** 40 + 7
    assert bridge_receive.element_id_key(big) == str(big)


def test_element_id_key_of_a_float_without_fraction_drops_the_point():
    # json.loads can return a float if the number arrives written with a
    # decimal point: 348219.0 and 348219 must produce the SAME key,
    # otherwise the same element would be imported twice
    assert bridge_receive.element_id_key(348219.0) == "348219"


def test_element_id_key_strips_surrounding_spaces():
    assert bridge_receive.element_id_key("  348219  ") == "348219"


def test_element_id_key_rejects_none():
    with pytest.raises(BridgeMessageError):
        bridge_receive.element_id_key(None)


def test_element_id_key_rejects_an_empty_string():
    with pytest.raises(BridgeMessageError):
        bridge_receive.element_id_key("")


def test_element_id_key_rejects_a_bool():
    # True is an int for Python: without an explicit check it would become "1"
    with pytest.raises(BridgeMessageError):
        bridge_receive.element_id_key(True)


# --- read_geometry_header ---------------------------------------------------------
#
# The field names are the ones written by MessageRouter.BuildGeometryHeader
# (src/Bilocus.Revit/Net/MessageRouter.cs) and tabulated in DESIGN.md 5.4.

def full_header():
    return {
        "type": "revit_geometry",
        "element_id": 348219,
        "name": "Walls - Basic Wall [348219]",
        "category": "Walls",
        "type_name": "Basic Wall",
        "vert_count": 3,
        "tri_count": 1,
        "origin": [1.5, -2.25, 3.0],
        "color": [0.6, 0.6, 0.6, 1.0],
    }


def test_reads_every_field_of_a_full_header():
    fields = bridge_receive.read_geometry_header(full_header())
    assert fields["element_id"] == "348219"
    assert fields["name"] == "Walls - Basic Wall [348219]"
    assert fields["category"] == "Walls"
    assert fields["type_name"] == "Basic Wall"
    assert fields["vert_count"] == 3
    assert fields["tri_count"] == 1
    assert fields["origin"] == (1.5, -2.25, 3.0)
    assert fields["color"] == (0.6, 0.6, 0.6, 1.0)


def test_color_is_a_tuple_of_four_floats():
    fields = bridge_receive.read_geometry_header(full_header())
    assert len(fields["color"]) == 4
    for component in fields["color"]:
        assert isinstance(component, float)


def test_empty_category_and_type_name_stay_empty_strings():
    # Revit writes them as an empty string, never null, for system
    # elements and symbols with no type
    header = full_header()
    header["category"] = ""
    header["type_name"] = ""
    fields = bridge_receive.read_geometry_header(header)
    assert fields["category"] == ""
    assert fields["type_name"] == ""


def test_missing_category_and_type_name_become_empty_strings():
    header = full_header()
    del header["category"]
    del header["type_name"]
    fields = bridge_receive.read_geometry_header(header)
    assert fields["category"] == ""
    assert fields["type_name"] == ""


def test_missing_name_falls_back_to_the_element_id():
    header = full_header()
    del header["name"]
    fields = bridge_receive.read_geometry_header(header)
    assert "348219" in fields["name"]


def test_empty_name_falls_back_to_the_element_id():
    # bpy.data.objects.new("") produces an object with an unusable name:
    # better an ugly but findable name
    header = full_header()
    header["name"] = ""
    fields = bridge_receive.read_geometry_header(header)
    assert "348219" in fields["name"]


def test_missing_color_falls_back_to_the_default():
    header = full_header()
    del header["color"]
    fields = bridge_receive.read_geometry_header(header)
    assert fields["color"] == bridge_receive.DEFAULT_COLOR


def test_malformed_color_falls_back_instead_of_raising():
    # color is cosmetic: a wrong color is not a reason to throw away a
    # wall's geometry
    header = full_header()
    header["color"] = [0.5, 0.5]
    fields = bridge_receive.read_geometry_header(header)
    assert fields["color"] == bridge_receive.DEFAULT_COLOR


def test_missing_element_id_is_rejected():
    header = full_header()
    del header["element_id"]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_missing_vert_count_is_rejected():
    header = full_header()
    del header["vert_count"]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_negative_tri_count_is_rejected():
    header = full_header()
    header["tri_count"] = -1
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_non_numeric_vert_count_is_rejected():
    header = full_header()
    header["vert_count"] = "tre"
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


# --- origin -------------------------------------------------------------------
#
# origin is the center of the element's bounding box in world coordinates,
# in meters: the payload's vertices arrive in LOCAL coordinates and origin
# is what puts them back in place. Without it, the object would have its
# origin at (0,0,0) and would rotate around the scene origin instead of on
# itself.
#
# Unlike color, origin has NO default: a wrong color is cosmetic, a wrong
# origin puts the wall in the wrong place. It is a CONTENT error
# (BridgeMessageError), not a framing one: the stream stays aligned at the
# frame boundary and only this element is discarded.

def test_reads_origin_as_a_tuple_of_three_floats():
    fields = bridge_receive.read_geometry_header(full_header())
    assert isinstance(fields["origin"], tuple)
    assert len(fields["origin"]) == 3
    for component in fields["origin"]:
        assert isinstance(component, float)


def test_integer_origin_components_become_floats():
    # System.Text.Json writes 0 and not 0.0 for a float that is zero
    header = full_header()
    header["origin"] = [0, 12, -3]
    fields = bridge_receive.read_geometry_header(header)
    assert fields["origin"] == (0.0, 12.0, -3.0)


def test_zero_origin_is_accepted():
    header = full_header()
    header["origin"] = [0.0, 0.0, 0.0]
    fields = bridge_receive.read_geometry_header(header)
    assert fields["origin"] == (0.0, 0.0, 0.0)


def test_missing_origin_is_rejected():
    header = full_header()
    del header["origin"]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_origin_of_wrong_length_is_rejected():
    header = full_header()
    header["origin"] = [1.0, 2.0]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_non_numeric_origin_component_is_rejected():
    header = full_header()
    header["origin"] = [1.0, "due", 3.0]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_bool_origin_component_is_rejected():
    # True is an int for Python: without the explicit check it would pass as 1
    header = full_header()
    header["origin"] = [True, 2.0, 3.0]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_origin_that_is_not_a_sequence_is_rejected():
    header = full_header()
    header["origin"] = 5.0
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)


def test_non_finite_origin_is_rejected():
    # json.loads accepts the NaN and Infinity literals. A NaN in
    # obj.location would make the object disappear from the viewport
    # without a single message.
    for bad in (float("nan"), float("inf"), float("-inf")):
        header = full_header()
        header["origin"] = [bad, 2.0, 3.0]
        with pytest.raises(BridgeMessageError):
            bridge_receive.read_geometry_header(header)


def test_origin_error_is_a_message_error_not_a_framing_error():
    from bridge_protocol import BridgeFramingError

    header = full_header()
    del header["origin"]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_geometry_header(header)

    assert not issubclass(BridgeMessageError, BridgeFramingError)


# --- read_batch_count ------------------------------------------------------------


def test_reads_the_count_of_a_batch_begin():
    assert bridge_receive.read_batch_count({"type": "revit_batch_begin", "count": 12}) == 12


def test_missing_count_becomes_unknown():
    assert bridge_receive.read_batch_count({"type": "revit_batch_begin"}) is None


def test_negative_count_becomes_unknown():
    # the count only serves the panel's status line: a nonsensical value is
    # ignored, the batch is not thrown away
    assert bridge_receive.read_batch_count({"type": "revit_batch_begin", "count": -3}) is None


def test_non_numeric_count_becomes_unknown():
    assert bridge_receive.read_batch_count({"type": "revit_batch_begin", "count": "molti"}) is None


# --- read_proxy_result -----------------------------------------------------
#
# The outcome of proxy creation, written by MessageRouter.BuildProxyResult
# (DESIGN.md 5.4). It is the only message that tells Blender how a write into
# the document went: without it, whoever presses "Create Proxy" only sees
# success by going to look inside Revit.

def proxy_header(**overrides):
    header = {
        "type": "proxy_result",
        "obj_id": "abc123",
        "name": "Cube",
        "ok": True,
        "requested": 12,
        "created": 10,
        "replaced": 4,
        "skipped": 2,
        "failed": 0,
        "planes_deleted": 0,
        "planes_kept": 0,
        "message": "",
    }
    header.update(overrides)
    return header


def test_reads_all_the_counts_of_a_successful_result():
    fields = bridge_receive.read_proxy_result(proxy_header())
    assert fields["obj_id"] == "abc123"
    assert fields["name"] == "Cube"
    assert fields["ok"] is True
    assert fields["requested"] == 12
    assert fields["created"] == 10
    assert fields["replaced"] == 4
    assert fields["skipped"] == 2
    assert fields["failed"] == 0


def test_proxy_result_name_falls_back_to_the_object_id():
    fields = bridge_receive.read_proxy_result(proxy_header(name=""))
    assert fields["name"] == "abc123"


def test_proxy_result_without_ok_is_rejected():
    header = proxy_header()
    del header["ok"]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_proxy_result(header)


def test_proxy_result_with_a_non_boolean_ok_is_rejected():
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_proxy_result(proxy_header(ok="yes"))


def test_proxy_result_without_a_count_is_rejected():
    # the counts ARE the message: without them, nothing is left to show and
    # reading them as zero would say "no proxy created", which is worse than
    # a discarded message with one console line
    header = proxy_header()
    del header["created"]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_proxy_result(header)


def test_proxy_result_with_a_negative_count_is_rejected():
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_proxy_result(proxy_header(created=-1))


def test_failed_proxy_result_carries_the_reason():
    fields = bridge_receive.read_proxy_result(proxy_header(
        ok=False, created=0, message="the document is read-only"))
    assert fields["ok"] is False
    assert "read-only" in fields["message"]


# --- format_proxy_result ---------------------------------------------------

def test_summary_of_a_clean_creation():
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(requested=3, created=3, replaced=0, skipped=0, failed=0)))
    assert "Cube" in text
    assert "3" in text


def test_summary_mentions_the_replaced_ones():
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(replaced=4)))
    assert "4" in text


def test_proxy_result_reads_the_sketch_plane_cleanup_counts():
    # every ModelCurve rests on a SketchPlane that Revit does not clean up on
    # its own: without cleanup every re-send left half the garbage behind in
    # the model, invisibly. These two numbers are the only place where, on
    # the Blender side, one can see that the cleanup worked.
    fields = bridge_receive.read_proxy_result(
        proxy_header(planes_deleted=4, planes_kept=1))
    assert fields["planes_deleted"] == 4
    assert fields["planes_kept"] == 1


def test_proxy_result_without_the_plane_counts_is_rejected():
    # same rule as the other counts: a missing field is a content error,
    # not a zero. A deduced zero would say "no plane cleaned up" when in
    # fact it is unknown.
    header = proxy_header()
    del header["planes_deleted"]
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_proxy_result(header)


def test_summary_mentions_the_cleaned_up_sketch_planes():
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(replaced=4, planes_deleted=4)))
    assert "4 planes cleaned up" in text


def test_summary_says_when_a_plane_was_kept_because_it_is_in_use():
    # a plane deliberately left is not a failed cleanup: Revit can reuse a
    # plane the user's sketches rest on, and taking it away would be much
    # worse than leaving an empty one behind
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(replaced=4, planes_deleted=3, planes_kept=1)))
    assert "1 planes left" in text


def test_summary_without_a_replacement_says_nothing_about_planes():
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(replaced=0, planes_deleted=0, planes_kept=0)))
    assert "planes" not in text


def test_summary_mentions_the_skipped_ones():
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(skipped=2)))
    assert "2" in text


def test_summary_of_a_failure_reports_the_reason():
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(ok=False, created=0, message="document is read-only")))
    assert "document is read-only" in text
    assert "NOT" in text


def test_summary_of_a_failure_without_a_reason_says_so():
    text = bridge_receive.format_proxy_result(bridge_receive.read_proxy_result(
        proxy_header(ok=False, created=0, message="")))
    assert text.strip() != ""


# --- arcs in proxy_result -----------------------------------------------------
#
# The arcs count comes from "Arcs and lines" mode. It is optional: an
# earlier Revit add-in does not send it, and for it the arcs really are zero.

def test_proxy_result_without_arcs_reads_zero_arcs():
    fields = bridge_receive.read_proxy_result(proxy_header())
    assert fields["arcs"] == 0
    assert "edges" in bridge_receive.format_proxy_result(fields)


def test_proxy_result_with_arcs_mentions_them():
    fields = bridge_receive.read_proxy_result(proxy_header(arcs=3))
    assert fields["arcs"] == 3
    text = bridge_receive.format_proxy_result(fields)
    assert "3 arcs" in text
    assert "segments" in text


def test_proxy_result_with_a_negative_arc_count_is_rejected():
    with pytest.raises(bridge_receive.BridgeMessageError):
        bridge_receive.read_proxy_result(proxy_header(arcs=-1))


# --- which object a pull updates (Shift+D copies) -------------------------
#
# Candidates are (current name, name saved at the last pull) in the order
# bpy.data.objects lists them. A Shift+D copy inherits the saved name but
# cannot have the same current name: Blender keeps names unique.

def test_pick_pulled_prefers_the_genuine_object_over_a_renamed_copy():
    # observed in the field: the copy renamed "Cut_solid" comes first in
    # name order and used to be the one overwritten
    candidates = [("Cut_solid", "Beam [7]"), ("Beam [7]", "Beam [7]")]
    assert bridge_receive.pick_pulled(candidates) == 1


def test_pick_pulled_ignores_a_copy_with_the_default_suffix():
    candidates = [("Beam [7]", "Beam [7]"), ("Beam [7].001", "Beam [7]")]
    assert bridge_receive.pick_pulled(candidates) == 0


def test_pick_pulled_creates_a_new_object_when_only_copies_are_left():
    # original deleted (or renamed): updating a copy would destroy the
    # user's work, a new object costs nothing
    assert bridge_receive.pick_pulled([("Cut_solid", "Beam [7]")]) is None


def test_pick_pulled_keeps_the_old_behavior_for_objects_without_the_saved_name():
    # files pulled by 0.1.0: the first match, as before
    assert bridge_receive.pick_pulled([("Beam [7]", None), ("Beam [7].001", None)]) == 0


def test_pick_pulled_with_no_candidates():
    assert bridge_receive.pick_pulled([]) is None
