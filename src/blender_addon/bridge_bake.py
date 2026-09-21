# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Bake protocol, Blender side, in the two modes DirectShape and family:
# categories, packing of the bake_mesh payload, headers of the four
# messages and reading of bake_result.
# This module must NOT import bpy: it is shared with the tests.
#
# Layout of the bake_mesh payload, little-endian, no padding, in this order:
#
#   positions     vert_count * 3   float32   x y z LOCAL, meters
#   face_sizes    face_count       uint32    vertices of each polygon (>= 3)
#   face_vertices loop_count       uint32    vertex indices, polygon after polygon
#   tri_vertices  tri_count * 3    uint32    Blender's triangles
#   tri_faces     tri_count        uint32    owning polygon
#
# See the wire contract in docs/plans/2026-09-14-fase-b.md, its extension in
# docs/plans/2026-09-14-fase-b2.md and DESIGN.md 5.3 / 5.4.
# The payload is identical in both modes: only the headers change.
#
# Why BOTH the polygons AND the triangles travel: planarity is decided by
# Revit, with its own tolerance, polygon by polygon. A flat quad stays a
# whole face; a warped one falls back to triangles, and those triangles must
# be the ones the user sees in Blender, not a triangulation invented on the
# other side.
#
# The validations of pack_bake_payload are the SAME as BakeMeshPayload.Parse
# on the C# side. On the Revit side these are content errors that arrive as
# a frame error; here they are rejected before producing any bytes, so the
# message read by whoever pressed the button talks about the object and not
# about the protocol.

import collections
import itertools
import math
import operator
import re
import struct

from bridge_edges import _normalize_object_id
from bridge_mesh import flat_matrix
from bridge_protocol import BridgeFramingError, BridgeMessageError
from bridge_receive import _read_count, _read_text


# --- categories -----------------------------------------------------------------
#
# Pairs (id, label). The id is the name of a Revit BuiltInCategory: it is
# the string that travels on the wire and that Revit resolves with
# Enum.TryParse. Whether a category exists but is not allowed for a
# DirectShape is decided by Revit (DirectShape.IsValidCategoryId) and only
# fails that one object: this list is the menu, not the source of truth on
# validity.
#
# The default comes FIRST: EnumProperty requires it among the items, and
# the menu order is the order in which the list is read.

DEFAULT_CATEGORY = "OST_GenericModel"

_CATEGORY_LABELS = (
    ("OST_GenericModel", "Generic Model"),
    ("OST_Walls", "Walls"),
    ("OST_Floors", "Floors"),
    ("OST_Roofs", "Roofs"),
    ("OST_Ceilings", "Ceilings"),
    ("OST_Columns", "Columns"),
    ("OST_StructuralColumns", "Structural Columns"),
    ("OST_StructuralFraming", "Structural Framing"),
    ("OST_Furniture", "Furniture"),
    ("OST_Casework", "Casework"),
    ("OST_SpecialityEquipment", "Specialty Equipment"),
    ("OST_Planting", "Planting"),
    ("OST_Site", "Site"),
    ("OST_Mass", "Mass"),
    ("OST_Stairs", "Stairs"),
    ("OST_StairsRailing", "Railings"),
    ("OST_Doors", "Doors"),
    ("OST_Windows", "Windows"),
    ("OST_Entourage", "Entourage"),
    ("OST_Parking", "Parking"),
)

# Tuples (id, label, description): the shape of EnumProperty items.
BAKE_CATEGORIES = tuple(
    (identifier, label,
     "The DirectShape ends up in the Revit category {} ({})".format(label, identifier))
    for identifier, label in _CATEGORY_LABELS)

# Twin of the BakeCategory.IsWellFormed regex on the C# side. fullmatch is
# used instead of match with ^...$: in Python (and in .NET too) $ accepts a
# trailing "\n", and "OST_Walls\n" would pass on this side only to be
# rejected on the other, or worse, end up in an Enum.TryParse that does not
# recognize it.
_CATEGORY_PATTERN = re.compile(r"OST_[A-Za-z0-9_]+")


