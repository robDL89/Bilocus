# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

bl_info = {
    "name": "Bilocus",
    "author": "Roberto Dolfini, with Claude Code (Anthropic)",
    "version": (0, 1, 1),
    "blender": (5, 1, 0),
    "location": "View3D > Sidebar > Bilocus",
    "description": "Live bridge to Revit over a socket",
    "category": "Import-Export",
}

import os
import sys
import time

# the addon's folder must be on the path because the modules are imported
# by plain name, so bridge_protocol.py stays testable outside Blender
_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)

import bpy

import bridge_protocol as protocol
import bridge_bake_send as bake
import bridge_client as client
import bridge_collection as coll
import bridge_import as imp
import bridge_mesh as mesh
import bridge_proxy as proxy
import bridge_receive as receive
import bridge_sync as sync
import panel


# --- live transform: coalescing and throttling -------------------------------
#
# depsgraph_update_post fires on every depsgraph evaluation, i.e. dozens of
# times a second while dragging an object. Sending one frame per firing
# would flood the socket with already-stale positions: what would reach
# Revit would be a queue of old states, and the lag would keep growing for
# as long as the mouse button is held.
#
# The fix has two parts:
#
# 1. COALESCING. The handler does not send: it writes into _PENDING, a dict
#    obj_id -> already-flattened matrix. Writing the same obj_id twice in
#    the same window overwrites, so a hundred updates of a dragged cube
#    become ONE frame, with the last position. The number of frames per
#    window is bounded by the number of objects moved, not by how often
#    Blender re-evaluates.
#
# 2. THROTTLE. A 1/30 s timer drains _PENDING. It is the same rate at which
#    Revit redraws the preview: sending more often would not show anything
#    more.
#
# What ends up in the dict is the MATRIX, not a reference to the object.
# It costs one extra list of 16 floats per firing and avoids the real
# problem: between the handler and the flush the object may have been
# deleted, or an undo may have freed its structure, and reading
# obj.matrix_world 33 ms later would mean touching memory Blender has
# already reclaimed.
#
# The handler and the timer both run on Blender's main thread, so no lock
# is needed: they cannot overlap. The socket's lock, that one is needed,
# and it is already inside BridgeClient.send.

_PENDING = {}

TRANSFORM_INTERVAL = 1.0 / 30.0
DRAIN_INTERVAL = 0.1


@bpy.app.handlers.persistent
def _on_depsgraph_update(scene, depsgraph):
    if not client.CLIENT.running:
        return

    collection = coll.find_collection()
    if collection is None:
        return

    for update in depsgraph.updates:
        # is_updated_geometry is IGNORED on purpose: geometry is manual,
        # sent only by the Sync button. Anyone passing through here to
        # "improve" the addon by adding auto-sync of geometry would flood
        # the socket with megabytes on every sculpting stroke.
        if not update.is_updated_transform:
            continue

        evaluated = update.id
        if not isinstance(evaluated, bpy.types.Object):
            continue
        if evaluated.type != 'MESH':
            continue

        original = evaluated.original
        if not coll.is_in_collection(original, collection):
            continue

        # peek_id and not stable_id: stable_id WRITES the custom property
        # when it is missing, and writing to an ID inside
        # depsgraph_update_post restarts the depsgraph, i.e. calls this
        # handler again, i.e. never ends. An object without an id was never
        # synced: Revit does not know it and would discard its transform
        # anyway. The id is given to it by the first Sync.
        obj_id = coll.peek_id(original)
        if obj_id is None:
            continue

        # the matrix is read from the EVALUATED object: it is the one
        # already updated by this depsgraph evaluation
        _PENDING[obj_id] = mesh.flat_matrix(evaluated.matrix_world)


def _flush_transforms():
    if _PENDING:
        # drain BEFORE sending: send() is synchronous and can take a few
        # milliseconds, and whatever arrives meanwhile must end up in the
        # next window, not be lost by a late clear()
        batch = list(_PENDING.items())
        _PENDING.clear()
        for obj_id, matrix in batch:
            sync.send_transform(obj_id, matrix)
    return TRANSFORM_INTERVAL


def _log(message):
    print("[Bilocus] {}".format(message))


def _tag_redraw():
    """Forces a redraw of the sidebar.

    Without this, the panel would only show the pull count the next time
    Blender happens to redraw on its own, i.e. when the mouse passes over
    it: objects would appear in the viewport while the status line lagged
    behind."""
    window_manager = getattr(bpy.context, "window_manager", None)
    if window_manager is None:
        return
    for window in window_manager.windows:
        screen = getattr(window, "screen", None)
        if screen is None:
            continue
        for area in screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()


