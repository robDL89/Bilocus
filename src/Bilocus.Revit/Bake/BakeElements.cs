// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Bake
{
    // What the bridge has left in the document for an obj_id, in the two
    // bake modes: DirectShape (Phase B), families and instances (Phase B2).
    //
    // One place because THREE callers use it: the DirectShape bake mode
    // switch, the family bake mode switch, and "Remove bake". Three copies of
    // the same lookup would diverge the day one gets fixed, and the forgotten
    // branch would be the one leaving an orphan instance in the model.
    //
    // Proxy curves carry the same mark but do NOT live here: the lookup is by
    // class, and none of the three operations must touch a proxy.
    internal static class BakeElements
    {
        public static IList<ElementId> DirectShapes(Document document, string objectId)
        {
            return ProxySchema.FindMarked(document, typeof(DirectShape), objectId);
        }

        public static IList<ElementId> Families(Document document, string objectId)
        {
            return ProxySchema.FindMarked(document, typeof(Family), objectId);
        }

        public static IList<ElementId> MarkedInstances(Document document, string objectId)
        {
            return ProxySchema.FindMarked(document, typeof(FamilyInstance), objectId);
        }

        // All instances of the given families, marked or not.
        //
        // Unmarked ones too: an instance dragged from the project browser
        // does not carry the mark, but it still leaves with the family, and
        // it is an element removed from the document that the count must
        // report.
        public static List<ElementId> InstancesOf(Document document, ICollection<ElementId> familyIds)
        {
            List<ElementId> found = new List<ElementId>();
            if (familyIds == null || familyIds.Count == 0) { return found; }

            HashSet<ElementId> families = new HashSet<ElementId>(familyIds);
            FilteredElementCollector collector =
                new FilteredElementCollector(document).OfClass(typeof(FamilyInstance));

            foreach (Element element in collector)
            {
                FamilyInstance instance = element as FamilyInstance;
                if (instance == null) { continue; }

                FamilySymbol symbol = instance.Symbol;
                if (symbol == null || symbol.Family == null) { continue; }

                if (families.Contains(symbol.Family.Id)) { found.Add(instance.Id); }
            }

            return found;
        }

        // What needs to be deleted to remove the bakes of a set of obj_id
        // from the document, and what the deletion must count.
        //
        // Delete receives DirectShapes, families and marked instances of
        // families that are NOT marked (a copy that survived its family).
        // Instances of marked families are not passed: they leave with the
        // family, and passing them along with the family would mean asking
        // Revit to delete the same element twice.
        //
        // The count instead includes all of them: "instances + families +
        // deleted DirectShapes" (bake_result contract). FamilySymbol,
        // tags and hosted dimensions that Delete also returns are NOT
        // counted: the user has never seen them as bridge elements.
        public sealed class Sweep
        {
            public readonly List<ElementId> ToDelete = new List<ElementId>();
            public readonly HashSet<ElementId> Countable = new HashSet<ElementId>();

            public bool IsEmpty
            {
                get { return ToDelete.Count == 0; }
            }
        }

        // includeDirectShapes / includeFamilies: the mode switch requests
        // only one (the other mode), the removal requests both.
        public static Sweep Collect(
            Document document, IEnumerable<string> objectIds, bool includeDirectShapes, bool includeFamilies)
        {
            if (objectIds == null) { throw new ArgumentNullException("objectIds"); }

            Sweep sweep = new Sweep();
            HashSet<ElementId> queued = new HashSet<ElementId>();
            List<ElementId> families = new List<ElementId>();
            List<ElementId> markedInstances = new List<ElementId>();

            foreach (string objectId in objectIds)
            {
                if (includeDirectShapes)
                {
                    foreach (ElementId id in DirectShapes(document, objectId))
                    {
                        if (queued.Add(id)) { sweep.ToDelete.Add(id); sweep.Countable.Add(id); }
                    }
                }

                if (includeFamilies)
                {
                    foreach (ElementId id in Families(document, objectId))
                    {
                        if (queued.Add(id)) { sweep.ToDelete.Add(id); sweep.Countable.Add(id); families.Add(id); }
                    }
                    markedInstances.AddRange(MarkedInstances(document, objectId));
                }
            }

            if (!includeFamilies) { return sweep; }

            HashSet<ElementId> goingWithFamily = new HashSet<ElementId>(InstancesOf(document, families));
            foreach (ElementId id in goingWithFamily) { sweep.Countable.Add(id); }

            foreach (ElementId id in markedInstances)
            {
                sweep.Countable.Add(id);
                if (!goingWithFamily.Contains(id) && queued.Add(id)) { sweep.ToDelete.Add(id); }
            }

            return sweep;
        }

        // Deletes inside a transaction already opened by the caller and
        // returns how many of the countable elements Revit says it removed.
        // The number comes from what Delete RETURNS, not from what was
        // requested: it is the document that says what left.
        public static int DeleteAndCount(Document document, Sweep sweep)
        {
            if (sweep == null || sweep.IsEmpty) { return 0; }

            ICollection<ElementId> deleted = document.Delete(sweep.ToDelete);
            if (deleted == null) { return 0; }

            int count = 0;
            foreach (ElementId id in deleted)
            {
                if (sweep.Countable.Contains(id)) { count++; }
            }
            return count;
        }
    }
}
