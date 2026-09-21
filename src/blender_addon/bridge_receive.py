# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Reading of messages coming from Revit: geometry payload, header fields
# and batch bookkeeping.
# This module must NOT import bpy: it is shared with the tests.
#
# Payload order: positions (vert*3 f32), normals (vert*3 f32),
# indices (tri*3 u32). Little-endian, meters, Z-up. See DESIGN.md 5.2 and 5.4.
#
# Positions are in coordinates LOCAL to the element: the world position is
# in the header's origin field, and that is where bridge_import puts the
# object. See _read_origin further down for why.
#
# Everything that can be decided without Blender lives here. The sibling
# module bridge_import.py touches bpy and for that reason is not covered by
# tests: every line that can live on this side must live on this side.

import math
import struct

from bridge_protocol import BridgeMessageError


def unpack_mesh_payload(payload, vertex_count, triangle_count):
    """Returns (positions, normals, indices) as tuples of numbers."""
    if vertex_count < 0:
        raise BridgeMessageError("negative vert_count: {}".format(vertex_count))
    if triangle_count < 0:
        raise BridgeMessageError("negative tri_count: {}".format(triangle_count))

    expected = vertex_count * 3 * 4 * 2 + triangle_count * 3 * 4
    if len(payload) != expected:
        raise BridgeMessageError(
            "payload of {} bytes but the header declares {} (vert {}, tri {})".format(
                len(payload), expected, vertex_count, triangle_count))

    floats = vertex_count * 3
    positions = struct.unpack_from("<{}f".format(floats), payload, 0)
    normals = struct.unpack_from("<{}f".format(floats), payload, floats * 4)
    indices = struct.unpack_from(
        "<{}I".format(triangle_count * 3), payload, floats * 4 * 2)

    return positions, normals, indices


def group_into_triples(flat):
    """Groups a flat sequence into tuples of 3 consecutive elements.
    Used for positions and normals before from_pydata."""
    if len(flat) % 3 != 0:
        raise BridgeMessageError(
            "sequence of length {} not a multiple of 3".format(len(flat)))
    return tuple(tuple(flat[i:i + 3]) for i in range(0, len(flat), 3))


def check_indices(indices, vertex_count):
    """Verifies that every index points to an existing vertex.

    from_pydata does not do this check: an out-of-range index does not
    raise a readable error, it writes past the end of the vertex array.
    Better to discard the message with a written reason.

    min and max are builtins in C, as in bridge_mesh.pack_mesh_payload: on a
    large mesh a Python loop over every index would cost more than the rest
    of the import combined."""
    if not len(indices):
        return
    lowest = min(indices)
    highest = max(indices)
    if lowest < 0 or highest >= vertex_count:
        bad = lowest if lowest < 0 else highest
        raise BridgeMessageError(
            "index {} out of the range 0..{}".format(bad, vertex_count - 1))


# --- header fields -----------------------------------------------------
#
# The names are the ones written by MessageRouter.BuildGeometryHeader and
# BuildBatchBegin in src/Bilocus.Revit/Net/MessageRouter.cs, tabulated in
# DESIGN.md 5.4. They are the source of truth: if they change there, they
# change here.

# Opaque light gray, used when the color is missing or arrives malformed.
DEFAULT_COLOR = (0.8, 0.8, 0.8, 1.0)


def element_id_key(value):
    """The key used to find the element again on the next pull.

    It is a STRING on purpose. Blender's integer custom properties are
    32-bit, while ElementId.Value in Revit 2024+ is a long: an id beyond
    2^31 would make the property assignment fail, i.e. the element would
    fail to import. The string also removes the ambiguity between 348219
    and 348219.0, which json.loads can produce depending on how the number
    was written."""
    if isinstance(value, bool) or value is None:
        raise BridgeMessageError("invalid element_id: {}".format(value))
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        if value != int(value):
            raise BridgeMessageError("non-integer element_id: {}".format(value))
        return str(int(value))
    if isinstance(value, str):
        text = value.strip()
        if not text:
            raise BridgeMessageError("empty element_id")
        return text
    raise BridgeMessageError(
        "element_id of unexpected type: {}".format(type(value).__name__))


def _read_count(header, name):
    value = header.get(name)
    if isinstance(value, bool) or not isinstance(value, int):
        raise BridgeMessageError("field {} missing or not an integer".format(name))
    if value < 0:
        raise BridgeMessageError("field {} is negative: {}".format(name, value))
    return value


def _read_text(header, name):
    value = header.get(name)
    if not isinstance(value, str):
        return ""
    return value


def _read_color(header):
    value = header.get("color")
    if not isinstance(value, (list, tuple)) or len(value) != 4:
        return DEFAULT_COLOR
    result = []
    for component in value:
        if isinstance(component, bool) or not isinstance(component, (int, float)):
            # color is cosmetic: a wrong component is not a reason to throw
            # away a wall's geometry
            return DEFAULT_COLOR
        result.append(float(component))
    return tuple(result)


