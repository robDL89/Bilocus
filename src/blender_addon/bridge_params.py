# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# The BL_ family parameters of a bake: rough dimensions of the object, sent
# with bake_mesh and written by Revit as type parameters of the family.
# Informative values: they do not drive the geometry, and they are rewritten
# at every bake.
#
# This module must NOT import bpy: it is shared with the tests.

import math

from bridge_protocol import BridgeFramingError

# key, Revit name, kind. The order is the panel's.
PARAMS = (
    ("length", "BL_Length", "length"),
    ("width", "BL_Width", "length"),
    ("height", "BL_Height", "length"),
    ("thickness", "BL_Thickness", "length"),
    ("area", "BL_Area", "area"),
    ("volume", "BL_Volume", "volume"),
)

# Everything but the volume, which is computed: a hand-written volume would
# be a number about a shape that does not have it.
OVERRIDABLE = ("length", "width", "height", "thickness", "area")


def _automatic(dimensions, area, volume):
    # Length and width ordered by size: "length" stays the long side
    # whichever local axis the object was modeled along.
    x, y, z = (float(c) for c in dimensions)
    return {
        "length": max(x, y),
        "width": min(x, y),
        "height": z,
        "thickness": None,
        "area": None if area is None else float(area),
        "volume": None if volume is None else float(volume),
    }


def rows(dimensions, area, volume, overrides):
    """(key, name, kind, value, source) per parameter, source "set", "auto"
    or "none" (value None: the parameter is not sent).

    dimensions is obj.dimensions (local extent times scale), area and volume
    are measured on the evaluated mesh, volume None for an open mesh."""
    automatic = _automatic(dimensions, area, volume)
    result = []
    for key, name, kind in PARAMS:
        if key in OVERRIDABLE and key in overrides:
            result.append((key, name, kind, float(overrides[key]), "set"))
        elif automatic[key] is not None:
            result.append((key, name, kind, automatic[key], "auto"))
        else:
            result.append((key, name, kind, None, "none"))
    return result


def build_params(dimensions, area, volume, overrides):
    """The params field of bake_mesh: [{name, kind, value}], SI units."""
    result = []
    for _key, name, kind, value, _source in rows(dimensions, area, volume, overrides):
        if value is None:
            continue
        if math.isnan(value) or math.isinf(value) or value < 0.0:
            raise BridgeFramingError("{}: {} is not a valid measure".format(name, value))
        result.append({"name": name, "kind": kind, "value": round(value, 6)})
    return result
