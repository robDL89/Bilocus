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
    check(len(a.data.vertices) == 4 and len(a.data.polygons) == 4,
          "12 non-indexed vertices welded into a closed tetrahedron")
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