def _read_origin(header):
    """The element's origin: three floats in meters, world coordinates.

    It is the center of the bounding box computed by
    TessellatedMesh.ComputeOrigin on the Revit side. The vertices in the
    payload arrive in LOCAL coordinates and this field is what puts them
    back in place: without it, the object would have its origin at (0,0,0)
    and would rotate around the scene origin instead of on itself.

    Unlike color it has NO default. Color is cosmetic, so a malformed value
    is substituted and things carry on; a malformed origin would put the
    element in the wrong place, which is the worst possible error for a
    reference geometry. It is a CONTENT error: the stream stays aligned at
    the frame boundary, only this one element is discarded."""
    value = header.get("origin")
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        raise BridgeMessageError(
            "origin field missing or not an array of 3 numbers: {}".format(value))

    result = []
    for component in value:
        if isinstance(component, bool) or not isinstance(component, (int, float)):
            raise BridgeMessageError(
                "non-numeric component in origin: {}".format(value))
        component = float(component)
        # json.loads accepts the NaN and Infinity literals. A NaN would end
        # up in obj.location and make the object disappear from the
        # viewport without a single error message, exactly like the
        # degenerate normals that ElementTessellator discards on the other
        # side.
        if not math.isfinite(component):
            raise BridgeMessageError(
                "non-finite component in origin: {}".format(value))
        result.append(component)
    return tuple(result)


def read_geometry_header(header):
    """Extracts the fields of revit_geometry, already validated and
    normalized."""
    element_id = element_id_key(header.get("element_id"))
    name = _read_text(header, "name")
    if not name:
        # bpy.data.objects.new("") produces an object with an unusable name:
        # better an ugly but findable name in the outliner
        name = "Revit {}".format(element_id)
    return {
        "element_id": element_id,
        "name": name,
        "category": _read_text(header, "category"),
        "type_name": _read_text(header, "type_name"),
        "vert_count": _read_count(header, "vert_count"),
        "tri_count": _read_count(header, "tri_count"),
        "origin": _read_origin(header),
        "color": _read_color(header),
    }


def read_batch_count(header):
    """The element count announced by revit_batch_begin, or None.

    Only used by the panel's status line, so a missing or nonsensical value
    becomes "unknown" rather than an error: the batch carries on anyway,
    because the real messages are the revit_geometry ones that follow."""
    value = header.get("count")
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    if value < 0:
        return None
    return value


# --- batch bookkeeping ---------------------------------------------------
#
# A batch is the sequence revit_batch_begin / N revit_geometry /
# revit_batch_end. Messages arrive from the queue ONE AT A TIME, so the
# state must survive from one timer tick to the next, and must also survive
# a batch that never closes.
#
# A batch stays open in three ways:
#
# 1. a new revit_batch_begin arrives before the previous one's
#    revit_batch_end. The old one closes as interrupted and the new one
#    starts clean.
# 2. the connection drops. The drain loop notices on the next tick and
#    closes it.
# 3. nothing more arrives while still connected (Revit dead without the
#    socket dropping, or SendSelection aborted with batch_end never sent).
#    After BATCH_TIMEOUT_SECONDS of silence the batch closes as interrupted.
#
# In all three cases the objects already imported STAY in the scene: they
# are valid geometry that arrived in full, and deleting it would be the
# wrong reaction to a problem that only concerns the closing message. Only
# the bookkeeping is closed, telling the panel that the pull is partial.

NO_PULL_MESSAGE = "no pull in this session"

# Generous on purpose: between two revit_geometry messages on localhost only
# milliseconds pass, so 15 s of silence do not happen in a healthy batch
# even with Revit under load.
BATCH_TIMEOUT_SECONDS = 15.0


