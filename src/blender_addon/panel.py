# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (c) 2026 Roberto Dolfini

import bpy

import bridge_bake_send as bake
import bridge_client as client
import bridge_collection as coll
import bridge_fit as fit
import bridge_import as imp
import bridge_protocol as protocol
import bridge_proxy as proxy
import bridge_sync as sync


class BILOCUS_OT_connect(bpy.types.Operator):
    bl_idname = "bilocus.connect"
    bl_label = "Connect"
    bl_description = "Connects to Revit"

    def execute(self, context):
        client.CLIENT.connect()
        # Revit starts with the default colors: send the scene's right away.
        sync.send_style(context.scene)
        self.report({'INFO'}, client.CLIENT.status)
        return {'FINISHED'}


class BILOCUS_OT_disconnect(bpy.types.Operator):
    bl_idname = "bilocus.disconnect"
    bl_label = "Disconnect"
    bl_description = "Closes the connection to Revit"

    def execute(self, context):
        client.CLIENT.disconnect()
        self.report({'INFO'}, client.CLIENT.status)
        return {'FINISHED'}


class BILOCUS_OT_sync(bpy.types.Operator):
    bl_idname = "bilocus.sync"
    bl_label = "Sync"
    bl_description = ("Sends to Revit the geometry of every object in the "
                      "{} collection".format(coll.COLLECTION_NAME))

    def execute(self, context):
        level, message = sync.sync_all(context)
        self.report({level}, message)
        if level == 'ERROR':
            return {'CANCELLED'}
        return {'FINISHED'}


class BILOCUS_OT_add_selected(bpy.types.Operator):
    bl_idname = "bilocus.add_selected"
    bl_label = "Add Selection"
    bl_description = ("Links the selected mesh objects to the {} collection "
                      "(does not move them)".format(coll.COLLECTION_NAME))

    def execute(self, context):
        added = coll.add_objects(list(context.selected_objects))
        self.report({'INFO'}, "added {} objects to {}".format(
            len(added), coll.COLLECTION_NAME))
        return {'FINISHED'}


class BILOCUS_OT_remove_selected(bpy.types.Operator):
    bl_idname = "bilocus.remove_selected"
    bl_label = "Remove Selection"
    bl_description = ("Unlinks the selected objects from the {} collection "
                      "and removes them from the preview".format(coll.COLLECTION_NAME))

    def execute(self, context):
        removed = coll.remove_objects(list(context.selected_objects))
        for obj_id in removed:
            sync.send_remove(obj_id)
        self.report({'INFO'}, "removed {} objects from {}".format(
            len(removed), coll.COLLECTION_NAME))
        return {'FINISHED'}


class BILOCUS_OT_clear(bpy.types.Operator):
    bl_idname = "bilocus.clear"
    bl_label = "Clear Preview"
    bl_description = "Clears the whole preview inside Revit without touching the scene"

    def execute(self, context):
        if not sync.send_clear():
            self.report({'ERROR'}, "not connected to Revit: nothing to clear")
            return {'CANCELLED'}
        self.report({'INFO'}, "preview cleared")
        return {'FINISHED'}


class BILOCUS_OT_create_proxy(bpy.types.Operator):
    bl_idname = "bilocus.create_proxy"
    bl_label = "Create Proxy"
    bl_description = ("Sends to Revit the selected edges of the active "
                      "object: they become snappable model lines. Resending "
                      "the same edges replaces that object's previous "
                      "proxies")

    def execute(self, context):
        level, message = proxy.send_proxy(context)
        self.report({level}, message)
        if level == 'ERROR':
            return {'CANCELLED'}
        return {'FINISHED'}


class BILOCUS_OT_bake_directshape(bpy.types.Operator):
    bl_idname = "bilocus.bake_directshape"
    bl_label = "Bake DirectShape"
    bl_description = ("Creates in Revit a DirectShape for every selected "
                      "object that is in {}, in the object's chosen category. "
                      "Baking again replaces, does not duplicate, and removes "
                      "the same object's family if there was one".format(
                          coll.COLLECTION_NAME))

    def execute(self, context):
        level, message = bake.bake_selected(context, "directshape")
        self.report({level}, message)
        if level == 'ERROR':
            return {'CANCELLED'}
        return {'FINISHED'}


