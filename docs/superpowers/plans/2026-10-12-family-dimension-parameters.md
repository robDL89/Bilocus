# Family Dimension Parameters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A family baked from Blender carries informative `BL_` type parameters (length, width, height, thickness, area, volume), automatic from the object's extent and overridable from an edge or face selection, except the computed volume.

**Architecture:** Blender computes the values (pure logic in a new `bridge_params.py`, bpy measuring in `bridge_bake_send.py`) and sends them in an optional `params` field of `bake_mesh`. Revit validates them in a pure `BakeParameter` class, then a thin `FamilyParameters` helper writes them in the family document (add, set, delete stale `BL_`) and on the project type after the load.

**Tech Stack:** Python (bpy, bmesh; pytest without bpy), C# (Revit API 2024-2027, xUnit on net8/net48 with linked sources).

**Spec:** `docs/superpowers/specs/2026-10-12-family-dimension-parameters-design.md`

## Global Constraints

- Parameter names: `BL_Length`, `BL_Width`, `BL_Height`, `BL_Thickness`, `BL_Area`, `BL_Volume`.
- Type parameters, group Dimensions (`GroupTypeId.Geometry`), specs `SpecTypeId.Length` / `Area` / `Volume`.
- Wire values in SI units (m, m2, m3); Revit converts with `UnitUtils.ConvertToInternalUnits` and `UnitTypeId.Meters` / `SquareMeters` / `CubicMeters`.
- Family bake only; DirectShape ignores `params`. Absent `params` = no parameters.
- Parameters without the `BL_` prefix are never touched.
- Volume is never overridable and is sent only for a closed mesh (every edge manifold).
- Code rules of the repo: ASCII only in source, no f-strings in Python.

## Review Focus

- Mesh with no faces or a single open face: area 0, no volume, the bake must not fail. -> Task 1 test `test_open_mesh_has_no_volume`.
- Override of 0 (user selected a degenerate edge): a 0 value is valid and sent, not dropped. -> Task 1 test `test_zero_override_is_kept`.
- Re-bake after clearing an override: the `BL_` parameter must disappear from the family. -> Task 4, live check in Task 5.
- Bake together: only the active object's values reach the family, members' values are ignored. -> Task 4 uses `host.Parameters`; live check in Task 5.
- Malformed `params` from a hand-made client (wrong kind, negative, NaN-like 1e39, name without prefix): error frame for that object, batch goes on. -> Task 3 tests.

---

### Task 1: Pure parameter logic and header field (Blender, no bpy)

**Files:**
- Create: `src/blender_addon/bridge_params.py`
- Modify: `src/blender_addon/bridge_bake.py` (`build_bake_mesh_header`, add `params=None`)
- Test: `tests/python/test_bridge_params.py`, `tests/python/test_bridge_bake.py`

**Interfaces:**
- Produces: `bridge_params.OVERRIDABLE = ("length", "width", "height", "thickness", "area")`; `bridge_params.rows(dimensions, area, volume, overrides) -> list of (key, name, kind, value_or_None, source)` with source `"auto"`, `"set"` or `"none"`; `bridge_params.build_params(dimensions, area, volume, overrides) -> list of {"name", "kind", "value"}`; `bridge_bake.build_bake_mesh_header(..., params=None)` writes `"params"` only when not None.

- [ ] **Step 1: Write the failing tests** (`tests/python/test_bridge_params.py`)

```python
import os, sys
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
```

In `tests/python/test_bridge_bake.py`, after `test_mesh_header_has_the_fields_of_the_contract`:

```python
def test_mesh_header_carries_params_only_when_given():
    assert "params" not in mesh_header()
    params = [{"name": "BL_Length", "kind": "length", "value": 2.0}]
    assert mesh_header(params=params)["params"] == params
```

- [ ] **Step 2: Run to verify they fail**

Run: `python -m pytest tests/python/test_bridge_params.py tests/python/test_bridge_bake.py -q`
Expected: FAIL, `ModuleNotFoundError: bridge_params` and unexpected keyword `params`.

- [ ] **Step 3: Implement** `src/blender_addon/bridge_params.py`

