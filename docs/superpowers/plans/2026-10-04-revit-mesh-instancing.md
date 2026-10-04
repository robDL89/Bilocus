# Revit -> Blender Mesh Instancing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Family instances with identical symbol geometry, pulled from Revit, share one mesh datablock in Blender; user work on a shared mesh survives re-pulls unless the mesh is untouched.

**Architecture:** Revit tessellates the symbol geometry of single-`GeometryInstance` elements, keys it by a quantized content hash, and sends each mesh once per batch (`revit_mesh`) followed by payload-less `revit_instance` messages carrying a full row-major matrix. Blender keeps received meshes in the batch state, resolves the mesh for a key from the objects already in the scene (majority wins), and decides replace-or-keep with a pure decision function (rule C: touched meshes are kept).

**Tech Stack:** C# (net48 + net8.0, xUnit, System.Text.Json), Revit API, Python 3.13 (pytest, no bpy in pure modules), Blender 5.1/5.2 `bpy` + `mathutils`.

**Spec:** `docs/superpowers/specs/2026-10-04-revit-mesh-instancing-design.md`

## Global Constraints

- Units on the wire: meters. Axes Z-up. Matrices: 16 floats, row-major, translation in `[3]`, `[7]`, `[11]` (DESIGN.md 5.2).
- `ProtocolVersion` / `PROTOCOL_VERSION` = 2, `frames.json` `constants.protocol_version` = 2.
- Python files: ASCII only, no f-strings (`"{}".format(...)`), as the rest of the addon.
- Pure modules (`bridge_receive.py`, `bridge_mesh.py`, `bridge_protocol.py`) must NOT import `bpy`.
- C# files linked into `Bilocus.Revit.Net.Tests` must NOT reference the Revit API.
- Comment density and voice: match the surrounding file (long "why" comments are the house style).
- Quantum for the mesh key: `1e-5` m (0.01 mm). Key: first 16 bytes of SHA-256, 32 lowercase hex chars.
- Object property `revit_mesh_key`; mesh properties `bilocus_mesh_key` (shared meshes only) and `bilocus_mesh_sig` (every mesh the bridge builds).
- Commits that touch code end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; text-only commits have no trailer.

## Commands

- C# tests: `dotnet test Bilocus.sln -c Release`
- Revit build: `dotnet build src/Bilocus.Revit/Bilocus.Revit.csproj -c Release -p:RevitVersion=2024` and `-p:RevitVersion=2025`
- Python tests: `python -m pytest -q` from `tests/python`
- Blender smoke: `"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background --factory-startup --python-exit-code 1 --python tests/blender/smoke_pull.py`

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `src/Bilocus.Protocol/BridgeConstants.cs` | modify | protocol version 2 |
| `src/blender_addon/bridge_protocol.py` | modify | protocol version 2 |
| `tests/vectors/frames.json` | modify | shared constant 2 |
| `src/Bilocus.Revit/Pull/InstanceGeometry.cs` | create | pure: mesh key, Revit transform -> row-major matrix, `InstancedMesh` container |
| `src/Bilocus.Revit/Net/MessageRouter.cs` | modify | `BuildMeshHeader`, `BuildInstanceHeader` |
| `src/Bilocus.Revit/Pull/ElementTessellator.cs` | modify | `TessellateInstance` (Revit API shell) |
| `src/Bilocus.Revit/Pull/SendSelectionCommand.cs` | modify | dedupe meshes, send `revit_mesh` once + `revit_instance` |
| `src/Bilocus.Revit/Pull/SendSelectionResult.cs` | modify | instance / shared mesh counts in the summary |
| `src/blender_addon/bridge_receive.py` | modify | pure: header readers, matrix, signature, majority, decisions, batch mesh store |
| `src/blender_addon/bridge_import.py` | modify | bpy: shared mesh resolution, touched check, instance import, flat path transitions |
| `src/blender_addon/__init__.py` | modify | dispatch `revit_mesh`, `revit_instance` |
| `tests/blender/smoke_pull.py` | create | pull smoke test in real Blender |
| `.github/workflows/ci.yml` | modify | run `smoke_pull.py` |
| `DESIGN.md`, `CHANGELOG.md` | modify | contract and release notes |

---

### Task 1: Protocol version 2

**Files:**
- Modify: `src/Bilocus.Protocol/BridgeConstants.cs:8`
- Modify: `src/blender_addon/bridge_protocol.py:12`
- Modify: `tests/vectors/frames.json` (`constants.protocol_version`)
- Modify: `tests/Bilocus.Revit.Net.Tests/MessageRouterTests.cs:89,113`, `tests/Bilocus.Revit.Net.Tests/BridgeServerTests.cs:21,58`, `tests/python/test_bridge_client.py:113`

**Interfaces:**
- Produces: `BridgeConstants.ProtocolVersion == 2`, `bridge_protocol.PROTOCOL_VERSION == 2`.

- [ ] **Step 1: Change the shared constant first (the "failing test")**

In `tests/vectors/frames.json` set `"protocol_version": 2`.

- [ ] **Step 2: Run both suites, verify the golden-vector constant tests fail**

Run: `dotnet test Bilocus.sln -c Release` and `python -m pytest -q` (in `tests/python`)
Expected: FAIL in `GoldenVectorTests` (constants) and `test_constants_match_shared_contract`.

- [ ] **Step 3: Bump both sides**

`src/Bilocus.Protocol/BridgeConstants.cs`:
```csharp
        public const int ProtocolVersion = 2;
```
`src/blender_addon/bridge_protocol.py`:
```python
PROTOCOL_VERSION = 2
```
In the test literals that send a hello/hello_ack meant to be ACCEPTED, replace `protocol_version\": 1` / `\"protocol_version\":1` with `2`: `MessageRouterTests.cs` lines 89 and 113, `BridgeServerTests.cs` lines 21 and 58, and in `test_bridge_client.py` line 113 use `"protocol_version": protocol.PROTOCOL_VERSION`. Leave `MessageRouterTests.cs:177` (`"1"` as a string is the non-integer case) untouched.

- [ ] **Step 4: Run both suites**