def is_well_formed_category(category):
    """True if `category` has the shape of a BuiltInCategory: OST_ followed
    by ASCII letters, digits and underscores. It does not say whether Revit
    accepts it for a DirectShape: that is only known inside Revit."""
    return isinstance(category, str) and _CATEGORY_PATTERN.fullmatch(category) is not None


# System categories of the menu: in Revit walls, floors, roofs, ceilings,
# stairs and railings are not loadable families, so a family document
# cannot take them on. This is a courtesy filter for the family bake: say
# right away in Blender "this category cannot go into a family" instead of
# announcing the object and letting it fail on the other side. The final
# answer stays with Revit, which also rejects categories not listed here.
NON_LOADABLE_CATEGORIES = frozenset((
    "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Stairs", "OST_StairsRailing",
))


def is_family_category(category):
    """True if `category` is well formed and is not one of the system
    categories in NON_LOADABLE_CATEGORIES. A malformed category is false: it
    would not even reach Revit, so it cannot become a family."""
    return is_well_formed_category(category) and category not in NON_LOADABLE_CATEGORIES


# --- contract limits --------------------------------------------------------
#
# Twins of MaxBakeObjects and MaxBakeFaces on the C# side. Written twice,
# here and in C#: this module's tests are the only place where a divergence
# shows up without opening Revit.

MAX_BAKE_OBJECTS = 500

# Twin of MaxFamilyBakeObjects. Lower because every object of a family bake
# opens, fills, loads and closes a family document: it is slow by
# construction, and a batch of 500 would keep Revit blocked for minutes.
MAX_FAMILY_BAKE_OBJECTS = 50

# Shared ceiling for vert_count, face_count and tri_count. loop_count has no
# ceiling of its own and does not need one: the rule "size-2 triangles per
# polygon" forces loop_count == tri_count + 2 * face_count, so it is already
# bounded by the other two. In the worst case the payload stays under 90 MB,
# far from the 256 MiB of MAX_PAYLOAD_BYTES.
MAX_BAKE_FACES = 2000000

BAKE_ACTIONS = ("bake", "remove")

# The two bake modes, twins of BakeTarget on the C# side. The first is the
# default: a batch with no target, for Revit, is a DirectShape bake as in
# Phase B.
BAKE_TARGETS = ("directshape", "family")

# The target of a removal bake_result: "Remove bake" takes away whatever the
# bridge created in both modes, so it does not belong to either.
REMOVE_TARGET = "all"


def expected_bake_payload_length(vert_count, face_count, loop_count, tri_count):
    """The length in bytes Revit expects given the header counts:
    12*vert + 4*face + 4*loop + 16*tri (three uint32 of tri_vertices plus
    one of tri_faces per triangle)."""
    return 12 * vert_count + 4 * face_count + 4 * loop_count + 16 * tri_count


# --- loop layout ------------------------------------------------------------

def check_loop_layout(loop_starts, loop_totals):
    """True if the polygon loops are contiguous and in polygon order.

    face_vertices on the wire is "polygon after polygon": it is exactly
    loops.vertex_index only if polygons[i].loop_start equals the sum of the
    preceding loop_total values. Blender guarantees this for normally built
    meshes, but it is not a documented invariant of the API: if this
    function says false, extraction must reorder instead of sending
    vertices attributed to the wrong polygon, which Revit cannot recognize
    as an error (the indices stay all in range).

    A single list comparison: accumulate and list equality run in C, no
    Python loop per polygon."""
    starts = list(loop_starts)
    totals = list(loop_totals)
    if len(starts) != len(totals):
        return False
    expected = list(itertools.accumulate(totals, initial=0))
    # the last value is the end of the last polygon, not the start of one
    expected.pop()
    return starts == expected


# --- bake_mesh payload ---------------------------------------------------------

