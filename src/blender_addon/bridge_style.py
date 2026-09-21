# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Colors of the preview inside Revit: one for the faces, one for the
# feature edges, valid for the whole preview. Sent with preview_style
# (DESIGN.md 5.3) at Connect, at every Sync and whenever the user changes
# them in the panel.
#
# This module must NOT import bpy: it is shared with the tests.

import math

from bridge_protocol import BridgeFramingError

# Same defaults as GeometryStore on the Revit side: medium gray faces,
# light gray edges, readable on the white background of Revit views.
DEFAULT_FACE_COLOR = (0.51, 0.51, 0.51)
DEFAULT_EDGE_COLOR = (0.69, 0.69, 0.69)


def _rgb(value, what):
    components = [float(c) for c in value]
    if len(components) != 3:
        raise BridgeFramingError("{} must have 3 components, has {}".format(what, len(components)))
    for c in components:
        if math.isnan(c) or c < 0.0 or c > 1.0:
            raise BridgeFramingError("{}: components must be between 0 and 1".format(what))
    # 4 decimals: far below what a screen shows, and it keeps the header
    # short and the comparison on the Revit side stable.
    return [round(c, 4) for c in components]


def build_style_header(face_color, edge_color):
    """face_color and edge_color are RGB sequences in 0..1. The scene
    properties use the COLOR_GAMMA subtype: the values are the sRGB ones the
    user sees in the color picker, which is what Revit expects."""
    return {
        "type": "preview_style",
        "face": _rgb(face_color, "face color"),
        "edge": _rgb(edge_color, "edge color"),
    }