Run: `dotnet test Bilocus.sln -c Release` and `python -m pytest -q`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Bilocus.Protocol/BridgeConstants.cs src/blender_addon/bridge_protocol.py tests/vectors/frames.json tests/Bilocus.Revit.Net.Tests/MessageRouterTests.cs tests/Bilocus.Revit.Net.Tests/BridgeServerTests.cs tests/python/test_bridge_client.py
git commit -m "Protocol version 2 for mesh instancing" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Pure instance geometry (C#)

**Files:**
- Create: `src/Bilocus.Revit/Pull/InstanceGeometry.cs`
- Modify: `tests/Bilocus.Revit.Net.Tests/Bilocus.Revit.Net.Tests.csproj` (add a linked `Compile`)
- Test: `tests/Bilocus.Revit.Net.Tests/InstanceGeometryTests.cs`

**Interfaces:**
- Consumes: `TessellatedMesh` (existing), `BridgeConstants.MetersPerFoot`.
- Produces:
  - `string InstanceGeometry.ComputeMeshKey(float[] positions)` - 32 lowercase hex chars.
  - `float[] InstanceGeometry.RowMajorFromTransform(double[] basisX, double[] basisY, double[] basisZ, double[] originFeet)` - 16 floats, translation in meters.
  - `sealed class InstancedMesh { TessellatedMesh Mesh; string MeshKey; float[] Matrix; }` with constructor `InstancedMesh(TessellatedMesh mesh, string meshKey, float[] matrix)`.

- [ ] **Step 1: Write the failing tests**

`tests/Bilocus.Revit.Net.Tests/InstanceGeometryTests.cs`:
```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Bilocus.Revit.Pull;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The pure half of instancing: the key that decides which instances
    // share a mesh in Blender, and the conversion of Revit's Transform into
    // the row-major matrix of the wire. A wrong key silently merges two
    // different families, a wrong matrix puts every window in the wrong
    // place: both are decided here, under test, and not in
    // ElementTessellator.
    public class InstanceGeometryTests
    {
        private static readonly float[] Triangle = new float[]
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 1f, 0f
        };

        [Fact]
        public void ComputeMeshKey_SameGeometry_SameKey()
        {
            float[] copy = (float[])Triangle.Clone();
            Assert.Equal(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(copy));
        }

        [Fact]
        public void ComputeMeshKey_Is32LowercaseHexChars()
        {
            string key = InstanceGeometry.ComputeMeshKey(Triangle);
            Assert.Equal(32, key.Length);
            foreach (char c in key)
            {
                Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), "not lowercase hex: " + key);
            }
        }

        // Float noise far below the quantum must not split identical
        // geometry into two meshes.
        [Fact]
        public void ComputeMeshKey_NoiseBelowQuantum_SameKey()
        {
            float[] noisy = (float[])Triangle.Clone();
            noisy[3] = 1.0000001f;
            Assert.Equal(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(noisy));
        }

        [Fact]
        public void ComputeMeshKey_DifferentGeometry_DifferentKey()
        {
            float[] other = (float[])Triangle.Clone();
            other[3] = 1.001f;
            Assert.NotEqual(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(other));
        }

        // Same vertices in another order are another tessellation: the key
        // is not a set hash, and does not need to be, because instances of
        // the same symbol come from the same GeometryElement.
        [Fact]
        public void ComputeMeshKey_DifferentVertexCount_DifferentKey()
        {
            float[] longer = new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 1f, 0f, 0f, 0f, 1f, 0f };
            Assert.NotEqual(InstanceGeometry.ComputeMeshKey(Triangle), InstanceGeometry.ComputeMeshKey(longer));
        }

        [Fact]
        public void ComputeMeshKey_NonFinite_Throws()
        {
            float[] bad = (float[])Triangle.Clone();
            bad[4] = float.NaN;
            Assert.Throws<ArgumentException>(() => InstanceGeometry.ComputeMeshKey(bad));
        }

        [Fact]
        public void ComputeMeshKey_LengthNotMultipleOf3_Throws()
        {
            Assert.Throws<ArgumentException>(() => InstanceGeometry.ComputeMeshKey(new float[] { 1f, 2f }));
        }

        [Fact]
        public void RowMajorFromTransform_Identity_TranslationInMeters()
        {
            float[] m = InstanceGeometry.RowMajorFromTransform(
                new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { 10, 20, 30 });

            Assert.Equal(16, m.Length);
            Assert.Equal(1f, m[0]);
            Assert.Equal(1f, m[5]);
            Assert.Equal(1f, m[10]);
            Assert.Equal(1f, m[15]);
            Assert.Equal(3.048f, m[3], 5);
            Assert.Equal(6.096f, m[7], 5);
            Assert.Equal(9.144f, m[11], 5);
        }

        // Revit's basis vectors are the COLUMNS of the rotation: BasisX is
        // where the family's local X ends up. Row-major on the wire means
        // BasisX goes to m[0], m[4], m[8].
        [Fact]
        public void RowMajorFromTransform_RotatedBasis_GoesInColumns()
        {
            // 90 degrees around Z: local X -> world Y, local Y -> world -X
            float[] m = InstanceGeometry.RowMajorFromTransform(
                new double[] { 0, 1, 0 }, new double[] { -1, 0, 0 }, new double[] { 0, 0, 1 },
                new double[] { 0, 0, 0 });

            double[] world = RowMajorMatrix.TransformPoints(m, new float[] { 1f, 0f, 0f });
            Assert.Equal(0.0, world[0], 6);
            Assert.Equal(1.0, world[1], 6);
            Assert.Equal(0.0, world[2], 6);
        }

        [Fact]
        public void RowMajorFromTransform_Mirrored_HasNegativeDeterminant()
        {
            float[] m = InstanceGeometry.RowMajorFromTransform(
                new double[] { -1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { 0, 0, 0 });

            Assert.True(RowMajorMatrix.Determinant3x3(m) < 0);
        }

        [Fact]
        public void RowMajorFromTransform_WrongLength_Throws()
        {
            Assert.Throws<ArgumentException>(() => InstanceGeometry.RowMajorFromTransform(
                new double[] { 1, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { 0, 0, 0 }));
        }

        [Fact]
        public void RowMajorFromTransform_NonFinite_Throws()
        {
            Assert.Throws<ArgumentException>(() => InstanceGeometry.RowMajorFromTransform(
                new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 },
                new double[] { double.NaN, 0, 0 }));
        }

        [Fact]
        public void InstancedMesh_NullArguments_Throw()
        {
            TessellatedMesh mesh = new TessellatedMesh(
                (float[])Triangle.Clone(), new float[9], new int[] { 0, 1, 2 }, new float[3]);
            float[] matrix = new float[16];

            Assert.Throws<ArgumentNullException>(() => new InstancedMesh(null, "k", matrix));
            Assert.Throws<ArgumentNullException>(() => new InstancedMesh(mesh, null, matrix));
            Assert.Throws<ArgumentNullException>(() => new InstancedMesh(mesh, "k", null));
        }
    }
}
```

Add to `Bilocus.Revit.Net.Tests.csproj`, right after the `TessellatedMesh.cs` link:
```xml
    <!-- Instancing: the mesh key and the Transform -> matrix conversion.
         Pure on purpose, for the same reason as TessellatedMesh: a wrong
         key merges different families, and only a test sees it before a
         render does. ElementTessellator.TessellateInstance stays out. -->
    <Compile Include="..\..\src\Bilocus.Revit\Pull\InstanceGeometry.cs" Link="Pull\InstanceGeometry.cs" />
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Bilocus.sln -c Release --filter FullyQualifiedName~InstanceGeometryTests`
Expected: build FAIL, `InstanceGeometry` / `InstancedMesh` not found.

- [ ] **Step 3: Write the implementation**

`src/Bilocus.Revit/Pull/InstanceGeometry.cs`:
```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Security.Cryptography;
using System.Text;
using Bilocus.Protocol;

namespace Bilocus.Revit.Pull
{
    // The pure half of instancing (spec 2026-10-04, section 2.2): which
    // instances share a mesh in Blender, and where each one goes.
    //
    // The key is a hash of the CONTENT of the symbol tessellation, not the
    // FamilySymbol id. Two instances of the same type can have different
    // geometry (instance parameters that drive the shape), and the id would
    // merge them silently; the content cannot. Positions are quantized to
    // KeyQuantum before hashing so that float noise does not split
    // identical geometry into two meshes.
    //
    // This file must NOT reference the Revit API: it is compiled into the
    // test project, where RevitAPI does not exist. ElementTessellator reads
    // the Transform's vectors into double arrays and hands them over.
    public static class InstanceGeometry
    {
        // 0.01 mm in meters: far below any modeling tolerance, far above
        // the float32 noise of a tessellation a few meters wide.
        public const double KeyQuantum = 1e-5;

        // 16 bytes of SHA-256 as hex: 128 bits are plenty against accidental
        // collisions on a selection, and the string stays short in the
        // custom properties Blender shows.
        private const int KeyBytes = 16;

        public static string ComputeMeshKey(float[] positions)
        {
            if (positions == null) throw new ArgumentNullException("positions");
            if (positions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "positions must have a length that is a multiple of 3, has " + positions.Length);
            }

            byte[] bytes = new byte[positions.Length * 8];
            for (int i = 0; i < positions.Length; i++)
            {
                float value = positions[i];
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new ArgumentException("non-finite position at index " + i);
                }

                long quantized = (long)Math.Round(value / KeyQuantum, MidpointRounding.AwayFromZero);

                // Little-endian written by hand: BitConverter would follow
                // the machine, and the key must not depend on it.
                for (int b = 0; b < 8; b++)
                {
                    bytes[i * 8 + b] = (byte)(quantized >> (8 * b));
                }
            }

            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(bytes);
            }

            StringBuilder text = new StringBuilder(KeyBytes * 2);
            for (int i = 0; i < KeyBytes; i++) { text.Append(digest[i].ToString("x2")); }
            return text.ToString();
        }

        // Revit's Transform as the wire's 16 row-major floats.
        //
        // The basis vectors are the COLUMNS of the rotation (BasisX is where
        // the family's local X ends up), so BasisX fills m[0], m[4], m[8].
        // They are unitless and stay as they are; only the origin goes from
        // feet to meters, because the payload's vertices are already in
        // meters. A mirrored instance has a basis with negative determinant
        // and arrives in Blender as a negative scale: nothing to do here.
        public static float[] RowMajorFromTransform(
            double[] basisX, double[] basisY, double[] basisZ, double[] originFeet)
        {
            CheckVector(basisX, "basisX");
            CheckVector(basisY, "basisY");
            CheckVector(basisZ, "basisZ");
            CheckVector(originFeet, "originFeet");

            double s = BridgeConstants.MetersPerFoot;
            return new float[]
            {
                (float)basisX[0], (float)basisY[0], (float)basisZ[0], (float)(originFeet[0] * s),
                (float)basisX[1], (float)basisY[1], (float)basisZ[1], (float)(originFeet[1] * s),
                (float)basisX[2], (float)basisY[2], (float)basisZ[2], (float)(originFeet[2] * s),
                0f, 0f, 0f, 1f
            };
        }

        private static void CheckVector(double[] vector, string name)
        {
            if (vector == null) throw new ArgumentNullException(name);
            if (vector.Length != 3)
            {
                throw new ArgumentException(name + " must have 3 components, has " + vector.Length);
            }
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(vector[i]) || double.IsInfinity(vector[i]))
                {
                    throw new ArgumentException(name + " has a non-finite component");
                }
            }
        }
    }

    // An instanceable element already tessellated: the symbol mesh in the
    // family's local space, its key, and where this instance puts it.
    public sealed class InstancedMesh
    {
        public TessellatedMesh Mesh { get; private set; }
        public string MeshKey { get; private set; }
        public float[] Matrix { get; private set; }

        public InstancedMesh(TessellatedMesh mesh, string meshKey, float[] matrix)
        {
            if (mesh == null) throw new ArgumentNullException("mesh");
            if (meshKey == null) throw new ArgumentNullException("meshKey");
            if (matrix == null) throw new ArgumentNullException("matrix");
            Mesh = mesh;
            MeshKey = meshKey;
            Matrix = matrix;
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Bilocus.sln -c Release --filter FullyQualifiedName~InstanceGeometryTests`
Expected: PASS (both TFMs).

- [ ] **Step 5: Commit**

```bash
git add src/Bilocus.Revit/Pull/InstanceGeometry.cs tests/Bilocus.Revit.Net.Tests/InstanceGeometryTests.cs tests/Bilocus.Revit.Net.Tests/Bilocus.Revit.Net.Tests.csproj
git commit -m "Pull: mesh key and instance matrix, pure half" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `revit_mesh` and `revit_instance` headers (C#)

**Files:**
- Modify: `src/Bilocus.Revit/Net/MessageRouter.cs` (after `BuildGeometryHeader`, ~line 618)
- Test: `tests/Bilocus.Revit.Net.Tests/SendSelectionMessagesTests.cs`

**Interfaces:**
- Produces:
  - `static string MessageRouter.BuildMeshHeader(string meshKey, int vertCount, int triCount)`
  - `static string MessageRouter.BuildInstanceHeader(long elementId, string name, string category, string typeName, string meshKey, float[] matrix, float[] color)`

- [ ] **Step 1: Write the failing tests** (append inside `SendSelectionMessagesTests`)

```csharp
        private static float[] Identity()
        {
            return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        [Fact]
        public void BuildMeshHeader_HasKeyAndCounts()
        {
            string header = MessageRouter.BuildMeshHeader("00ff", 12, 4);

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("revit_mesh", root.GetProperty("type").GetString());
            Assert.Equal("00ff", root.GetProperty("mesh_key").GetString());
            Assert.Equal(12, root.GetProperty("vert_count").GetInt32());
            Assert.Equal(4, root.GetProperty("tri_count").GetInt32());
        }

        [Fact]
        public void BuildMeshHeader_EmptyKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => MessageRouter.BuildMeshHeader("", 3, 1));
            Assert.Throws<ArgumentNullException>(() => MessageRouter.BuildMeshHeader(null, 3, 1));
        }

        [Fact]
        public void BuildInstanceHeader_HasAllFields()
        {
            float[] matrix = Identity();
            matrix[3] = 1.5f;
            string header = MessageRouter.BuildInstanceHeader(
                42L, "Windows - 120x140 [42]", "Windows", "120x140", "00ff",
                matrix, new float[] { 0.45f, 0.55f, 0.65f, 1f });

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("revit_instance", root.GetProperty("type").GetString());
            Assert.Equal(42L, root.GetProperty("element_id").GetInt64());
            Assert.Equal("Windows - 120x140 [42]", root.GetProperty("name").GetString());
            Assert.Equal("Windows", root.GetProperty("category").GetString());
            Assert.Equal("120x140", root.GetProperty("type_name").GetString());
            Assert.Equal("00ff", root.GetProperty("mesh_key").GetString());

            JsonElement m = root.GetProperty("matrix");
            Assert.Equal(16, m.GetArrayLength());
            Assert.Equal(1.5f, m[3].GetSingle());
            Assert.Equal(1f, m[15].GetSingle());

            Assert.Equal(4, root.GetProperty("color").GetArrayLength());
            Assert.False(root.TryGetProperty("vert_count", out _));
        }

        [Fact]
        public void BuildInstanceHeader_NullCategoryAndTypeName_BecomeEmptyStrings()
        {
            string header = MessageRouter.BuildInstanceHeader(
                1L, "x", null, null, "k", Identity(), new float[] { 0, 0, 0, 1 });

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("", root.GetProperty("category").GetString());
            Assert.Equal("", root.GetProperty("type_name").GetString());
        }

        [Fact]
        public void BuildInstanceHeader_WrongMatrixLength_Throws()
        {
            Assert.Throws<ArgumentException>(() => MessageRouter.BuildInstanceHeader(
                1L, "x", "c", "t", "k", new float[12], new float[] { 0, 0, 0, 1 }));
        }

        [Fact]
        public void BuildInstanceHeader_EmptyKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => MessageRouter.BuildInstanceHeader(
                1L, "x", "c", "t", "", Identity(), new float[] { 0, 0, 0, 1 }));
        }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Bilocus.sln -c Release --filter FullyQualifiedName~SendSelectionMessagesTests`
Expected: build FAIL, `BuildMeshHeader` / `BuildInstanceHeader` not defined.

- [ ] **Step 3: Implement** (in `MessageRouter.cs`, right after `BuildGeometryHeader`)

```csharp
        // revit_mesh: the geometry of a family symbol, in the family's local
        // space, sent ONCE per batch before the first revit_instance that
        // uses it. The payload is the same as revit_geometry's: same writer,
        // same parser on the other side.
        public static string BuildMeshHeader(string meshKey, int vertCount, int triCount)
        {
            CheckMeshKey(meshKey);

            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "revit_mesh");
                    writer.WriteString("mesh_key", meshKey);
                    writer.WriteNumber("vert_count", vertCount);
                    writer.WriteNumber("tri_count", triCount);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        // revit_instance: an element that shows the mesh of mesh_key, placed
        // by a full matrix (rotation and mirroring included) instead of
        // revit_geometry's translation-only origin. No payload: the geometry
        // travelled in revit_mesh. Same rule as BuildGeometryHeader for
        // category and type_name: never a JSON null.
        public static string BuildInstanceHeader(
            long elementId, string name, string category, string typeName,
            string meshKey, float[] matrix, float[] color)
        {
            if (name == null) throw new ArgumentNullException("name");
            CheckMeshKey(meshKey);
            if (matrix == null) throw new ArgumentNullException("matrix");
            if (color == null) throw new ArgumentNullException("color");
            if (matrix.Length != 16)
            {
                throw new ArgumentException("matrix must have 16 components, it has " + matrix.Length);
            }
            if (color.Length != 4)
            {
                throw new ArgumentException("color must have 4 components, it has " + color.Length);
            }

            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "revit_instance");
                    writer.WriteNumber("element_id", elementId);
                    writer.WriteString("name", name);
                    writer.WriteString("category", category == null ? "" : category);
                    writer.WriteString("type_name", typeName == null ? "" : typeName);
                    writer.WriteString("mesh_key", meshKey);
                    writer.WriteStartArray("matrix");
                    for (int i = 0; i < matrix.Length; i++) { writer.WriteNumberValue(matrix[i]); }
                    writer.WriteEndArray();
                    writer.WriteStartArray("color");
                    for (int i = 0; i < color.Length; i++) { writer.WriteNumberValue(color[i]); }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private static void CheckMeshKey(string meshKey)
        {
            if (meshKey == null) throw new ArgumentNullException("meshKey");
            if (meshKey.Length == 0) throw new ArgumentException("meshKey is empty");
        }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Bilocus.sln -c Release --filter FullyQualifiedName~SendSelectionMessagesTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Bilocus.Revit/Net/MessageRouter.cs tests/Bilocus.Revit.Net.Tests/SendSelectionMessagesTests.cs
