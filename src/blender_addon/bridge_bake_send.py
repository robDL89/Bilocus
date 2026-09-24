# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

# The Blender-side bake, in the two modes DirectShape and family: the
# per-object properties (category and "accept non-closed solid"), sending
# the bake_begin / bake_mesh / bake_end batch, removal and the outcome.
#
# This module touches bpy: it must stay as thin as possible. Everything
# that can be decided without Blender (categories, packing, headers,
# reading and formatting bake_result) lives in bridge_bake.py and is
# covered by tests.
#
# Source: the SELECTED objects that are also in ToRevit. Not the whole collection like Sync: the bake writes real
# elements into the project file, and selection is the way to say "these
# three, not the other forty". Not free selection: the collection
# guarantees that what ends up in Revit is what was seen in the preview,
# with the same id.

import bpy

import bridge_bake as bake
import bridge_client as client
import bridge_collection as coll
import bridge_extract as extract
import bridge_mesh as mesh
import bridge_sync as sync

# Name of the per-object property, in a constant because the panel and the
# smoke test read it too: a typo in a scattered string would give a false
# hasattr and a panel row that disappears, with no error at all.
CATEGORY_PROPERTY = "bilocus_bake_category"
TOGETHER_PROPERTY = "bilocus_bake_together"
ACCEPT_OPEN_PROPERTY = "bilocus_bake_accept_open"
SMOOTH_MESH_PROPERTY = "bilocus_bake_smooth_mesh"

NO_RESULT_MESSAGE = "no bake in this session"

# Not a cap, and not part of the contract: above this many objects the Bake
# Family button asks for confirmation. Every object of a family bake opens,
# fills, loads and closes a family document, so fifty is already minutes of
# a busy Revit; the user may still want 120 plain boxes as families, up to
# bake.MAX_BAKE_OBJECTS like any bake.
FAMILY_BAKE_CONFIRM_ABOVE = 50

# Outcome of the last bake or the last removal, read by the panel. Lives in
# the module and not in the scene: it is session information, it must not
# be saved into the .blend. Same choice as bridge_sync.LAST_SYNC and
# bridge_proxy.LAST_PROXY.
LAST_BAKE = {"message": NO_RESULT_MESSAGE}


# --- per-object properties ---------------------------------------------------

def register_properties():
    # On the OBJECT and not on the scene: it is
    # saved in the .blend together with the object, so the next bake finds
    # the category on its own, and a Shift+D copies it onto the duplicate
    # along with everything else.
    setattr(bpy.types.Object, CATEGORY_PROPERTY, bpy.props.EnumProperty(
        name="Revit Category",
        description="Category of the DirectShape or family that the bake "
                    "creates in Revit for this object",
        items=bake.BAKE_CATEGORIES,
        default=bake.DEFAULT_CATEGORY))
    # Per-object like the category: an open shell
    # is fine for a canopy and not for a column, so the choice belongs to
    # the object and not to the scene. Off by default: a family that does
    # not enclose a volume must be a choice, not a surprise.
    setattr(bpy.types.Object, ACCEPT_OPEN_PROPERTY, bpy.props.BoolProperty(
        name="Accept non-closed solid",
        description="In Family Bake, also accepts an open mesh, which in "
                    "Revit becomes a shell with no volume. A non-manifold "
                    "mesh is still rejected. Ignored by DirectShape Bake",
        default=False))
    # Per-object too: a shell shown smooth in a render view and a column
    # that must be cut in section can sit in the same scene. Off by
    # default: losing the volume must be a choice.
    setattr(bpy.types.Object, SMOOTH_MESH_PROPERTY, bpy.props.BoolProperty(
        name="Smooth mesh (no volume)",
        description="In DirectShape Bake, sends the object to Revit as a "
                    "mesh: drawn smooth, without the edges of its faces. "
                    "A closed mesh is still cut and filled in section, but "
                    "it has no volume, no joins, no voids and cannot be "
                    "dimensioned. Ignored by "
                    "Family Bake, which needs a solid",
        default=False))
    # On the SCENE: it is a way of baking, not a property of an object.
    setattr(bpy.types.Scene, TOGETHER_PROPERTY, bpy.props.BoolProperty(
        name="Bake together",
        description="Family Bake: one family with all the selected objects, "
                    "named after the active object and placed at its origin. "
                    "Off: one family per object",
        default=False))


def unregister_properties():
    for name in (SMOOTH_MESH_PROPERTY, ACCEPT_OPEN_PROPERTY, CATEGORY_PROPERTY):
        if hasattr(bpy.types.Object, name):
            delattr(bpy.types.Object, name)
    if hasattr(bpy.types.Scene, TOGETHER_PROPERTY):
        delattr(bpy.types.Scene, TOGETHER_PROPERTY)


