// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Globalization;

namespace Bilocus.Revit.Pull
{
    // Outcome of a selection send to Blender: counts, timings and the text
    // summary that ends up in SendSelectionCommand's TaskDialog.
    //
    // No reference to the Revit API on purpose: it is the part of
    // SendSelectionCommand that can be verified without opening Revit, with
    // the same pattern that made ElementNaming testable. Public, mutable
    // fields instead of a ten-parameter constructor: the caller fills them in
    // as the command advances through two phases (tessellation, sending), and
    // one value may not be known yet when another already is.
    public sealed class SendSelectionResult
    {
        // Elements whose revit_geometry went out in full.
        public int SentCount;

        // Elements with empty geometry (levels, grids, views, annotations):
        // not an error, the normal case for certain categories.
        public int SkippedEmptyCount;

        // DirectShape with the bridge's mark, i.e. bakes that came from
        // Blender (Phase B). Skipped on purpose: sending them back to Blender
        // would close the feedback loop, and a baked object would return to
        // Blender as a copy of itself. Counted separately from "no geometry"
        // because they do have geometry, and whoever selected them needs to
        // understand why they did not go out.
        public int SkippedBridgeCount;

        // Elements whose tessellation threw an exception. Counted and named,
        // so that one failure does not go unnoticed just because the others
        // went through anyway.
        public int FailedCount;
        public string LastFailureLabel = "";
        public string LastFailureError = "";

        public int TotalTriangles;

        // The two phases must be timed separately: this is the dominant cost
        // of this part of the bridge and nobody knows which of the two
        // dominates until it is measured on real elements.
        public double TessellateMs;
        public double SendMs;

        // True if the send was interrupted mid-batch. BridgeServer.Send can
        // fail either because a frame was rejected before touching the stream
        // (recoverable) or because a write was interrupted midway (terminal,
        // the connection is already closed by BridgeServer itself): in both
        // cases this command does not insist, it stops and says so.
        public bool Aborted;
        public string AbortReason = "";

        public string BuildSummaryText()
        {
            string text = string.Format(CultureInfo.InvariantCulture,
                "Sent: {0} elements ({1} triangles)\n" +
                "Skipped (no geometry): {2}\n" +
                "Skipped (bridge-generated bakes): {3}\n" +
                "Failed: {4}",
                SentCount, TotalTriangles, SkippedEmptyCount, SkippedBridgeCount, FailedCount);

            if (FailedCount > 0)
            {
                text = text + string.Format(
                    "\nLast failed element: {0} - {1}", LastFailureLabel, LastFailureError);
            }

            text = text + string.Format(CultureInfo.InvariantCulture,
                "\nTessellation time: {0:F0} ms\nSend time: {1:F0} ms", TessellateMs, SendMs);

            if (Aborted)
            {
                text = text + "\nWARNING: send interrupted - " + AbortReason;
            }

            return text;
        }
    }
}