git commit -m "Pull: revit_mesh and revit_instance headers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Revit side - tessellate instances and send them

**Files:**
- Modify: `src/Bilocus.Revit/Pull/ElementTessellator.cs`
- Modify: `src/Bilocus.Revit/Pull/SendSelectionCommand.cs`
- Modify: `src/Bilocus.Revit/Pull/SendSelectionResult.cs`
- Test: `tests/Bilocus.Revit.Net.Tests/SendSelectionResultTests.cs`

**Interfaces:**
- Consumes: `InstanceGeometry`, `InstancedMesh` (Task 2), `MessageRouter.BuildMeshHeader`, `BuildInstanceHeader` (Task 3).
- Produces: `static InstancedMesh ElementTessellator.TessellateInstance(Element element)` (null = not instanceable); `SendSelectionResult.InstanceCount`, `SendSelectionResult.SharedMeshCount`.

- [ ] **Step 1: Write the failing summary tests** (append in `SendSelectionResultTests`)

```csharp
        [Fact]
        public void BuildSummaryText_WithInstances_ReportsSharedMeshes()
        {
            SendSelectionResult result = new SendSelectionResult();
            result.SentCount = 12;
            result.InstanceCount = 10;
            result.SharedMeshCount = 2;

            Assert.Contains("Instanced: 10 elements on 2 shared meshes", result.BuildSummaryText());
        }

        // No instances, no line: the summary of a selection of walls stays
        // exactly as it was.
        [Fact]
        public void BuildSummaryText_WithoutInstances_HasNoInstancedLine()
        {
            SendSelectionResult result = new SendSelectionResult();
            result.SentCount = 3;

            Assert.DoesNotContain("Instanced", result.BuildSummaryText());
        }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Bilocus.sln -c Release --filter FullyQualifiedName~SendSelectionResultTests`
Expected: build FAIL, `InstanceCount` not defined.

- [ ] **Step 3: Implement `SendSelectionResult`**

Add the fields after `TotalTriangles`:
```csharp
        // Elements sent as revit_instance, and how many distinct meshes they
        // share. The ratio is the measure of what instancing saves: ten
        // windows on one mesh is ten times less geometry on the wire.
        public int InstanceCount;
        public int SharedMeshCount;
```
In `BuildSummaryText`, right after the `FailedCount > 0` block:
```csharp
            if (InstanceCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    "\nInstanced: {0} elements on {1} shared meshes", InstanceCount, SharedMeshCount);
            }
```

- [ ] **Step 4: Run the summary tests**

Run: `dotnet test Bilocus.sln -c Release --filter FullyQualifiedName~SendSelectionResultTests`
Expected: PASS.

- [ ] **Step 5: Implement `ElementTessellator.TessellateInstance`**

Extract the `Options` construction from `Tessellate` into a helper (keep its comments) and call it from `Tessellate`:
```csharp
        private static Options CreateOptions()
        {
            Options options = new Options();

            // No View, deliberately. Options.View and Options.DetailLevel are
            // mutually exclusive by documentation, and a view would give
            // view-specific geometry: the same element would produce
            // different meshes depending on what is open at that moment. A
            // reference needs deterministic model geometry.
            options.DetailLevel = ViewDetailLevel.Fine;
            options.ComputeReferences = false;
            options.IncludeNonVisibleObjects = false;
            return options;
        }
```
`Tessellate` then starts with `GeometryElement geometry = element.get_Geometry(CreateOptions());`.