class BILOCUS_OT_bake_family(bpy.types.Operator):
    bl_idname = "bilocus.bake_family"
    bl_label = "Bake Family"
    bl_description = ("Creates in Revit a loadable family BL_<name> and its "
                      "instance for every selected object that is in {}. "
                      "Baking again updates the family keeping the voids, "
                      "and removes the same object's DirectShape if there "
                      "was one. Walls, floors, roofs, ceilings, stairs and "
                      "railings are not allowed".format(coll.COLLECTION_NAME))

    def execute(self, context):
        level, message = bake.bake_selected(context, "family")
        self.report({level}, message)
        if level == 'ERROR':
            return {'CANCELLED'}
        return {'FINISHED'}


class BILOCUS_OT_remove_bake(bpy.types.Operator):
    bl_idname = "bilocus.remove_bake"
    bl_label = "Remove Bake"
    bl_description = ("Deletes in Revit everything the bridge created for "
                      "the selected objects: DirectShapes, families and "
                      "their instances. Deleting an object in Blender does "
                      "not do this on its own")

    def invoke(self, context, event):
        objects, ids = bake.remove_targets(context)
        if not ids:
            # no dialog for nothing: a confirmation that would delete
            # nothing teaches people to press "Remove" without reading
            self.report({'ERROR'}, bake.describe_no_remove_targets(context))
            return {'CANCELLED'}

        # The count is in the dialog because the deletion happens in a
        # project file, outside Blender's undo: "3 objects" read before
        # confirming is the last useful moment to notice an extra Ctrl+A.
        message = ("Delete in Revit the DirectShapes, families and instances "
                   "of {} selected objects?".format(len(objects)))
        try:
            return context.window_manager.invoke_confirm(
                self, event, title="Remove Bake", message=message,
                confirm_text="Remove")
        except TypeError:
            # title, message and confirm_text do not exist in versions
            # before the new confirmation dialog: better the generic
            # confirmation than a button that raises
            return context.window_manager.invoke_confirm(self, event)

    def execute(self, context):
        level, message = bake.remove_bake_selected(context)
        self.report({level}, message)
        if level == 'ERROR':
            return {'CANCELLED'}
        return {'FINISHED'}


class BILOCUS_OT_apply_bake_category(bpy.types.Operator):
    bl_idname = "bilocus.apply_bake_category"
    bl_label = "Apply to Selected"
    bl_description = ("Copies the Revit category and the 'Accept open solid' "
                      "checkbox of the active object to every selected mesh "
                      "object")
    # UNDO because it changes data saved in the .blend, Ctrl+Z must restore
    # the previous categories, like any other change to objects
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        target = proxy.active_mesh(context)
        if target is None:
            self.report({'ERROR'}, "no active mesh to copy the category from")
            return {'CANCELLED'}
        count = bake.apply_category_to_selected(context)
        self.report({'INFO'}, "category {} and open solid {} copied to {} objects".format(
            bake.category_of(target),
            "accepted" if bake.accept_open_of(target) else "rejected", count))
        return {'FINISHED'}