def pack_bake_payload(positions, face_sizes, face_vertices, tri_vertices, tri_faces):
    """The bytes of the bake_mesh payload in the contract's layout.

    All arguments are FLAT sequences, in the shape foreach_get fills them:
    positions (3 floats per vertex), face_sizes (loop_total), face_vertices
    (loops.vertex_index), tri_vertices (3 indices per triangle), tri_faces
    (polygon_index). The header counts are derived from the same sequences:
    vert_count = len(positions) // 3, face_count = len(face_sizes),
    loop_count = len(face_vertices), tri_count = len(tri_faces).

    No Python loop per vertex or per index: min, max, sum, Counter and
    struct.pack are written in C. On a mesh with a million triangles a "for"
    over every index would cost more than the packing itself, on Blender's
    main thread, with the window frozen.

    Raises BridgeFramingError before returning any byte."""
    if len(positions) % 3 != 0:
        raise BridgeFramingError(
            "positions must have a length that is a multiple of 3, has {}".format(len(positions)))
    if len(tri_vertices) % 3 != 0:
        raise BridgeFramingError(
            "tri_vertices must have a length that is a multiple of 3, has {}".format(
                len(tri_vertices)))

    vert_count = len(positions) // 3
    face_count = len(face_sizes)
    loop_count = len(face_vertices)
    tri_count = len(tri_vertices) // 3
    _check_counts(vert_count, face_count, tri_count)

    if len(tri_faces) != tri_count:
        raise BridgeFramingError(
            "tri_faces is {} long but there are {} triangles".format(len(tri_faces), tri_count))

    smallest = min(face_sizes)
    if smallest < 3:
        raise BridgeFramingError(
            "polygon {} has {} vertices, at least 3 are needed".format(
                list(face_sizes).index(smallest), smallest))

    total = sum(face_sizes)
    if total != loop_count:
        raise BridgeFramingError(
            "the sum of face_sizes is {} but face_vertices has {} indices".format(
                total, loop_count))

    position_bytes = _pack_float32(positions, "positions")

    _check_index_range(face_vertices, vert_count, "face_vertices")
    _check_index_range(tri_vertices, vert_count, "tri_vertices")
    _check_index_range(tri_faces, face_count, "tri_faces")
    _check_triangles_per_polygon(face_sizes, tri_faces, face_count)

    return b"".join([
        position_bytes,
        _pack_uint32(face_sizes, "face_sizes"),
        _pack_uint32(face_vertices, "face_vertices"),
        _pack_uint32(tri_vertices, "tri_vertices"),
        _pack_uint32(tri_faces, "tri_faces"),
    ])


def _check_counts(vert_count, face_count, tri_count):
    # The minimum thresholds are those below which no object exists: fewer
    # than three vertices do not close a polygon, and an object with no
    # faces or no triangles has nothing to become a DirectShape.
    _check_bounds("vert_count", vert_count, 3)
    _check_bounds("face_count", face_count, 1)
    _check_bounds("tri_count", tri_count, 1)


def _check_bounds(name, value, minimum):
    if value < minimum:
        raise BridgeFramingError(
            "{} is {}, must be at least {}".format(name, value, minimum))
    if value > MAX_BAKE_FACES:
        raise BridgeFramingError(
            "{} {} is over the maximum of {}".format(name, value, MAX_BAKE_FACES))


def _pack_float32(values, what):
    # struct.pack first, then the sum. The order matters: struct.pack
    # rejects finite values beyond a float32's range (OverflowError) but
    # lets NaN and infinities through. Once it has passed, every finite
    # value is under 3.4e38, so the sum of a few million values cannot leave
    # a double's range: if the sum is not finite, there is a NaN or an
    # infinity inside, with no false positives and no Python loop.
    try:
        data = struct.pack("<{}f".format(len(values)), *values)
    except (OverflowError, struct.error) as error:
        raise BridgeFramingError(
            "{} contains a value that cannot be represented as float32: {}".format(
                what, error))
    if not math.isfinite(sum(values)):
        raise BridgeFramingError(
            "{} contains non-finite values (NaN or infinity)".format(what))
    return data