def together_of(scene):
    return bool(getattr(scene, TOGETHER_PROPERTY, False))


def category_of(obj):
    """The category chosen for the object. The getattr covers the case
    where the property is not registered yet, like limits() in
    bridge_sync."""
    return getattr(obj, CATEGORY_PROPERTY, bake.DEFAULT_CATEGORY)


def accept_open_of(obj):
    """The object's "accept non-closed solid" checkbox, False if the
    property is not registered.

    No convenience bool(): the BoolProperty already returns a bool, and if
    something else ever arrives it must be build_bake_mesh_header that
    rejects it, failing only that object with the reason."""
    return getattr(obj, ACCEPT_OPEN_PROPERTY, False)


def smooth_mesh_of(obj):
    """The object's "smooth mesh" checkbox, False if the property is not
    registered. Same no-bool() reasoning as accept_open_of."""
    return getattr(obj, SMOOTH_MESH_PROPERTY, False)


# --- who is sent ------------------------------------------------------------------

def _selected(context):
    # getattr and not context.selected_objects: the member only exists in
    # contexts that have a view layer, and the panel must not raise in
    # draw() if it is one day drawn from a context that lacks it
    return list(getattr(context, "selected_objects", None) or ())


def bake_targets(context=None):
    """The objects a bake would send right now: selected, mesh, inside
    ToRevit (nesting included), not disabled in the viewport. In name
    order, like objects_to_send, so two identical bakes produce the same
    sequence of frames.

    The hide_viewport filter is the same as objects_to_send and for the
    same reason: a disabled object is not in the depsgraph, evaluated_get
    would return the shape before modifiers, and a DirectShape silently
    different from the preview is worse than a skipped object. Staying
    aligned with objects_to_send also matters for the ids, which come from
    there (see bake_selected)."""
    if context is None:
        context = bpy.context

    collection = coll.find_collection()
    if collection is None:
        return []

    result = []
    for obj in _selected(context):
        if obj.type != 'MESH' or obj.hide_viewport:
            continue
        if not coll.is_in_collection(obj, collection):
            continue
        result.append(obj)

    result.sort(key=lambda item: item.name)
    return result


def remove_targets(context=None):
    """(objects, ids) for "Remove bake": the selected objects that have a
    bridge id, in name order, and their ids without repeats.

    ToRevit is not looked at: an object removed from the collection after
    the bake still has its DirectShape in Revit, and that is exactly one of
    the cases where you want to remove it. An object with no id, instead,
    has never gone through the bridge: in Revit there is nothing carrying
    its mark.

    peek_id and not assign_ids: removing must not write onto the objects.
    The flip side is a Shift+D copy never passed through Sync or Bake: it
    still carries the original's id, and selecting it would remove the
    ORIGINAL's DirectShape. This is why an id carried by more than one
    object in the file is excluded (see shared_id_objects): better a
    removal refused with its reason than an element deleted in the project
    file instead of another one.

    The ids are deduplicated anyway: bake_remove rejects duplicates across
    the whole message."""
    if context is None:
        context = bpy.context

    shared = _shared_ids()
    objects = [obj for obj in _selected(context)
               if coll.peek_id(obj) is not None and coll.peek_id(obj) not in shared]
    objects.sort(key=lambda item: item.name)

    ids = []
    seen = set()
    # loop per selected OBJECT, not per vertex
    for obj in objects:
        obj_id = coll.peek_id(obj)
        # comparison on the normalized form, as bake_remove does
        key = obj_id.strip()
        if key in seen:
            continue
        seen.add(key)
        ids.append(obj_id)
    return objects, ids


def has_remove_candidates(context=None):
    """True if at least one selected object carries a bridge id.

    The panel's check for enabling "Remove Bake". It looks only at the
    selection, not at the whole file like remove_targets: draw() runs on
    every redraw of the sidebar, and a pass over bpy.data.objects there is
    a permanent slowdown on a large scene. Ids shared with a copy are
    filtered by the operator's invoke, which also says why."""
    if context is None:
        context = bpy.context
    return any(coll.peek_id(obj) is not None for obj in _selected(context))


def _shared_ids():
    """The bridge ids carried by more than one object in the file.

    The whole of bpy.data.objects is looked at, not just the selection nor
    ToRevit: the copy that steals the id can be anywhere, and the original
    may have left the collection. Loop per OBJECT, not per vertex, and no
    writes."""
    seen = set()
    shared = set()
    for obj in bpy.data.objects:
        obj_id = coll.peek_id(obj)
        if obj_id is None:
            continue
        if obj_id in seen:
            shared.add(obj_id)
        seen.add(obj_id)
    return shared


