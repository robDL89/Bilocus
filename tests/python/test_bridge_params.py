# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Tests for bridge_params.py, the BL_ family parameters of a bake.
# Does NOT import bpy: runs under pytest with the system Python.

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "blender_addon"))

import pytest
import bridge_params
from bridge_protocol import BridgeFramingError


def by_name(params):
    return {p["name"]: p for p in params}


def test_length_and_width_are_ordered_by_size():
    params = by_name(bridge_params.build_params((0.6, 2.4, 0.9), 5.0, 1.2, {}))
    assert params["BL_Length"]["value"] == 2.4
    assert params["BL_Width"]["value"] == 0.6
    assert params["BL_Height"]["value"] == 0.9


def test_kinds_and_automatic_set():
    params = by_name(bridge_params.build_params((1.0, 2.0, 3.0), 22.0, 6.0, {}))
    assert set(params) == {"BL_Length", "BL_Width", "BL_Height", "BL_Area", "BL_Volume"}
    assert params["BL_Area"]["kind"] == "area"
    assert params["BL_Volume"]["kind"] == "volume"
    assert params["BL_Length"]["kind"] == "length"


def test_thickness_only_when_overridden():
    assert "BL_Thickness" not in by_name(bridge_params.build_params((1, 2, 3), 1.0, None, {}))
    params = by_name(bridge_params.build_params((1, 2, 3), 1.0, None, {"thickness": 0.05}))
    assert params["BL_Thickness"]["value"] == 0.05


def test_override_wins_over_automatic():
    params = by_name(bridge_params.build_params((1, 2, 3), 9.0, 6.0, {"length": 7.5, "area": 4.0}))
    assert params["BL_Length"]["value"] == 7.5
    assert params["BL_Area"]["value"] == 4.0


def test_open_mesh_has_no_volume():
    assert "BL_Volume" not in by_name(bridge_params.build_params((1, 1, 0), 0.0, None, {}))


def test_volume_override_is_ignored():
    params = by_name(bridge_params.build_params((1, 1, 1), 6.0, 1.0, {"volume": 99.0}))
    assert params["BL_Volume"]["value"] == 1.0


def test_zero_override_is_kept():
    params = by_name(bridge_params.build_params((1, 2, 3), 1.0, None, {"thickness": 0.0}))
    assert params["BL_Thickness"]["value"] == 0.0


def test_rows_say_where_each_value_comes_from():
    rows = {r[0]: r for r in bridge_params.rows((1, 2, 3), 1.0, None, {"height": 2.5})}
    assert rows["height"][3:] == (2.5, "set")
    assert rows["length"][3:] == (2.0, "auto")
    assert rows["thickness"][3:] == (None, "none")
    assert rows["volume"][3:] == (None, "none")


@pytest.mark.parametrize("bad", [-0.1, float("nan"), float("inf")])
def test_invalid_values_are_rejected(bad):
    with pytest.raises(BridgeFramingError):
        bridge_params.build_params((1, 2, 3), 1.0, None, {"length": bad})