Add after `Tessellate`:
```csharp
        // The instanceable path (spec 2026-10-04, section 2.1): returns the
        // symbol's mesh in the family's LOCAL space, its key and the
        // instance's matrix, or null when the element must take the flat
        // path of Tessellate.
        //
        // Instanceable means: the top-level geometry is EXACTLY ONE
        // GeometryInstance and nothing else that produces triangles. Revit
        // already returns a cut or joined family instance as plain solids,
        // so those fall out here on their own; empty solids, curves and
        // points next to the instance are harmless and allowed. When in
        // doubt, flat: a wrong share is a silent error, a missed one only
        // costs memory.
        //
        // GetSymbolGeometry and not GetInstanceGeometry: here the point is
        // the geometry BEFORE the instance's transform, the one identical
        // across instances. Nested instances inside it are flattened by
        // Collect with GetInstanceGeometry, which for them means "in the
        // coordinate system of the symbol that owns them".
        public static InstancedMesh TessellateInstance(Element element)
        {
            if (element == null) { return null; }

            GeometryElement geometry = element.get_Geometry(CreateOptions());
            if (geometry == null) { return null; }

            GeometryInstance single = null;
            foreach (GeometryObject obj in geometry)
            {
                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                {
                    if (single != null) { return null; }
                    single = instance;
                    continue;
                }
                if (HasTriangles(obj)) { return null; }
            }
            if (single == null) { return null; }

            List<float> positions = new List<float>();
            List<float> normals = new List<float>();
            Collect(single.GetSymbolGeometry(), positions, normals, 1);
            if (positions.Count == 0) { return null; }

            int[] indices = new int[positions.Count / 3];
            for (int i = 0; i < indices.Length; i++) { indices[i] = i; }

            // Origin unused on this path: the matrix places the object.
            TessellatedMesh mesh = new TessellatedMesh(
                positions.ToArray(), normals.ToArray(), indices, new float[3]);

            Transform transform = single.Transform;
            float[] matrix = InstanceGeometry.RowMajorFromTransform(
                ToArray(transform.BasisX), ToArray(transform.BasisY),
                ToArray(transform.BasisZ), ToArray(transform.Origin));

            return new InstancedMesh(mesh, InstanceGeometry.ComputeMeshKey(mesh.Positions), matrix);
        }

        private static bool HasTriangles(GeometryObject obj)
        {
            Solid solid = obj as Solid;
            if (solid != null) { return solid.Faces != null && solid.Faces.Size > 0; }

            Mesh mesh = obj as Mesh;
            if (mesh != null) { return mesh.NumTriangles > 0; }

            return false;
        }

        private static double[] ToArray(XYZ vector)
        {
            return new double[] { vector.X, vector.Y, vector.Z };
        }
```

- [ ] **Step 6: Implement `SendSelectionCommand`**

Replace `PendingElement` with a version that carries either a flat mesh or a key + matrix:
```csharp
        // An element already tessellated successfully, waiting to be sent.
        // Flat elements carry their own mesh; instances carry only the key
        // of a mesh held once in the batch's dictionary, and their matrix.
        private sealed class PendingElement
        {
            public readonly long ElementIdValue;
            public readonly string Name;
            public readonly string Category;
            public readonly string TypeName;
            public readonly TessellatedMesh Mesh;
            public readonly string MeshKey;
            public readonly float[] Matrix;
            public readonly int TriangleCount;

            private PendingElement(
                long elementIdValue, string name, string category, string typeName,
                TessellatedMesh mesh, string meshKey, float[] matrix, int triangleCount)
            {
                ElementIdValue = elementIdValue;
                Name = name;
                Category = category;
                TypeName = typeName;
                Mesh = mesh;
                MeshKey = meshKey;
                Matrix = matrix;
                TriangleCount = triangleCount;
            }

            public static PendingElement Flat(
                long elementIdValue, string name, string category, string typeName, TessellatedMesh mesh)
            {
                return new PendingElement(
                    elementIdValue, name, category, typeName, mesh, null, null, mesh.TriangleCount);
            }

            public static PendingElement Instance(
                long elementIdValue, string name, string category, string typeName, InstancedMesh instanced)
            {
                return new PendingElement(
                    elementIdValue, name, category, typeName, null,
                    instanced.MeshKey, instanced.Matrix, instanced.Mesh.TriangleCount);
            }
        }
```

In `Execute`, declare next to `ready`:
```csharp
            // One mesh per key: the duplicates produced by tessellating every
            // instance are dropped here, so a selection of a hundred windows
            // keeps one window's geometry in memory, not a hundred.
            Dictionary<string, TessellatedMesh> sharedMeshes = new Dictionary<string, TessellatedMesh>();
```
Replace `mesh = ElementTessellator.Tessellate(element);` inside the try with:
```csharp
                    instanced = ElementTessellator.TessellateInstance(element);
                    mesh = instanced != null ? instanced.Mesh : ElementTessellator.Tessellate(element);
```
(declare `InstancedMesh instanced;` next to `TessellatedMesh mesh;`), and replace `ready.Add(new PendingElement(elementIdValue, name, category, typeName, mesh));` with:
```csharp
                if (instanced != null)
                {
                    if (!sharedMeshes.ContainsKey(instanced.MeshKey))
                    {
                        sharedMeshes.Add(instanced.MeshKey, instanced.Mesh);
                    }
                    ready.Add(PendingElement.Instance(elementIdValue, name, category, typeName, instanced));
                }
                else
                {
                    ready.Add(PendingElement.Flat(elementIdValue, name, category, typeName, mesh));
                }
```
Call `SendBatch(server, ready, sharedMeshes, result);` and replace the body of the send loop in `SendBatch` (new parameter `Dictionary<string, TessellatedMesh> sharedMeshes`):
```csharp
            HashSet<string> sentKeys = new HashSet<string>();
            bool midBatchFailure = false;
            foreach (PendingElement pending in ready)
            {
                bool sent;
                if (pending.MeshKey == null)
                {
                    // Mesh.Origin is the bounding box center of the element in
                    // world coordinates: the payload's vertices are already
                    // local, and this is the field that puts them back in place
                    // in Blender.
                    string geometryHeader = MessageRouter.BuildGeometryHeader(
                        pending.ElementIdValue, pending.Name, pending.Category, pending.TypeName,
                        pending.Mesh.VertexCount, pending.Mesh.TriangleCount,
                        pending.Mesh.Origin, ElementNaming.DefaultColor());
                    byte[] payload = MeshPayloadWriter.Write(
                        pending.Mesh.Positions, pending.Mesh.Normals, pending.Mesh.Indices);
                    sent = server.Send(new Frame(geometryHeader, payload));
                }
                else
                {
                    // The shared mesh goes out once per batch, right before
                    // its first instance. Revit does not know what Blender
                    // already has, and does not guess: Blender decides.
                    sent = true;
                    if (!sentKeys.Contains(pending.MeshKey))
                    {
                        TessellatedMesh shared = sharedMeshes[pending.MeshKey];
                        string meshHeader = MessageRouter.BuildMeshHeader(
                            pending.MeshKey, shared.VertexCount, shared.TriangleCount);
                        byte[] meshPayload = MeshPayloadWriter.Write(
                            shared.Positions, shared.Normals, shared.Indices);
                        sent = server.Send(new Frame(meshHeader, meshPayload));
                        if (sent)
                        {
                            sentKeys.Add(pending.MeshKey);
                            result.SharedMeshCount++;
                        }
                    }
                    if (sent)
                    {
                        string instanceHeader = MessageRouter.BuildInstanceHeader(
                            pending.ElementIdValue, pending.Name, pending.Category, pending.TypeName,
                            pending.MeshKey, pending.Matrix, ElementNaming.DefaultColor());
                        sent = server.Send(new Frame(instanceHeader, null));
                    }
                }

                if (!sent)
                {
                    // (keep the existing comment about not insisting)
                    midBatchFailure = true;
                    result.Aborted = true;
                    result.AbortReason = string.Format(
                        "send interrupted after {0} elements, failed on \"{1}\"",
                        result.SentCount, pending.Name);
                    break;
                }

                result.SentCount++;
                if (pending.MeshKey != null) { result.InstanceCount++; }
                result.TotalTriangles += pending.TriangleCount;
            }
```
The `revit_batch_end` block after the loop stays unchanged. Update the class comment at the top: it now sends `revit_batch_begin / revit_mesh / revit_instance / revit_geometry / revit_batch_end`.

- [ ] **Step 7: Build both Revit versions and run all C# tests**

Run: `dotnet build src/Bilocus.Revit/Bilocus.Revit.csproj -c Release -p:RevitVersion=2024`, then `-p:RevitVersion=2025`, then `dotnet test Bilocus.sln -c Release`
Expected: both builds succeed with no new warnings; all tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Bilocus.Revit/Pull/ElementTessellator.cs src/Bilocus.Revit/Pull/SendSelectionCommand.cs src/Bilocus.Revit/Pull/SendSelectionResult.cs tests/Bilocus.Revit.Net.Tests/SendSelectionResultTests.cs
git commit -m "Pull: send family instances as shared meshes" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Pure Blender-side reading and decisions

**Files:**
- Modify: `src/blender_addon/bridge_receive.py`
- Test: `tests/python/test_bridge_receive.py`