def _handle_message(header, payload, now):
    kind = header.get("type")

    if kind == "hello_ack":
        problem = receive.check_hello_ack(header)
        if problem is not None:
            # talking on would only produce rejected messages: better to
            # stop here with the reason on the panel
            client.CLIENT.disconnect()
            client.CLIENT.status = problem
            _log(problem)
        else:
            client.CLIENT.status = "handshake ok, Revit {}".format(
                header.get("revit_version", "?"))
        _tag_redraw()

    elif kind == "error":
        # a CONTENT error from Revit's router: the connection is alive, but
        # a message was rejected. Without this branch the only place to
        # see it would be the Status button inside Revit.
        client.CLIENT.status = "Revit rejected it: {}".format(
            header.get("message", "?"))
        _log(client.CLIENT.status)

    elif kind == "proxy_result":
        # The only message that says how a WRITE into the Revit document
        # went. On the Revit side creation opens no dialog, on purpose:
        # without this branch, whoever pressed "Create Proxy" would only
        # see success by going to look inside Revit. The redraw is needed
        # because the outcome arrives on its own, without the user
        # touching anything: without tag_redraw the panel would keep
        # showing the "pending" line until the mouse passes over it.
        _log("proxy: {}".format(proxy.handle_result(header)))
        _tag_redraw()

    elif kind == "bake_result":
        # The outcome of a bake (DirectShape or family) or of a removal.
        # Same role and same reason as proxy_result: Revit opens no dialogs
        # on the path triggered by the network, so this line is the only
        # place in Blender where the write's outcome can be read. The
        # redraw removes the "pending" line from the panel without waiting
        # for the mouse to pass over it.
        _log("bake_result: {}".format(bake.handle_result(header)))
        _tag_redraw()

    elif kind == "revit_batch_begin":
        note = imp.handle_batch_begin(header, now)
        if note is not None:
            _log(note)

    elif kind == "revit_geometry":
        problem = imp.handle_geometry(header, payload, now)
        if problem is not None:
            _log(problem)

    elif kind == "revit_batch_end":
        if not imp.handle_batch_end(now):
            # closed without any batch open: not a problem, just a message
            # with nothing to close (the begin was lost and not even a
            # revit_geometry arrived)
            _log("revit_batch_end with no batch open")
        _log("pull finished: {}".format(imp.LAST_PULL.message))
        _tag_redraw()


def _drain_incoming():
    # runs on the main thread: bpy is usable here
    while not client.CLIENT.incoming.empty():
        header, payload = client.CLIENT.incoming.get()
        try:
            _handle_message(header, payload, time.time())
        except Exception as error:
            # Timer safety net. An exception that bubbles up to here makes
            # Blender disable the timer, i.e. silently switches off the
            # ENTIRE queue drain: from that point the addon stays
            # connected but deaf, and does not tell anyone. Better to lose
            # a message and print it to the console.
            _log("message discarded: {}: {}".format(type(error).__name__, error))

    # A batch can stay open: Revit can fail mid-send and never send
    # revit_batch_end, or the connection can drop in the middle. The check
    # is HERE and not inside a dispatch branch precisely because in those
    # cases no message arrives to trigger it. Objects already imported
    # stay in the scene: only the bookkeeping is closed.
    reason = imp.check_open_batch(client.CLIENT.running, time.time())
    if reason is not None:
        _log("pull interrupted: {}".format(imp.LAST_PULL.message))
        _tag_redraw()

    return DRAIN_INTERVAL


def _unregister_handler():
    # comparison by name and not by identity: after an addon reload the
    # function object in the list belongs to the old module and a remove()
    # by identity would not find it, leaving two active handlers
    handlers = bpy.app.handlers.depsgraph_update_post
    for existing in list(handlers):
        if getattr(existing, "__name__", "") == _on_depsgraph_update.__name__:
            handlers.remove(existing)


def register():
    sync.register_properties()
    imp.register_properties()
    # before the classes: the panel draws the per-object property
    bake.register_properties()
    proxy.register_properties()

    for cls in panel.CLASSES:
        bpy.utils.register_class(cls)

    _unregister_handler()
    bpy.app.handlers.depsgraph_update_post.append(_on_depsgraph_update)

    if not bpy.app.timers.is_registered(_drain_incoming):
        bpy.app.timers.register(_drain_incoming, persistent=True)
    if not bpy.app.timers.is_registered(_flush_transforms):
        bpy.app.timers.register(_flush_transforms, persistent=True)


def unregister():
    _unregister_handler()
    _PENDING.clear()

    for timer in (_flush_transforms, _drain_incoming):
        if bpy.app.timers.is_registered(timer):
            bpy.app.timers.unregister(timer)

    client.CLIENT.disconnect()

    for cls in reversed(panel.CLASSES):
        bpy.utils.unregister_class(cls)

    proxy.unregister_properties()
    bake.unregister_properties()
    imp.unregister_properties()
    sync.unregister_properties()