def describe_no_remove_targets(context=None):
    """Why "Remove bake" has nothing to send, in one line.

    It distinguishes the two cases because they call for different actions:
    an object never passed through the bridge has nothing in Revit, an id
    shared with a copy is fixed with a Sync or a Bake, which assign a new
    id to the copy."""
    if context is None:
        context = bpy.context

    shared = _shared_ids()
    blocked = [obj.name for obj in _selected(context)
               if coll.peek_id(obj) is not None and coll.peek_id(obj) in shared]
    if blocked:
        return ("{} shares its id with a copy (Shift+D): press Sync or Bake "
                "to separate them, then repeat the removal".format(
                    ", ".join(sorted(blocked)[:3])))
    return ("no selected object has ever gone through the bridge: "
            "nothing to remove in Revit")


# --- bake ---------------------------------------------------------------------

def bake_selected(context=None, target="directshape", together=False):
    """Sends bake_begin, one bake_mesh per object, bake_end.

    target is the bake mode, "directshape" or "family" (bake.BAKE_TARGETS).
    The path is the same in both modes; family only changes the object
    limit and skips ahead of time the categories that cannot become a
    family.

    together (family only): one family with all the objects, named after
    and placed at the ACTIVE object, which must be among them.

    Returns (level, message) with level in 'INFO', 'WARNING', 'ERROR': the
    panel operator passes them as-is to self.report.

    Does NOT wait for the outcome, like send_proxy: the reply is
    bake_result, which arrives on the queue and passes through
    handle_result during the drain. Waiting here would mean blocking
    Blender for the whole duration of the Revit transaction."""
    if context is None:
        context = bpy.context

    # Before everything else: a wrong target is a caller defect, and the
    # message must say so instead of talking about connection or objects
    if not isinstance(target, str) or target not in bake.BAKE_TARGETS:
        return 'ERROR', "unknown bake mode: {!r}".format(target)
    family = target == "family"

    if not client.CLIENT.running:
        return 'ERROR', "not connected to Revit: press Connect"

    targets = bake_targets(context)
    if not targets:
        return 'ERROR', ("nothing to bake: select objects that are inside "
                         "{}".format(coll.COLLECTION_NAME))

    editing = sync.objects_in_edit_mode(targets)
    if editing:
        # Same decision as Sync, for the same reason (see
        # objects_in_edit_mode): in Edit Mode the evaluated mesh is the one
        # from the last exit, and the bake would write a shape different
        # from what is on screen into the document.
        return 'ERROR', ("Bake refused: {} is in Edit Mode. Go back to Object "
                         "Mode (Tab) and press Bake again".format(", ".join(editing[:3])))

    skipped = []
    if family:
        # System categories (walls, floors...) do not go into a family:
        # these objects are NOT announced. If announced, Revit would fail
        # them one by one after receiving the geometry; here it is said
        # right away which object and why, with no bytes on the wire. The
        # filter comes before the limit: skipped objects do not take up
        # slots in the batch. No mesh evaluation, just a property read.
        eligible = []
        for obj in targets:
            category = category_of(obj)
            if bake.is_family_category(category):
                eligible.append(obj)
            else:
                skipped.append("{} (category {} not allowed for a family)".format(
                    obj.name, category))
        targets = eligible
        if not targets:
            return 'ERROR', "nothing to bake as a family - SKIPPED: {}".format(
                "; ".join(skipped[:3]))

    if len(targets) > bake.MAX_BAKE_OBJECTS:
        # check BEFORE evaluating any mesh: the message is written without
        # doing the expensive part, and Revit would reject the announcement
        # anyway. Above FAMILY_BAKE_CONFIRM_ABOVE the Bake Family button has
        # already asked; a script calling the operator is not asked.
        return 'ERROR', "{} objects selected, the maximum for a bake is {}".format(
            len(targets), bake.MAX_BAKE_OBJECTS)

    warn_limit, reject_limit = sync.limits(context.scene)

    # Ids across the WHOLE collection, not just the targets. Shift+D
    # duplicates are only recognized by looking at all of them: baking only
    # the copy, the original would not be among the targets, the copy would
    # keep the original's id and the bake would REPLACE the original's
    # DirectShape in Revit. assign_ids leaves the id with the first one in
    # name order and generates a new one for the others, exactly like Sync:
    # preview and bake call the same object by the same id.
    collection_objects = coll.objects_to_send()
    collection_ids = coll.assign_ids(collection_objects)
    # as_pointer and not the name: two objects linked from different
    # libraries can have the same name, not the same address
    id_by_pointer = dict(zip(
        [obj.as_pointer() for obj in collection_objects], collection_ids))

    depsgraph = context.evaluated_depsgraph_get()

    # Whoever is over the threshold is NOT announced. An id announced and
    # never arrived is only counted by Revit as "missing"; here instead it
    # can say which object and how many vertices. To know this before
    # bake_begin, the vertices of the evaluated mesh are counted without
    # reading its data.
    announced = []
    warned = []
    for obj in targets:
        obj_id = id_by_pointer.get(obj.as_pointer())
        if obj_id is None:
            # bake_targets and objects_to_send filter the same way: this is
            # only reached if they ever diverge, and without an id nothing
            # is baked
            skipped.append("{} (outside {})".format(obj.name, coll.COLLECTION_NAME))
            continue
        vertex_count = extract.evaluated_vertex_count(obj, depsgraph)
        budget = mesh.check_vertex_budget(vertex_count, warn_limit, reject_limit)
        if budget == mesh.BUDGET_REJECT:
            skipped.append("{} ({} vertices)".format(obj.name, vertex_count))
            continue
        if budget == mesh.BUDGET_WARN:
            warned.append("{} ({} vertices)".format(obj.name, vertex_count))
        announced.append((obj, obj_id))

    if not announced:
        return 'ERROR', "nothing to bake - SKIPPED: {}".format("; ".join(skipped[:3]))

    host = None
    if family and together and len(announced) > 1:
        # The active object names the family, places it and identifies it
        # on the next bake: it must be one of the objects going out.
        active = getattr(context, "active_object", None)
        for obj, obj_id in announced:
            if active is not None and obj.as_pointer() == active.as_pointer():
                host = obj_id
                break
        if host is None:
            return 'ERROR', ("Bake together: the active object must be one of the "
                             "selected objects in {} (Ctrl+click it to make it "
                             "active)".format(coll.COLLECTION_NAME))

    try:
        begin = bake.build_bake_begin_header(
            [obj_id for _obj, obj_id in announced], target, host)
    except Exception as error:
        # rejected before producing any bytes: the connection is intact
        return 'ERROR', "bake cannot be sent: {}".format(error)

    if not client.CLIENT.send(begin):
        return 'ERROR', "connection lost before the bake"

    sent = 0
    failed = []
    lost = False
    try:
        for obj, obj_id in announced:
            problem, header, payload = _prepare_mesh(obj, obj_id, depsgraph, reject_limit)
            if problem is not None:
                # Rejected before producing any bytes: the connection is
                # intact. The object is already announced, so Revit will
                # count it as missing and will not touch it: its previous
                # DirectShape, if any, stays as it was.
                failed.append("{} ({})".format(obj.name, problem))
                continue
            if not client.CLIENT.send(header, payload):
                if not _connected():
                    lost = True
                    break
                # send fails with the socket alive: frame dropped before
                # writing a single byte (over MAX_PAYLOAD_BYTES), recoverable
                # like the case above
                failed.append("{} ({})".format(obj.name, client.CLIENT.status))
                continue
            sent += 1
    finally:
        # bake_end ALWAYS after bake_begin, even with objects failed
        # partway through or an unexpected exception: it is bake_end that
        # puts the batch into execution. Without it, Revit would keep a
        # batch open that nobody closes, the objects that arrived would not
        # be written and the panel would wait for an outcome that never
        # comes. With a lost connection the send fails right away and does
        # no harm: a batch left open is discarded by the next bake_begin.
        client.CLIENT.send(bake.build_bake_end_header())

    if lost:
        return _record('ERROR', "connection lost after {} out of {} objects: Revit will "
                                "not write anything from a batch that is not closed".format(
                                    sent, len(announced)))

    notes = []
    if skipped:
        notes.append("SKIPPED: {}".format("; ".join(skipped[:3])))
    if failed:
        notes.append("NOT sent: {}".format("; ".join(failed[:3])))
    if warned:
        notes.append("heavy: {}".format("; ".join(warned[:3])))

    if sent == 0:
        # bake_end went out anyway: Revit will reply with ok false and all
        # objects missing, and that outcome will replace this line
        level = 'ERROR'
        message = "no object sent"
    else:
        level = 'WARNING' if notes else 'INFO'
        message = "{} - waiting for the outcome from Revit".format(_sent_text(sent))
        if family:
            # the directshape stays as it was in Phase B; the family
            # declares itself, because the wait for a family bake is much
            # longer
            message = "family bake{}: {}".format(" together" if host else "", message)
    if notes:
        message = "{} - {}".format(message, " - ".join(notes))
    return _record(level, message)