**Interfaces:**
- Produces (all in `bridge_receive`):
  - `read_mesh_header(header) -> {"mesh_key": str, "vert_count": int, "tri_count": int}`
  - `read_instance_header(header) -> {"element_id", "name", "category", "type_name", "mesh_key", "matrix": tuple of 16 floats, "color"}`
  - `determinant3x3(matrix16) -> float`
  - `matrix_rows(matrix16) -> tuple of 4 tuples of 4 floats`
  - `mesh_signature(coord_bytes, face_count) -> str` (hex)
  - `most_used(items) -> item or None`
  - `instance_update(exists, was_instance, touched, reset_enabled) -> (replace_mesh, apply_matrix)`
  - `flat_update_resets_transform(was_instance, reset_enabled) -> bool`

- [ ] **Step 1: Write the failing tests** (append to `tests/python/test_bridge_receive.py`)

```python
# --- instancing: revit_mesh / revit_instance -------------------------------

IDENTITY = (1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0)


def instance_header(**overrides):
    header = {"type": "revit_instance", "element_id": 42, "name": "Windows [42]",
              "category": "Windows", "type_name": "120x140", "mesh_key": "00ff",
              "matrix": list(IDENTITY), "color": [0.5, 0.5, 0.5, 1.0]}
    header.update(overrides)
    return header


def test_reads_mesh_header():
    fields = bridge_receive.read_mesh_header(
        {"type": "revit_mesh", "mesh_key": "00ff", "vert_count": 3, "tri_count": 1})
    assert fields == {"mesh_key": "00ff", "vert_count": 3, "tri_count": 1}


@pytest.mark.parametrize("key", [None, "", "   ", 7])
def test_mesh_header_without_a_usable_key_is_rejected(key):
    header = {"type": "revit_mesh", "vert_count": 3, "tri_count": 1}
    if key is not None:
        header["mesh_key"] = key
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_mesh_header(header)


def test_reads_instance_header():
    fields = bridge_receive.read_instance_header(instance_header())
    assert fields["element_id"] == "42"
    assert fields["mesh_key"] == "00ff"
    assert fields["matrix"] == IDENTITY
    assert fields["type_name"] == "120x140"


def test_instance_without_name_gets_a_findable_one():
    fields = bridge_receive.read_instance_header(instance_header(name=""))
    assert fields["name"] == "Revit 42"


@pytest.mark.parametrize("matrix", [
    None,
    [1.0] * 15,
    [1.0] * 15 + ["x"],
    [1.0] * 15 + [True],
    list(IDENTITY[:15]) + [float("nan")],
    list(IDENTITY[:3]) + [float("inf")] + list(IDENTITY[4:]),
])
def test_malformed_matrix_is_a_content_error(matrix):
    header = instance_header()
    if matrix is None:
        del header["matrix"]
    else:
        header["matrix"] = matrix
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_instance_header(header)


def test_singular_matrix_is_rejected():
    flat = list(IDENTITY)
    flat[10] = 0.0  # scale 0 on Z: the mesh would be squashed flat
    with pytest.raises(BridgeMessageError):
        bridge_receive.read_instance_header(instance_header(matrix=flat))


def test_mirrored_matrix_is_accepted():
    flat = list(IDENTITY)
    flat[0] = -1.0
    fields = bridge_receive.read_instance_header(instance_header(matrix=flat))
    assert bridge_receive.determinant3x3(fields["matrix"]) < 0


def test_matrix_rows_are_row_major():
    flat = list(IDENTITY)
    flat[3], flat[7], flat[11] = 1.0, 2.0, 3.0
    rows = bridge_receive.matrix_rows(tuple(flat))
    assert rows[0] == (1.0, 0.0, 0.0, 1.0)
    assert rows[1][3] == 2.0
    assert rows[2][3] == 3.0
    assert rows[3] == (0.0, 0.0, 0.0, 1.0)


def test_mesh_signature_changes_with_coordinates_and_faces():
    coords = struct.pack("<9f", *KNOWN_POSITIONS)
    moved = struct.pack("<9f", *((0.1,) + KNOWN_POSITIONS[1:]))
    base = bridge_receive.mesh_signature(coords, 1)
    assert base == bridge_receive.mesh_signature(coords, 1)
    assert base != bridge_receive.mesh_signature(moved, 1)
    assert base != bridge_receive.mesh_signature(coords, 2)


def test_most_used_picks_the_majority():
    assert bridge_receive.most_used(["a", "b", "b"]) == "b"


def test_most_used_on_a_tie_picks_the_first_met():
    assert bridge_receive.most_used(["a", "b", "b", "a"]) == "a"


def test_most_used_of_nothing_is_none():
    assert bridge_receive.most_used([]) is None


# The decision table of spec section 4.4: (exists, was_instance, touched,
# reset) -> (replace_mesh, apply_matrix).
@pytest.mark.parametrize("exists, was_instance, touched, reset, expected", [
    (False, False, False, False, (True, True)),    # new object
    (True, True, True, False, (False, False)),     # touched: your work wins
    (True, True, True, True, (False, True)),       # touched, toggle on: only the transform
    (True, True, False, False, (True, False)),     # untouched: follows Revit
    (True, True, False, True, (True, True)),
    (True, False, False, False, (True, True)),     # was flat: local frame changed, always placed
    (True, False, True, False, (True, True)),
])
def test_instance_update_decision_table(exists, was_instance, touched, reset, expected):
    assert bridge_receive.instance_update(exists, was_instance, touched, reset) == expected


@pytest.mark.parametrize("was_instance, reset, expected", [
    (False, False, False),
    (False, True, True),
    (True, False, True),   # instance -> flat: the frame changed, always reset
    (True, True, True),
])
def test_flat_update_reset_rule(was_instance, reset, expected):
    assert bridge_receive.flat_update_resets_transform(was_instance, reset) == expected
```

- [ ] **Step 2: Run to verify failure**

Run: `python -m pytest -q test_bridge_receive.py` (in `tests/python`)
Expected: FAIL, `AttributeError: module 'bridge_receive' has no attribute 'read_mesh_header'` (and the others).

- [ ] **Step 3: Implement** in `bridge_receive.py`

Add `import collections` and `import hashlib` to the imports. After `read_geometry_header` add:
```python
# --- instancing: revit_mesh / revit_instance -------------------------------
#
# Written by MessageRouter.BuildMeshHeader and BuildInstanceHeader on the
# Revit side (spec 2026-10-04). A revit_mesh carries the geometry of a family
# symbol once per batch; every revit_instance that follows points at it by
# mesh_key and is placed by a full matrix instead of origin.

# Below this the 3x3 part squashes the mesh flat on some axis: not a
# placement Revit can produce, so a content error rather than an invisible
# object.
MIN_DETERMINANT = 1e-9


def _read_mesh_key(header):
    value = header.get("mesh_key")
    if not isinstance(value, str) or not value.strip():
        raise BridgeMessageError("mesh_key missing or empty: {}".format(value))
    return value.strip()


def determinant3x3(matrix):
    """Determinant of the rotation-scale part of a row-major 4x4. Negative
    means mirrored, which Revit does produce and Blender shows as a
    negative scale."""
    a, b, c = matrix[0], matrix[1], matrix[2]
    d, e, f = matrix[4], matrix[5], matrix[6]
    g, h, i = matrix[8], matrix[9], matrix[10]
    return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)


def _read_matrix(header):
    """The instance's matrix: 16 floats, row-major, meters.

    Read STRICTLY, like origin and for the same reason: a wrong placement is
    the worst error for reference geometry, and a NaN would make the object
    vanish from the viewport without a word."""
    value = header.get("matrix")
    if not isinstance(value, (list, tuple)) or len(value) != 16:
        raise BridgeMessageError(
            "matrix field missing or not an array of 16 numbers: {}".format(value))
    result = []
    for component in value:
        if isinstance(component, bool) or not isinstance(component, (int, float)):
            raise BridgeMessageError("non-numeric component in matrix: {}".format(value))
        component = float(component)
        if not math.isfinite(component):
            raise BridgeMessageError("non-finite component in matrix: {}".format(value))
        result.append(component)
    if abs(determinant3x3(result)) < MIN_DETERMINANT:
        raise BridgeMessageError("singular matrix: {}".format(value))
    return tuple(result)


def matrix_rows(matrix):
    """Row-major flat 16 -> four rows, the shape mathutils.Matrix takes."""
    return tuple(tuple(matrix[row * 4:row * 4 + 4]) for row in range(4))


def read_mesh_header(header):
    return {
        "mesh_key": _read_mesh_key(header),
        "vert_count": _read_count(header, "vert_count"),
        "tri_count": _read_count(header, "tri_count"),
    }


def read_instance_header(header):
    element_id = element_id_key(header.get("element_id"))
    name = _read_text(header, "name")
    if not name:
        name = "Revit {}".format(element_id)
    return {
        "element_id": element_id,
        "name": name,
        "category": _read_text(header, "category"),
        "type_name": _read_text(header, "type_name"),
        "mesh_key": _read_mesh_key(header),
        "matrix": _read_matrix(header),
        "color": _read_color(header),
    }


def mesh_signature(coord_bytes, face_count):
    """Fingerprint of a mesh's geometry as the pull left it.

    coord_bytes are the vertex coordinates as Blender stores them (float32,
    read with foreach_get), so an untouched mesh gives back the same bytes
    exactly. UVs and materials stay out on purpose: texturing a mesh is not
    touching its geometry."""
    digest = hashlib.sha1()
    digest.update(coord_bytes)
    digest.update(struct.pack("<Q", face_count))
    return digest.hexdigest()


def most_used(items):
    """The most frequent item, the first met on a tie; None if empty.

    Which mesh a key resolves to when the objects sharing it disagree: the
    user replaced some of them, not all, and the majority is the best guess
    of what that type looks like now."""
    if not items:
        return None
    return collections.Counter(items).most_common(1)[0][0]


def instance_update(exists, was_instance, touched, reset_enabled):
    """(replace_mesh, apply_matrix) for a revit_instance. Spec section 4.4.

    A new object, or one that was flat until now, always gets the mesh and
    the matrix: for the flat one the local frame changed (bounding box
    center -> insertion point plus rotation), and keeping its old location
    with the new mesh would shift the geometry without a word. An instance
    already there keeps a TOUCHED mesh (the user's work wins) and keeps its
    position unless the reset toggle is on."""
    if not exists or not was_instance:
        return True, True
    return not touched, bool(reset_enabled)


def flat_update_resets_transform(was_instance, reset_enabled):
    """Whether a revit_geometry update resets the object to origin.

    An object that was an instance had the insertion point as its frame:
    the flat mesh is centered on the bounding box, so the transform goes
    back to origin regardless of the toggle."""
    return bool(was_instance or reset_enabled)
```

