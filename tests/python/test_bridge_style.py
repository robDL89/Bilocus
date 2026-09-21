# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_style.py, the preview_style header.
# Does NOT import bpy: runs under pytest with the system Python.

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_style
from bridge_protocol import BridgeFramingError


def test_header_has_type_and_both_colors():
    header = bridge_style.build_style_header((0.2, 0.3, 0.4), (0.9, 0.8, 0.7))
    assert header == {"type": "preview_style", "face": [0.2, 0.3, 0.4], "edge": [0.9, 0.8, 0.7]}


def test_components_are_rounded_to_four_decimals():
    header = bridge_style.build_style_header((0.123456, 0, 1), (1, 1, 1))
    assert header["face"] == [0.1235, 0.0, 1.0]


def test_defaults_are_valid_and_match_the_revit_side():
    header = bridge_style.build_style_header(
        bridge_style.DEFAULT_FACE_COLOR, bridge_style.DEFAULT_EDGE_COLOR)
    # GeometryStore.DefaultFaceColor / DefaultEdgeColor
    assert header["face"] == [0.51, 0.51, 0.51]
    assert header["edge"] == [0.69, 0.69, 0.69]


@pytest.mark.parametrize("face, edge", [
    ((0.2, 0.3), (0.9, 0.8, 0.7)),
    ((0.2, 0.3, 0.4, 1.0), (0.9, 0.8, 0.7)),
    ((0.2, 0.3, 1.5), (0.9, 0.8, 0.7)),
    ((0.2, 0.3, 0.4), (-0.1, 0.8, 0.7)),
    ((0.2, float("nan"), 0.4), (0.9, 0.8, 0.7)),
])
def test_invalid_colors_are_rejected(face, edge):
    with pytest.raises(BridgeFramingError):
        bridge_style.build_style_header(face, edge)