class BILOCUS_PT_panel(bpy.types.Panel):
    bl_label = "Bilocus"
    bl_idname = "BILOCUS_PT_panel"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Bilocus"

    def draw(self, context):
        layout = self.layout

        box = layout.box()
        box.label(text="Port: {}".format(protocol.DEFAULT_PORT))
        box.label(text=client.CLIENT.status)
        row = box.row()
        row.operator("bilocus.connect")
        row.operator("bilocus.disconnect")

        # draw() runs on every redraw of the sidebar: only objects are
        # counted here (a pass over the collection), NEVER triangles.
        # Counting triangles requires evaluating the meshes, and that is
        # the kind of work that turns an open panel into a permanent
        # viewport slowdown. Triangles are read from the last Sync.
        objects = coll.objects_to_send()

        box = layout.box()
        box.label(text="Collection: {}".format(coll.COLLECTION_NAME))
        if coll.find_collection() is None:
            box.label(text="not created yet", icon='INFO')
        box.label(text="objects ready: {}".format(len(objects)))

        row = box.row()
        row.operator("bilocus.add_selected")
        row.operator("bilocus.remove_selected")

        editing = sync.objects_in_edit_mode(objects)
        if editing:
            warn = box.column()
            warn.alert = True
            warn.label(text="Edit Mode: {}".format(", ".join(editing[:2])),
                       icon='ERROR')
            warn.label(text="Sync blocked, press Tab")

        column = box.column()
        column.scale_y = 1.6
        column.enabled = client.CLIENT.running and not editing
        column.operator("bilocus.sync", icon='FILE_REFRESH')
        box.operator("bilocus.clear", icon='TRASH')

        box = layout.box()
        box.label(text="Last sync")
        box.label(text=sync.LAST_SYNC["message"])

        # Preview colors: applied in Revit immediately, no Sync needed.
        box = layout.box()
        box.label(text="Preview colors in Revit")
        row = box.row()
        row.prop(context.scene, "bilocus_face_color")
        row.prop(context.scene, "bilocus_edge_color")

        # Proxy: the only bridge command that WRITES into the Revit
        # document. Different source from the rest of the panel, and this
        # is intentional: not the ToRevit collection but the selected edges
        # of the active object.
        #
        # The count sits next to the button because that button creates
        # real elements in a project file: knowing how many edges are about
        # to be sent BEFORE pressing is the difference between three
        # reference lines and three hundred lines to delete by hand.
        box = layout.box()
        box.label(text="Snappable proxies")

        target = proxy.active_mesh(context)
        selected = None
        proxy_editing = target is not None and target.mode == 'EDIT'

        if target is None:
            box.label(text="no active mesh", icon='INFO')
        else:
            box.label(text="object: {}".format(target.name))

        if proxy_editing:
            # In Edit Mode the flags in mesh.edges are the ones from the
            # last exit from Edit Mode: showing their count would mean
            # writing a number that does not match what is highlighted on
            # screen. Better not to count at all and say why.
            warn = box.column()
            warn.alert = True
            warn.label(text="Edit Mode: stale selection", icon='ERROR')
            warn.label(text="creation blocked, press Tab")
        elif target is not None:
            selected, note = proxy.count_selected_edges(target)
            if selected is None:
                box.label(text=note, icon='INFO')
            else:
                box.label(text="selected edges: {}".format(selected))

        # Mode and offset sit above the button: they decide HOW MANY
        # elements end up in the document, like the count above.
        box.prop(context.scene, "bilocus_proxy_mode", expand=True)
        if context.scene.bilocus_proxy_mode == proxy.MODE_ARCS:
            box.prop(context.scene, "bilocus_proxy_tolerance_mm")
            if context.scene.bilocus_proxy_tolerance_mm <= 0.0:
                box.label(text="0: using the minimum of {} mm".format(
                    fit.MIN_TOLERANCE_MM), icon='INFO')

        column = box.column()
        column.scale_y = 1.4
        # selected None means "not counted", not "zero": the button stays
        # pressable and the real check is done by the operator, only once
        column.enabled = (client.CLIENT.running and target is not None
                          and not proxy_editing and selected != 0)
        # EXPORT is the twin of IMPORT, already used below for the opposite
        # direction: a wrong icon identifier does not give a button with no
        # icon, it makes draw() raise and takes down the ENTIRE panel, and
        # this pair is the only one already seen working inside Blender.
        column.operator("bilocus.create_proxy", icon='EXPORT')
        box.label(text=proxy.LAST_PROXY["message"])

        # Bake: the selected objects of ToRevit become real elements in the
        # Revit document, DirectShapes or families. It is the command that
        # writes the most into the project file, so what is needed to
        # decide sits next to the buttons: the active object's category and
        # checkbox, and how many objects are about to be sent.
        #
        # No face count: it would require evaluating the meshes on every
        # redraw, the same reason the panel does not count triangles. Faces
        # are read from the bake outcome.
        #
        # Icons: only EXPORT and TRASH, already used above. Same warning as
        # for the proxy applies: a nonexistent icon takes down the whole
        # panel, not just this section.
        box = layout.box()
        box.label(text="Bake into Revit")

        # target is the active mesh already read for the proxies: the
        # category is chosen on the active object, and "Apply to Selected"
        # copies from there
        if target is None:
            box.label(text="no active mesh", icon='INFO')
        else:
            box.label(text="active object: {}".format(target.name))
            # the guard prevents a failed registration from turning every
            # redraw into an exception, as for the thresholds below
            if hasattr(target, bake.CATEGORY_PROPERTY):
                box.prop(target, bake.CATEGORY_PROPERTY, text="Category")
            # right below the category: it is the other half of "what this
            # object becomes", and "Apply to Selected" copies them together
            if hasattr(target, bake.ACCEPT_OPEN_PROPERTY):
                box.prop(target, bake.ACCEPT_OPEN_PROPERTY, text="Accept open solid")

        row = box.row()
        row.enabled = target is not None
        row.operator("bilocus.apply_bake_category")

        targets = bake.bake_targets(context)
        box.label(text="selected in {}: {}".format(coll.COLLECTION_NAME, len(targets)))

        bake_editing = sync.objects_in_edit_mode(targets)
        if bake_editing:
            warn = box.column()
            warn.alert = True
            warn.label(text="Edit Mode: {}".format(", ".join(bake_editing[:2])),
                       icon='ERROR')
            warn.label(text="bake blocked, press Tab")

        column = box.column()
        column.scale_y = 1.4
        # Same conditions for both modes, in the same column: filtering
        # categories for the family bake is done by the operator, which
        # knows how to say WHICH object was skipped. Disabling "Bake
        # Family" here for a single wall in the selection would hide the
        # reason.
        column.enabled = client.CLIENT.running and bool(targets) and not bake_editing
        # EXPORT on both: it is the only icon identifier already seen
        # working inside Blender 5.1 and 5.2. The text tells them apart.
        column.operator("bilocus.bake_directshape", icon='EXPORT')
        column.operator("bilocus.bake_family", icon='EXPORT')

        removable, _removable_ids = bake.remove_targets(context)
        row = box.row()
        row.enabled = client.CLIENT.running and bool(removable)
        row.operator("bilocus.remove_bake", icon='TRASH')
        box.label(text=bake.LAST_BAKE["message"])

        # Opposite direction: what comes FROM Revit with "Send Selection".
        # The pull has no button here, it is commanded from the Revit
        # ribbon: this section only mirrors the last batch received.
        pull = imp.LAST_PULL
        box = layout.box()
        box.label(text="Last pull from Revit")
        box.label(text=pull.message,
                  icon='IMPORT' if pull.open else 'NONE')
        if pull.received or pull.failed:
            box.label(text="created {} - updated {}".format(
                pull.created, pull.updated))
        # find_collection and not ensure_collection: draw() must not have
        # side effects on the file, same as for ToRevit
        if imp.find_collection() is None:
            box.label(text="collection {}: not created yet".format(
                imp.COLLECTION_NAME), icon='INFO')
        else:
            box.label(text="collection: {}".format(imp.COLLECTION_NAME))

        # Checkbox off by default: in the normal flow the element is moved
        # aside to model against it, and bringing it back to its place on
        # every pull would be a punishment. It is turned on when the
        # position must be faithful, i.e. when aligning geometry that goes
        # back into Revit. Same guard as the thresholds below.
        if hasattr(context.scene, imp.RESET_LOCATION_PROPERTY):
            box.prop(context.scene, imp.RESET_LOCATION_PROPERTY)

        # the guard prevents a failed register_properties from turning the
        # panel into an exception on every redraw
        if hasattr(context.scene, "bilocus_warn_vertices"):
            box = layout.box()
            box.label(text="Per-object limits")
            box.prop(context.scene, "bilocus_warn_vertices")
            box.prop(context.scene, "bilocus_max_vertices")


CLASSES = (
    BILOCUS_OT_connect,
    BILOCUS_OT_disconnect,
    BILOCUS_OT_sync,
    BILOCUS_OT_add_selected,
    BILOCUS_OT_remove_selected,
    BILOCUS_OT_clear,
    BILOCUS_OT_create_proxy,
    BILOCUS_OT_bake_directshape,
    BILOCUS_OT_bake_family,
    BILOCUS_OT_remove_bake,
    BILOCUS_OT_apply_bake_category,
    BILOCUS_PT_panel,
)