def _prepare_mesh(obj, obj_id, depsgraph, reject_limit):
    """(problem, header, payload): problem is None if the object is ready.

    Except Exception and not just BridgeFramingError: even a bpy
    RuntimeError on a single object must become "this one did not go
    out", not an exception that stops the batch for everyone else.

    accept_open is written in both modes, as the contract wants: Revit
    ignores it in the directshape, and the header stays the same."""
    try:
        positions, face_sizes, face_vertices, tri_vertices, tri_faces, vertex_count = \
            extract.extract_polygons(obj, depsgraph, reject_limit)
        if positions is None:
            if vertex_count > 0:
                # counted under the threshold before bake_begin and over it
                # now: the depsgraph does not change between the two
                # readings, it should not happen, but if it does the object
                # does not go out
                return "{} vertices over the limit".format(vertex_count), None, None
            return "no evaluable geometry", None, None

        payload = bake.pack_bake_payload(
            positions, face_sizes, face_vertices, tri_vertices, tri_faces)
        # counts from the same sequences passed to pack_bake_payload: this
        # is the condition for the header and the payload to agree on the
        # Revit side
        header = bake.build_bake_mesh_header(
            obj_id, obj.name, category_of(obj), obj.matrix_world,
            len(positions) // 3, len(face_sizes), len(face_vertices), len(tri_faces),
            accept_open_of(obj), smooth_mesh_of(obj))
    except Exception as error:
        return str(error), None, None
    return None, header, payload