```python
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# The BL_ family parameters of a bake: rough dimensions of the object, sent
# with bake_mesh and written by Revit as type parameters of the family.
# Informative values: they do not drive the geometry.
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
```

In `bridge_bake.build_bake_mesh_header`: add `params=None` as last parameter, document it ("the BL_ family parameters, bridge_params.build_params; only the family bake uses them, absent when None"), and before `return` build the dict into a variable, then `if params is not None: header["params"] = list(params)`.

- [ ] **Step 4: Run to verify they pass**

Run: `python -m pytest tests/python -q`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/blender_addon/bridge_params.py src/blender_addon/bridge_bake.py tests/python/test_bridge_params.py tests/python/test_bridge_bake.py
git commit -m "Compute BL_ family parameters and carry them in bake_mesh"
```

### Task 2: Measuring, overrides, operators, panel and sending (Blender, bpy)

**Files:**
- Modify: `src/blender_addon/bridge_bake_send.py` (measure, overrides, `_prepare_mesh` sends params for family)
- Modify: `src/blender_addon/panel.py` (operators `bilocus.set_bake_param`, `bilocus.clear_bake_param`, box in the bake section, `CLASSES`)

**Interfaces:**
- Consumes: `bridge_params.rows`, `bridge_params.build_params`, `bridge_params.OVERRIDABLE`, `build_bake_mesh_header(..., params=...)`.
- Produces: `bridge_bake_send.OVERRIDE_PREFIX = "bilocus_param_"`; `measure(obj, depsgraph) -> (dimensions, area, volume_or_None)`; `overrides_of(obj) -> dict`; `selection_measure(obj, key) -> (value, problem)`.

- [ ] **Step 1: Implement measuring and overrides** in `bridge_bake_send.py` (add `import bmesh` and `import bridge_params as params_logic`):

```python
OVERRIDE_PREFIX = "bilocus_param_"


def measure(obj, depsgraph):
    """(dimensions, area, volume) of the evaluated object. Area and volume
    with the object's rotation and scale, no translation; volume None when
    the mesh is not closed."""
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        bm.transform(obj.matrix_world.to_3x3().to_4x4())
        area = sum(face.calc_area() for face in bm.faces)
        closed = len(bm.edges) > 0 and all(edge.is_manifold for edge in bm.edges)
        volume = abs(bm.calc_volume()) if closed else None
    finally:
        bm.free()
        evaluated.to_mesh_clear()
    return tuple(obj.dimensions), area, volume


def overrides_of(obj):
    result = {}
    for key in params_logic.OVERRIDABLE:
        value = obj.get(OVERRIDE_PREFIX + key)
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            result[key] = float(value)
    return result


def selection_measure(obj, key):
    """(value, problem) from the Edit Mode selection: summed edge lengths
    for the lengths, summed face areas for the area, in world scale."""
    if obj is None or obj.type != 'MESH' or obj.mode != 'EDIT':
        return None, "enter Edit Mode on the object and select what to measure"
    bm = bmesh.from_edit_mesh(obj.data).copy()
    try:
        bm.transform(obj.matrix_world.to_3x3().to_4x4())
        if key == "area":
            faces = [f for f in bm.faces if f.select]
            if not faces:
                return None, "select one or more faces"
            return sum(f.calc_area() for f in faces), None
        edges = [e for e in bm.edges if e.select]
        if not edges:
            return None, "select one or more edges"
        return sum(e.calc_length() for e in edges), None
    finally:
        bm.free()
```

In `_prepare_mesh(obj, obj_id, depsgraph, reject_limit)` add a `family` argument (pass `family` from `bake_selected`), and pass to the header:

```python
        params = None
        if family:
            dimensions, area, volume = measure(obj, depsgraph)
            params = params_logic.build_params(dimensions, area, volume, overrides_of(obj))
