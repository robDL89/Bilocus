// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace Bilocus.Revit.Proxy
{
    // The mark: the Extensible Storage schema with which the bridge signs the
    // elements it creates in the Revit document.
    //
    // From this phase on the add-in stops being read-only on the model.
    // Without a mark it would not be possible to replace or bulk-remove
    // proxies, and above all it would not be possible to distinguish what the
    // bridge created from what the user drew. The mark is the only thing that
    // makes the writing reversible.
    //
    // Schema identity (GUID, names, vendor id) is in ProxyNaming, which is
    // also compiled by the tests.
    //
    // NOTE ON THE ACCESS LEVEL. Both read and write are public. Public read
    // is mandatory: with Vendor access, another session or another add-in
    // could not even enumerate what the bridge left in the model, and the
    // elements would become unidentifiable orphans. Public write is a more
    // debatable choice and is worth justifying: Vendor access is tied to the
    // VendorId declared in the .addin manifest, which is a modifiable field.
    // If it ever changed, the bridge could still read its own marks but no
    // longer delete them, falling right back into the situation the fixed
    // GUID exists to avoid. In exchange, the restriction would defend very
    // little: whoever wants to break a proxy can already delete the
    // ModelCurve, and for that Extensible Storage is useless anyway. It is
    // therefore accepted that the mark is alterable, and what is read is not
    // blindly trusted (see ProxyNaming.MatchesObjectId, lenient by design).
    public static class ProxySchema
    {
        // Registers the schema in the session, or returns the one already
        // registered.
        //
        // The Lookup before construction is not an optimization: it is
        // mandatory. SchemaBuilder.Finish throws if a schema with the same
        // identity already exists, which happens from the second call onward
        // in the same Revit session.
        //
        // Requires no transaction: registering the schema lives in the
        // session, not in the document. Only writing the Entity onto an
        // element touches the document.
        public static Schema GetOrCreate()
        {
            Schema existing = Schema.Lookup(ProxyNaming.SchemaGuid);
            if (existing != null)
            {
                // A schema with our GUID already exists but without our
                // field: either it is a previous version of the bridge, or it
                // is a GUID collision with another add-in. Either way it is
                // better to stop here with an understandable message than to
                // let Entity.Get throw further down, in the middle of a loop
                // over every curve in the model.
                if (existing.GetField(ProxyNaming.ObjectIdFieldName) == null)
                {
                    throw new InvalidOperationException(
                        "A schema with Bilocus's GUID already exists in this session but does not "
                        + "contain the field " + ProxyNaming.ObjectIdFieldName
                        + ": the proxy mark is not usable.");
                }

                return existing;
            }

            SchemaBuilder builder = new SchemaBuilder(ProxyNaming.SchemaGuid);
            builder.SetSchemaName(ProxyNaming.SchemaName);
            builder.SetVendorId(ProxyNaming.VendorId);
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation(
                "Mark of the elements created by Bilocus. Contains the identifier "
                + "of the source Blender object.");
            builder.AddSimpleField(ProxyNaming.ObjectIdFieldName, typeof(string));

            return builder.Finish();
        }

        // Applies the mark to an already created element.
        //
        // Must run inside a transaction already opened by the caller: it does
        // not open one of its own, because marking and creating the element
        // are a single user action and must be a single entry in the undo
        // history.
        public static void Mark(Element element, string objectId)
        {
            if (element == null) { throw new ArgumentNullException("element"); }

            // Normalize before writing: if the obj_id were invalid it would
            // only be discovered at replacement time, when the duplicate
            // proxies are already in the document.
            string normalized = ProxyNaming.NormalizeObjectId(objectId);

            Document document = element.Document;
            if (document == null || !document.IsModifiable)
            {
                throw new InvalidOperationException(
                    "ProxySchema.Mark requires a transaction already open on the document.");
            }

            Schema schema = GetOrCreate();
            Entity entity = new Entity(schema);
            entity.Set<string>(ProxyNaming.ObjectIdFieldName, normalized);
            element.SetEntity(entity);
        }

        // The obj_id stored on an element, or null if the element does not
        // carry the bridge's mark.
        //
        // Element.GetEntity does not return null when the mark is missing: it
        // returns an invalid Entity (verified against RevitAPI.xml). Hence the
        // IsValid check, which is the only thing that distinguishes a bridge
        // element from a user one.
        public static string ReadObjectId(Element element)
        {
            if (element == null) { return null; }

            Schema schema = GetOrCreate();
            return ReadObjectId(element, schema);
        }

        // Overload with the schema already resolved: this is what is needed
        // inside a loop over thousands of curves, where redoing the Lookup on
        // every iteration is wasted work.
        private static string ReadObjectId(Element element, Schema schema)
        {
            if (!schema.ReadAccessGranted()) { return null; }

            Entity entity = element.GetEntity(schema);
            if (entity == null || !entity.IsValid()) { return null; }

            string stored = entity.Get<string>(ProxyNaming.ObjectIdFieldName);
            if (stored == null) { return null; }

            // An empty string in the mark does not identify anything: treating
            // it as "unmarked" prevents a removal by empty obj_id from
            // sweeping the document clean.
            return stored.Trim().Length == 0 ? null : stored;
        }

        // All the PROXIES marked by the bridge in the document: the
        // CurveElement, and only them.
        //
        // The OfClass(CurveElement) filter is not a detail: without it, the
        // Entity would have to be requested for every element in the
        // document, and on a project model that is hundreds of thousands of
        // calls across the managed boundary.
        //
        // From Phase B the mark is not only on curves anymore: the bake's
        // DirectShape also carry it, with the same schema and the same
        // field. This search and the one below by obj_id stay on
        // CurveElement ON PURPOSE: "Remove proxy" and proxy replacement use
        // them, and neither must sweep away a bake. Proxy and bake are
        // distinguished by element class, so whoever searches for something
        // else asks for its class with the three-argument overload. Whoever
        // adds a new marked type picks its class there, and does NOT widen
        // these two: widening them would mean "Remove proxy" starts deleting
        // things that are not proxies.
        public static IList<ElementId> FindMarked(Document document)
        {
            return FindMarked(document, null);
        }

        // The proxies marked with a specific obj_id. This is what replacement
        // needs: by resending the same Blender object, the previous proxies of
        // that object go away, those of other objects stay.
        //
        // With objectId null it returns every marked proxy.
        public static IList<ElementId> FindMarked(Document document, string objectId)
        {
            return FindMarked(document, typeof(CurveElement), objectId);
        }

        // The elements of a class marked by the bridge: with a specific
        // obj_id, or all of them with objectId null. The bake calls it with
        // typeof(DirectShape).
        //
        // The class is mandatory and has no "whole document" fallback: this
        // is what keeps proxies and bake separate, and what keeps the search
        // on a fast filter instead of on every element in the model. One
        // single body for every search: a second copy of the loop would
        // diverge the day one of the two is fixed, for example on the lenient
        // comparison of obj_ids.
        public static IList<ElementId> FindMarked(Document document, Type elementClass, string objectId)
        {
            if (elementClass == null) { throw new ArgumentNullException("elementClass"); }

            List<ElementId> found = new List<ElementId>();
            if (document == null) { return found; }

            Schema schema = GetOrCreate();
            if (!schema.ReadAccessGranted()) { return found; }

            FilteredElementCollector collector =
                new FilteredElementCollector(document).OfClass(elementClass);

            foreach (Element element in collector)
            {
                string stored = ReadObjectId(element, schema);
                if (stored == null) { continue; }

                if (objectId != null && !ProxyNaming.MatchesObjectId(stored, objectId))
                {
                    continue;
                }

                found.Add(element.Id);
            }

            return found;
        }
    }
}