- [ ] **Step 4: Run tests**

Run: `python -m pytest -q` (in `tests/python`)
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/blender_addon/bridge_receive.py tests/python/test_bridge_receive.py
git commit -m "Pull: read instancing messages and decide updates, pure half" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Batch state holds shared meshes and counts overwritten edits

**Files:**
- Modify: `src/blender_addon/bridge_receive.py` (`BatchState`)
- Test: `tests/python/test_bridge_batch.py`

**Interfaces:**
- Produces: `BatchState.meshes` (dict `mesh_key -> (positions, indices)`), `BatchState.store_mesh(key, positions, indices, now)`, `BatchState.record(created, now, overwritten=False)`, `BatchState.overwritten`.

- [ ] **Step 1: Write the failing tests** (append to `tests/python/test_bridge_batch.py`)

```python
# --- shared meshes of the batch ----------------------------------------------

def test_stored_meshes_live_until_the_batch_ends():
    state = make_state()
    state.begin(2, 100.0)
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.1)
    assert state.meshes["k"] == ((0.0,) * 9, (0, 1, 2))
    state.end(100.2)
    assert state.meshes == {}


def test_an_interrupted_batch_drops_its_meshes():
    state = make_state()
    state.begin(2, 100.0)
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.1)
    assert state.check_interrupted(False, 100.2) is not None
    assert state.meshes == {}


def test_a_new_begin_drops_the_previous_meshes():
    state = make_state()
    state.begin(2, 100.0)
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.1)
    state.begin(1, 100.2)
    assert state.meshes == {}


def test_a_mesh_without_begin_opens_an_implicit_batch_and_survives():
    state = make_state()
    state.store_mesh("k", (0.0,) * 9, (0, 1, 2), 100.0)
    assert state.open
    state.record(True, 100.1)
    assert "k" in state.meshes


def test_overwritten_edits_are_reported_in_the_summary():
    state = make_state()
    state.begin(2, 100.0)
    state.record(False, 100.1, overwritten=True)
    state.record(False, 100.2)
    state.end(100.3)
    assert state.overwritten == 1
    assert "1 edited mesh overwritten" in state.message


def test_no_overwrite_no_mention():
    state = make_state()
    state.begin(1, 100.0)
    state.record(True, 100.1)
    state.end(100.2)
    assert "overwritten" not in state.message
```

- [ ] **Step 2: Run to verify failure**

Run: `python -m pytest -q test_bridge_batch.py`
Expected: FAIL, `AttributeError: 'BatchState' object has no attribute 'store_mesh'`.

- [ ] **Step 3: Implement**

In `BatchState._clear` add:
```python
        self.overwritten = 0
        # revit_mesh payloads of this batch, by key. They become datablocks
        # only when an instance uses them (bridge_import), so a batch that
        # dies halfway leaves no orphan meshes in the file.
        self.meshes = {}
```
In `end()` and in `check_interrupted()` (in the branch that closes the batch, before setting `self.message`) add `self.meshes = {}`.

Change `record`:
```python
    def record(self, created, now, overwritten=False):
        """An imported element. created tells whether it was created or
        updated; overwritten whether the user's edits on its mesh were lost
        because the element no longer shares geometry (instance -> flat)."""
        self._ensure_open(now)
        self.received += 1
        if created:
            self.created += 1
        else:
            self.updated += 1
        if overwritten:
            self.overwritten += 1
        self.last_activity = now
        self.message = self._progress()
```
Add:
```python
    def store_mesh(self, key, positions, indices, now):
        """A revit_mesh: kept until the end of the batch."""
        self._ensure_open(now)
        self.meshes[key] = (positions, indices)
        self.last_activity = now
```
Change `_summary`:
```python
    def _summary(self):
        text = "{} objects: {} created, {} updated".format(
            self.received, self.created, self.updated)
        if self.overwritten:
            # The only case where a pull discards the user's work: say it
            # where the user looks, not only in the console.
            text = "{}, {} edited mesh{} overwritten".format(
                text, self.overwritten, "" if self.overwritten == 1 else "es")
        return self._with_failures(text)
```

- [ ] **Step 4: Run tests**

Run: `python -m pytest -q`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/blender_addon/bridge_receive.py tests/python/test_bridge_batch.py
git commit -m "Pull: batch keeps shared meshes and counts overwritten edits" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Blender import of instances, flat-path transitions, dispatch, smoke test

**Files:**
- Modify: `src/blender_addon/bridge_import.py`
- Modify: `src/blender_addon/__init__.py:202-205`
- Create: `tests/blender/smoke_pull.py`
- Modify: `.github/workflows/ci.yml` (blender job)

**Interfaces:**
- Consumes: everything from Tasks 5 and 6.
- Produces: `bridge_import.handle_mesh(header, payload, now)`, `bridge_import.handle_instance(header, payload, now, scene=None)`, `bridge_import.import_instance(fields, scene=None) -> bool`, `bridge_import.import_geometry(...) -> (created, overwritten)`, constants `MESH_KEY_PROPERTY`, `MESH_KEY_MARK`, `MESH_SIG_MARK`.

- [ ] **Step 1: Write the failing smoke test**