```
then `build_bake_mesh_header(..., accept_open_of(obj), smooth_mesh_of(obj), params)`.

- [ ] **Step 2: Operators and panel** in `panel.py`:

```python
class BILOCUS_OT_set_bake_param(bpy.types.Operator):
    bl_idname = "bilocus.set_bake_param"
    bl_label = "Set from selection"
    bl_description = ("Sets this family parameter from the Edit Mode selection: "
                      "edges for lengths, faces for the area. Frozen value: "
                      "set it again if you reshape the object")
    key: bpy.props.StringProperty()

    def execute(self, context):
        obj = context.active_object
        value, problem = bake_send.selection_measure(obj, self.key)
        if problem is not None:
            self.report({'ERROR'}, problem)
            return {'CANCELLED'}
        obj[bake_send.OVERRIDE_PREFIX + self.key] = value
        self.report({'INFO'}, "{}: {:.4f}".format(self.key, value))
        return {'FINISHED'}


class BILOCUS_OT_clear_bake_param(bpy.types.Operator):
    bl_idname = "bilocus.clear_bake_param"
    bl_label = "Back to automatic"
    bl_description = "Removes the value set by hand: the parameter goes back to automatic"
    key: bpy.props.StringProperty()

    def execute(self, context):
        obj = context.active_object
        if obj is not None and (bake_send.OVERRIDE_PREFIX + self.key) in obj:
            del obj[bake_send.OVERRIDE_PREFIX + self.key]
        return {'FINISHED'}
```

Add both to `CLASSES`. In the bake box, after the category row, when `target` is a mesh: a sub-box "Family parameters (informative)" that calls `bake_send.measure(target, context.evaluated_depsgraph_get())` only in Object Mode (in Edit Mode show the stored overrides and "values update in Object Mode"), then for each row of `params_logic.rows(...)`: label `name`, value formatted (m with 3 decimals, m2 / m3 with 3), source; for keys in `OVERRIDABLE` an icon button `bilocus.set_bake_param` (icon `EYEDROPPER`) and, when source is "set", `bilocus.clear_bake_param` (icon `X`). Volume row shows "open mesh" when None.

- [ ] **Step 3: Verify**

Run: `python -m pytest tests/python -q` (no regressions) and `python -c "import ast,sys; [ast.parse(open(p,encoding='utf-8').read()) for p in sys.argv[1:]]" src/blender_addon/panel.py src/blender_addon/bridge_bake_send.py`
Expected: pass. The bpy part is verified live in Task 5.

- [ ] **Step 4: Commit**

```bash
git add src/blender_addon/bridge_bake_send.py src/blender_addon/panel.py
git commit -m "Measure, override and send the BL_ family parameters from Blender"
```

### Task 3: Parse and validate params (Revit, pure)

**Files:**
- Create: `src/Bilocus.Revit/Bake/BakeParameter.cs`
- Modify: `src/Bilocus.Revit/Bake/BakeMeshRequest.cs` (`Parameters` property, `Parse` optional argument)
- Modify: `src/Bilocus.Revit/Net/MessageRouter.cs` (`HandleBakeMesh` reads `params`)
- Modify: `tests/Bilocus.Revit.Net.Tests/Bilocus.Revit.Net.Tests.csproj` (link `BakeParameter.cs`)
- Test: `tests/Bilocus.Revit.Net.Tests/BakeParameterTests.cs`, `MessageRouterTests.cs`

**Interfaces:**
- Produces: `BakeParameter` with `Name`, `Kind` (`"length"|"area"|"volume"`), `Value` (double, SI), `static BakeParameter Create(string name, string kind, double value)` throwing `ArgumentException`; `const string Prefix = "BL_"`; `BakeMeshRequest.Parameters` (`IList<BakeParameter>`, never null).

- [ ] **Step 1: Failing tests** `BakeParameterTests.cs`:

```csharp
using System;
using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    public class BakeParameterTests
    {
        [Fact]
        public void Create_KeepsTheValues()
        {
            BakeParameter p = BakeParameter.Create("BL_Length", "length", 2.4);
            Assert.Equal("BL_Length", p.Name);
            Assert.Equal("length", p.Kind);
            Assert.Equal(2.4, p.Value);
        }

        [Theory]
        [InlineData("Length", "length", 1.0)]
        [InlineData("BL_", "length", 1.0)]
        [InlineData("BL_Len gth", "length", 1.0)]
        [InlineData("BL_Length", "weight", 1.0)]
        [InlineData("BL_Length", "length", -0.1)]
        [InlineData("BL_Length", "length", double.PositiveInfinity)]
        [InlineData("BL_Length", "length", double.NaN)]
        public void Create_RejectsInvalid(string name, string kind, double value)
        {
            Assert.Throws<ArgumentException>(() => BakeParameter.Create(name, kind, value));
        }

        [Fact]
        public void Create_AcceptsZero()
        {
            Assert.Equal(0.0, BakeParameter.Create("BL_Thickness", "length", 0.0).Value);
        }
    }
}
```

In `MessageRouterTests.cs`, next to the other `BakeMesh_*` tests:

```csharp
        [Fact]
        public void BakeMesh_WithValidParams_IsAccepted()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();
            BakeBegin(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeaderWith("obj-1",
                "\"params\": [{\"name\": \"BL_Length\", \"kind\": \"length\", \"value\": 2.4}]"),
                BakeGolden.Payload());
            Assert.Empty(sink.Sent);
        }

        [Theory]
        [InlineData("\"params\": {}")]
        [InlineData("\"params\": [{\"name\": \"Length\", \"kind\": \"length\", \"value\": 1}]")]
        [InlineData("\"params\": [{\"name\": \"BL_Length\", \"kind\": \"weight\", \"value\": 1}]")]
        [InlineData("\"params\": [{\"name\": \"BL_Length\", \"kind\": \"length\", \"value\": -1}]")]
        [InlineData("\"params\": [{\"name\": \"BL_Length\", \"kind\": \"length\"}]")]
        [InlineData("\"params\": [{\"name\": \"BL_Length\", \"kind\": \"length\", \"value\": 1e400}]")]
        public void BakeMesh_WithMalformedParams_RepliesError(string field)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();
            BakeBegin(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeaderWith("obj-1", field), BakeGolden.Payload());
            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_mesh:", ErrorMessageOf(sink, 0));
        }