def _pack_uint32(values, what):
    # The ranges are already checked: struct.error can only arise here from
    # a non-integer value, which is a caller defect and deserves a message
    # that names which array.
    try:
        return struct.pack("<{}I".format(len(values)), *values)
    except struct.error as error:
        raise BridgeFramingError(
            "{} contains a non-integer value: {}".format(what, error))


def _check_index_range(values, limit, what):
    lowest = min(values)
    highest = max(values)
    if lowest < 0 or highest >= limit:
        bad = lowest if lowest < 0 else highest
        raise BridgeFramingError(
            "{} contains index {} out of range 0..{}".format(
                what, bad, limit - 1))


def _check_triangles_per_polygon(face_sizes, tri_faces, face_count):
    # A polygon with n vertices splits into exactly n-2 triangles, and
    # Blender guarantees it. If the count does not add up, polygons and
    # triangles come from two different readings of the mesh and the object
    # is garbage: a non-planar polygon would fall back onto the triangles
    # of another polygon.
    #
    # It is counted, the order is not assumed: loop_triangles does not
    # promise to list triangles grouped by polygon. counts.get is dict's
    # get, in C: unlike counts[i] it does not go through
    # Counter.__missing__, which is Python, for polygons with no triangles.
    counts = collections.Counter(tri_faces)
    found = list(map(counts.get, range(face_count), itertools.repeat(0)))
    expected = list(map(operator.sub, face_sizes, itertools.repeat(2)))
    if found != expected:
        index = list(map(operator.ne, found, expected)).index(True)
        raise BridgeFramingError(
            "polygon {} has {} vertices but {} triangles instead of {}: "
            "tri_faces and face_sizes are misaligned".format(
                index, face_sizes[index], found[index], expected[index]))


# --- Blender -> Revit message headers -------------------------------------------
#
# The field names are the contract with MessageRouter.cs. Changing one here
# without changing it there does not produce a compile error: it produces a
# runtime frame error, which only shows up by opening Revit.

def build_bake_begin_header(obj_ids, target="directshape", host=None):
    """The bake_begin header: the objects about to arrive and the bake mode,
    "directshape" or "family".

    The ids are sent already in the canonical form of
    ProxyNaming.NormalizeObjectId: it is the string with which Revit marks
    the DirectShape or the family and with which it will find them again at
    the next bake. An id that changes shape crossing the wire would create a
    duplicate instead of replacing.

    target is ALWAYS written, even when it equals the default: Revit treats
    its absence as directshape, but an explicit header reads in the logs
    without knowing that rule. The object limit depends on the target.

    host: None for a normal bake. The obj_id of the active object for "Bake
    together": one family with all the objects, named after the host,
    placed at its origin and identified by its obj_id. Only with target
    family, and the host must be one of obj_ids."""
    # The target is checked before the ids: a wrong value is a caller
    # defect, and the message must say so instead of talking about the
    # objects. isinstance before "in": a number or a list must not reach a
    # comparison with the contract's strings.
    if not isinstance(target, str) or target not in BAKE_TARGETS:
        raise BridgeFramingError(
            "bake_begin: unknown target {!r}, allowed values are {}".format(
                target, ", ".join(BAKE_TARGETS)))
    limit = MAX_FAMILY_BAKE_OBJECTS if target == "family" else MAX_BAKE_OBJECTS
    header = {
        "type": "bake_begin",
        "obj_ids": _normalize_object_ids(obj_ids, "bake_begin", limit),
        "target": target,
    }
    if host is not None:
        if target != "family":
            raise BridgeFramingError("bake_begin: host (bake together) is only allowed with target family")
        normalized = _normalize_object_ids([host], "bake_begin")[0]
        if normalized not in header["obj_ids"]:
            raise BridgeFramingError(
                "bake_begin: host {!r} is not among the announced obj_ids".format(normalized))
        header["host"] = normalized
    return header