`tests/blender/smoke_pull.py`:
```python
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Smoke test of the pull with mesh instancing, Blender side, inside real
# Blender.
#
#   "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background
#       --factory-startup --python-exit-code 1 --python tests/blender/smoke_pull.py
#
# No socket: the messages Revit would send are fed straight into the same
# handle_* functions the addon's drain calls. What it tests is spec
# 2026-10-04 section 4: shared datablock, rule C (touched meshes survive),
# the asset reaching a new element, material carry-over, flat <-> instance
# transitions, missing keys, interrupted batches.
#
# ASCII only and no f-strings, like the addon.

import os
import struct
import sys
import traceback

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(os.path.dirname(os.path.dirname(HERE)), "src", "blender_addon")
if SRC not in sys.path:
    sys.path.insert(0, SRC)

try:
    import bridge_import as imp
    import bridge_mesh as mesh_pack
except BaseException:
    traceback.print_exc()
    print("[smoke_pull] FAILED: modules in {} not importable".format(SRC))
    sys.stdout.flush()
    sys.exit(1)

PREFIX = "[smoke_pull]"

# a tetrahedron as non-indexed triangles, like ElementTessellator emits
TETRA = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0)]
TETRA_FACES = [(0, 2, 1), (0, 1, 3), (1, 2, 3), (0, 3, 2)]


def say(text):
    print("{} {}".format(PREFIX, text))


def check(condition, text):
    if not condition:
        raise AssertionError(text)
    say("ok: {}".format(text))


def tetra_payload(scale=1.0):
    positions = []
    for face in TETRA_FACES:
        for index in face:
            positions.extend(component * scale for component in TETRA[index])
    count = len(positions) // 3
    normals = [0.0, 0.0, 1.0] * count
    indices = list(range(count))
    return mesh_pack.pack_mesh_payload(positions, normals, indices), count


def matrix(tx=0.0, ty=0.0, tz=0.0, sx=1.0):
    return [sx, 0.0, 0.0, tx,
            0.0, 1.0, 0.0, ty,
            0.0, 0.0, 1.0, tz,
            0.0, 0.0, 0.0, 1.0]


def send_mesh(key, scale=1.0, now=0.0):
    payload, count = tetra_payload(scale)
    header = {"type": "revit_mesh", "mesh_key": key,
              "vert_count": count, "tri_count": count // 3}
    return imp.handle_mesh(header, payload, now)


def send_instance(element_id, key, m, now=0.0):
    header = {"type": "revit_instance", "element_id": element_id,
              "name": "Win [{}]".format(element_id), "category": "Windows",
              "type_name": "W", "mesh_key": key, "matrix": m,
              "color": [0.5, 0.5, 0.5, 1.0]}
    return imp.handle_instance(header, b"", now)


def send_flat(element_id, origin, now=0.0):
    payload, count = tetra_payload()
    header = {"type": "revit_geometry", "element_id": element_id,
              "name": "Win [{}]".format(element_id), "category": "Windows",
              "type_name": "W", "vert_count": count, "tri_count": count // 3,
              "origin": list(origin), "color": [0.5, 0.5, 0.5, 1.0]}
    return imp.handle_geometry(header, payload, now)


def pulled(element_id):
    return imp.find_object(str(element_id))


def batch(count, now=0.0):
    imp.handle_batch_begin({"type": "revit_batch_begin", "count": count}, now)


def end(now=0.0):
    imp.handle_batch_end(now)


def main():
    imp.register_properties()
    scene = bpy.context.scene

    # 1. three instances of one key share one datablock, mirrored included
    batch(3)
    check(send_mesh("K1") is None, "revit_mesh K1 stored")
    check(len([m for m in bpy.data.meshes if m.get(imp.MESH_KEY_MARK) == "K1"]) == 0,
          "no datablock before an instance uses the mesh")
    for element_id, m in ((1, matrix()), (2, matrix(tx=5.0)), (3, matrix(tx=10.0, sx=-1.0))):
        check(send_instance(element_id, "K1", m) is None, "instance {} imported".format(element_id))
    end()
    a, b, c = pulled(1), pulled(2), pulled(3)
    check(a.data == b.data == c.data and a.data.users == 3, "three objects, one mesh")
    check(abs(b.location.x - 5.0) < 1e-6, "instance placed by its matrix")
    check(c.matrix_world.to_3x3().determinant() < 0, "mirrored instance keeps a negative determinant")

    # 2. rule C: an edited mesh survives a re-pull
    shared = a.data
    shared.vertices[0].co.x += 0.25
    edited_x = shared.vertices[0].co.x
    batch(3)
    send_mesh("K1")
    for element_id in (1, 2, 3):
        send_instance(element_id, "K1", matrix())
    end()
    check(pulled(1).data == shared and abs(shared.vertices[0].co.x - edited_x) < 1e-6,
          "edited shared mesh kept on re-pull")
    check(abs(pulled(2).location.x - 5.0) < 1e-6, "position kept with the reset toggle off")

    # 3. asset linked to all instances (Ctrl+L): a new element gets the asset
    asset = bpy.data.meshes.new("Asset")
    asset.from_pydata(TETRA, [], TETRA_FACES)
    for element_id in (1, 2, 3):
        pulled(element_id).data = asset
    batch(1)
    send_mesh("K1")
    send_instance(4, "K1", matrix(tx=15.0))
    end()
    check(pulled(4).data == asset, "new element of the same key arrives with the asset")

    # 4. untouched mesh follows Revit and carries its materials over
    batch(2)
    send_mesh("K2")
    send_instance(5, "K2", matrix())
    send_instance(6, "K2", matrix())
    end()
    old = pulled(5).data
    old_name = old.name
    material = bpy.data.materials.new("Glass")
    old.materials.append(material)
    batch(2)
    send_mesh("K3", scale=2.0)
    send_instance(5, "K3", matrix())
    send_instance(6, "K3", matrix())
    end()
    check(pulled(5).data == pulled(6).data and pulled(5).data.get(imp.MESH_KEY_MARK) == "K3",
          "untouched mesh replaced by the new key, still shared")
    check(pulled(5).data.materials[0] == material, "materials carried over to the new mesh")
    check(bpy.data.meshes.get(old_name) is None, "old bridge mesh removed once orphaned")

    # 5. flat -> instance: the transform is always applied
    batch(1)
    send_flat(7, (5.0, 0.0, 0.0))
    end()
    pulled(7).location = (100.0, 0.0, 0.0)
    batch(1)
    send_mesh("K1")
    send_instance(7, "K1", matrix(tx=5.0))
    end()
    check(abs(pulled(7).location.x - 5.0) < 1e-6, "flat -> instance placed by the matrix")
    check(pulled(7).get(imp.MESH_KEY_PROPERTY) == "K1", "object marked as instance")

    # 6. instance -> flat: the touched asset is overwritten and reported
    batch(1)
    send_flat(1, (0.0, 0.0, 0.0))
    end()
    check(pulled(1).data != asset and pulled(1).get(imp.MESH_KEY_PROPERTY) is None,
          "instance -> flat: own mesh, no longer an instance")
    check("1 edited mesh overwritten" in imp.LAST_PULL.message,
          "overwrite reported: {}".format(imp.LAST_PULL.message))
    check(asset.users >= 1 and bpy.data.meshes.get("Asset") is not None,
          "the asset itself is never removed by the bridge")

    # 7. a key that arrived nowhere is a counted failure
    batch(1)
    problem = send_instance(9, "K9", matrix())
    end()
    check(problem is not None and imp.LAST_PULL.failed == 1, "missing key counted as failure")

    # 8. an interrupted batch leaves no meshes behind
    batch(1)
    send_mesh("K8")
    imp.check_open_batch(False, 1.0)
    check(not imp.LAST_PULL.meshes, "interrupted batch dropped its meshes")
    check(not any(m.get(imp.MESH_KEY_MARK) == "K8" for m in bpy.data.meshes),
          "no datablock for an unused mesh")

    imp.unregister_properties()


try:
    main()
except BaseException:
    traceback.print_exc()
    print("{} FAILED on Blender {}".format(PREFIX, bpy.app.version_string))
    sys.stdout.flush()
    sys.exit(1)

print("{} ALL GREEN on Blender {}".format(PREFIX, bpy.app.version_string))
sys.stdout.flush()
```

- [ ] **Step 2: Run to verify failure**

Run: `"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background --factory-startup --python-exit-code 1 --python tests/blender/smoke_pull.py`
Expected: FAIL, `AttributeError: module 'bridge_import' has no attribute 'handle_mesh'`.

- [ ] **Step 3: Implement `bridge_import.py`**