```

Link the new source in the test csproj next to `BakeMeshRequest.cs`:
`<Compile Include="..\..\src\Bilocus.Revit\Bake\BakeParameter.cs" Link="Bake\BakeParameter.cs" />`

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Bilocus.Revit.Net.Tests -c Release`
Expected: build error, `BakeParameter` not found.

- [ ] **Step 3: Implement** `BakeParameter.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Revit.Bake
{
    // One BL_ family parameter of a bake_mesh: a rough dimension of the
    // Blender object, informative, written as a type parameter of the
    // family. Pure, no Revit API: FamilyParameters does the writing.
    public sealed class BakeParameter
    {
        public const string Prefix = "BL_";

        public string Name { get; private set; }
        public string Kind { get; private set; }

        // SI units: m, m2 or m3 by Kind.
        public double Value { get; private set; }

        private BakeParameter(string name, string kind, double value)
        {
            Name = name;
            Kind = kind;
            Value = value;
        }

        public static BakeParameter Create(string name, string kind, double value)
        {
            if (name == null || name.Length <= Prefix.Length || !name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("parameter name '" + name + "' must start with " + Prefix);
            }
            for (int i = Prefix.Length; i < name.Length; i++)
            {
                if (!char.IsLetter(name[i]))
                {
                    throw new ArgumentException("parameter name '" + name + "' may only contain letters after " + Prefix);
                }
            }
            if (kind != "length" && kind != "area" && kind != "volume")
            {
                throw new ArgumentException("parameter " + name + ": kind '" + kind + "' is not length, area or volume");
            }
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
            {
                throw new ArgumentException("parameter " + name + ": value must be a finite number >= 0");
            }
            return new BakeParameter(name, kind, value);
        }
    }
}
```

`BakeMeshRequest`: add `public IList<BakeParameter> Parameters { get; private set; }`, a constructor argument, and `IList<BakeParameter> parameters = null` as last optional argument of the full `Parse`; store `new List<BakeParameter>(parameters ?? new BakeParameter[0])` (add `using System.Collections.Generic;`).

`MessageRouter.HandleBakeMesh`: after `smoothMesh`, `List<BakeParameter> parameters = OptionalBakeParameters(root);` and pass it to `Parse`. New helper:

```csharp
        // bake_mesh's params: absent = none. Present it must be an array of
        // {name, kind, value}; each one is validated by BakeParameter.
        private static List<BakeParameter> OptionalBakeParameters(JsonElement root)
        {
            List<BakeParameter> result = new List<BakeParameter>();
            JsonElement field;
            if (!root.TryGetProperty("params", out field)) { return result; }
            if (field.ValueKind != JsonValueKind.Array)
            {
                throw new ContentException("field params is not an array");
            }
            foreach (JsonElement item in field.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new ContentException("field params: every entry must be an object");
                }
                string name = RequireString(item, "name");
                string kind = RequireString(item, "kind");
                JsonElement value;
                double number;
                if (!item.TryGetProperty("value", out value) || value.ValueKind != JsonValueKind.Number
                    || !value.TryGetDouble(out number))
                {
                    throw new ContentException("field params: " + name + " has no numeric value");
                }
                result.Add(BakeParameter.Create(name, kind, number));
            }
            return result;
        }
```

Add `using Bilocus.Revit.Bake;` only if not already present.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Bilocus.sln -c Release`
Expected: all pass on net8 and net48. Note `1e400` parses to infinity in `TryGetDouble` or fails: both must end in an error frame; adjust nothing if the test passes.

- [ ] **Step 5: Commit**

```bash
git add src/Bilocus.Revit/Bake/BakeParameter.cs src/Bilocus.Revit/Bake/BakeMeshRequest.cs src/Bilocus.Revit/Net/MessageRouter.cs tests/Bilocus.Revit.Net.Tests
git commit -m "Parse and validate the BL_ params of bake_mesh"
```

### Task 4: Write the parameters in the family and on the project type (Revit API)

**Files:**
- Create: `src/Bilocus.Revit/Bake/FamilyParameters.cs`
- Modify: `src/Bilocus.Revit/Bake/FamilyBaker.cs` (`CreateFamily` and `UpdateFamily`)

**Interfaces:**
- Consumes: `BakeMeshRequest.Parameters`, `BakeParameter`.
- Produces: `FamilyParameters.Apply(Document familyDoc, IList<BakeParameter> parameters, string typeName)` (inside an open family transaction); `FamilyParameters.WriteToType(FamilySymbol symbol, IList<BakeParameter> parameters)` (inside an open project transaction).

- [ ] **Step 1: Implement** `FamilyParameters.cs`:

```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Bilocus.Revit.Bake
{
    // Writes the BL_ parameters of a bake: type parameters of the family,
    // group Dimensions, informative values. Parameters without the BL_
    // prefix are never touched; BL_ parameters that did not arrive are
    // deleted, because stale data is worse than missing data.
    public static class FamilyParameters
    {
        public static void Apply(Document familyDoc, IList<BakeParameter> parameters, string typeName)
        {
            FamilyManager manager = familyDoc.FamilyManager;
            if (manager.CurrentType == null) { manager.NewType(typeName); }

            Dictionary<string, BakeParameter> wanted = new Dictionary<string, BakeParameter>();
            foreach (BakeParameter p in parameters) { wanted[p.Name] = p; }

            List<FamilyParameter> existing = new List<FamilyParameter>();
            foreach (FamilyParameter fp in manager.Parameters) { existing.Add(fp); }

            foreach (FamilyParameter fp in existing)
            {
                string name = fp.Definition.Name;
                if (!name.StartsWith(BakeParameter.Prefix)) { continue; }
                BakeParameter p;
                if (!wanted.TryGetValue(name, out p) || fp.Definition.GetDataType() != SpecOf(p.Kind) || fp.IsInstance)
                {
                    manager.RemoveParameter(fp);
                }
            }

            foreach (BakeParameter p in parameters)
            {
                FamilyParameter fp = manager.get_Parameter(p.Name);
                if (fp == null)
                {
                    fp = manager.AddParameter(p.Name, GroupTypeId.Geometry, SpecOf(p.Kind), false);
                }
                manager.Set(fp, ToInternal(p));
            }
        }

        public static void WriteToType(FamilySymbol symbol, IList<BakeParameter> parameters)
        {
            if (symbol == null) { return; }
            foreach (BakeParameter p in parameters)
            {
                Parameter parameter = symbol.LookupParameter(p.Name);
                if (parameter != null && !parameter.IsReadOnly) { parameter.Set(ToInternal(p)); }
            }
        }

        private static ForgeTypeId SpecOf(string kind)
        {
            if (kind == "area") { return SpecTypeId.Area; }
            if (kind == "volume") { return SpecTypeId.Volume; }
            return SpecTypeId.Length;
        }

        private static double ToInternal(BakeParameter p)
        {
            ForgeTypeId unit = p.Kind == "area" ? UnitTypeId.SquareMeters
                : p.Kind == "volume" ? UnitTypeId.CubicMeters : UnitTypeId.Meters;
            return UnitUtils.ConvertToInternalUnits(p.Value, unit);
        }
    }
}
```

- [ ] **Step 2: Hook into FamilyBaker**
  - `CreateFamily`, inside the family transaction lambda, after the marking loop and before `return null;`: `FamilyParameters.Apply(familyDoc, host.Parameters, familyName);`
  - `UpdateFamily`, inside the family transaction lambda, after `removedMembers = RemoveOtherMembers(...)`: `FamilyParameters.Apply(familyDoc, host.Parameters, familyName);`
  - `UpdateFamily`, in the project transaction that follows the load (the one that computes `switched` / `notMoved` on `target`): `FamilyParameters.WriteToType(FirstSymbol(target), host.Parameters);` as its first statement.
  - Read the exact surrounding code before editing and keep the existing comment style: one short comment at each call stating why (re-bake loads with `overwriteParameterValues = false`, hence the write on the type).

- [ ] **Step 3: Verify build** for all Revit versions:

Run: `for v in 2024 2025 2026 2027; do dotnet build src/Bilocus.Revit/Bilocus.Revit.csproj -c Release -p:RevitVersion=$v; done` and `dotnet test Bilocus.sln -c Release`
Expected: 0 errors, 0 warnings, tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Bilocus.Revit/Bake/FamilyParameters.cs src/Bilocus.Revit/Bake/FamilyBaker.cs
git commit -m "Write the BL_ parameters in the baked family and on its project type"
```

