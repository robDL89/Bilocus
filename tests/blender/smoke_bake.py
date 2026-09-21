# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# Smoke test of the DirectShape bake, Blender side, inside real Blender.
#
#   "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" --background
#       --factory-startup --python tests/blender/smoke_bake.py
#
# What it tests, in this order:
#  1. extract_polygons on a mesh with a flat quad, a flat hexagon, a
#     non-planar quad and two triangles, plus an Array modifier: counts of
#     the EVALUATED mesh, triangles per polygon, local positions, rejection
#     threshold; then pack_bake_payload and the header, with the contract
#     length.
#  2. The addon INSTALLED by tools/deploy-blender.ps1: identical to src, it
#     is enabled, registers the two per-object properties and the four
#     operators, and the panel draws with no non-existent icons.
#  3. A full round trip against a fake Revit: a TCP server on a thread reads
#     the frames with bridge_protocol, bake_selected sends bake_begin /
#     bake_mesh / bake_end, the returning bake_result passes through the
#     addon's drain; then bake_remove, a family bake (target, accept_open,
#     Walls category skipped, outcome in the panel), the 50-object family
#     limit and copying category and checkbox onto the selected objects.
#
# The test does NOT start the event loop: in the background the addon's
# timers do not run, so this script drains the client's queue itself by
# calling the same _handle_message used by the drain.
#
# Exits with code 1 at the first failed check. The explicit sys.exit is
# needed: without --python-exit-code an uncaught exception lets Blender
# exit with code 0, and the test would look green.
#
# ASCII only and no f-strings, like the addon: it runs in the same
# interpreter and under the same rules.

import collections
import hashlib
import importlib
import itertools
import math
import os
import queue
import socket
import struct
import sys
import threading
import time
import traceback

import addon_utils
import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC_PARENT = os.path.join(ROOT, "src")
SRC = os.path.join(SRC_PARENT, "blender_addon")
ADDON_NAME = "bilocus"

# The modules are imported from src, as the addon does from its own folder:
# it is the repo's code that is under test. The installed addon is checked
# separately, comparing it byte for byte with src.
if SRC not in sys.path:
    sys.path.insert(0, SRC)

# The imports are also inside a try with sys.exit(1): a module that fails
# to import (syntax error, missing file) would raise OUTSIDE main's try,
# and Blender would exit with 0. Seen happening: a false green precisely in
# the worst case.
try:
    import bridge_bake as bake
    import bridge_bake_send as bake_send
    import bridge_client as client
    import bridge_collection as coll
    import bridge_extract as extract
    import bridge_protocol as protocol
except BaseException:
    traceback.print_exc()
    print("[smoke_bake] FAILED: modules in {} not importable".format(SRC))
    sys.stdout.flush()
    sys.exit(1)

PREFIX = "[smoke_bake]"

# id shared by two objects: simulates a Shift+D, which copies the custom property
DUPLICATE_ID = "0123456789abcdef0123456789abcdef"

CUBE_VERTS = [(-0.5, -0.5, -0.5), (0.5, -0.5, -0.5), (0.5, 0.5, -0.5), (-0.5, 0.5, -0.5),
              (-0.5, -0.5, 0.5), (0.5, -0.5, 0.5), (0.5, 0.5, 0.5), (-0.5, 0.5, 0.5)]
CUBE_FACES = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
TRIANGLE_VERTS = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0)]


def say(text):
    print("{} {}".format(PREFIX, text))


def check(condition, text):
    # a function and not assert: assert statements disappear with -O, and
    # the message of a passing check is as useful in the log as a failing
    # one's
    if not condition:
        raise AssertionError(text)
    say("ok: {}".format(text))


# --- scene -----------------------------------------------------------------------

def new_mesh_object(name, verts, edges, faces, collection, location=(0.0, 0.0, 0.0)):
    data = bpy.data.meshes.new(name)
    data.from_pydata(verts, edges, faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    obj.location = location
    collection.objects.link(obj)
    return obj


def add_array(obj, count):
    modifier = obj.modifiers.new("Array", 'ARRAY')
    modifier.count = count
    modifier.use_merge_vertices = False
    return modifier


def hexagon(center_x, center_y, radius):
    return [(center_x + radius * math.cos(math.pi * k / 3.0),
             center_y + radius * math.sin(math.pi * k / 3.0), 0.0) for k in range(6)]


def build_sample(collection):
    """Flat quad, flat hexagon, non-planar quad, two triangles: 18 vertices,
    5 polygons. With the Array at 2, the evaluated mesh has twice that."""
    verts = [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0), (0.0, 1.0, 0.0)]
    verts += hexagon(3.5, 0.5, 0.5)
    # a vertex raised by 0.3: Revit must triangulate this quad
    verts += [(6.0, 0.0, 0.0), (7.0, 0.0, 0.0), (7.0, 1.0, 0.3), (6.0, 1.0, 0.0)]
    verts += [(9.0, 0.0, 0.0), (10.0, 0.0, 0.0), (9.0, 1.0, 0.0), (10.0, 1.0, 0.0)]
    faces = [(0, 1, 2, 3), (4, 5, 6, 7, 8, 9), (10, 11, 12, 13), (14, 15, 16), (15, 17, 16)]
    obj = new_mesh_object("BakeA_array", verts, [], faces, collection, (100.0, 0.0, 0.0))
    add_array(obj, 2)
    return obj