class BatchState(object):
    """Counters and message of the last pull from Revit.

    Every method that records time receives it from the caller instead of
    reading the clock: that is what makes the timeout testable."""

    def __init__(self):
        self.message = NO_PULL_MESSAGE
        self._clear()

    def _clear(self):
        self.open = False
        self.expected = None
        self.received = 0
        self.created = 0
        self.updated = 0
        self.failed = 0
        self.last_activity = 0.0

    def begin(self, count, now):
        """Opens a batch. If one was left open, returns the reason for its
        closure, otherwise None."""
        note = None
        if self.open:
            note = "previous batch never closed, {} objects".format(self.received)
        self._clear()
        self.open = True
        self.expected = count
        self.last_activity = now
        self.message = self._progress()
        return note

    def record(self, created, now):
        """An imported element. created tells whether it was created or
        updated."""
        self._ensure_open(now)
        self.received += 1
        if created:
            self.created += 1
        else:
            self.updated += 1
        self.last_activity = now
        self.message = self._progress()

    def record_failure(self, now):
        self._ensure_open(now)
        self.failed += 1
        self.last_activity = now
        self.message = self._progress()

    def end(self, now):
        """Closes the batch. False if none was open."""
        if not self.open:
            return False
        self.open = False
        self.last_activity = now
        self.message = self._summary()
        return True

    def check_interrupted(self, connected, now):
        """Closes a batch left open. Returns the reason, or None if there
        was nothing to close."""
        if not self.open:
            return None
        if not connected:
            reason = "connection dropped before revit_batch_end"
        elif now - self.last_activity > BATCH_TIMEOUT_SECONDS:
            reason = "no message for {:.0f} s".format(now - self.last_activity)
        else:
            return None
        self.open = False
        self.message = "{} - INTERRUPTED: {}".format(self._summary(), reason)
        return reason

    def _ensure_open(self, now):
        # geometry arrived without a revit_batch_begin: an implicit batch is
        # opened instead of discarding the message. Losing a wall because a
        # header without a payload got lost would be the wrong trade.
        if self.open:
            return
        self._clear()
        self.open = True
        self.expected = None
        self.last_activity = now

    def _progress(self):
        if self.expected is None:
            text = "pull in progress: {} objects".format(self.received)
        else:
            text = "pull in progress: {} of {}".format(self.received, self.expected)
        return self._with_failures(text)

    def _summary(self):
        text = "{} objects: {} created, {} updated".format(
            self.received, self.created, self.updated)
        return self._with_failures(text)

    def _with_failures(self, text):
        if self.failed:
            return "{} ({} failed)".format(text, self.failed)
        return text


# --- proxy_result ---------------------------------------------------------
#
# The outcome of proxy creation, written by MessageRouter.BuildProxyResult
# on the Revit side (DESIGN.md 5.4).
#
# It exists to plug a precise hole: proxy creation starts from an
# ExternalEvent triggered by the network, and the Revit side deliberately
# opens NO dialog - a modal that pops open while the user is in the middle
# of a Revit command is worse than the problem it would report. But whoever
# pressed the button is in Blender, and without this message, on SUCCESS
# they would receive nothing: only failure would reach them, as an error
# frame. A button that only speaks up when things go wrong is a button
# nobody trusts.
#
# The counts are read STRICTLY, unlike revit_geometry's color: here they are
# the entire content of the message, and reading a zero in place of a
# missing field would say "created nothing" when in fact it is unknown.

#
# planes_deleted and planes_kept concern REPLACEMENT: every ModelCurve rests
# on a SketchPlane that Revit does not clean up on its own when the curve
# goes away, and without cleanup every re-send left half the garbage behind
# in the model. The path that cleans up is exactly the one that opens no
# dialog in Revit: if these two numbers did not make it this far, the only
# place to look at them would be on the other side of the bridge.

_PROXY_COUNTS = (
    "requested", "created", "replaced", "skipped", "failed",
    "planes_deleted", "planes_kept",
)


def read_proxy_result(header):
    """Extracts the fields of proxy_result, already validated."""
    obj_id = _read_text(header, "obj_id")
    name = _read_text(header, "name")
    if not name:
        name = obj_id if obj_id else "?"

    ok = header.get("ok")
    if not isinstance(ok, bool):
        raise BridgeMessageError("ok field missing or not a boolean")

    fields = {
        "obj_id": obj_id,
        "name": name,
        "ok": ok,
        "message": _read_text(header, "message"),
    }
    for key in _PROXY_COUNTS:
        fields[key] = _read_count(header, key)

    # Arcs created, from "Arcs and lines" mode. The only optional count: an
    # earlier Revit add-in does not send it, and for it the arcs really are
    # zero because it does not know how to create them. When present, it
    # follows the same strict rule as the other counts.
    fields["arcs"] = _read_count(header, "arcs") if "arcs" in header else 0
    return fields


def format_proxy_result(fields):
    """The line the panel shows after creation."""
    if not fields["ok"]:
        reason = fields["message"]
        if not reason:
            reason = "reason not reported by Revit"
        return "proxies NOT created for '{}': {}".format(fields["name"], reason)

    if fields.get("arcs"):
        text = "proxies for '{}': {} lines ({} arcs) over {} segments".format(
            fields["name"], fields["created"], fields["arcs"], fields["requested"])
    else:
        text = "proxies for '{}': {} lines over {} edges".format(
            fields["name"], fields["created"], fields["requested"])

    if fields["replaced"]:
        text = "{}, {} replaced".format(text, fields["replaced"])
        # Only when there was a replacement: outside that case the two
        # counts are zero and say nothing.
        if fields["planes_deleted"]:
            text = "{}, {} planes cleaned up".format(text, fields["planes_deleted"])
        if fields["planes_kept"]:
            text = "{}, {} planes left because in use".format(
                text, fields["planes_kept"])
    if fields["skipped"]:
        text = "{}, {} skipped because too short".format(text, fields["skipped"])
    if fields["failed"]:
        text = "{}, {} failed".format(text, fields["failed"])

    return text