### Task 5: Docs, deploy and live verification

**Files:**
- Modify: `DESIGN.md` (`bake_mesh` row: add `params?`; paragraph on params), `README.md` (Bake bullet: informative BL_ parameters, family only), `CHANGELOG.md` (Unreleased entry)

- [ ] **Step 1: Docs.** DESIGN: `bake_mesh` row gets `params?`; a paragraph after the bake_mesh description stating the shape `[{name, kind, value}]`, SI units, family only, BL_ prefix, stale BL_ deleted, written on the project type after the load. README Bake bullet: one sentence "A family also carries `BL_` type parameters (length, width, height, area, volume, optional thickness): informative values from the object's extent, overridable in the panel, not linked to the geometry." CHANGELOG `## Unreleased` with a "New: **family dimension parameters.**" entry.
- [ ] **Step 2: Deploy** Revit 2024-2027 and Blender 4.2/5.1/5.2 with the tools scripts (Revit and Blender closed), combining the open branches as done for the previous features.
- [ ] **Step 3: Live via MCP** (Revit 2025 + Blender 5.2):
  1. Blender: box 2.4 x 0.6 x 0.9 m in ToRevit, Bake Family. Revit: the type has BL_Length 2.4, BL_Width 0.6, BL_Height 0.9, BL_Area 8.28 (2*(2.4*0.6+2.4*0.9+0.6*0.9)), BL_Volume 1.296; no BL_Thickness.
  2. Edit Mode, select one 0.6 edge, set Thickness; re-bake; the type has BL_Thickness 0.6.
  3. Clear Thickness, re-bake; BL_Thickness is gone from the family and the type.
  4. Delete one face (open mesh) with Accept open on, re-bake; BL_Volume is gone.
  5. Bake together with a second object active-first: values are the active object's.
- [ ] **Step 4: Commit docs**, push, open the PR.