Imports at the top:
```python
from array import array

import bpy
from mathutils import Matrix

import bridge_receive as receive
from bridge_protocol import BridgeMessageError
```
Constants, after `PULL_NAME_PROPERTY`:
```python
# Instancing (spec 2026-10-04). On the OBJECT: the key of the Revit geometry
# it shows, set for every object pulled as an instance and removed when the
# element arrives flat again. On the MESH: the key it was built from (shared
# meshes only), and the signature of its geometry at pull time, on EVERY
# mesh the bridge builds - that is also how the bridge tells its own meshes
# from the user's when cleaning up.
MESH_KEY_PROPERTY = "revit_mesh_key"
MESH_KEY_MARK = "bilocus_mesh_key"
MESH_SIG_MARK = "bilocus_mesh_sig"
```
Helpers, after `_as_key`:
```python
def _signature(mesh):
    coords = array('f', [0.0]) * (len(mesh.vertices) * 3)
    mesh.vertices.foreach_get("co", coords)
    return receive.mesh_signature(coords.tobytes(), len(mesh.polygons))


def _build_mesh(name, positions, indices, mesh_key=None):
    """A new mesh datablock from the payload, already marked.

    Grouping and index checking come BEFORE meshes.new: a malformed message
    must surface here, not after leaving an orphan mesh behind."""
    verts = receive.group_into_triples(positions)
    receive.check_indices(indices, len(verts))
    faces = receive.group_into_triples(indices)

    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    mesh.update()
    if mesh_key is not None:
        mesh[MESH_KEY_MARK] = mesh_key
    mesh[MESH_SIG_MARK] = _signature(mesh)
    return mesh


def _in_edit_mode(mesh):
    for obj in bpy.data.objects:
        if obj.data == mesh and obj.mode == 'EDIT':
            return True
    return False


def is_touched(mesh):
    """Rule C: whether the user worked on this mesh's geometry.

    Without the key mark it is not the bridge's shared mesh (an asset linked
    with Ctrl+L, or a flat mesh). In edit mode the data is not flushed yet,
    so the signature cannot be trusted: touched, to be safe. Otherwise the
    geometry is compared with the signature taken at pull time."""
    if mesh is None or mesh.get(MESH_KEY_MARK) is None:
        return True
    if _in_edit_mode(mesh):
        return True
    return _signature(mesh) != mesh.get(MESH_SIG_MARK)


def find_key_mesh(mesh_key):
    """The mesh the objects of this key are showing now, or None.

    No registry: the truth is what is in the scene. When the objects
    disagree (the user replaced some, not all) the majority wins. Linear
    scan, same reasoning as find_object."""
    names = [obj.data.name for obj in bpy.data.objects
             if obj.type == 'MESH' and obj.data is not None
             and obj.get(MESH_KEY_PROPERTY) == mesh_key]
    name = receive.most_used(names)
    return None if name is None else bpy.data.meshes.get(name)


def _remove_if_orphan(mesh):
    # Only meshes the bridge built: an asset left without users is still the
    # user's, and Blender purges it on save if they really do not want it.
    # Flat meshes from releases before the signature stay orphans until the
    # next save, which is harmless.
    if mesh is not None and mesh.users == 0 and mesh.get(MESH_SIG_MARK) is not None:
        bpy.data.meshes.remove(mesh)


def _mesh_for_key(fields):
    """(mesh, fresh) for an instance: the one already in the scene for its
    key, otherwise a new one from this batch's revit_mesh."""
    mesh = find_key_mesh(fields["mesh_key"])
    if mesh is not None:
        return mesh, False
    stored = LAST_PULL.meshes.get(fields["mesh_key"])
    if stored is None:
        raise BridgeMessageError(
            "mesh_key {} arrived neither in this batch nor in the scene".format(
                fields["mesh_key"]))
    positions, indices = stored
    name = fields["type_name"] or fields["name"]
    return _build_mesh(name, positions, indices, fields["mesh_key"]), True


def _write_properties(obj, fields):
    obj[ID_PROPERTY] = fields["element_id"]
    obj[CATEGORY_PROPERTY] = fields["category"]
    obj[TYPE_PROPERTY] = fields["type_name"]
    # obj.name and not fields["name"]: Blender may have cut it (63 bytes) or
    # suffixed it (name taken), and the comparison is with the real name.
    obj[PULL_NAME_PROPERTY] = obj.name
    obj.color = fields["color"]
```
Rewrite `import_geometry` (keep its docstring, add a line about the return value and the instance -> flat transition):
```python
def import_geometry(fields, positions, indices, scene=None):
    """... (existing docstring) ...

    Returns (created, overwritten). overwritten is True when the object was
    an instance whose mesh the user had touched: the element no longer
    shares geometry, so the flat mesh replaces that work (spec 4.4)."""
    element_id = fields["element_id"]

    if scene is None:
        scene = bpy.context.scene

    mesh_data = _build_mesh(fields["name"], positions, indices)
    obj = find_object(element_id)

    created = obj is None
    overwritten = False
    if created:
        obj = bpy.data.objects.new(fields["name"], mesh_data)
        ensure_collection(scene).objects.link(obj)
        # (keep the existing comment about the origin on the element)
        obj.location = fields["origin"]
    else:
        was_instance = obj.get(MESH_KEY_PROPERTY) is not None
        overwritten = was_instance and is_touched(obj.data)

        # (keep the existing IN-PLACE replacement comment)
        old_mesh = obj.data
        obj.data = mesh_data

        if receive.flat_update_resets_transform(was_instance, reset_location_enabled(scene)):
            # (keep the existing "WHOLE transform is reset" comment, and add:)
            # An object that was an instance is always reset: its frame was
            # the insertion point, the flat mesh is centered on the bounding
            # box.
            obj.location = fields["origin"]
            obj.rotation_euler = (0.0, 0.0, 0.0)
            obj.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
            obj.scale = (1.0, 1.0, 1.0)

        # (keep the existing orphan comment)
        _remove_if_orphan(old_mesh)

        if was_instance:
            del obj[MESH_KEY_PROPERTY]

        obj.name = fields["name"]

    _write_properties(obj, fields)
    return created, overwritten
```
Add after `import_geometry`:
```python
def import_instance(fields, scene=None):
    """Creates or updates the object for a revit_instance message. Returns
    True if the object was created. Spec 2026-10-04 section 4.4; the
    decision itself is receive.instance_update, tested without Blender."""
    if scene is None:
        scene = bpy.context.scene

    obj = find_object(fields["element_id"])
    exists = obj is not None
    was_instance = exists and obj.get(MESH_KEY_PROPERTY) is not None
    touched = was_instance and is_touched(obj.data)
    replace_mesh, apply_matrix = receive.instance_update(
        exists, was_instance, touched, reset_location_enabled(scene))

    mesh, fresh = (_mesh_for_key(fields) if replace_mesh else (None, False))

    if not exists:
        obj = bpy.data.objects.new(fields["name"], mesh)
        ensure_collection(scene).objects.link(obj)
    elif replace_mesh and obj.data != mesh:
        old_mesh = obj.data
        # A fresh mesh inherits the material slots of the one it replaces:
        # a texturing done on an untouched mesh survives a type change in
        # Revit. UVs do not: the geometry is new. A mesh reused from the
        # siblings already has its own materials and is left alone.
        if fresh and old_mesh is not None:
            for material in old_mesh.materials:
                mesh.materials.append(material)
        obj.data = mesh
        _remove_if_orphan(old_mesh)

    if apply_matrix:
        obj.matrix_world = Matrix(receive.matrix_rows(fields["matrix"]))

    if exists:
        obj.name = fields["name"]
    _write_properties(obj, fields)
    obj[MESH_KEY_PROPERTY] = fields["mesh_key"]
    return not exists
```
Update `handle_geometry`:
```python
        created, overwritten = import_geometry(fields, positions, indices, scene)
    except Exception as error:
        ... unchanged ...
    LAST_PULL.record(created, now, overwritten)
    return None
```
Add after `handle_geometry`:
```python
def handle_mesh(header, payload, now):
    """A revit_mesh message: the payload is kept in the batch state, not
    turned into a datablock. Returns a string to print, or None.

    A malformed one is not counted as a failed element, because it is not
    an element: the instances pointing at it fail on their own, and THEY
    are counted."""
    try:
        fields = receive.read_mesh_header(header)
        positions, _normals, indices = receive.unpack_mesh_payload(
            payload, fields["vert_count"], fields["tri_count"])
        receive.check_indices(indices, fields["vert_count"])
    except Exception as error:
        return "shared mesh discarded: {}: {}".format(type(error).__name__, error)

    LAST_PULL.store_mesh(fields["mesh_key"], positions, indices, now)
    return None


def handle_instance(header, payload, now, scene=None):
    """A revit_instance message. Same isolation as handle_geometry: an
    element that blows up is counted and skipped, never propagated to the
    timer. The payload is empty by protocol and ignored."""
    try:
        fields = receive.read_instance_header(header)
        created = import_instance(fields, scene)
    except Exception as error:
        LAST_PULL.record_failure(now)
        return "element not imported: {}: {}".format(
            type(error).__name__, error)

    LAST_PULL.record(created, now)
    return None
```

- [ ] **Step 4: Dispatch in `__init__.py`** (after the `revit_geometry` branch)

```python
    elif kind == "revit_mesh":
        problem = imp.handle_mesh(header, payload, now)
        if problem is not None:
            _log(problem)

    elif kind == "revit_instance":
        problem = imp.handle_instance(header, payload, now)
        if problem is not None:
            _log(problem)
```

- [ ] **Step 5: Run the smoke tests and the pure suites**

Run: `"C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background --factory-startup --python-exit-code 1 --python tests/blender/smoke_pull.py`
Expected: `[smoke_pull] ALL GREEN on Blender 5.2.x`.
Run: same command with `tests/blender/smoke_bake.py` -> `ALL GREEN` (the installed-addon check compares with src: if it reports a difference, run `tools/deploy-blender.ps1` first, as for any addon change).
Run: `python -m pytest -q` in `tests/python` -> all PASS.

- [ ] **Step 6: CI step** (in `.github/workflows/ci.yml`, blender job, after the smoke_bake step)

```yaml
      - name: Run tests/blender/smoke_pull.py
        run: |
          ./blender/blender --background --factory-startup --python-exit-code 1 \
            --python tests/blender/smoke_pull.py
```

- [ ] **Step 7: Commit**

```bash
git add src/blender_addon/bridge_import.py src/blender_addon/__init__.py tests/blender/smoke_pull.py .github/workflows/ci.yml
git commit -m "Pull: shared meshes in Blender, touched meshes survive re-pull" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Documentation

**Files:**
- Modify: `DESIGN.md` (5.4 table and text, 1 status line if needed)
- Modify: `CHANGELOG.md`

- [ ] **Step 1: DESIGN.md 5.4**

Add the two rows to the 5.4 table (same text as spec section 3). After the paragraph that starts "**Positions travel in coordinates LOCAL to the element**", add a subsection:

```markdown
#### `revit_mesh` / `revit_instance` (instancing)

A family instance whose top-level geometry is exactly one
`GeometryInstance` is sent as `revit_instance`: the symbol geometry,
tessellated in the family's local space, travels once per batch as
`revit_mesh`, keyed by `mesh_key` (hash of the positions quantized to
0.01 mm), and every instance points at it. Here `origin` does not apply:
the instance carries a full row-major `matrix` (rotation and mirroring
included) and the object's origin is the family insertion point.
Everything else - walls, floors, cut or joined instances - stays
`revit_geometry`.

On the Blender side objects with the same key share one mesh datablock.
A shared mesh the user has touched (edited geometry, replaced via Ctrl+L,
or in edit mode at pull time) is never replaced by a pull; an untouched
one follows Revit and carries its material slots over. The decision table
and the transitions between flat and instanced are in
`docs/superpowers/specs/2026-10-04-revit-mesh-instancing-design.md`
section 4.4.
```
Change the sentence "The `GeometryInstance` objects are flattened on the Revit side regardless: there is no sensible local rotation left to send, so `origin` is a translation and not a full matrix." to: "For `revit_geometry` the `GeometryInstance` objects are flattened on the Revit side: there is no sensible local rotation left to send, so `origin` is a translation and not a full matrix. Instanceable elements take `revit_instance` instead, see below."

- [ ] **Step 2: CHANGELOG.md** - add at the top:

```markdown
## Unreleased

- New: **instanced pull.** Family instances with identical geometry share
  one mesh in Blender: edit, UV or replace one (Ctrl+L Link Object Data
  from an asset) and every copy follows. A new element of the same type
  arrives with what its siblings show. A shared mesh you have edited is
  kept on the next pull; an untouched one follows Revit. Protocol version
  2: update both the Revit add-in and the Blender add-on.
```

- [ ] **Step 3: Commit** (text only: no trailer)

```bash
git add DESIGN.md CHANGELOG.md
git commit -m "Docs: instanced pull in DESIGN 5.4 and changelog"
```

---

### Task 9: Manual verification in Revit (Roberto)

Not automatable: needs a running Revit with the add-in deployed (`tools/deploy-revit.ps1`) and Blender with the addon (`tools/deploy-blender.ps1`).

- [ ] Ten identical windows -> one mesh in Blender (Object Data shows "10" users), summary "Instanced: 10 elements on 1 shared meshes".
- [ ] A mirrored window: correct position and orientation, faces not inside out.
- [ ] A window cut by a void (or joined): arrives flat, its own mesh.
- [ ] A family with a nested family: the nested part is in the right place inside the shared mesh.
- [ ] Width as an instance parameter on two windows of the same type: two different meshes.
- [ ] UV + material on one window, re-pull: kept. Ctrl+L asset, add a window in Revit, re-pull: the new one shows the asset.
- [ ] Walls and floors: behave exactly as before.
- [ ] Compare the Revit summary times with a v0.1.1 pull of the same selection.