def build_grid(name, collection):
    """5x4 grid = 20 vertices; with the Array at 3 the evaluated mesh has 60."""
    verts = [(float(x), float(y), 0.0) for y in range(4) for x in range(5)]
    faces = []
    for y in range(3):
        for x in range(4):
            first = y * 5 + x
            faces.append((first, first + 1, first + 6, first + 5))
    obj = new_mesh_object(name, verts, [], faces, collection, (0.0, -10.0, 0.0))
    add_array(obj, 3)
    return obj


def select_only(objects, active=None):
    view_layer = bpy.context.view_layer
    for obj in view_layer.objects:
        obj.select_set(False)
    for obj in objects:
        obj.select_set(True)
    if active is None and objects:
        active = objects[0]
    view_layer.objects.active = active


def polygon_vertex_sets(face_sizes, face_vertices):
    starts = list(itertools.accumulate(face_sizes, initial=0))
    return [set(face_vertices[starts[i]:starts[i + 1]]) for i in range(len(face_sizes))]


# --- 1. extraction ------------------------------------------------------------------

def test_extraction(collection):
    say("--- 1. extraction and packing")
    obj = build_sample(collection)
    bpy.context.view_layer.update()
    depsgraph = bpy.context.evaluated_depsgraph_get()

    check(len(obj.data.vertices) == 18 and len(obj.data.polygons) == 5,
          "original mesh: 18 vertices, 5 polygons")
    check(extract.evaluated_vertex_count(obj, depsgraph) == 36,
          "evaluated_vertex_count counts the evaluated mesh (Array x2: 36 vertices)")

    positions, face_sizes, face_vertices, tri_vertices, tri_faces, vertex_count = \
        extract.extract_polygons(obj, depsgraph)

    check(vertex_count == 36 and len(positions) == 108,
          "extract_polygons reads the evaluated mesh: 36 vertices, 108 coordinates")
    check(len(face_sizes) == 10, "face_count 10 (5 polygons times 2 copies)")
    check(collections.Counter(face_sizes) == collections.Counter({4: 4, 6: 2, 3: 4}),
          "polygons: 4 quads, 2 hexagons, 4 triangles")
    check(len(face_vertices) == 40 and sum(face_sizes) == 40,
          "loop_count 40, equal to the sum of face_sizes")
    check(len(tri_faces) == 20 and len(tri_vertices) == 60, "tri_count 20")

    counts = collections.Counter(tri_faces)
    check([counts[i] for i in range(len(face_sizes))] == [size - 2 for size in face_sizes],
          "every polygon has exactly size-2 triangles")

    sets = polygon_vertex_sets(face_sizes, face_vertices)
    stray = [t for t in range(len(tri_faces))
             if not set(tri_vertices[3 * t:3 * t + 3]) <= sets[tri_faces[t]]]
    check(not stray, "every triangle uses only vertices of its own polygon")

    xs = positions[0::3]
    zs = positions[2::3]
    check(min(xs) >= 0.0 and max(xs) < 50.0,
          "LOCAL positions: the x=100 location is not applied to the vertices")
    check(any(abs(z - 0.3) < 1e-6 for z in zs), "the raised vertex of the non-planar quad is there")

    rejected = extract.extract_polygons(obj, depsgraph, 20)
    check(rejected[0] is None and rejected[4] is None and rejected[5] == 36,
          "reject_over=20: None lists, vertex_count still valid at 36")

    payload = bake.pack_bake_payload(positions, face_sizes, face_vertices, tri_vertices, tri_faces)
    expected = bake.expected_bake_payload_length(36, 10, 40, 20)
    check(expected == 952 and len(payload) == expected,
          "payload of {} bytes, the contract's length".format(len(payload)))

    header = bake.build_bake_mesh_header(
        "smoke", obj.name, bake.DEFAULT_CATEGORY, obj.matrix_world,
        len(positions) // 3, len(face_sizes), len(face_vertices), len(tri_faces))
    check(len(header["matrix"]) == 16 and header["matrix"][3] == 100.0,
          "row-major matrix with the x=100 translation in position 3")

    check(not bake.check_loop_layout([3, 0], [4, 3]),
          "check_loop_layout recognizes a non-contiguous layout")
    check(extract.polygon_ordered_loops([10, 11, 12, 0, 1, 2, 3], [3, 0], [4, 3])
          == [0, 1, 2, 3, 10, 11, 12],
          "polygon_ordered_loops puts the vertices back polygon after polygon")
    return obj


# --- 2. installed addon --------------------------------------------------------------

def _digest(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def deployed_differences(addon_dir):
    source = sorted(name for name in os.listdir(SRC) if name.endswith(".py"))
    deployed = sorted(name for name in os.listdir(addon_dir) if name.endswith(".py"))
    problems = []
    if source != deployed:
        problems.append("different file list: src {} installed {}".format(source, deployed))
    for name in source:
        if name in deployed and _digest(os.path.join(SRC, name)) != _digest(
                os.path.join(addon_dir, name)):
            problems.append(name)
    return problems


def enable_addon():
    """(addon module, installed). With the addon not visible it falls back
    to the src package and says so in the log."""
    say("--- 2. addon")
    found = [module for module in addon_utils.modules() if module.__name__ == ADDON_NAME]

    if not found:
        say("NOTE: {} not visible with --factory-startup on Blender {}: registering "
            "the addon from src/blender_addon instead".format(ADDON_NAME, bpy.app.version_string))
        if SRC_PARENT not in sys.path:
            sys.path.insert(0, SRC_PARENT)
        module = importlib.import_module("blender_addon")
        module.register()
        installed = False
    else:
        addon_dir = os.path.dirname(os.path.abspath(found[0].__file__))
        problems = deployed_differences(addon_dir)
        check(not problems,
              "addon installed in {} identical to src/blender_addon (if this fails: "
              "tools/deploy-blender.ps1) {}".format(addon_dir, problems if problems else ""))
        module = addon_utils.enable(ADDON_NAME, default_set=False)
        check(module is not None, "addon_utils.enable({!r}) succeeded".format(ADDON_NAME))
        installed = True

    check(hasattr(bpy.types.Object, bake_send.CATEGORY_PROPERTY),
          "bpy.types.Object.{} registered".format(bake_send.CATEGORY_PROPERTY))
    check(hasattr(bpy.types.Object, bake_send.ACCEPT_OPEN_PROPERTY),
          "bpy.types.Object.{} registered".format(bake_send.ACCEPT_OPEN_PROPERTY))
    accept = bpy.types.Object.bl_rna.properties[bake_send.ACCEPT_OPEN_PROPERTY]
    check(accept.type == 'BOOLEAN' and accept.default is False
          and accept.name == "Accept non-closed solid",
          "accept_open: BoolProperty off by default, correct UI name")
    items = bpy.types.Object.bl_rna.properties[bake_send.CATEGORY_PROPERTY].enum_items
    check(len(items) == len(bake.BAKE_CATEGORIES) and items[0].identifier == bake.DEFAULT_CATEGORY,
          "{} categories in the menu, OST_GenericModel first".format(len(items)))
    for name in ("BILOCUS_OT_bake_directshape", "BILOCUS_OT_bake_family",
                 "BILOCUS_OT_remove_bake", "BILOCUS_OT_apply_bake_category",
                 "BILOCUS_PT_panel"):
        check(hasattr(bpy.types, name), "{} registered".format(name))
    return module, installed


def disable_addon(module, installed):
    if installed:
        addon_utils.disable(ADDON_NAME, default_set=False)
    else:
        module.unregister()
    check(not hasattr(bpy.types.Object, bake_send.CATEGORY_PROPERTY)
          and not hasattr(bpy.types.Object, bake_send.ACCEPT_OPEN_PROPERTY),
          "disabling the addon removes the per-object properties")
    check(not hasattr(bpy.types, "BILOCUS_OT_bake_directshape")
          and not hasattr(bpy.types, "BILOCUS_OT_bake_family"),
          "disabling the addon removes the bake operators")


# --- panel drawn on a fake layout -----------------------------------------------

class FakeLayout(object):
    """Records what draw() asks the layout for, and rejects what would make
    the whole panel disappear in real Blender: a non-existent icon, an
    unregistered operator, a property that does not exist. In the
    background there is no sidebar to draw, but draw() can still be
    called."""

    def __init__(self, lines, icons, parent=None, depth=0):
        self.lines = lines
        self.icons = icons
        self.parent = parent
        self.depth = depth
        self.enabled = True
        self.alert = False
        self.scale_y = 1.0

    def _active(self):
        node = self
        while node is not None:
            if not node.enabled:
                return False
            node = node.parent
        return True

    def _check_icon(self, icon):
        if icon not in self.icons:
            raise AssertionError("non-existent icon {!r}: in real Blender draw() raises "
                                 "and the whole panel disappears".format(icon))

    def box(self):
        self.lines.append((self.depth, "box", "", True))
        return FakeLayout(self.lines, self.icons, self, self.depth + 1)

    def row(self, **_kwargs):
        return FakeLayout(self.lines, self.icons, self, self.depth)

    def column(self, **_kwargs):
        return FakeLayout(self.lines, self.icons, self, self.depth)

    def label(self, text="", icon='NONE', **_kwargs):
        self._check_icon(icon)
        shown = text if icon == 'NONE' else "{} [{}]".format(text, icon)
        self.lines.append((self.depth, "label", shown, self._active()))

    def operator(self, idname, text=None, icon='NONE', **_kwargs):
        self._check_icon(icon)
        prefix, name = idname.split(".")
        class_name = "{}_OT_{}".format(prefix.upper(), name)
        if not hasattr(bpy.types, class_name):
            raise AssertionError("unregistered operator: {}".format(idname))
        label = text if text is not None else getattr(bpy.types, class_name).bl_label
        shown = "[{}] {}".format(label, idname)
        if icon != 'NONE':
            shown = "{} [{}]".format(shown, icon)
        self.lines.append((self.depth, "operator", shown, self._active()))

    def prop(self, data, prop, text=None, **_kwargs):
        if not hasattr(data, prop):
            raise AssertionError("non-existent property: {}".format(prop))
        shown = "{} = {}".format(text if text is not None else prop, getattr(data, prop))
        self.lines.append((self.depth, "prop", shown, self._active()))


def draw_panel():
    icons = set(item.identifier for item in
                bpy.types.UILayout.bl_rna.functions["label"].parameters["icon"].enum_items)
    lines = []
    holder = type("PanelHolder", (object,), {})()
    holder.layout = FakeLayout(lines, icons)
    sys.modules["panel"].BILOCUS_PT_panel.draw(holder, bpy.context)
    return lines


def box_titles(lines):
    return [lines[i + 1][2] for i in range(len(lines) - 1) if lines[i][1] == "box"]


def bake_section(lines):
    start = None
    for index, line in enumerate(lines):
        if line[1] == "label" and line[2] == "Bake into Revit":
            start = index
            break
    if start is None:
        raise AssertionError("'Bake into Revit' section missing from the panel")
    depth = lines[start][0]
    section = []
    for line in lines[start:]:
        if line[0] < depth:
            break
        section.append(line)
    return section


def format_line(line):
    depth, kind, text, active = line
    note = "" if active else "   (inactive)"
    return "{}{}: {}{}".format("  " * depth, kind, text, note)


def operator_active(lines, idname):
    for line in lines:
        if line[1] == "operator" and idname in line[2]:
            return line[3]
    raise AssertionError("operator {} missing from the panel".format(idname))


# --- 3. fake Revit -----------------------------------------------------------------

def bake_result_header(action, target, **counts):
    """A bake_result as MessageRouter.BuildBakeResult writes it, with the
    Phase B2 fields. target, switched and not_moved are ALWAYS written: the
    Blender side reads them strictly, and a fake Revit that omitted them
    would only make the test pass until somebody reads the outcome."""
    header = {"type": "bake_result", "action": action}
    for key in ("requested", "created", "replaced", "recreated", "removed",
                "failed", "missing", "as_mesh", "faces_planar", "faces_triangulated"):
        header[key] = counts.get(key, 0)
    header["ok"] = True
    header["target"] = target
    header["switched"] = counts.get("switched", 0)
    header["not_moved"] = counts.get("not_moved", 0)
    header["message"] = ""
    # coherence with the contract checked here, on the server thread: a
    # wrong header becomes a fake-Revit failure and stops the round trip
    bake.read_bake_result(header)
    return header


class FakeRevit(object):
    """A fake Revit: accepts a connection, reads the frames with
    bridge_protocol and replies to bake_end and bake_remove with a
    bake_result coherent with what it received."""

    def __init__(self):
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.port = self.listener.getsockname()[1]
        self.frames = []
        self.failure = None
        self.condition = threading.Condition()
        self.announced = []
        self.arrived = {}
        self.open_accepted = 0
        self.target = None
        self.thread = threading.Thread(target=self._serve)
        self.thread.daemon = True
        self.thread.start()

    def _serve(self):
        connection = None
        try:
            connection, _address = self.listener.accept()
            read_exactly = protocol.make_socket_reader(connection)
            while True:
                message = protocol.decode_message(read_exactly)
                if message is None:
                    break
                header, payload = message
                reply = self._reply(header)
                with self.condition:
                    self.frames.append((header, payload))
                    self.condition.notify_all()
                if reply is not None:
                    connection.sendall(protocol.encode_frame(reply))
        except Exception as error:
            # at the end of the test the client closes: on Windows recv can
            # end up as an OSError instead of zero bytes, and it is not a
            # failure
            with self.condition:
                self.failure = "{}: {}".format(type(error).__name__, error)
                self.condition.notify_all()
        finally:
            if connection is not None:
                connection.close()
            self.listener.close()

    def _reply(self, header):
        kind = header.get("type")
        if kind == "bake_begin":
            self.announced = list(header["obj_ids"])
            self.arrived = {}
            self.open_accepted = 0
            # like Revit: absent target = directshape
            self.target = header.get("target", "directshape")
            return None
        if kind == "bake_mesh":
            self.arrived[header["obj_id"]] = header["face_count"]
            # in family mode every object with the checkbox counts as an
            # open shell (as_mesh): the fake one does not know if the mesh
            # is closed, and the test only uses the checkbox on the open
            # object
            if self.target == "family" and header.get("accept_open") is True:
                self.open_accepted += 1
            return None
        if kind == "bake_end":
            requested = len(self.announced)
            return bake_result_header(
                "bake", self.target, requested=requested, created=len(self.arrived),
                missing=requested - len(self.arrived),
                as_mesh=self.open_accepted,
                faces_planar=sum(self.arrived.values()))
        if kind == "bake_remove":
            count = len(header["obj_ids"])
            return bake_result_header(
                "remove", bake.REMOVE_TARGET, requested=count, removed=count)
        return None

    def count(self, kind):
        return len([header for header, _payload in self.frames if header.get("type") == kind])

    def types(self):
        with self.condition:
            return [header.get("type") for header, _payload in self.frames]

    def last(self, kind):
        with self.condition:
            for header, payload in reversed(self.frames):
                if header.get("type") == kind:
                    return header, payload
        return None, None

    def wait_for(self, kind, count=1, timeout=10.0):
        deadline = time.time() + timeout
        with self.condition:
            while self.count(kind) < count and self.failure is None:
                remaining = deadline - time.time()
                if remaining <= 0:
                    break
                self.condition.wait(remaining)
            return self.count(kind) >= count


def wait_incoming(kind, timeout=10.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            header, payload = client.CLIENT.incoming.get(timeout=0.1)
        except queue.Empty:
            continue
        if header.get("type") == kind:
            return header, payload
    return None, None


def split_bake_payload(header, payload):
    """(face_sizes, tri_faces) read from the bytes, the way Revit would
    read them."""
    vert_count = header["vert_count"]
    face_count = header["face_count"]
    loop_count = header["loop_count"]
    tri_count = header["tri_count"]
    offset = 12 * vert_count
    face_sizes = list(struct.unpack_from("<{}I".format(face_count), payload, offset))
    offset += 4 * face_count + 4 * loop_count + 12 * tri_count
    tri_faces = list(struct.unpack_from("<{}I".format(tri_count), payload, offset))
    return face_sizes, tri_faces


def test_end_to_end(scene, collection, sample, addon_module):
    say("--- 3. full round trip against a fake Revit")

    # Bake scene:
    #  A BakeA_array   in ToRevit, selected, Walls, 36 evaluated vertices
    #  B BakeB_cube    in ToRevit, selected, default category
    #  C BakeC_dup     in ToRevit, NOT selected, same id as A (Shift+D)
    #  D BakeD_outside selected but OUTSIDE ToRevit
    #  E BakeE_heavy   in ToRevit, selected, 20 original vertices but 60
    #                  evaluated: over the threshold of 40, must not be
    #                  announced
    #  F BakeF_edges   in ToRevit, selected, edges only: announced, the
    #                  pack fails, bake_end must still go out
    scene.bilocus_max_vertices = 40
    sample.bilocus_bake_category = "OST_Walls"
    sample[coll.ID_PROPERTY] = DUPLICATE_ID
    cube = new_mesh_object("BakeB_cube", CUBE_VERTS, [], CUBE_FACES, collection, (0.0, 5.0, 0.0))
    duplicate = new_mesh_object("BakeC_dup", TRIANGLE_VERTS, [], [(0, 1, 2)], collection)
    duplicate[coll.ID_PROPERTY] = DUPLICATE_ID
    outside = new_mesh_object("BakeD_outside", TRIANGLE_VERTS, [], [(0, 1, 2)], scene.collection)
    heavy = build_grid("BakeE_heavy", collection)
    edges_only = new_mesh_object("BakeF_edges", TRIANGLE_VERTS, [(0, 1), (1, 2)], [], collection)
    bpy.context.view_layer.update()

    depsgraph = bpy.context.evaluated_depsgraph_get()
    check(len(heavy.data.vertices) == 20
          and extract.evaluated_vertex_count(heavy, depsgraph) == 60,
          "BakeE_heavy: 20 original vertices, 60 evaluated")

    select_only([sample, cube, heavy, edges_only], active=sample)
    check([obj.name for obj in bake_send.bake_targets(bpy.context)]
          == ["BakeA_array", "BakeB_cube", "BakeE_heavy", "BakeF_edges"],
          "bake_targets: selected objects inside ToRevit, in name order")

    level, message = bake_send.bake_selected(bpy.context)
    check(level == 'ERROR' and "not connected" in message,
          "without a connection bake_selected refuses: {}".format(message))
    level, message = bake_send.remove_bake_selected(bpy.context)
    check(level == 'ERROR' and "not connected" in message,
          "without a connection remove_bake_selected refuses")

    lines = draw_panel()
    titles = box_titles(lines)
    index = titles.index("Bake into Revit")
    check(titles[index - 1] == "Snappable proxies" and titles[index + 1] == "Last pull from Revit",
          "Bake section between the proxies and the pull: {}".format(titles))
    check(not operator_active(lines, "bilocus.bake_directshape"),
          "panel: Bake inactive without a connection")

    server = FakeRevit()
    client.CLIENT.connect("127.0.0.1", server.port)
    check(client.CLIENT.running, "bridge_client connected to the fake Revit on port {}".format(
        server.port))
    check(server.wait_for("hello"), "hello received from the fake Revit")

    select_only([outside])
    level, message = bake_send.bake_selected(bpy.context)
    check(level == 'ERROR' and coll.COLLECTION_NAME in message,
          "no target inside ToRevit: error, no frame ({})".format(message))

    select_only([sample, cube, heavy, edges_only], active=sample)
    lines = draw_panel()
    section = bake_section(lines)
    say("panel, Bake section (connected, 4 selected in ToRevit):")
    for line in section:
        say("    {}".format(format_line(line)))
    check(operator_active(lines, "bilocus.bake_directshape"),
          "panel: Bake active with connection and a target")
    check(any("in ToRevit: 4" in line[2] for line in section),
          "panel: target count 4")
    check(any(line[1] == "prop" and "OST_Walls" in line[2] for line in section),
          "panel: category of the active object")
    check(operator_active(lines, "bilocus.bake_family"),
          "panel: Family Bake active under the same conditions")
    order = [line[2] for line in section if line[1] in ("prop", "operator")]
    expected_order = ["Category = OST_Walls", "Accept non-closed solid = False",
                      "[Apply to selected] bridge.apply_bake_category",
                      "[Bake DirectShape] bridge.bake_directshape [EXPORT]",
                      "[Bake Family] bridge.bake_family [EXPORT]",
                      "[Remove bake] bridge.remove_bake [TRASH]"]
    check(order == expected_order, "panel: property and button order {}".format(order))

    level, message = bake_send.bake_selected(bpy.context)
    say("bake_selected -> {}: {}".format(level, message))
    check(level == 'WARNING', "bake with one skipped and one failed: WARNING level")
    check("BakeE_heavy (60 vertices)" in message and "BakeF_edges" in message,
          "the message names the skipped and the failed object")
    check(bake_send.LAST_BAKE["message"].startswith(
        "sent 2 objects - waiting for the outcome from Revit"),
          "LAST_BAKE waiting for the outcome")

    check(server.wait_for("bake_end"), "bake_end arrived")
    kinds = server.types()
    check(kinds == ["hello", "bake_begin", "bake_mesh", "bake_mesh", "bake_end"],
          "sequence on the wire {}".format(kinds))

    check(sample[coll.ID_PROPERTY] == DUPLICATE_ID,
          "Shift+D: BakeA_array, first by name, keeps the id")
    check(duplicate[coll.ID_PROPERTY] != DUPLICATE_ID,
          "Shift+D: BakeC_dup, not selected, gets a new id (assign_ids over all of ToRevit)")

    begin, _payload = server.last("bake_begin")
    expected_ids = [DUPLICATE_ID, cube[coll.ID_PROPERTY], edges_only[coll.ID_PROPERTY]]
    check(begin["obj_ids"] == expected_ids,
          "bake_begin announces A, B, F and not E, over the threshold")
    check(begin.get("target") == "directshape",
          "bake_begin of DirectShape Bake writes target directshape")

    with server.condition:
        meshes = [frame for frame in server.frames if frame[0].get("type") == "bake_mesh"]
    first, second = meshes[0][0], meshes[1][0]
    check(first["obj_id"] == DUPLICATE_ID and first["name"] == "BakeA_array"
          and first["category"] == "OST_Walls",
          "bake_mesh A: id, name, Walls category from the object's property")
    check((first["vert_count"], first["face_count"], first["loop_count"], first["tri_count"])
          == (36, 10, 40, 20), "bake_mesh A: counts of the evaluated mesh")
    check(first["matrix"][3] == 100.0, "bake_mesh A: matrix_world with x=100")
    check(second["obj_id"] == cube[coll.ID_PROPERTY] and second["category"] == bake.DEFAULT_CATEGORY,
          "bake_mesh B: default category")
    check((second["vert_count"], second["face_count"], second["loop_count"], second["tri_count"])
          == (8, 6, 24, 12) and second["matrix"][7] == 5.0,
          "bake_mesh B: cube with 8 vertices, 6 quads, 12 triangles, y=5")
    for header, payload in meshes:
        check(header.get("accept_open") is False,
              "{}: accept_open written even in the directshape, False".format(header["name"]))
        check(len(payload) == bake.expected_bake_payload_length(
            header["vert_count"], header["face_count"], header["loop_count"], header["tri_count"]),
              "{}: payload length equal to the header's".format(header["name"]))
        face_sizes, tri_faces = split_bake_payload(header, payload)
        counts = collections.Counter(tri_faces)
        check([counts[i] for i in range(len(face_sizes))] == [size - 2 for size in face_sizes],
              "{}: from the bytes, size-2 triangles per polygon".format(header["name"]))

    header, payload = wait_incoming("bake_result")
    check(header is not None, "bake_result back on the client's queue")
    addon_module._handle_message(header, payload, time.time())
    text = bake_send.LAST_BAKE["message"]
    check(text == "bake: 2 created, 0 updated, 0 recreated - 16 whole faces, "
                  "0 triangulated faces - 1 object never arrived",
          "outcome passed through the addon's drain: {}".format(text))

    # removal: G is a copy outside ToRevit with A's id (a Shift+D that never
    # went through Sync or Bake), D has no id. The shared id must NOT go
    # out: removing A's DirectShape by selecting its copy would delete the
    # wrong element in the project file. A is excluded together with G,
    # because from here it is impossible to know which of the two is the
    # original.
    ghost = new_mesh_object("BakeG_copy", TRIANGLE_VERTS, [], [(0, 1, 2)], scene.collection)
    ghost[coll.ID_PROPERTY] = DUPLICATE_ID
    select_only([ghost], active=ghost)
    objects, ids = bake_send.remove_targets(bpy.context)
    check(objects == [] and ids == [], "remove_targets: the lone copy with a shared id does not go out")
    reason = bake_send.describe_no_remove_targets(bpy.context)
    check("BakeG_copy" in reason and "Shift+D" in reason,
          "describe_no_remove_targets names the copy: {}".format(reason))

    select_only([sample, cube, outside, ghost], active=sample)
    objects, ids = bake_send.remove_targets(bpy.context)
    check([obj.name for obj in objects] == ["BakeB_cube"]
          and ids == [cube[coll.ID_PROPERTY]],
          "remove_targets: only objects with a non-shared id, even outside ToRevit")
    level, message = bake_send.remove_bake_selected(bpy.context)
    check(level == 'INFO', "remove_bake_selected: {}".format(message))
    check(server.wait_for("bake_remove"), "bake_remove arrived")
    remove, _payload = server.last("bake_remove")
    check(remove["obj_ids"] == [cube[coll.ID_PROPERTY]], "bake_remove with only the cube's id")
    header, payload = wait_incoming("bake_result")
    check(header is not None, "bake_result of the removal returned")
    addon_module._handle_message(header, payload, time.time())
    text = bake_send.LAST_BAKE["message"]
    check(text.startswith("removed 1 element") and text.endswith("1 object"),
          "outcome of the removal: {}".format(text))

    test_family(server, collection, sample, cube, addon_module)

    sample.bilocus_bake_accept_open = True
    select_only([sample, cube, outside], active=sample)
    count = bake_send.apply_category_to_selected(bpy.context)
    check(count == 2 and cube.bilocus_bake_category == "OST_Walls"
          and outside.bilocus_bake_category == "OST_Walls",
          "apply_category_to_selected copies Walls onto the other 2 selected objects")
    check(cube.bilocus_bake_accept_open is True and outside.bilocus_bake_accept_open is True,
          "apply_category_to_selected also copies the accept_open checkbox")

    client.CLIENT.disconnect()
    server.thread.join(5.0)
    check(server.types()[-1] == "bake_end" and server.count("bake_end") == 2,
          "no frame after the family bake: {}".format(server.types()))
    level, _message = bake_send.bake_selected(bpy.context)
    check(level == 'ERROR', "after disconnect the bake refuses again")


def test_family(server, collection, sample, cube, addon_module):
    say("--- 3b. family bake against the same fake Revit")

    # Family bake scene (same connection, same server):
    #  A BakeA_array  Walls category: sent in the directshape, SKIPPED here
    #  B BakeB_cube   default category, checkbox off
    #  H BakeH_open   a single quad (open mesh), checkbox on
    opened = new_mesh_object("BakeH_open", [(0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (1.0, 1.0, 0.0),
                                            (0.0, 1.0, 0.0)], [], [(0, 1, 2, 3)],
                             collection, (0.0, 20.0, 0.0))
    opened.bilocus_bake_accept_open = True
    bpy.context.view_layer.update()
    check(sample.bilocus_bake_category == "OST_Walls" and not bake.is_family_category("OST_Walls"),
          "BakeA_array still has the Walls category, not loadable")

    before = len(server.types())
    select_only([sample], active=sample)
    level, message = bake_send.bake_selected(bpy.context, "family")
    check(level == 'ERROR' and "BakeA_array" in message and "OST_Walls" in message
          and len(server.types()) == before,
          "family with only the wall: error, no frame ({})".format(message))

    level, message = bake_send.bake_selected(bpy.context, "sphere")
    check(level == 'ERROR' and "unknown" in message, "unknown target refused")

    select_only([sample, cube, opened], active=opened)
    lines = draw_panel()
    section = bake_section(lines)
    check(any(line[1] == "prop" and line[2] == "Accept non-closed solid = True"
              for line in section),
          "panel: active object's checkbox on")

    level, message = bake_send.bake_selected(bpy.context, "family")
    say("bake_selected(family) -> {}: {}".format(level, message))
    check(level == 'WARNING' and "SKIPPED: BakeA_array (category OST_Walls" in message,
          "family: the wall is among the skipped, with the reason")
    check(bake_send.LAST_BAKE["message"].startswith(
        "family bake: sent 2 objects - waiting for the outcome from Revit"),
          "LAST_BAKE of the family waiting for the outcome")

    check(server.wait_for("bake_end", 2), "second bake_end arrived")
    kinds = server.types()[before:]
    check(kinds == ["bake_begin", "bake_mesh", "bake_mesh", "bake_end"],
          "family sequence on the wire {}".format(kinds))

    begin, _payload = server.last("bake_begin")
    check(begin["target"] == "family", "bake_begin with target family")
    check(begin["obj_ids"] == [cube[coll.ID_PROPERTY], opened[coll.ID_PROPERTY]],
          "bake_begin family announces B and H, not wall A")

    with server.condition:
        meshes = [frame[0] for frame in server.frames[before:]
                  if frame[0].get("type") == "bake_mesh"]
    first, second = meshes
    check(first["name"] == "BakeB_cube" and first["accept_open"] is False
          and first["category"] == bake.DEFAULT_CATEGORY,
          "bake_mesh B: accept_open False, default category")
    check(second["name"] == "BakeH_open" and second["accept_open"] is True
          and (second["face_count"], second["tri_count"]) == (1, 2)
          and second["matrix"][7] == 20.0,
          "bake_mesh H: accept_open True, a single quad, y=20")
    check(all(header["obj_id"] != DUPLICATE_ID for header in meshes),
          "no bake_mesh for the wall in the family")

    header, payload = wait_incoming("bake_result")
    check(header is not None and header["target"] == "family"
          and header["switched"] == 0 and header["not_moved"] == 0,
          "bake_result family returned with target, switched and not_moved")
    addon_module._handle_message(header, payload, time.time())
    text = bake_send.LAST_BAKE["message"]
    expected = ("family: 2 created, 0 updated - 7 whole faces, 0 triangulated faces "
                "- 1 as open shell")
    check(text == expected, "family outcome passed through the drain: {}".format(text))

    lines = draw_panel()
    section = bake_section(lines)
    check(section[-1][1] == "label" and section[-1][2] == expected,
          "panel: last line of the Bake section is the family outcome")

    # Family limit: 51 eligible objects rejected BEFORE any frame. They are
    # removed right afterwards, so as not to change the rest of the test's
    # counts.
    many = [new_mesh_object("BakeZ_{:02d}".format(index), TRIANGLE_VERTS, [], [(0, 1, 2)],
                            collection) for index in range(bake.MAX_FAMILY_BAKE_OBJECTS + 1)]
    bpy.context.view_layer.update()
    before = len(server.types())
    select_only(many)
    level, message = bake_send.bake_selected(bpy.context, "family")
    check(level == 'ERROR' and "the maximum for a family bake is 50" in message
          and len(server.types()) == before,
          "family with 51 objects: rejected with no frame ({})".format(message))
    for obj in many:
        data = obj.data
        bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.meshes.remove(data)


def main():
    say("Blender {} - modules from {}".format(bpy.app.version_string, SRC))
    scene = bpy.context.scene
    collection = coll.ensure_collection(scene)
    sample = test_extraction(collection)
    module, installed = enable_addon()
    say("addon {} from {}".format("installed" if installed else "from src",
                                os.path.dirname(os.path.abspath(module.__file__))))
    test_end_to_end(scene, collection, sample, module)
    disable_addon(module, installed)


try:
    main()
except BaseException:
    traceback.print_exc()
    print("{} FAILED on Blender {}".format(PREFIX, bpy.app.version_string))
    sys.stdout.flush()
    try:
        client.CLIENT.disconnect()
    except Exception:
        pass
    sys.exit(1)

print("{} ALL GREEN on Blender {}".format(PREFIX, bpy.app.version_string))
sys.stdout.flush()
