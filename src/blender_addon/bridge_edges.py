# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Preparation and packing of edges for the proxy_edges message.
# This module must NOT import bpy: it is shared with the tests.
#
# Payload: edge_count * 6 float32 little-endian, two endpoints per edge,
# x y z each. WORLD coordinates, in meters. See DESIGN.md 5.3.
#
# Unlike geometry, the message carries neither matrix nor origin: proxies
# are one-off creations that must end up where they are seen, so the
# object's matrix is applied HERE, before it is sent. That is why
# transform_point lives in this module and not in bridge_proxy.py: it is
# the only part of the path that can be verified without opening Blender.

import math
import struct

from bridge_protocol import BridgeFramingError

# The wire contract, also written in ProxyEdgeRequest.cs. If it changes here
# without changing there, no compilation fails: a runtime frame error does,
# and it only shows up by opening Revit. This module's tests are the only
# place where the contract is written a second time.
FLOATS_PER_EDGE = 6
BYTES_PER_EDGE = FLOATS_PER_EDGE * 4

# The arcs of the "Arcs and lines" mode travel AFTER the edges, in the same
# payload: start, end, midpoint at half sweep, x y z each. The order is that
# of Arc.Create(end0, end1, pointOnArc) on the Revit side. Twin of
# ProxyEdgeRequest.FloatsPerArc.
FLOATS_PER_ARC = 9
BYTES_PER_ARC = FLOATS_PER_ARC * 4

# Safety ceiling on the number of segments (edges plus arcs) in a single
# request, twin of ProxyEdgeRequest.MaxEdges. Going over it on the Revit
# side is a content error: better to notice before sending, so the message
# read by whoever pressed the button talks about the selection and not
# about the protocol.
MAX_EDGES = 10000

# Minimum edge length worth sending, in meters.
#
# It is DELIBERATELY tiny. The real tolerance is Revit's own,
# Application.ShortCurveTolerance (about 0.8 mm), and ProxyBuilder applies
# it by reading it from the application instead of making one up: it is the
# only way to skip exactly the curves Revit would reject, and it is a value
# this side does not know. Reproducing it here would mean silently
# discarding edges Revit would have accepted, after the user selected them
# by hand.
#
# What this threshold removes is only the pathological case: the edge of
# exactly zero length, which comes from meshes with overlapping vertices
# and which, if it were the only one selected, would send a message good
# only for being rejected. Short but non-zero edges go through, Revit counts
# them as dropped and reports it back with proxy_result.
MIN_EDGE_LENGTH = 1e-9


def transform_point(matrix, x, y, z):
    """Brings a point into world coordinates with a row-major 4x4 matrix.

    `matrix` is four rows of four, i.e. the shape in which obj.matrix_world
    is read in Blender (matrix[row][column]).

    The division by w is NOT done: Blender object matrices are affine, the
    last row is always (0,0,0,1). Doing it would cost a branch per point and
    cover a case that does not exist in this path."""
    rows = list(matrix)
    if len(rows) != 4:
        raise BridgeFramingError(
            "matrix with {} rows instead of 4".format(len(rows)))

    flat = []
    for row in rows:
        values = list(row)
        if len(values) != 4:
            raise BridgeFramingError(
                "matrix row with {} elements instead of 4".format(len(values)))
        flat.append(values)

    return (
        flat[0][0] * x + flat[0][1] * y + flat[0][2] * z + flat[0][3],
        flat[1][0] * x + flat[1][1] * y + flat[1][2] * z + flat[1][3],
        flat[2][0] * x + flat[2][1] * y + flat[2][2] * z + flat[2][3],
    )


def _check_point(point):
    values = tuple(point)
    if len(values) != 3:
        raise BridgeFramingError(
            "point with {} components instead of 3".format(len(values)))
    return values


def _check_edge(edge):
    points = tuple(edge)
    if len(points) != 2:
        raise BridgeFramingError(
            "edge with {} endpoints instead of 2".format(len(points)))
    return _check_point(points[0]), _check_point(points[1])


def prepare_edges(edges, min_length=MIN_EDGE_LENGTH):
    """Filters the edges before sending.

    Returns (kept, dropped), where `kept` is the list of surviving edges in
    arrival order and `dropped` a dict with three counts: 'short',
    'duplicate', 'invalid'.

    A malformed edge (not two points of three components) raises instead:
    it is a caller defect, not data to discard.

    DUPLICATES are removed because they are common on a real mesh:
    overlapping vertices, leftover doubles from a missed merge, two faces
    sharing the same edge drawn twice. Inside Revit each would become a
    ModelCurve overlapping another, invisible to the eye and impossible to
    select separately, that then stays in the file.

    The comparison ignores DIRECTION: (a,b) and (b,a) are the same segment
    and would produce the exact same line. The first one to arrive
    survives, with its own direction: two sends of the same selection must
    produce the same bytes, otherwise comparing two wire captures says
    nothing."""
    kept = []
    seen = set()
    dropped = {"short": 0, "duplicate": 0, "invalid": 0}

    for edge in edges:
        start, end = _check_edge(edge)

        finite = True
        for value in start + end:
            if not math.isfinite(value):
                finite = False
                break
        if not finite:
            # A NaN or an infinity would make ProxyEdgeRequest.Parse reject
            # the WHOLE message. Discarding only the sick edge here saves
            # all the others, and it is the same choice already made for
            # degenerate edges.
            dropped["invalid"] += 1
            continue

        dx = end[0] - start[0]
        dy = end[1] - start[1]
        dz = end[2] - start[2]
        if math.sqrt(dx * dx + dy * dy + dz * dz) <= min_length:
            dropped["short"] += 1
            continue

        # key insensitive to direction: the two endpoints sorted relative
        # to each other
        key = (start, end) if start <= end else (end, start)
        if key in seen:
            dropped["duplicate"] += 1
            continue
        seen.add(key)

        kept.append((start, end))

    return kept, dropped