def build_bake_mesh_header(obj_id, name, category, matrix,
                           vert_count, face_count, loop_count, tri_count,
                           accept_open=False):
    """The bake_mesh header. The counts must be those of the sequences
    passed to pack_bake_payload (see its docstring).

    The matrix is obj.matrix_world, 16 row-major floats: the positions of
    the payload stay LOCAL as in geometry, and Revit applies the matrix.
    Both 4x4 and already-flat are accepted, as for geometry.

    accept_open says whether the object can become a family even as an open
    shell. Revit ignores it in the directshape batch, but it is ALWAYS
    written: the header is the same in both modes."""
    normalized = _normalize_object_id(obj_id)

    # Only a real bool. A 1 or a "true" on the wire is a content error for
    # Revit, and a silent bool(x) here would turn even the string "False"
    # of a badly read property into True.
    if not isinstance(accept_open, bool):
        raise BridgeFramingError(
            "accept_open must be a boolean, is {!r}".format(accept_open))

    label = name if isinstance(name, str) else ""
    label = label.strip()
    if not label:
        # same fallback as BakeMeshRequest.Parse: a DirectShape with no name
        # ends up in the project identified only by its ElementId
        label = normalized

    if not is_well_formed_category(category):
        raise BridgeFramingError(
            "category {!r} does not have the OST_Name shape of a Revit category".format(
                category))

    flat = flat_matrix(matrix)
    # json.dumps would write NaN as a non-JSON literal, and
    # System.Text.Json would reject the whole header: better to stop it
    # here with the field name.
    _pack_float32(flat, "matrix")

    vert_count = _header_count(vert_count, "vert_count")
    face_count = _header_count(face_count, "face_count")
    loop_count = _header_count(loop_count, "loop_count")
    tri_count = _header_count(tri_count, "tri_count")
    _check_counts(vert_count, face_count, tri_count)

    # Direct consequence of the "size-2 triangles per polygon" rule: summing
    # over every polygon gives tri_count = loop_count - 2 * face_count.
    # Revit would reject the header+payload pair anyway, but only after
    # receiving megabytes; an inconsistent header shows up here, from the
    # numbers alone, and it is usually a len(tri_vertices) passed instead of
    # len(tri_faces).
    if tri_count != loop_count - 2 * face_count:
        raise BridgeFramingError(
            "inconsistent tri_count {}: with loop_count {} and face_count {} "
            "the triangles must be {}".format(
                tri_count, loop_count, face_count, loop_count - 2 * face_count))

    return {
        "type": "bake_mesh",
        "obj_id": normalized,
        "name": label,
        "category": category,
        "matrix": flat,
        "vert_count": vert_count,
        "face_count": face_count,
        "loop_count": loop_count,
        "tri_count": tri_count,
        "accept_open": accept_open,
    }


def build_bake_end_header():
    return {"type": "bake_end"}


def build_bake_remove_header(obj_ids):
    """The bake_remove header. Same id rules as bake_begin."""
    return {"type": "bake_remove", "obj_ids": _normalize_object_ids(obj_ids, "bake_remove")}


def _header_count(value, name):
    # bool is a subclass of int in Python: True would end up in the JSON as
    # true, not as 1, and Revit would reject it as a non-numeric field.
    if isinstance(value, bool):
        raise BridgeFramingError("{} must be an integer, is {!r}".format(name, value))
    try:
        return operator.index(value)
    except TypeError:
        raise BridgeFramingError("{} must be an integer, is {!r}".format(name, value))