def _connected():
    return client.CLIENT.running and client.CLIENT.socket is not None


def _sent_text(count):
    # "sent 1 objects" would read wrong on every single-object bake
    if count == 1:
        return "sent 1 object"
    return "sent {} objects".format(count)


def _objects_text(count):
    if count == 1:
        return "1 object"
    return "{} objects".format(count)


def _record(level, message):
    LAST_BAKE["message"] = message
    return level, message


# --- removal and category ----------------------------------------------------

def remove_bake_selected(context=None):
    """Sends bake_remove for the selected objects that have an id.

    Returns (level, message). Does not wait for the outcome, like
    bake_selected. The confirmation is asked by the operator, not by this
    function: this is reached with the decision already made."""
    if context is None:
        context = bpy.context

    if not client.CLIENT.running:
        return 'ERROR', "not connected to Revit: press Connect"

    objects, ids = remove_targets(context)
    if not ids:
        return 'ERROR', describe_no_remove_targets(context)

    try:
        header = bake.build_bake_remove_header(ids)
    except Exception as error:
        # over MAX_BAKE_OBJECTS, or an unreadable id: nothing has gone out
        return 'ERROR', "removal cannot be sent: {}".format(error)

    if not client.CLIENT.send(header):
        return 'ERROR', "connection lost during removal"

    return _record('INFO', "removal requested for {} - waiting for the outcome from Revit".format(
        _objects_text(len(objects))))


def apply_category_to_selected(context=None):
    """Copies the category AND the "accept non-closed solid" checkbox of the
    active object onto all other selected mesh objects. Returns how many it
    changed: zero even when there is no active mesh to copy from.

    The properties travel together because together they describe what
    the object becomes in Revit: copying only the category would leave a
    selection of objects half accepted and half rejected as an open shell,
    or half smooth and half solid, and it would only be discovered from
    the bake's outcome.

    Does not filter on ToRevit: the category is a property of the object,
    and assigning it before adding it to the collection is a legitimate
    order of work."""
    if context is None:
        context = bpy.context

    active = getattr(context, "active_object", None)
    if active is None or active.type != 'MESH':
        return 0

    value = category_of(active)
    accept_open = accept_open_of(active)
    smooth_mesh = smooth_mesh_of(active)
    count = 0
    for obj in _selected(context):
        if obj.type != 'MESH' or obj == active:
            continue
        setattr(obj, CATEGORY_PROPERTY, value)
        setattr(obj, ACCEPT_OPEN_PROPERTY, accept_open)
        setattr(obj, SMOOTH_MESH_PROPERTY, smooth_mesh)
        count += 1
    return count


# --- outcome ------------------------------------------------------------------------

def handle_result(header):
    """Records a bake_result that arrived from Revit, from a bake in either
    mode or from a removal. Returns the message.

    Raises BridgeMessageError if the header is not readable: the drain in
    __init__.py catches it and writes it to the console, without turning
    off the timer."""
    fields = bake.read_bake_result(header)
    LAST_BAKE["message"] = bake.format_bake_result(fields)
    return LAST_BAKE["message"]