def pack_edge_payload(edges):
    """The bytes of the proxy_edges payload: six little-endian float32 per
    edge.

    Rejects before producing any bytes, not after: the empty list
    (edge_count zero is a content error on the Revit side, not a removal),
    exceeding the MAX_EDGES ceiling and non-finite coordinates. The latter
    is a last line of defense: prepare_edges has already removed them, but
    this function must not assume it is always called after it."""
    flat = []
    count = 0
    for edge in edges:
        start, end = _check_edge(edge)
        count += 1
        if count > MAX_EDGES:
            raise BridgeFramingError(
                "more than {} edges in a single request".format(MAX_EDGES))
        for value in start + end:
            value = float(value)
            if not math.isfinite(value):
                raise BridgeFramingError(
                    "non-finite coordinate in edge {}".format(count - 1))
            flat.append(value)

    if count == 0:
        raise BridgeFramingError("no edge to send")

    return struct.pack("<{}f".format(len(flat)), *flat)


def pack_proxy_payload(edges, arcs):
    """The bytes of the proxy_edges payload with arcs: first the edges, six
    float32 each, then the arcs, nine float32 each.

    Without arcs it is byte for byte pack_edge_payload: the Polyline mode
    does not change. Edges can be missing if there are arcs (a closed
    circle becomes only arcs); the total cannot."""
    edge_list = list(edges)
    arc_list = list(arcs)
    if not arc_list:
        return pack_edge_payload(edge_list)

    total = len(edge_list) + len(arc_list)
    if total > MAX_EDGES:
        raise BridgeFramingError(
            "more than {} segments in a single request".format(MAX_EDGES))

    head = pack_edge_payload(edge_list) if edge_list else b""

    flat = []
    for count, arc in enumerate(arc_list):
        points = tuple(arc)
        if len(points) != 3:
            raise BridgeFramingError(
                "arc with {} points instead of 3".format(len(points)))
        for point in points:
            for value in _check_point(point):
                value = float(value)
                if not math.isfinite(value):
                    raise BridgeFramingError(
                        "non-finite coordinate in arc {}".format(count))
                flat.append(value)

    return head + struct.pack("<{}f".format(len(flat)), *flat)


def build_proxy_edges_header(obj_id, name, edge_count, arc_count=0):
    """The proxy_edges header (DESIGN.md 5.3).

    obj_id is normalized with the same rules as
    ProxyNaming.NormalizeObjectId: trim, non-empty, no control characters.
    Sending it already in canonical form is not fussiness: it is the string
    with which Revit marks the elements it creates and the one with which
    it will find them again on replacement. An id that changes shape
    crossing the wire would pile up overlapping proxies instead of
    replacing them, without any error being raised.

    `arc_count` enters the header only if positive: in Polyline mode the
    header stays the usual one, with its four fields."""
    normalized = _normalize_object_id(obj_id)

    label = name if isinstance(name, str) else ""
    label = label.strip()
    if not label:
        label = normalized

    count = int(edge_count)
    arcs = int(arc_count)
    if arcs < 0:
        raise BridgeFramingError(
            "arc_count cannot be negative, is {}".format(arcs))
    if count < 0 or (count == 0 and arcs == 0):
        raise BridgeFramingError(
            "edge_count must be positive, is {}".format(count))
    if count + arcs > MAX_EDGES:
        raise BridgeFramingError(
            "edge_count {} is over the maximum of {}".format(count + arcs, MAX_EDGES))

    header = {
        "type": "proxy_edges",
        "obj_id": normalized,
        "name": label,
        "edge_count": count,
    }
    if arcs > 0:
        header["arc_count"] = arcs
    return header


def _normalize_object_id(obj_id):
    if not isinstance(obj_id, str):
        raise BridgeFramingError("obj_id is not a string")

    trimmed = obj_id.strip()
    if not trimmed:
        raise BridgeFramingError("obj_id is empty")

    for character in trimmed:
        # the same characters ProxyNaming.IsValidObjectId rejects: inside a
        # JSON header a \n or a \0 would come back different from how they
        # started, and the replacement would silently stop working
        if ord(character) < 32 or ord(character) == 127:
            raise BridgeFramingError(
                "obj_id with control characters: {!r}".format(obj_id))

    return trimmed


def describe_prepared(kept_count, dropped):
    """How many edges remain and how many were removed, with the reason.

    It is a fragment, not a complete sentence: it is used both by the panel
    before sending ("ready: ...") and by the message after sending
    ("sent: ...")."""
    text = "{} edges".format(kept_count)

    reasons = []
    if dropped.get("short"):
        reasons.append("{} zero-length".format(dropped["short"]))
    if dropped.get("duplicate"):
        reasons.append("{} duplicate".format(dropped["duplicate"]))
    if dropped.get("invalid"):
        reasons.append("{} with invalid coordinates".format(dropped["invalid"]))

    if reasons:
        text = "{} ({} dropped: {})".format(
            text,
            dropped.get("short", 0) + dropped.get("duplicate", 0)
            + dropped.get("invalid", 0),
            ", ".join(reasons))

    return text