def _normalize_object_ids(obj_ids, message_type, limit=None):
    # A string is iterable: without this check "abc" would become three
    # objects named "a", "b" and "c".
    if obj_ids is None or isinstance(obj_ids, (str, bytes)):
        raise BridgeFramingError(
            "{}: obj_ids must be a list of strings".format(message_type))
    values = list(obj_ids)
    if not values:
        raise BridgeFramingError("{}: no object in the message".format(message_type))
    # The default is resolved here and not in the signature: a default in
    # the signature would freeze the value at import time, and bake_remove
    # stays on the directshape limit because it removes in both modes.
    if limit is None:
        limit = MAX_BAKE_OBJECTS
    if len(values) > limit:
        raise BridgeFramingError(
            "{}: {} objects is over the maximum of {}".format(
                message_type, len(values), limit))

    # a loop per OBJECT, at most 500: not the forbidden per-vertex loop
    normalized = [_normalize_object_id(value) for value in values]

    # the comparison is on the normalized form: "abc" and " abc " are the
    # same mark in the document, so the same object twice.
    if len(set(normalized)) != len(normalized):
        duplicate = collections.Counter(normalized).most_common(1)[0][0]
        raise BridgeFramingError(
            "{}: duplicate obj_id {!r}".format(message_type, duplicate))
    return normalized


# --- bake_result ------------------------------------------------------------------
#
# The outcome of the bake or the removal, written by
# MessageRouter.BuildBakeResult. As with proxy_result the counts are read
# STRICTLY: they are the entire content of the message, and a zero inferred
# from a missing field would say "created nothing" when in fact it is
# unknown. Only message is lenient: it is empty when there is nothing to say.
#
# target, switched and not_moved are from Phase B2 and are read strictly
# like the others: the bake_result of a Revit with only the Phase B add-in,
# which does not have them, is rejected. This is intentional: the two sides
# are updated together, and an outcome read halfway ("0 replaced") would lie
# about precisely the new part.

_BAKE_COUNTS = (
    "requested", "created", "replaced", "recreated", "removed",
    "failed", "missing", "as_mesh", "faces_planar", "faces_triangulated",
    "switched", "not_moved",
)


def read_bake_result(header):
    """Extracts the already-validated fields of bake_result.

    Returns a dict with action (str, "bake" or "remove"), target (str,
    "directshape" or "family" for a bake, "all" for a removal), ok (bool),
    message (str, "" if absent) and the twelve integer counts >= 0 of
    _BAKE_COUNTS. Raises BridgeMessageError on unknown action, missing or
    unknown target incoherent with action, non-boolean ok, missing,
    non-integer or negative count."""
    action = header.get("action")
    if not isinstance(action, str) or action not in BAKE_ACTIONS:
        raise BridgeMessageError(
            "missing or unknown action field: {!r}".format(action))

    # The action/target pair is the one from the contract: a bake is one of
    # the two modes, a removal is both. An "all" on a bake, or a "family" on
    # a removal, would make the panel write the wrong mode's line: better an
    # error in the console than a misleading outcome.
    target = header.get("target")
    allowed = (REMOVE_TARGET,) if action == "remove" else BAKE_TARGETS
    if not isinstance(target, str) or target not in allowed:
        raise BridgeMessageError(
            "missing or unknown target field for action {}: {!r}".format(action, target))

    ok = header.get("ok")
    if not isinstance(ok, bool):
        raise BridgeMessageError("ok field missing or not a boolean")

    fields = {
        "action": action,
        "target": target,
        "ok": ok,
        "message": _read_text(header, "message"),
    }
    for key in _BAKE_COUNTS:
        fields[key] = _read_count(header, key)
    return fields


def format_bake_result(fields):
    """The line the panel shows after a bake or a removal."""
    if fields["action"] == "remove":
        return _format_remove(fields)
    return _format_bake(fields)


def _format_bake(fields):
    if fields["target"] == "family":
        return _format_family_bake(fields)

    if not fields["ok"]:
        text = "bake FAILED"
        if fields["failed"]:
            text = "{}: {} out of {} {}".format(
                text, _plural(fields["failed"], "failure", "failures"),
                fields["requested"], _noun(fields["requested"], "object", "objects"))
        text = "{} - {}".format(text, _reason(fields))
        return _with_missing(text, fields)

    text = "bake: {} created, {} updated, {} recreated - {}, {}".format(
        fields["created"], fields["replaced"], fields["recreated"],
        _plural(fields["faces_planar"], "whole face", "whole faces"),
        _plural(fields["faces_triangulated"], "triangulated face", "triangulated faces"))

    if fields["as_mesh"]:
        # Open or non-manifold mesh: in Revit it has no volume, and the user
        # only notices when a section does not cut it the way they expect.
        text = "{} - {} as mesh, not solid".format(
            text, _plural(fields["as_mesh"], "object", "objects"))
    return _with_tail(text, fields)


