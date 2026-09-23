// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Bilocus.Geometry;
using Bilocus.Protocol;

namespace Bilocus.Revit.Proxy
{
    // The point where Bilocus stops being read-only on the model: here the
    // edges selected in Blender become ModelCurve inside the Revit document.
    //
    // ONE SINGLE TRANSACTION PER USER ACTION. This is not a style preference:
    // the transaction name ends up in the undo history, and if a single
    // creation needed three, a Ctrl+Z would not be enough and the user would
    // find the model half done. The rest of this file's structure follows
    // from this: the subcategory, removing the previous proxies, creating the
    // curves and applying the mark all live inside the same
    // using(Transaction), and the methods it calls were written on purpose so
    // as not to open one of their own.
    //
    // DEGENERATE EDGES ARE SKIPPED, NOT MADE TO FAIL. A real mesh contains
    // edges of zero or near-zero length, and a single bad edge must not take
    // down the twenty good ones next to it. They are counted and reported, so
    // they do not disappear silently.
    public static class ProxyBuilder
    {
        // Must read identical in Revit's undo history: if it ever changes, so
        // does what the user sees written next to Ctrl+Z.
        public const string TransactionName = "Bilocus: create proxy";

        public static ProxyBuildResult Build(Document document, ProxyEdgeRequest request)
        {
            if (request == null) { throw new ArgumentNullException("request"); }

            ProxyBuildResult result = new ProxyBuildResult();
            result.ObjectId = request.ObjectId;
            result.Name = request.Name;
            result.RequestedCount = request.SegmentCount;

            string refusal = DescribeWhyNotWritable(document);
            if (refusal != null)
            {
                result.Succeeded = false;
                result.FailureReason = refusal;
                return result;
            }

            long start = Stopwatch.GetTimestamp();

            // The ids of the planes of the REPLACED proxies survive the
            // transaction: after the commit they are needed to go and look at
            // what is really left in the document instead of trusting what
            // Delete reported.
            List<ElementId> replacedPlanes = new List<ElementId>();

            try
            {
                using (Transaction transaction = new Transaction(document, TransactionName))
                {
                    transaction.Start();

                    try
                    {
                        List<ElementId> created = Populate(document, request, result, replacedPlanes);
                        MeasureSketchPlanes(document, created, result);
                    }
                    catch (Exception)
                    {
                        // A failure halfway through must NOT leave half a proxy
                        // in the document: everything is rolled back and
                        // reported. This is what makes it acceptable to create
                        // elements from a network message.
                        transaction.RollBack();
                        throw;
                    }

                    TransactionStatus status = transaction.Commit();
                    if (status == TransactionStatus.Committed)
                    {
                        result.Succeeded = true;
                    }
                    else
                    {
                        result.Succeeded = false;
                        result.FailureReason = "the transaction was not committed: " + status;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Succeeded = false;
                result.FailureReason = ex.GetType().Name + ": " + ex.Message;
            }

            if (result.Succeeded)
            {
                // After the transaction is closed, for the same reason that
                // holds in RemoveProxyCommand: before the commit the document
                // state is the one being modified.
                SketchPlaneCleaner.CountSurviving(
                    document, replacedPlanes, result.ReplacedSketchPlanes);
            }

            result.ElapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            return result;
        }

        // The reason why it CANNOT be written, or null if it can.
        //
        // Must be called BEFORE opening any transaction. Trying and catching
        // the exception would give the same outcome but a message written by
        // Revit for a developer, not for someone who just pressed a button in
        // Blender and wants to know what to do next.
        //
        // Public because it applies to BOTH directions of writing: it is also
        // used by RemoveProxyCommand before deleting, and from Phase B also by
        // BakeBuilder and BakeRemover. A second copy would diverge on the same
        // day one of the two gets fixed, and the forgotten branch would be the
        // one that attempts a transaction on a document that does not accept
        // one. For the same reason the messages talk about the bridge and not
        // about proxies: they also reach whoever pressed "Bake".
        public static string DescribeWhyNotWritable(Document document)
        {
            if (document == null)
            {
                return "no active document in Revit: open a project and try again";
            }

            // ModelCurve are created with document.Create, which in a family
            // is not the right path (there one uses document.FamilyCreate) and
            // above all is not what is needed: proxies are scaffolding for
            // modeling inside a project.
            if (document.IsFamilyDocument)
            {
                return "the active document is a family: the bridge only writes into a project";
            }

            if (document.IsLinked)
            {
                return "the active document is a link: it cannot be modified";
            }

            // IsReadOnly is dynamic: it is also true while Revit is handling
            // failures, and in that state not even opening a transaction is
            // allowed.
            if (document.IsReadOnly)
            {
                return "the document is currently read-only";
            }

            // IsModifiable true here means a transaction is ALREADY open by
            // someone else. Opening another one on top would give a nested
            // transaction, which is not what is wanted, and above all it would
            // mean our creation ends up inside the undo of another operation.
            if (document.IsModifiable)
            {
                return "a transaction is already open on this document: try again in a moment";
            }

            return null;
        }

        // The body of the transaction. Returns the ids of the created curves,
        // which are needed to measure the planes.
        private static List<ElementId> Populate(
            Document document,
            ProxyEdgeRequest request,
            ProxyBuildResult result,
            List<ElementId> replacedPlanes)
        {
            // The schema lives in the session, not in the document: registering
            // it here writes nothing, but doing it before the loop avoids
            // discovering a GUID collision halfway through creation.
            ProxySchema.GetOrCreate();

            // Replacement: by resending the same
            // Blender object, the previous proxies of THAT object go away.
            // Silent and intentional: it is the tool's mental model, "resend,
            // redo". Those of other objects are left untouched.
            //
            // It goes through the SAME method as explicit removal, and this is
            // not a detail: this is the path used most often - the user
            // resends the edges many times for every "Remove proxy" - and this
            // is where the loss of sketch planes weighed the most. A second
            // copy of the cleanup would diverge on the same day one of the two
            // gets fixed.
            IList<ElementId> previous = ProxySchema.FindMarked(document, request.ObjectId);
            if (previous.Count > 0)
            {
                result.ReplacedCount = previous.Count;
                result.DeletedElementCount = SketchPlaneCleaner.DeleteCurvesAndOrphanPlanes(
                    document, previous, result.ReplacedSketchPlanes, replacedPlanes);
            }

            // After deletion and before creation: if the subcategory does not
            // exist yet, this is the call that creates it, inside the same
            // transaction.
            GraphicsStyle style = ProxyLineStyle.GetOrCreate(document);

            // Revit's minimum tolerance on curve length. Read from the
            // application instead of inventing one: it is the same threshold
            // below which Line.CreateBound throws, so comparing against it is
            // the only way to skip exactly the edges Revit would reject.
            double tolerance = document.Application.ShortCurveTolerance;

            List<ElementId> created = new List<ElementId>();

            for (int i = 0; i < request.EdgeCount; i++)
            {
                XYZ from = ToFeet(request.StartOf(i));
                XYZ to = ToFeet(request.EndOf(i));

                if (from.DistanceTo(to) <= tolerance)
                {
                    result.SkippedDegenerateCount++;
                    continue;
                }

                try
                {
                    ElementId id = CreateLine(document, from, to, style, request.ObjectId);
                    if (id == null)
                    {
                        result.FailedCount++;
                        result.LastFailureReason = "Revit did not return a model line";
                        continue;
                    }

                    created.Add(id);
                    result.CreatedCount++;
                }
                catch (Exception ex)
                {
                    // Isolated to the single edge, like degenerate ones but
                    // counted separately: a rejection for a reason other than
                    // length is a defect worth looking at, not a physiological
                    // case.
                    result.FailedCount++;
                    result.LastFailureReason = string.Format(
                        "edge {0}: {1}: {2}", i, ex.GetType().Name, ex.Message);
                }
            }

            // The arcs of "Arcs and lines" mode, after the lines. Same rules:
            // degenerate ones are skipped and counted, a rejection stays
            // isolated to the single arc.
            for (int i = 0; i < request.ArcCount; i++)
            {
                XYZ from = ToFeet(request.ArcStartOf(i));
                XYZ to = ToFeet(request.ArcEndOf(i));
                XYZ mid = ToFeet(request.ArcMidOf(i));

                // Arc.Create throws if two of the three points coincide within
                // the tolerance: this is the physiological case of a tiny arc,
                // to be counted like lines that are too short and not as a
                // failure.
                if (from.DistanceTo(mid) <= tolerance || mid.DistanceTo(to) <= tolerance)
                {
                    result.SkippedDegenerateCount++;
                    continue;
                }

                try
                {
                    ElementId id = CreateArc(document, from, to, mid, style, request.ObjectId);
                    if (id == null)
                    {
                        result.FailedCount++;
                        result.LastFailureReason = "Revit did not return a model arc";
                        continue;
                    }

                    created.Add(id);
                    result.CreatedCount++;
                    result.CreatedArcCount++;
                }
                catch (Exception ex)
                {
                    // Typically three aligned points: Blender already sends
                    // them as a straight line, but the round trip through
                    // float32 on the wire can straighten an almost flat arc.
                    result.FailedCount++;
                    result.LastFailureReason = string.Format(
                        "arc {0}: {1}: {2}", i, ex.GetType().Name, ex.Message);
                }
            }

            return created;
        }

        private static ElementId CreateArc(
            Document document, XYZ from, XYZ to, XYZ mid, GraphicsStyle style, string objectId)
        {
            Arc arc = Arc.Create(from, to, mid);

            // The sketch plane is that of the arc, with the normal oriented
            // according to ProxyPlaneNormal's convention. arc.Normal depends
            // on the direction of travel: used as-is, the arcs of an S shape
            // had their normal half pointing up and half pointing down, and
            // beams picked with Pick Lines followed suit. The plane is the
            // same, only the direction changes.
            XYZ raw = arc.Normal;
            double[] oriented = ProxyPlaneNormal.Orient(new double[] { raw.X, raw.Y, raw.Z });
            Plane plane = Plane.CreateByNormalAndOrigin(
                new XYZ(oriented[0], oriented[1], oriented[2]), arc.Center);
            return AddModelCurve(document, arc, plane, style, objectId);
        }

        private static ElementId CreateLine(
            Document document, XYZ from, XYZ to, GraphicsStyle style, string objectId)
        {
            XYZ direction = (to - from).Normalize();

            // The "plan" plane: it contains the line and the perpendicular
            // horizontal, so it is only as tilted as the line's slope. A beam
            // picked with Pick Lines does not roll sideways.
            double[] plan = ProxyPlaneNormal.ForLine(
                new double[] { direction.X, direction.Y, direction.Z });

            XYZ normal;
            if (plan != null)
            {
                normal = new XYZ(plan[0], plan[1], plan[2]);
            }
            else
            {
                // Vertical line: the plan plane does not exist.
                // PerpendicularVector picks the axis the direction is LEAST
                // aligned with, and this avoids the null vector.
                float[] axis = PerpendicularVector.Compute(
                    new float[] { (float)direction.X, (float)direction.Y, (float)direction.Z });
                normal = new XYZ(axis[0], axis[1], axis[2]);
            }

            // Re-orthogonalization in double. The normal comes from float or
            // from a normalized direction: a residual component along the
            // direction on the order of 1e-7 is enough for the curve to NOT
            // lie exactly in the plane, and NewModelCurve rejects curves out
            // of plane. Removing the projection costs two lines.
            normal = normal - direction.Multiply(normal.DotProduct(direction));
            normal = normal.Normalize();

            Plane plane = Plane.CreateByNormalAndOrigin(normal, from);
            return AddModelCurve(document, Line.CreateBound(from, to), plane, style, objectId);
        }

        private static ElementId AddModelCurve(
            Document document, Curve geometry, Plane plane, GraphicsStyle style, string objectId)
        {
            SketchPlane sketchPlane = SketchPlane.Create(document, plane);

            ModelCurve curve = document.Create.NewModelCurve(geometry, sketchPlane);
            if (curve == null) { return null; }

            // The line style before the mark: if the assignment fails, the
            // transaction still rolls back in full and the document is not
            // left with a curve marked with the wrong style.
            curve.LineStyle = style;
            ProxySchema.Mark(curve, objectId);

            return curve.Id;
        }

        // Measures how many distinct SketchPlane really support the created
        // curves.
        //
        // Needed to determine whether a
        // create-then-remove cycle brings the document back to the starting
        // count. These are two separate questions: how many planes exist for
        // N curves, and whether the plane attached to the curve is the one
        // passed to NewModelCurve. Revit can reuse a geometrically equivalent
        // one instead of ours, and in that case the plane to evaluate at
        // removal time is not the one we had in hand.
        //
        // The measurement must NOT be able to make the creation fail: if a
        // read throws, the number is given up and reported as "not measured",
        // but the curves stay. A diagnostic count is not worth a transaction.
        private static void MeasureSketchPlanes(
            Document document, List<ElementId> created, ProxyBuildResult result)
        {
            if (created.Count == 0) { return; }

            try
            {
                HashSet<ElementId> distinct = new HashSet<ElementId>();
                int reused = 0;

                foreach (ElementId id in created)
                {
                    CurveElement curve = document.GetElement(id) as CurveElement;
                    if (curve == null) { return; }

                    SketchPlane attached = curve.SketchPlane;
                    if (attached == null) { return; }

                    if (!distinct.Add(attached.Id)) { reused++; }
                }

                result.SketchPlaneCount = distinct.Count;
                result.ReusedSketchPlaneCount = reused;
                result.SketchPlanesMeasured = true;
            }
            catch (Exception)
            {
                result.SketchPlanesMeasured = false;
            }
        }

        // Meters (what arrives from Blender) to feet (Revit's internal units).
        // The factor lives in BridgeConstants, where it already exists for the
        // opposite direction: two different constants for the same conversion
        // would diverge the day someone fixes only one of them.
        private static XYZ ToFeet(float[] point)
        {
            return new XYZ(
                point[0] * BridgeConstants.FeetPerMeter,
                point[1] * BridgeConstants.FeetPerMeter,
                point[2] * BridgeConstants.FeetPerMeter);
        }
    }
}
