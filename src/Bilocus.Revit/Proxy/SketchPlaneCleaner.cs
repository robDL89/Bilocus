// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Bilocus.Revit.Proxy
{
    // Deletion of proxy curves, sketch planes included.
    //
    // WHY IT EXISTS. Every ModelCurve rests on a SketchPlane, and Revit does
    // NOT take it away when the curve goes: Document.Delete promises the
    // elements "totally dependent upon" the one being deleted, and the
    // dependency runs the other way - it is the curve that depends on the
    // plane. Measured in the field: 60 proxies removed, 60 elements deleted,
    // 60 distinct planes left in the document with nothing on top. Two
    // document elements per edge, half of which stayed behind on every
    // create-remove cycle, invisibly.
    //
    // WHY CONDITIONAL AND NOT ASSUMED. The plane passed to
    // SketchPlane.Create may not be the one Revit actually attaches to the
    // curve, and Revit can reuse a plane that already exists and carries the
    // user's own sketches. Field measurement says that with 60 curves
    // created in one go it did not, but that does not prove it never does in
    // a document full of sketches. Deleting a plane that supports the user's
    // geometry would be far worse than the defect this class closes, so every
    // plane is deleted only after asking Revit whether anyone else depends on
    // it.
    //
    // ONE COPY FOR TWO PATHS. Proxy curves are deleted from explicit removal
    // (RemoveProxyCommand) and from replacement inside ProxyBuilder, which is
    // the one used most: the user resends the edges many times for every
    // explicit removal. Fixing only one would have left the loss where it is
    // most frequent, and two copies would diverge on the same day one gets
    // fixed. Same reason DescribeWhyNotWritable is public on ProxyBuilder.
    //
    // NO TRANSACTION IN HERE, like the rest of this folder's methods: it runs
    // in the caller's transaction, because a Ctrl+Z must keep putting
    // everything back in one shot.
    public static class SketchPlaneCleaner
    {
        // Deletes the given curves and, right after, the planes they rested on
        // that nobody needs anymore.
        //
        // Returns how many ids Revit reported deleting in total, curves and
        // planes together. Fills planes with the counts and consideredPlanes
        // with the ids of the planes read, which are needed by whoever wants
        // to verify after the commit what is really left in the document.
        public static int DeleteCurvesAndOrphanPlanes(
            Document document,
            IList<ElementId> curveIds,
            SketchPlaneCleanupResult planes,
            IList<ElementId> consideredPlanes)
        {
            if (document == null) { throw new ArgumentNullException("document"); }
            if (curveIds == null) { throw new ArgumentNullException("curveIds"); }
            if (planes == null) { throw new ArgumentNullException("planes"); }

            // Before deleting: afterwards the curve no longer exists, and with
            // it goes the only way back to its plane.
            List<ElementId> candidates = ReadSketchPlaneIds(document, curveIds, planes);

            if (consideredPlanes != null)
            {
                foreach (ElementId id in candidates) { consideredPlanes.Add(id); }
            }

            // A single call with all the ids, not one per element:
            // Document.Delete in bulk leaves it to Revit to order the
            // dependencies, and on a few thousand curves the time difference
            // is not marginal.
            ICollection<ElementId> deleted = document.Delete(new List<ElementId>(curveIds));
            int total = deleted == null ? 0 : deleted.Count;

            total += DeleteOrphanPlanes(document, candidates, planes);
            return total;
        }

        // How many of the planes considered STILL exist in the document.
        //
        // Must be called with the transaction CLOSED: before the commit the
        // document state is the one being modified, and an element deleted
        // but not yet confirmed is not an answer to the question.
        public static void CountSurviving(
            Document document, IList<ElementId> consideredPlanes, SketchPlaneCleanupResult planes)
        {
            if (planes == null) { return; }
            if (!planes.Measured) { return; }
            if (document == null || consideredPlanes == null || consideredPlanes.Count == 0) { return; }

            try
            {
                int surviving = 0;

                foreach (ElementId planeId in consideredPlanes)
                {
                    if (document.GetElement(planeId) != null) { surviving++; }
                }

                planes.SurvivingCount = surviving;
                planes.SurvivingChecked = true;
            }
            catch (Exception ex)
            {
                planes.SurvivingChecked = false;
                planes.FailureReason = ex.GetType().Name + ": " + ex.Message;
            }
        }

        // The distinct sketch planes really attached to the curves. Reads
        // curve.SketchPlane instead of remembering what was passed to
        // SketchPlane.Create: it is the only reliable path, since Revit may
        // have attached a different plane to the curve.
        private static List<ElementId> ReadSketchPlaneIds(
            Document document, IList<ElementId> curveIds, SketchPlaneCleanupResult planes)
        {
            List<ElementId> found = new List<ElementId>();

            try
            {
                HashSet<ElementId> distinct = new HashSet<ElementId>();

                foreach (ElementId id in curveIds)
                {
                    CurveElement curve = document.GetElement(id) as CurveElement;
                    if (curve == null) { continue; }

                    SketchPlane attached = curve.SketchPlane;
                    if (attached == null) { continue; }

                    if (distinct.Add(attached.Id)) { found.Add(attached.Id); }
                }

                planes.ConsideredCount = found.Count;
                planes.Measured = true;
            }
            catch (Exception ex)
            {
                // Without the read there is no possible cleanup, but the
                // curves must go anyway: this falls back to the behavior from
                // before the fix, which left the planes but deleted what the
                // user asked to delete.
                planes.Measured = false;
                planes.ConsideredCount = 0;
                planes.FailureReason = ex.GetType().Name + ": " + ex.Message;
                found.Clear();
            }

            return found;
        }

        // The actual deletion of the planes left uncovered. Returns how many
        // ids Revit reported removing.
        private static int DeleteOrphanPlanes(
            Document document, List<ElementId> candidates, SketchPlaneCleanupResult planes)
        {
            if (!planes.Measured || candidates.Count == 0) { return 0; }

            try
            {
                List<ElementId> orphans = new List<ElementId>();

                foreach (ElementId planeId in candidates)
                {
                    Element element = document.GetElement(planeId);
                    if (element == null)
                    {
                        planes.VanishedWithCurvesCount++;
                        continue;
                    }

                    // If the id does not point to a sketch plane, something
                    // does not match our model of the document: it is left
                    // alone. Deleting an element we did not understand is the
                    // worst thing this code could do.
                    if (!(element is SketchPlane))
                    {
                        planes.KeptCount++;
                        continue;
                    }

                    if (HasOtherDependents(element))
                    {
                        planes.KeptCount++;
                        continue;
                    }

                    orphans.Add(planeId);
                }

                if (orphans.Count == 0) { return 0; }

                ICollection<ElementId> deleted = document.Delete(orphans);
                planes.DeletedCount = orphans.Count;
                return deleted == null ? 0 : deleted.Count;
            }
            catch (Exception ex)
            {
                // The curves are already gone and the transaction is still
                // good: cleaning up the planes is a bonus and not worth the
                // operation the user asked for.
                planes.Measured = false;
                planes.FailureReason = ex.GetType().Name + ": " + ex.Message;
                return 0;
            }
        }

        // True if someone ELSE still depends on this element.
        //
        // GetDependentElements returns, by contract, the elements that "will
        // be deleted if the input Element is deleted": if the set is empty,
        // deleting the plane takes nothing else with it and is safe.
        //
        // THE DOUBT ABOUT THE CONTENTS. The documentation does not say
        // whether the set also contains the element itself, and without
        // Revit open there is no way to check. Both cases are handled here by
        // discarding our own id: if Revit does not include it, discarding it
        // finds nothing and changes nothing; if it does include it, without
        // the discard every plane would look occupied and the leak would
        // stay whole. Only one of the two errors is costly, but neither is
        // acceptable.
        //
        // A plane that throws while being queried is kept: a doubt about what
        // rests on it is resolved by leaving it alone.
        private static bool HasOtherDependents(Element element)
        {
            ICollection<ElementId> dependents;
            try
            {
                dependents = element.GetDependentElements(null);
            }
            catch (Exception)
            {
                return true;
            }

            if (dependents == null) { return false; }

            ElementId own = element.Id;

            foreach (ElementId id in dependents)
            {
                if (id == null) { continue; }
                if (own.Equals(id)) { continue; }
                return true;
            }

            return false;
        }
    }
}