def _format_family_bake(fields):
    # recreated does not appear: for a family the category change happens
    # in place, Revit always sends it as 0 and writing it would just be
    # noise.
    if not fields["ok"]:
        text = "family bake FAILED"
        if fields["failed"]:
            text = "{}: {} out of {} {}".format(
                text, _plural(fields["failed"], "failure", "failures"),
                fields["requested"], _noun(fields["requested"], "object", "objects"))
        text = "{} - {}".format(text, _reason(fields))
        return _with_missing(text, fields)

    text = "family: {} created, {} updated - {}, {}".format(
        fields["created"], fields["replaced"],
        _plural(fields["faces_planar"], "whole face", "whole faces"),
        _plural(fields["faces_triangulated"], "triangulated face", "triangulated faces"))

    if fields["as_mesh"]:
        # In the family mode as_mesh counts the objects accepted as an open
        # shell (the "accept non-closed solid" checkbox): the family
        # exists, but its FreeFormElement does not enclose a volume, and the
        # user must know it before cutting it with a void.
        text = "{} - {} as open shell".format(text, fields["as_mesh"])
    return _with_tail(text, fields)


def _with_tail(text, fields):
    # The tails common to both modes, in the order that is easiest to read:
    # first what was removed or left in place, then what did not arrive,
    # last the failures, because their reason closes the line after the
    # colon.
    if fields["switched"]:
        # Decision 7: an object has only one mode in Revit, and the bake
        # deleted the other one. It must be said, because something the
        # user did not explicitly ask to remove disappears.
        text = "{} - {} of the other mode {}".format(
            text, _plural(fields["switched"], "element", "elements"),
            _noun(fields["switched"], "replaced", "replaced"))
    if fields["not_moved"]:
        # Copies made in Revit carry the mark: with more than one instance
        # the bridge does not know which one to move, and only updates the
        # geometry.
        text = "{} - {} not {} (copies in Revit)".format(
            text, _plural(fields["not_moved"], "instance", "instances"),
            _noun(fields["not_moved"], "moved", "moved"))
    text = _with_missing(text, fields)
    if fields["failed"]:
        text = "{} - {}".format(text, _plural(fields["failed"], "failure", "failures"))
    if fields["message"]:
        # The reason for the LAST failure, with its name: with more than one
        # failure it is a sample, not the full list, but it says where to
        # start looking.
        text = "{}: {}".format(text, fields["message"])
    return text


def _format_remove(fields):
    if not fields["ok"]:
        return "removal FAILED - {}".format(_reason(fields))

    text = "{} {} from {}".format(
        _noun(fields["removed"], "removed", "removed"),
        _plural(fields["removed"], "element", "elements"),
        _plural(fields["requested"], "object", "objects"))
    if fields["failed"]:
        text = "{} - {}".format(text, _plural(fields["failed"], "failure", "failures"))
    if fields["message"]:
        text = "{}: {}".format(text, fields["message"])
    return text


def _with_missing(text, fields):
    # Announced in bake_begin but never arrived: the Blender side failed the
    # pack halfway through the batch. Revit did not touch them, neither
    # created nor removed.
    if fields["missing"]:
        return "{} - {} never {}".format(
            text, _plural(fields["missing"], "object", "objects"),
            _noun(fields["missing"], "arrived", "arrived"))
    return text


def _reason(fields):
    if fields["message"]:
        return fields["message"]
    return "reason not reported by Revit"


def _noun(count, singular, plural):
    return singular if count == 1 else plural


def _plural(count, singular, plural):
    return "{} {}".format(count, _noun(count, singular, plural))
