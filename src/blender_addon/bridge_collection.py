# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# The ToRevit collection and the objects' stable ids.
#
# This module touches bpy: everything that can be decided without Blender
# lives in bridge_mesh.py (resolve_ids) and is covered by the tests.
#
# Decision 4 of CLAUDE.md: the preview source is ONLY this collection. Not
# the scene, not the selection.

import uuid

import bpy

import bridge_mesh as mesh

# ASCII mandatory: the name ends up in logs, in the panel's messages and in
# the strings crossing the socket. No arrows, no symbols.
COLLECTION_NAME = "ToRevit"

# Custom property carrying the stable id. The object's NAME is not an
# identifier: it changes, and it changes on its own too (".001" on a copy).
ID_PROPERTY = "bilocus_id"


def new_id():
    return uuid.uuid4().hex


def find_collection():
    """The collection if it exists, otherwise None. Does NOT create it.

    The panel calls this, not ensure_collection: draw() runs on every
    redraw of the sidebar and must not have side effects on the file (a
    file marked as modified just for opening a panel is an excellent way to
    lose the user's trust)."""
    return bpy.data.collections.get(COLLECTION_NAME)


def ensure_collection(scene=None):
    """The collection, creating it and linking it to the scene if missing."""
    if scene is None:
        scene = bpy.context.scene

    collection = bpy.data.collections.get(COLLECTION_NAME)
    if collection is None:
        collection = bpy.data.collections.new(COLLECTION_NAME)

    # children_recursive and not children: the user may have nested ToRevit
    # inside another collection, and it is already in the scene. Relinking
    # it to the master would raise RuntimeError.
    linked = False
    for child in scene.collection.children_recursive:
        if child is collection:
            linked = True
            break
    if not linked:
        scene.collection.children.link(collection)

    return collection


def objects_to_send(collection=None):
    """The mesh objects of the collection, in name order.

    The order is alphabetical and not arbitrary because two consecutive
    Syncs on the same scene must produce the same frame sequence: when
    something does not add up, a diff between two captures only makes
    sense under that condition."""
    if collection is None:
        collection = find_collection()
    if collection is None:
        return []

    result = []
    # all_objects and not objects: this also includes collections nested
    # inside ToRevit, which is what whoever drags a group in there expects
    for obj in collection.all_objects:
        if obj.type != 'MESH':
            continue
        if obj.hide_viewport:
            # "Disable in Viewports" (monitor icon) removes the object from
            # the depsgraph. evaluated_get would return the ORIGINAL, i.e.
            # the shape before modifiers, without raising anything: better
            # not to send it than to send it silently wrong. It also
            # disappears from Revit, because its id never enters
            # sync_begin.
            continue
        result.append(obj)

    result.sort(key=lambda item: item.name)
    return result


def is_in_collection(obj, collection=None):
    """True if the object is in the collection, nesting included.

    The test is by name because bpy_prop_collection resolves `in` as a
    lookup on the key: object names are unique inside bpy.data.objects, so
    name clashes are not a risk."""
    if obj is None:
        return False
    if collection is None:
        collection = find_collection()
    if collection is None:
        return False
    return obj.name in collection.all_objects


def stable_id(obj):
    """The object's stable id, generating and saving it if absent.

    WARNING: this function WRITES to obj when the id is missing, and
    writing a custom property restarts the depsgraph. It must not be
    called inside a depsgraph_update_post handler, on pain of an infinite
    loop. See the comment on the handler in __init__.py."""
    value = obj.get(ID_PROPERTY)
    if not isinstance(value, str) or not value:
        value = new_id()
        obj[ID_PROPERTY] = value
    return value


def peek_id(obj):
    """The id if present, otherwise None. Writes nothing: it is the variant
    usable from the depsgraph handler."""
    value = obj.get(ID_PROPERTY)
    if isinstance(value, str) and value:
        return value
    return None


def assign_ids(objects):
    """The ids of a list of objects, resolving the duplicates left by
    Shift+D. The logic lives in bridge_mesh.resolve_ids and is tested
    there."""
    current = [obj.get(ID_PROPERTY) for obj in objects]
    ids, changed = mesh.resolve_ids(current, new_id)
    for index in changed:
        objects[index][ID_PROPERTY] = ids[index]
    return ids


def add_objects(objects):
    """Links the mesh objects to the collection. Returns the ones added.

    LINK, not move: the object also stays in its original collection. The
    bridge must not reorganize the scene of whoever uses it."""
    collection = ensure_collection()
    added = []
    for obj in objects:
        if obj.type != 'MESH':
            continue
        if obj.name in collection.objects:
            continue
        collection.objects.link(obj)
        added.append(obj)
    return added


def remove_objects(objects):
    """Unlinks the objects from the collection. Returns the ids of the ones
    that were actually there, so the caller can send a `remove` for each.

    Acts only on DIRECT membership: an object inside a collection nested
    under ToRevit must be removed from that one."""
    collection = find_collection()
    if collection is None:
        return []

    removed = []
    for obj in objects:
        if obj.name not in collection.objects:
            continue
        obj_id = peek_id(obj)
        if len(obj.users_collection) <= 1:
            # ToRevit is the object's only collection: unlinking it would
            # make it disappear from the scene. Happens to whoever moved it
            # there with M instead of this panel's button.
            bpy.context.scene.collection.objects.link(obj)
        collection.objects.unlink(obj)
        if obj_id is not None:
            removed.append(obj_id)
    return removed
