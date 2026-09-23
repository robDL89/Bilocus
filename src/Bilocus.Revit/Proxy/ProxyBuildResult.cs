// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Globalization;

namespace Bilocus.Revit.Proxy
{
    // Outcome of a proxy creation: counts, measurements, and the readable
    // summary that ends up in the Status button and in the response frame.
    //
    // No reference to the Revit API, for the same reason as SendSelectionResult
    // in Phase A2: it is the part of ProxyBuilder that can be verified without
    // opening Revit. Public, mutable fields because the constructor fills them
    // in as the transaction proceeds.
    //
    // The counts are not decoration. From this phase on the bridge leaves
    // elements in a project file: knowing how many it created, how many it
    // replaced and how many it skipped is the only way to notice that it did
    // something different from what was wanted, and to notice it right away
    // instead of at the next open-and-close.
    public sealed class ProxyBuildResult
    {
        public string ObjectId = "";
        public string Name = "";

        // Edges declared in the message.
        public int RequestedCount;

        // ModelCurve actually created, arcs included.
        public int CreatedCount;

        // How many of the created ModelCurve are arcs ("Arcs and lines" mode).
        public int CreatedArcCount;

        // Proxies with the same obj_id found in the document and removed
        // before recreating (resending replaces).
        public int ReplacedCount;

        // Elements actually removed from the document during replacement: the
        // previous curves plus the sketch planes left uncovered. If it equals
        // ReplacedCount exactly, no plane was cleaned up - either because there
        // were none, or because they were all still in use, and
        // ReplacedSketchPlanes says which of the two.
        public int DeletedElementCount;

        // Outcome of cleaning up the planes of the REPLACED proxies, in the
        // same format used by explicit removal. Proxy curves are deleted from
        // two paths, and the planes must be counted and reported the same way
        // in both.
        public readonly SketchPlaneCleanupResult ReplacedSketchPlanes = new SketchPlaneCleanupResult();

        // Edges below Revit's minimum curve tolerance. Skipped and counted,
        // not made to fail: a real mesh contains some, and a single zero
        // length edge must not cost all the good ones next to it.
        public int SkippedDegenerateCount;

        // Edges that Revit rejected for a reason other than length. Isolated
        // the same way, but kept distinct: a degenerate one is physiological,
        // this one is a defect worth looking at.
        public int FailedCount;
        public string LastFailureReason = "";

        // DISTINCT sketch planes actually attached to the created curves, read
        // after creation. They are not necessarily the ones SketchPlane.Create
        // returned: Revit can reuse a geometrically equivalent one. Hence the
        // two separate counters.
        public int SketchPlaneCount;
        public int ReusedSketchPlaneCount;

        // False if measuring the planes failed: the counts above must be read
        // as "not measured", not as zero.
        public bool SketchPlanesMeasured;

        public bool Succeeded;

        // The reason when Succeeded is false: document not writable,
        // transaction not open, commit rejected. It is a problem with Revit or
        // the document, not with the message received.
        public string FailureReason = "";

        public double ElapsedMs;

        // How many document elements the created edges cost, on average. It is
        // zero until the planes have been measured.
        //
        // The expected number is 2: one ModelCurve and one SketchPlane per
        // edge. If it comes out under 2, Revit reused some planes; if it comes
        // out over, it is creating extra ones on its own, and that
        // must be known.
        public double ElementsPerEdge
        {
            get
            {
                if (!SketchPlanesMeasured || CreatedCount == 0) { return 0; }
                return (double)(CreatedCount + SketchPlaneCount) / CreatedCount;
            }
        }

        public static ProxyBuildResult Failed(ProxyEdgeRequest request, string reason)
        {
            ProxyBuildResult result = new ProxyBuildResult();
            if (request != null)
            {
                result.ObjectId = request.ObjectId;
                result.Name = request.Name;
                result.RequestedCount = request.SegmentCount;
            }
            result.Succeeded = false;
            result.FailureReason = reason == null ? "" : reason;
            return result;
        }

        public string BuildSummaryText()
        {
            if (!Succeeded)
            {
                return string.Format(
                    "Proxy NOT created for '{0}': {1}", Name, FailureReason);
            }

            // The Phase A3 text stays identical when there are no arcs.
            string text = CreatedArcCount > 0
                ? string.Format(CultureInfo.InvariantCulture,
                    "Proxy for '{0}': {1} created ({2} arcs) out of {3} segments, {4} replaced",
                    Name, CreatedCount, CreatedArcCount, RequestedCount, ReplacedCount)
                : string.Format(CultureInfo.InvariantCulture,
                    "Proxy for '{0}': {1} created out of {2} edges, {3} replaced",
                    Name, CreatedCount, RequestedCount, ReplacedCount);

            if (ReplacedCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    " ({0} elements removed)", DeletedElementCount);
            }

            if (SkippedDegenerateCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    ", {0} skipped because too short", SkippedDegenerateCount);
            }

            if (FailedCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    ", {0} failed - last: {1}", FailedCount, LastFailureReason);
            }

            if (SketchPlanesMeasured)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    "\nSketch planes: {0} distinct, {1} reused by Revit, {2:F2} elements per edge",
                    SketchPlaneCount, ReusedSketchPlaneCount, ElementsPerEdge);
            }
            else if (CreatedCount > 0)
            {
                text = text + "\nSketch planes: not measured";
            }

            if (ReplacedCount > 0)
            {
                string cleaned = ReplacedSketchPlanes.BuildReportText();
                if (cleaned.Length > 0) { text = text + "\nReplacement. " + cleaned; }
            }

            text = text + string.Format(CultureInfo.InvariantCulture,
                "\nTime: {0:F0} ms", ElapsedMs);

            return text;
        }
    }
}
